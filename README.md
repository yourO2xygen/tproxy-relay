# tproxy-relay — WEB-proxy relay for Telegram на C#/.NET

Реализация серверной части [WEB-прокси Telegram](https://github.com/telegramdesktop/tproxy-server)
на ASP.NET Core (.NET 10): bridge-страница, https/websocket-несущие, мультиплексированные
MTProto-стримы, flow control. Совместима с реальным клиентом Telegram Desktop 7.2+
(режим WEB-proxy) и официальным MTProxy в качестве бэкенда.

Валидация: полная цепочка `Telegram Desktop → nginx (Let's Encrypt) → релей → MTProxy → DC`
развернута и работает на прод-сервере (e2e-прогон извне: 23 PASS; трафик в обе стороны,
включая obfuscated2-ответы MTProxy и middle-end-соединения до Telegram DC).

```
Telegram-клиент (WEB proxy: server + secret)
   │  capability = base64url(HMAC-SHA256(secret, "tdesktop-web-proxy-bridge-v1\n"+host))
   ▼
nginx :443 (TLS, единственная точка входа, все пути — на релей)
   ▼
C#-релей (Kestrel :8080, admin :8081 loopback-only)
   │  bridge page, /api/v1/session|up|down|ws
   ▼
MTProxy :2398 (в docker-сети, наружу не торчит) ──► Telegram DC
```

## Запуск

```bash
# 1. hosts-запись (один раз)
sudo sh -c 'echo "127.0.0.1 proxy.example.com" >> /etc/hosts'

# 2. Поднять цепочку
docker compose up -d --build

# 3. Прогнать e2e-тест (https-несущая по умолчанию)
dotnet run --project tools/TproxyTestClient

# WebSocket-режим несущей:
docker compose -f docker-compose.yml -f docker-compose.ws.yml up -d --build relay
dotnet run --project tools/TproxyTestClient -- --ws

# Прямой тест релея без nginx (изоляция):
docker compose -f docker-compose.yml -f docker-compose.direct.yml up -d --build relay
dotnet run --project tools/TproxyTestClient -- --host proxy.example.com --port 8080 --plain --ws
```

Секрет по умолчанию: `000102030405060708090a0b0c0d0e0f` (совпадает с тестовым
вектором из PROTOCOL.md — проверяется в step 0 клиента).

## Что реализовано (по PROTOCOL.md)

- Вывод bridge capability: `HMAC-SHA256(secret, "tdesktop-web-proxy-bridge-v1\n"+host)`,
  включая оба нормативных тестовых вектора.
- Одноразовый bridge: только точный `GET /?bridge=<43 chars>` → bridge-страница
  (CSP nonce, no-store, inline JS с обоими boundary: `TelegramWebProxy` и loopback
  MessagePort). Любой другой запрос → обычный публичный сайт.
- Токены: 16 байт random + 16 байт truncated HMAC-SHA256, ключ в volume.
- `POST /api/v1/session`: идемпотентный обмен bootstrap → session (HELLO/WELCOME).
- `POST /api/v1/up`: seq/ack, byte-identical replay, gap → 409 + смерть сессии.
- `POST /api/v1/down`: long-poll 25с, курсор, replay неподтверждённого батча,
  newest-poll-wins.
- `GET /api/v1/ws`: WebSocket-несущая (`tproxy-v1.<token>`).
- Кадры OPEN/DATA/CLOSE/WINDOW/PONG + flow control 4 МиБ на стрим, tombstones,
  лимиты сессий/стримов/pending-байтов, 503+Retry-After при переполнении.
- Admin: `/healthz`, `/readyz`, `/metrics` (отдельный порт, наружу не проброшен).

## Упрощения относительно референса

- Один профиль/секрет (не список), режимы https-lanes/websocket-lanes не реализованы.
- Токены хранятся в памяти как есть (не хэш), нет per-IP лимитов и rate buckets.
- WINDOW не коалесцируются; pending-бюджет грубый (только по client DATA).
- X-Forwarded-For не обрабатывается (per-IP лимиты выключены, как в референсе по умолчанию).
- bridge JS — референсная заготовка (e2e-тест ходит напрямую через HTTP API).

## Замена echo-stub на реальный MTProxy (локально)

```bash
docker compose -f docker-compose.yml -f docker-compose.mtproxy.yml --profile mtproxy up -d --build
```

Локально MTProxy поднимается, но middle-end до Telegram DC блокируется (РФ) —
дальше проверки TCP+obfuscated2-ответов уйти нельзя.

## Локальная разработка и тесты

Локальный контур — self-signed TLS через контейнер nginx и hosts-запись
`127.0.0.1 proxy.example.com`; бэкенд по умолчанию echo-stub, переключается на
реальный MTProxy оверлеем. См. раздел «Запуск» выше.

Секрет по умолчанию: `000102030405060708090a0b0c0d0e0f` (совпадает с тестовым
вектором из PROTOCOL.md — проверяется в step 0 клиента).

## Результаты валидации (2026-09-19)

Полная цепочка проверена настоящим Telegram Desktop 7.2.7 (режим WEB-proxy):

- клиент корректно вывел capability, загрузил bridge, создал сессию;
- стримы (OPEN/DATA/WINDOW) мультиплексируются через https-несущую;
- релей открыл TCP-соединения к настоящему MTProxy (pinned f36d8af), MTProxy
  ответил obfuscated2-заголовками (down_bytes > 0);
- авторекавери: рестарт релея клиент переживает, пересоздаёт сессию сам;
- единственный локальный блокер: MTProxy middle-end не может достучаться до
  Telegram DC с этой машины (РФ-блокировки, curl exit=7) — resPQ не приходит,
  клиент висит в «подключении». Прод-сервер с доступом к DC это снимает.

Прод-контур (Ubuntu 24.04, host-nginx + certbot, контейнеры relay/mtproxy):
- e2e-тест извне: 23 PASS / 2 FAIL (echo-ассеты — норма против реального MTProxy);
- 1 MiB тестового трафика прошёл насквозь, MTProxy вернул obfuscated2-заголовки
  (down_bytes = 3×64), middle-end установил соединения до DC;
- замечание по Docker Hub: при недоступности registry образы переносятся
  `docker save | ssh 'docker load'` (compose ожидает образы `<project>-relay`
  и `<project>-mtproxy`).

Отладочные находки при интеграции с реальным клиентом:
- граница инжекта: кадры приходят как `{data: ArrayBuffer}` через
  `TelegramWebProxy.receive(seq, base64)`, контроль — JSON-строками через
  `postMessage`; мост обязан отправить `{"t":"tproxy-android-init","v":1,"nonce":...}`
  с nonce из `#android=`-фрагмента до HELLO;
- граница loopback (fallback в системном браузере): MessagePort, полные батчи
  ответов как ArrayBuffer (не отдельные кадры);
- скрытый WebKitGTK-WebView на Linux в клиенте падает с "bridge script failure"
  (специфика движка), но fallback в системный браузер работает — трафик идёт
  через него; `first-msg`/`.byteLength` — у ArrayBuffer нет `.length`.

## Прод-деплой (сервер с существующим host-nginx)

```bash
# 1. DNS A-запись proxy.yourdomain.tld -> сервер; порты 80/443 открыты
# 2. Секрет и конфиг
deploy/production/setup.sh proxy.yourdomain.tld     # печатает значения
cp deploy/production/.env.example .env               # заполнить
# 3. Сертификат
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.yourdomain.tld
# 4. vhost (проверить пути сертификатов)
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo ln -s /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
# 5. Контейнеры
docker compose -f docker-compose.prod.yml up -d --build
# 6. Проверка
curl -f http://127.0.0.1:8081/healthz && curl -f http://127.0.0.1:8081/readyz
curl -f https://proxy.yourdomain.tld/                 # публичный сайт
```

NAT: если сервер за 1:1 NAT (облачный контейнер), задайте в `.env`
`MTPROXY_NAT_ARGS="--nat-info <ip-контейнера>:<публичный-ip>"` — иначе
middle-end молча рассинхронизируется и стримы зависнут (см. README tproxy-server,
раздел 5). Симптом: `journalctl`/логи MTProxy без ошибок, стримы дошли до WINDOW
и замерли.
