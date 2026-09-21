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
(600, секрет не печатается в терминал; показать кредиты — `setup.sh <домен> --show`).
Альтернатива вручную: `cp deploy/production/.env.example .env` и заполнить
`TPROXY_SECRET_HEX` (`openssl rand -hex 16`) и `MTPROXY_PUBLIC_IP`. Секрет можно
передавать файлом (`TPROXY_SECRET_HEX_FILE`, docker-secrets-стиль) вместо env.

#### config.json (необязательно)

Все лимиты и таймауты настраиваются файлом `config.json` (путь — `TPROXY_CONFIG`,
по умолчанию рядом с бинарём; пример — `deploy/config.example.json`). Приоритет:
значения по умолчанию < `config.json` < переменные окружения (`TPROXY_*`).
Секрет в config.json не принимается — только env/файл. Полная матрица лимитов
включает rate-корзины (`new_sessions/streams/bootstraps_per_minute` + burst),
глобальный pending-бюджет (байты и элементы), кап параллельных коннектов к
бэкенду и опциональные per-IP лимиты (`max_sessions_per_ip`,
`max_bootstraps_per_ip`, 0 = выключено). Несовместимые значения (например,
`carrier_batch_bytes > 2 МиБ` или контрольный резерв, не оставляющий места
данным) приводят к отказу запуска.

#### Base path (необязательно)

Релей может жить под префиксом пути: `https://proxy.example.com/<слаг>/` —
тогда обычный сайт на хосте остаётся нетронутым, а сканеры не видят
прокси-поверхность в корне. Слаг — 16 символов base32 (генерирует `setup.sh`
по умолчанию; `--base-path none` = корень). Capability выводится из
`host+path` (контекст v2), а ссылки для клиентов используют **маркированный**
секрет `base64url(0x70 || секрет)` — старые клиенты такой ссылке откажут
явно, а не попробуют подключиться к пустому хосту. `setup.sh` печатает
готовые `t.me`/`tg://` ссылки. Смена префикса меняет все capability —
это перевыпуск: клиенту нужен новый адрес и новый секрет.

```bash
deploy/production/setup.sh proxy.yourdomain.tld --show --base-path myslug
# Сервер:  proxy.yourdomain.tld/myslug
# Секрет:  <маркированный base64url>
```

### Управление ключами (опционально)

Ключи живут в SQLite (`/data/keys.db` на volume релея). Каждый ключ — свой
клиентский секрет и **свой процесс MTProxy** в общем контейнере (супервизор
читает `registry.txt` из volume). Встроенный env-секрет продолжает работать
на порту 2398 независимо от управляемых ключей. Ревок/пауза мгновенно
закрывает сессии ключа; трафик агрегируется по ключам и дням.

Три способа управления — все опциональны, по умолчанию всё выключено
(fail-closed: включено без обязательных параметров = отказ запуска):

| Способ | Включение | Доступ |
|---|---|---|
| **Static** (без управления) | ничего | seed-файл `/data/keys/seed.json` (`[{"name":"ivan","secret_hex":"..."}]`), импорт при старте |
| **Admin API** | `TPROXY_API_ENABLED=true` + `TPROXY_API_TOKEN` | loopback `:8081/admin/*`, Bearer-токен (ssh-туннель): `/admin/stats`, `/admin/keys` (CRUD, `?reveal=1` — показать секреты), `/admin/sessions`, `/admin/traffic?days=N` |
| **Telegram-бот** | `TPROXY_BOT_ENABLED=true` + `TPROXY_BOT_TOKEN` + `TPROXY_BOT_ADMINS` | команды `/stats /keys /key <имя> /revoke /pause /resume /traffic` — только из админ-чатов |

Бот выдаёт ключ вместе с готовой `t.me`-ссылкой (учитывает base path и
маркированный секрет).

#### Bootstrap chat-id бота

Ваш chat-id боту нужно сообщить один раз: напишите боту любое сообщение и
найдите в логах релея строку `event=tg_stranger chat=<id>` — этот `<id>`
подставьте в `TPROXY_BOT_ADMINS` и пересоздайте релей. Пока id не прописан,
бот читает сообщения, но отвечает только незнакомцам в лог (канал не
компрометируется). Подробности команд — по `/help` в самом боте.

#### Admin API — справочник

Базовый префикс `/admin` на админ-порте (loopback), `Authorization: Bearer
<TPROXY_API_TOKEN>`. Нет/неверный токен → 404 (без оракула).

| Путь | Ответ |
|---|---|
| `GET /admin/stats` | `{sessions_active, streams_active, bootstraps_outstanding, up_bytes_total, down_bytes_total, limit_hits_total, pending_bytes, keys[]}` |
| `GET /admin/keys[?reveal=1]` | список ключей; секрет — только при `reveal=1` (любое другое значение скрывает) |
| `POST /admin/keys` | `{"name":"...", "secret_hex?":"..."}` → 201 с секретом и t.me-ссылкой; 400 (битый JSON/тип/имя), 409 (дубль) |
| `DELETE /admin/keys/{id}` | 200 `{revoked, sessions_closed}`; 404 если нет/уже отозван |
| `POST /admin/keys/{id}/pause` · `/resume` | 200; пауза закрывает сессии ключа |
| `GET /admin/sessions` | живые сессии (без токенов) |
| `GET /admin/traffic?days=N` | агрегаты трафика по ключам за N дней (1–90) |

### Ops

- Статистика MTProxy (встроенный порт): `docker exec tproxy-mtproxy curl -s 127.0.0.1:8888/stats`
- Метрики релея: `curl 127.0.0.1:8081/metrics` (внутри сервера)
- Роутинг-конфиг MTProxy (`proxy-multi.conf`) обновляется супервизором
  ежедневно; прокси перезапускается только если данные изменились
- Обновление релея: `git pull && docker compose -f docker-compose.prod.yml up -d --build relay`
  (сессии инвалидируются, клиенты переподключаются автоматически); откат —
  на предыдущий git-тег и та же команда

#### Runbook

**Где логи.** `docker logs tproxy-relay` (ротация 3×10 МиБ), журнал детей
mtproxy — `/tmp/mtproxy/log-<порт>` внутри контейнера mtproxy.

**Бэкап keys.db.** Реестр ключей — единственное состояние:
`docker exec tproxy-relay sh -c 'sqlite3 /data/keys.db ".backup /tmp/keys.db"'`
(в образе нет sqlite3 — используйте `docker cp` после краткого `compose stop
relay`, либо `cp` прямо на volume: WAL-режим держит консистентность при
чтении). Восстановление: остановить relay, вернуть файл, стартовать.

**Ротация секрета builtin-профиля.** Поменять `TPROXY_SECRET_HEX` →
`up -d relay mtproxy`; все клиенты builtin-ключа переподключаются по новой
ссылке. Управляемые ключи ротируются через `/revoke` + `/key`.

**Симптом → действие:**

| Симптом | Причина / действие |
|---|---|
| Новые сессии сразу закрываются, `tproxy_limit_hits_total` растёт | исчерпан pending-бюджет утечкой (до v1.2.0) или реальный штурм: смотрите `tproxy_pending_bytes` — при монотонном росте без спада рестарт relay (временная мера до v1.2.0) |
| Контейнер релея OOMKilled | не должно случаться с v1.2.0 (GC-лимит); проверьте, что не подняли pending-бюджеты выше ~25% mem_limit |
| mtproxy `unhealthy` | дети мертвы и не респавнятся — `docker logs tproxy-mtproxy`, логи на `/tmp/mtproxy/log-*` |
| Бот молчит | упал polling: `docker logs tproxy-relay \| grep tg_`; `tg_api_timeout` — норма (долгий полл) |
| Вечный 503 на `/api/v1/*` | бэкенд недоступен: `/readyz` на админ-порте покажет `backend unreachable` |
| Забыли админ-токен API | `TPROXY_API_TOKEN` в `.env` на сервере; смените и `up -d relay` |

**Алерты (минимум).** По Prometheus: `tproxy_pending_bytes > 200 МиБ`
(подход к бюджету), `tproxy_limit_hits_total` растёт быстрее ~10/мин,
`up == 0` (healthcheck), память контейнера > 60% лимита.

### Разработка (dev-флоу)

```bash
dotnet test tests/TproxyRelay.Tests            # 130 юнит+интеграционных
dotnet run --project tools/TproxyTestClient    # e2e против локального стека
docker compose up -d --build                   # стек: nginx+relay+echo-stub
docker compose -f docker-compose.yml -f docker-compose.ws.yml up -d --build
                                               # то же в websocket-режиме
```

- Локальный https-стек слушает `:80/:443` (самоподписанный CA — см. скрипты
  `deploy/`), админ — `127.0.0.1:8081`.
- `docker-compose.ws.yml` — overlay, переключающий релей в websocket-режим.
- Тестовый клиент умеет полный https-флоу (28 проверок) и websocket (23);
  echo-ассерты против реального MTProxy ожидаемо падают (бэкенд не эхо).
- Интеграционные тесты поднимают настоящий релей на свободных портах
  (`tests/TproxyRelay.Tests/Integration/`) — окружение им не нужно.
- Конфиг-слои: env → `config.json` → дефолты; полный справочник env —
  `.env.example`, пример config.json — `deploy/config.example.json`.

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
## Что реализовано (по [PROTOCOL.md](docs/PROTOCOL.md); также [BASE_PATH.md](docs/BASE_PATH.md) и [PUBLIC_SITE.md](docs/PUBLIC_SITE.md))

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
- Admin: `/healthz`, `/readyz`, `/metrics` (отдельный порт, наружу не проброшен);
  метрики включают `tproxy_pending_bytes`, `tproxy_backend_dials_in_flight`,
  `tproxy_limit_hits_total`.
- Юнит-тесты: `dotnet test tests/TproxyRelay.Tests`; CI: build + test + docker build.

## Отличия от референса

Паритет с tproxy-server достигнут по функциональности (фазы 4–10); перечислены
сознательные отличия реализации, а не отсутствующие функции.

- Носители: все четыре режима (`https`, `websocket`, `https-lanes`,
  `websocket-lanes`) с lane-scoped seq/cursor/replay; ключи — полноценный
  реестр в SQLite (создание/отзыв/пауза/трафик) вместо одного секрета.
- Токены хранятся в памяти как есть (не хэш) — как в референсе; секрет в
  token.key в volume.
- WINDOW коалесцируются (GrantPending); pending-бюджет трёхуровневый:
  per-session + global (байты и элементы) + per-lane в lanes-режимах.
- X-Forwarded-For принимается строго одним значением (список → 400); при
  выключенных per-IP лимитах — как в референсе по умолчанию.
- Поверх референса: base-path деплой (v2-capability, слаг, маркированные
  ссылки t.me), public_dir/public_upstream сайты, Admin API (loopback,
  fail-closed) и Telegram-бот управления ключами.
