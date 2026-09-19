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
хостовой nginx :443 (Let's Encrypt, единственная точка входа;
   │            все пути проксируются на 127.0.0.1:8080)
   ▼
tproxy-relay, контейнер (Kestrel :8080; admin :8081 loopback-only)
   │  bridge page, /api/v1/session|up|down|ws
   ▼  docker-сеть tproxy-net (172.20.0.0/24)
MTProxy, контейнер :2398 (наружу не торчит) ──► Telegram DC
```

## Установка на сервер (canonical flow)

Топология: **хостовой nginx (TLS) → tproxy-relay (контейнер) → MTProxy (контейнер)**.
Проверено на Ubuntu 24.04: nginx и certbot на хосте, оба бэкенда в docker.

Подразумевается Debian/Ubuntu-раскладка nginx (`sites-available`/`sites-enabled`)
и уже существующий веб-сервер с другими сайтами — флоу только **добавляет** свой
vhost-файл и ничего не правит в чужих.

### 1. Сабдомен

У своего DNS-провайдера создайте A-запись сабдомена на публичный IP сервера:

```
proxy.yourdomain.tld.   IN  A   <IP сервера>
```

Дождитесь резолва (`getent hosts proxy.yourdomain.tld`). Порты 80/443 должны
быть открыты в фаерволе (`ufw allow 80,443/tcp` при необходимости).

### 2. Проект и .env

```bash
git clone https://github.com/yourO2xygen/tproxy-relay /opt/tproxy-relay   # или rsync
cd /opt/tproxy-relay
deploy/production/setup.sh proxy.yourdomain.tld
```

`setup.sh` сам определит публичный IP, сгенерирует секрет и создаст `.env`
(600). Альтернатива вручную: `cp deploy/production/.env.example .env` и заполнить
`TPROXY_SECRET_HEX` (`openssl rand -hex 16`) и `MTPROXY_PUBLIC_IP`.

### 3. Контейнеры

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

Проверка: `docker ps | grep tproxy` (relay — healthy), `curl -f http://127.0.0.1:8081/readyz`.

Если Docker Hub с сервера недоступен (TLS-таймауты) — соберите/возьмите образы
там, где он доступен, и перенесите:

```bash
docker build -t tproxy-relay:latest -f deploy/relay/Dockerfile .
docker build -t tproxy-mtproxy:latest deploy/mtproxy
docker save tproxy-relay:latest tproxy-mtproxy:latest | gzip | ssh root@server 'gunzip | docker load'
```

### 4. nginx: vhost для ACME и сертификат

Пример конфига: [`deploy/production/nginx-vhost.conf.example`](deploy/production/nginx-vhost.conf.example)
(внутри — инструкция по этапам; замените `proxy.yourdomain.tld` на свой домен).
На первом этапе активен только блок `:80` с `.well-known/acme-challenge`:

```bash
sudo mkdir -p /var/www/certbot
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo ln -sf /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/tproxy.conf
sudo nginx -t && sudo systemctl reload nginx

# сертификат Let's Encrypt (certbot ставится: apt-get install -y certbot)
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.yourdomain.tld
# продления — штатным systemd-таймером certbot, webroot-блок остаётся в vhost
```

### 5. nginx: блок :443

В `/etc/nginx/sites-available/tproxy.conf` раскомментируйте блок `:443`
(пути сертификатов уже стандартные для certbot) и перечитайте конфиг:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

Ключевые директивы (уже в примере): `client_max_body_size 4m`,
`proxy_request_buffering off` / `proxy_buffering off` (стриминг батчей),
`proxy_read_timeout 75s` (> long-poll 25с), `access_log off`
(bridge-URL и bearer несущей не должны попадать в логи).

### 6. Сквозная проверка и клиент

```bash
curl -f https://proxy.yourdomain.tld/          # публичный сайт (анти-пробинг)
dotnet run --project tools/TproxyTestClient -- --host proxy.yourdomain.tld --secret <hex>
# ожидание: все PASS, кроме echo-ассертов (бэкенд — реальный MTProxy, не echo-stub)
```

Кредиты для клиента Telegram: Настройки → Прокси → добавить, тип **WEB**:
сервер `proxy.yourdomain.tld`, секрет `<hex>`. Ссылка для шаринга:
`https://t.me/webproxy?server=proxy.yourdomain.tld&secret=<hex>`.

### NAT-грабли

MTProxy за docker-NAT обязан представляться middle-end'у публичным адресом —
за это отвечает `MTPROXY_NAT_ARGS` (`--nat-info <локальный-ip>:<публичный-ip>`).
Локальный IP зафиксирован в compose (`172.20.0.2` в сети `tproxy-net`),
публичный приходит из `.env` (`MTPROXY_PUBLIC_IP`). Симптом пропущенного NAT:
без ошибок в логах стримы доходят до WINDOW и зависают. Если подсеть
`172.20.0.0/24` на сервере занята — поменяйте её в compose и `MTPROXY_LOCAL_IP`.
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

## Локальная разработка (тестовый контур)

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

Тестовый контур: self-signed TLS через контейнер nginx, бэкенд по умолчанию
echo-stub.

## Замена echo-stub на реальный MTProxy (локально)

```bash
docker compose -f docker-compose.yml -f docker-compose.mtproxy.yml --profile mtproxy up -d --build
```

Локально MTProxy поднимается, но middle-end до Telegram DC блокируется (РФ) —
дальше проверки TCP+obfuscated2-ответов уйти нельзя.

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

