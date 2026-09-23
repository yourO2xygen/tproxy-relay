# tproxy-relay — Telegram MTProto поверх обычного HTTPS

[English](README_EN.md) | **Русский**

[![ci](https://github.com/yourO2xygen/tproxy-relay/actions/workflows/ci.yml/badge.svg)](https://github.com/yourO2xygen/tproxy-relay/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/tag/yourO2xygen/tproxy-relay?sort=semver&label=release)](https://github.com/yourO2xygen/tproxy-relay/releases)
[![license](https://img.shields.io/github/license/yourO2xygen/tproxy-relay)](LICENSE)

Self-hosted релей [WEB-прокси Telegram](https://github.com/telegramdesktop/tproxy-server) на C#/.NET 10:
MTProto-трафик мимикрирует под обычный HTTPS-трафик вашего сайта. Цензор и провайдер видят
рядовое TLS-соединение к домену с валидным сертификатом Let's Encrypt; случайный посетитель
в браузере видит обычную страницу. Реальный MTProxy спрятан в docker-сети и наружу не торчит.

Совместим с Telegram Desktop 7.2+ (тип прокси **WEB**) и официальным MTProxy в качестве бэкенда.

```
 Telegram-клиент (прокси: тип WEB)            Обычный браузер
    │  сервер + секрет                             │
    ▼                                              ▼
┌─────────────── хост: nginx (systemd), :443 TLS ────────────────┐
│         Let's Encrypt; весь трафик → 127.0.0.1:8080            │
└───────────────────────────────┬────────────────────────────────┘
                                ▼
              tproxy-relay (контейнер, 127.0.0.1:8080)
                bridge-страница, /api/v1/session|up|down|ws
                не-прокси запросам — сайт-прикрытие
                                │  docker-сеть tproxy-net
                                ▼
              MTProxy (контейнер, :2398, наружу не торчит) ──► Telegram DC
```

Содержание: [Как это работает](#как-это-работает) · [Возможности](#возможности) ·
[Требования](#требования) · [Быстрый старт](#быстрый-старт) · [Установка по шагам](#установка) ·
[Креды клиента](#креды-клиента) · [Управление ключами](#управление-ключами) ·
[Сайт-прикрытие](#сайт-прикрытие) · [Конфигурация](#конфигурация) · [Обслуживание](#обслуживание) ·
[Диагностика](#диагностика) · [Разработка](#разработка)

## Как это работает

1. Из секрета и имени хоста выводится **capability** — подпись
   `HMAC-SHA256(secret, "tdesktop-web-proxy-bridge-v1\n" + host)`. Именно она
   оказывается в URL одноразовой bridge-страницы, которую клиент открывает внутри
   встроенного браузера.
2. Bridge-страница через JS устанавливает первую несущую и обменивается
   bootstrap-токеном на сессионный. Дальше трафик идёт по HTTPS-запросам
   (`/api/v1/session`, `/api/v1/up`, `/api/v1/down`) или по одному WebSocket —
   внутри мультиплексированные MTProto-стримы с flow control (4 МиБ на стрим).
3. Релей терминирует несущую и переливает данные в MTProto-бэкенд (официальный
   MTProxy в соседнем контейнере), тот говорит с Telegram DC.

Полное нормативное описание протокола — [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Возможности

- **Маскировка**: TLS-сертификат домена, bridge-страница и сайт-прикрытие — снаружи
  соединение неотличимо от обычного сайта (обязателен валидный сертификат).
- **Четыре несущие**: `https` (long-poll, по умолчанию), `websocket`,
  `https-lanes` / `websocket-lanes` (по сокету на стрим).
- **Base path**: релей может жить под `https://host/<слаг>/` — корень домена
  остаётся настоящему сайту, сканеры не видят прокси-поверхность.
- **Управление ключами**: каждому клиенту — свой секрет и свой процесс MTProxy;
  реестр в SQLite, создание/пауза/отзыв/трафик через Admin API или Telegram-бота.
- **Бюджеты и лимиты**: сессии/стримы/pending-байты, rate-корзины, per-IP лимиты,
  503 + Retry-After при переполнении; защита от сам-DoS и OOM (GC-лимит под
  `mem_limit`).
- **Безопасный контур**: admin-порт только на loopback, fail-closed включение
  API/бота, контейнеры read-only с `cap_drop: ALL`, непривилегированный запуск.
- **Метрики и здоровье**: `/metrics` (Prometheus), `/healthz`, `/readyz`.

## Требования

| Что | Минимум |
|---|---|
| Сервер | VPS 1 vCPU / 1 ГиБ (комфортно — 2 ГиБ), Ubuntu 22.04/24.04 или Debian 12 |
| Домен | сабдомен с A-записью на IP сервера, свободные порты **80 и 443** |
| Сертификат | Let's Encrypt (ставится по шагам ниже) или свой |
| ПО | nginx (systemd), Docker + compose-плагин, git |
| Клиент | Telegram Desktop 7.2+ / мобильные клиенты с типом прокси **WEB** |

> [!NOTE]
> Всё тяжёлое живёт в контейнерах: nginx на хосте только проксирует трафик на
> `127.0.0.1:8080`, релей и MTProxy собираются и запускаются docker compose-ом.

## Быстрый старт

Для тех, кто знает, что такое DNS, certbot и compose. Каждый шаг подробно —
в [«Установке»](#установка).

```bash
# 1. DNS: A-запись proxy.example.com -> IP сервера (у вашего DNS-провайдера)
dig +short proxy.example.com            # должен вернуть IP сервера

# 2. nginx + certbot
sudo apt update && sudo apt install -y nginx certbot
sudo mkdir -p /var/www/certbot

# 3. Проект и .env (секрет, IP, слаг генерируются сами)
git clone https://github.com/yourO2xygen/tproxy-relay /opt/tproxy-relay
cd /opt/tproxy-relay
deploy/production/setup.sh proxy.example.com

# 4. vhost: этап 1 (:80), сертификат
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo sed -i 's/proxy\.yourdomain\.tld/proxy.example.com/g' /etc/nginx/sites-available/tproxy.conf
sudo ln -sf /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.example.com

# 5. Контейнеры (соберут релей и MTProxy)
docker compose -f docker-compose.prod.yml up -d --build
curl -f http://127.0.0.1:8081/readyz    # ok

# 6. vhost: этап 2 — раскомментировать блок :443 и перечитать nginx
sudo nano /etc/nginx/sites-available/tproxy.conf
sudo nginx -t && sudo systemctl reload nginx

# 7. Кредиты для клиента Telegram
deploy/production/setup.sh proxy.example.com --show
```

## Установка

### Шаг 1. DNS и фаервол

У DNS-провайдера создайте A-запись сабдомена на публичный IP сервера:

```
proxy.example.com.   IN  A   203.0.113.10
```

Дождитесь резолва и откройте порты:

```bash
getent hosts proxy.example.com          # ожидание: 203.0.113.10
sudo ufw allow 80,443/tcp               # если пользуется ufw
```

**Варианты.** Запись можно вести и на `@`-домен, если главный сайт не нужен —
но для маскировки приличнее отдельный сабдомен, живущий как «обычный сайт».
IPv6 (AAAA) поддерживается: релей не привязан к семейству адресов, TLS
терминирует nginx.

### Шаг 2. nginx (systemd)

```bash
sudo apt update && sudo apt install -y nginx
sudo systemctl enable --now nginx
systemctl status nginx --no-pager       # active (running)
```

**Варианты.**

- **nginx уже стоит и обслуживает другие сайты** — ничего переустанавливать не
  нужно: флоу ниже только *добавляет* свой vhost-файл и не трогает чужие.
  Раскладка предполагается Debian/Ubuntu (`sites-available`/`sites-enabled`);
  на RHEL-подобных положите конфиг в `/etc/nginx/conf.d/tproxy.conf`.
- **nginx в контейнере вместо systemd** — тоже работает (проксируйте на
  `relay:8080` внутри общей docker-сети), но каноничный флоу этого репозитория —
  хостовой nginx как systemd-сервис.

### Шаг 3. vhost nginx, этап 1: ACME

Возьмите пример и подставьте домен (или сделайте то же руками):

```bash
sudo mkdir -p /var/www/certbot
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo nano /etc/nginx/sites-available/tproxy.conf   # замените proxy.yourdomain.tld
sudo ln -sf /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

На этом этапе активен только блок `:80` — вебрут для выпуска сертификата и
редирект на HTTPS:

```nginx
server {
    listen 80;
    server_name proxy.example.com;

    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }
    location / {
        return 301 https://$host$request_uri;
    }
}
```

<details>
<summary><strong>Вариант: релей под путём (base path), сайт в корне остаётся у nginx</strong></summary>

Релей умеет жить под `https://host/<слаг>/` — тогда в корне домена может
работать настоящий сайт. Проще всего отдать весь трафик релею (он сам
обработает префикс, а корень закроет сайтом-прикрытием — см.
[«Сайт-прикрытие»](#сайт-прикрытие)). Если хотите, чтобы корень отдавал сам
nginx, добавьте в блоке `:443` точную локацию с сохранением префикса:

```nginx
location /ваш-слаг/ {
    proxy_pass http://127.0.0.1:8080;    # БЕЗ слэша на конце — путь не переписывается
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_buffering off;
    proxy_read_timeout 75s;
}
```

Слаг генерирует `setup.sh` (16 символов base32); включение base path меняет
креды всех клиентов — подробности в [docs/BASE_PATH.md](docs/BASE_PATH.md).

</details>

### Шаг 4. Сертификат

```bash
sudo apt install -y certbot
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.example.com
```

Продления делает штатный systemd-таймер certbot (`systemctl list-timers |
grep certbot`); вебрут-блок в vhost остаётся — он нужен и для продлений.

**Варианты.**

- **Свой/корпоративный сертификат** — пропустите certbot, на шаге 7 укажите
  свои пути `ssl_certificate`/`ssl_certificate_key`.
- **Wildcart `*.example.com`** — укажите его пути в блоке `:443`; вебрут-блок
  можно удалить вместе с redirect-локацией, оставив `:80` редирект.
- **certbot 429 (rate limit)** — лимиты Let's Encrypt на неделю выпуска;
  для отладки добавьте `--staging`, а боевой сертификат выпустите позже.

### Шаг 5. Проект и .env

```bash
git clone https://github.com/yourO2xygen/tproxy-relay /opt/tproxy-relay
cd /opt/tproxy-relay
deploy/production/setup.sh proxy.example.com
```

Скрипт сам: определит публичный IP (для NAT MTProxy), сгенерирует секрет
(`openssl rand -hex 16`) и случайный слаг base path, создаст `.env` с правами
600. Секрет **не печатается** в терминал — по умолчанию он лежит только в `.env`.

**Варианты.**

```bash
# Показать кредиты для клиента (секрет и t.me-ссылки):
deploy/production/setup.sh proxy.example.com --show

# Прокси в корне домена, без base path:
deploy/production/setup.sh proxy.example.com --base-path none

# Свой слаг вместо случайного:
deploy/production/setup.sh proxy.example.com --base-path myproxy

# Вручную (без скрипта):
cp deploy/production/.env.example .env && nano .env
# обязательные поля: TPROXY_PUBLIC_HOSTNAME, TPROXY_SECRET_HEX, MTPROXY_PUBLIC_IP
```

- Повторный запуск скрипта существующий `.env` не перезаписывает.
- Секрет можно передавать файлом вместо переменной: `TPROXY_SECRET_HEX_FILE`
  (docker-secrets-стиль) — переменная тогда не нужна.
- Смена `TPROXY_PUBLIC_HOSTNAME` или `TPROXY_BASE_PATH` меняет все capability —
  это **перевыпуск**: клиентам нужны новые ссылки и секрет.
- Полный справочник переменных — [.env.example](.env.example).

### Шаг 6. Docker и запуск контейнеров

```bash
curl -fsSL https://get.docker.com | sudo sh       # Docker + compose-плагин
sudo usermod -aG docker $USER                     # опционально: docker без sudo

docker compose -f docker-compose.prod.yml up -d --build
docker ps --format 'table {{.Names}}\t{{.Status}}' # оба контейнера (healthy)
curl -f http://127.0.0.1:8081/readyz               # ok
```

Поднимаются два контейнера: `tproxy-relay` (публикует на `127.0.0.1:8080`
транспорт и `127.0.0.1:8081` админку — наружу не торчат) и `tproxy-mtproxy`
(внутри docker-сети, портов наружу нет).

**Варианты.**

- **Docker Hub с сервера недоступен** (TLS-таймауты) — соберите образы там,
  где он доступен, и перенесите:

  ```bash
  docker build -t tproxy-relay:latest -f deploy/relay/Dockerfile .
  docker build -t tproxy-mtproxy:latest deploy/mtproxy
  docker save tproxy-relay:latest tproxy-mtproxy:latest | gzip | ssh root@server 'gunzip | docker load'
  ```

- **Занятая подсеть** — compose использует `172.20.0.0/24`; если она на сервере
  занята, поменяйте её в `docker-compose.prod.yml` и переменную
  `MTPROXY_LOCAL_IP` в `.env`.
- **Тонкая настройка лимитов** — память контейнеров, pending-бюджеты и прочее
  меняются в `.env`/`config.json` (см. [«Конфигурация»](#конфигурация)); держите
  pending-бюджеты ≤ ~25% `mem_limit` релея.

### Шаг 7. vhost nginx, этап 2: блок :443

Раскомментируйте блок `:443` в `/etc/nginx/sites-available/tproxy.conf`
(пути сертификатов в примере уже стандартные для certbot) и перечитайте:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

Ключевые директивы (уже в примере) и зачем они:

```nginx
server {
    listen 443 ssl http2;
    server_name proxy.example.com;

    ssl_certificate     /etc/letsencrypt/live/proxy.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/proxy.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    add_header Strict-Transport-Security "max-age=63072000" always;
    limit_conn tproxy_conn 32;      # ≤32 соединений с одного IP
    access_log off;                 # bridge-URL и bearer не должны попадать в логи

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;   # строго одно значение
        proxy_set_header Upgrade $http_upgrade;          # нужно websocket-несущей
        proxy_set_header Connection $connection_upgrade;

        client_max_body_size 4m;         # максимум батча + заголовки
        proxy_request_buffering off;     # стриминг uplink
        proxy_buffering off;             # стриминг downlink (long-poll)
        proxy_read_timeout 75s;          # > long-poll релея (25с) с запасом
        proxy_send_timeout 75s;
    }
}
```

**Варианты.**

- **WebSocket-несущая** — заголовки `Upgrade`/`Connection` в примере уже есть;
  достаточно задать в `.env` `TPROXY_CARRIER_MODE=websocket` и пересоздать релей
  (`docker compose -f docker-compose.prod.yml up -d relay`). Есть ещё
  `https-lanes` и `websocket-lanes` (сокет на стрим) — для большинства случаев
  хватает базовых двух.
- **Несколько сайтов на одном nginx** — каждому свой vhost-файл в
  `sites-available`; релей не мешает соседям и ничего в них не правит.

### Шаг 8. Проверка и клиент

```bash
curl -f https://proxy.example.com/          # страница-прикрытие (не заглушка nginx)
curl -f http://127.0.0.1:8081/healthz       # ok
curl -s http://127.0.0.1:8081/metrics | head # tproxy_* метрики
```

Кредиты для клиента:

```bash
deploy/production/setup.sh proxy.example.com --show
```

В Telegram: **Настройки → Прокси → Добавить**, тип **WEB**, сервер
`proxy.example.com`, секрет `<из вывода скрипта>`. Ссылка для шаринга (её же
печатает скрипт и бот):

```
https://t.me/webproxy?server=proxy.example.com&secret=<секрет>
tg://webproxy?server=proxy.example.com&secret=<секрет>
```

<details>
<summary><strong>Вариант: сквозная проверка тестовым клиентом (нужен .NET 10 SDK)</strong></summary>

Тестовый клиент живёт в ветке `dev` (28 проверок для https, 23 — для websocket):

```bash
git clone -b dev https://github.com/yourO2xygen/tproxy-relay /tmp/tproxy-dev
cd /tmp/tproxy-dev
dotnet run --project tools/TproxyTestClient -- --host proxy.example.com --secret <hex>
# ожидание: все PASS, кроме echo-ассертов (бэкенд — реальный MTProxy, не эхо-заглушка)
```

</details>

## Креды клиента

- **Секрет** — hex 32 символа (16 байт). Форма с `dd`-префиксом (44 символа)
  тоже поддерживается. При включённом base path в ссылках используется
  **маркированный** секрет `base64url(0x70 || секрет)` — старые клиенты его
  явно отвергнут, а не попробуют подключиться не туда.
- **Capability** выводится из секрета и имени хоста (+ пути при base path):
  сменили домен или слаг — все ссылки подлежат перевыпуску.
- Секрет = полный доступ к прокси. Храните как пароль; отозвать скомпрометированный
  ключ — [«Управление ключами»](#управление-ключами).

## Управление ключами

Встроенный секрет из `.env` работает всегда (порт MTProxy 2398). Сверх него
можно завести управляемые ключи — у каждого свой секрет и **свой процесс
MTProxy**; реестр в SQLite (`/data/keys.db` на volume релея). Отзыв/пауза
мгновенно закрывают сессии ключа; трафик агрегируется по ключам и дням.

Все способы опциональны и сочетаются в любых комбинациях; по умолчанию всё
выключено (включение без обязательных параметров = отказ запуска, fail-closed).

| Вариант | Что включить в `.env` | Доступ |
|---|---|---|
| Ничего (builtin) | — | секрет из `.env` |
| **Admin API** | `TPROXY_API_ENABLED=true` + `TPROXY_API_TOKEN` | loopback `:8081`, Bearer |
| **Telegram-бот** | `TPROXY_BOT_ENABLED=true` + `TPROXY_BOT_TOKEN` + `TPROXY_BOT_ADMINS` | только админ-чаты |
| API + бот | оба блока | оба канала, общий реестр |

После правки `.env` пересоздайте релей: `docker compose -f docker-compose.prod.yml up -d relay`.

### Вариант: Admin API

Админ-порт наружу не публикуется — работайте через ssh-туннель:

```bash
ssh -L 8081:127.0.0.1:8081 root@server
# в другом терминале, на своей машине:
TOKEN=...   # значение TPROXY_API_TOKEN
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8081/admin/stats
```

| Путь | Ответ |
|---|---|
| `GET /admin/stats` | сессии, стримы, трафик, pending, лимит-хиты, ключи |
| `GET /admin/keys[?reveal=1]` | список ключей; секреты — только при `reveal=1` |
| `POST /admin/keys` | `{"name":"ivan"}` → 201, секрет и готовая t.me-ссылка |
| `DELETE /admin/keys/{id}` | отзыв: `{revoked, sessions_closed}` |
| `POST /admin/keys/{id}/pause` · `/resume` | пауза/возобновление ключа |
| `GET /admin/sessions` | живые сессии (без токенов) |
| `GET /admin/traffic?days=N` | трафик по ключам за N дней (1–90) |

Нет или неверный токен → 404 (без оракула). Примеры:

```bash
# Создать ключ и получить ссылку для клиента:
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' -d '{"name":"ivan"}' \
     http://127.0.0.1:8081/admin/keys
# Отозвать:
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8081/admin/keys/<id>
```

<details>
<summary><strong>Вариант: статический импорт ключей без API и бота</strong></summary>

Положите seed-файл на volume релея — при старте он импортируется:

```bash
cat > seed.json <<'EOF'
[{"name":"ivan","secret_hex":"<64 hex-символов>"}]
EOF
docker cp seed.json tproxy-relay:/data/keys/seed.json
docker compose -f docker-compose.prod.yml restart relay
```

</details>

### Вариант: Telegram-бот

Токен — от [@BotFather](https://t.me/BotFather). Ваш chat-id бот узнаёт один раз:

1. Включите бота (`TPROXY_BOT_ENABLED=true`, токен в `TPROXY_BOT_TOKEN`,
   `TPROXY_BOT_ADMINS` пока пусто) и пересоздайте релей.
2. Напишите боту любое сообщение; в логах появится
   `event=tg_stranger chat=<id>`.
3. Впишите `<id>` в `TPROXY_BOT_ADMINS` и пересоздайте релей.

```bash
docker logs tproxy-relay 2>&1 | grep tg_stranger
```

Команды (только из админ-чатов): `/stats`, `/keys`, `/key <имя>`, `/revoke`,
`/pause`, `/resume`, `/traffic`, `/help`. Ключ бот выдаёт вместе с готовой
`t.me`-ссылкой (учитывает base path и маркированный секрет).

## Сайт-прикрытие

По умолчанию все пути вне транспортного API отвечают встроенной нейтральной
страницей. Вместо неё можно отдавать настоящий сайт — случайный гость видит
обычный контент, деплой релея маскируется. Режимы взаимоисключающие
(оба сразу — ошибка конфигурации).

**Вариант 1: ничего (по умолчанию).** Встроенная заглушка, настраивать нечего.

**Вариант 2: статика (`TPROXY_PUBLIC_DIR`).** Каталог читается в память при
старте (изменили файлы — `restart relay`); GET/HEAD, ETag, 304, одиночный Range,
`404.html` на промахи, обход по `../` отвергается. Пример через
`docker-compose.override.yml` (compose подхватывает его автоматически):

```yaml
# docker-compose.override.yml
services:
  relay:
    environment:
      TPROXY_PUBLIC_DIR: /srv/site
    volumes:
      - ./site:/srv/site:ro
```

```bash
mkdir -p site && echo '<h1>Мой сайт</h1>' > site/index.html
docker compose -f docker-compose.prod.yml up -d relay
```

**Вариант 3: upstream (`TPROXY_PUBLIC_UPSTREAM`).** Стримящий обратный прокси на
ваше приложение: оригинальные Host и путь сохраняются, hop-by-hop заголовки
вырезаются, обрыв клиента освобождает соединение:

```yaml
services:
  relay:
    environment:
      TPROXY_PUBLIC_UPSTREAM: http://site:80   # соседний контейнер в tproxy-net
```

Подробности — [docs/PUBLIC_SITE.md](docs/PUBLIC_SITE.md). Транспортные пути
(`/api/v1/*`, bridge) приоритетнее сайта всегда.

## Конфигурация

Слои (приоритет сверху вниз): переменные окружения (`TPROXY_*`) → `config.json`
→ встроенные дефолты. Секрет через `config.json` не принимается — только env
или файл. Пример config.json — [deploy/config.example.json](deploy/config.example.json);
к релею его подключить так:

```yaml
# docker-compose.override.yml
services:
  relay:
    volumes:
      - ./config.json:/app/config.json:ro
```

Основные переменные (полный справочник — [.env.example](.env.example)):

| Переменная | По умолчанию | Описание |
|---|---|---|
| `TPROXY_SECRET_HEX` | — (обязателен) | секрет MTProxy, 32 hex-символа; или `TPROXY_SECRET_HEX_FILE` |
| `TPROXY_PUBLIC_HOSTNAME` | — (обязателен) | домен прокси; участвует в capability |
| `TPROXY_CARRIER_MODE` | `https` | несущая: `https` \| `websocket` \| `https-lanes` \| `websocket-lanes` |
| `TPROXY_BASE_PATH` | пусто | слаг base path; пусто = корень домена |
| `TPROXY_MAX_SESSIONS` | `128` | глобальный кап сессий |
| `TPROXY_MAX_STREAMS_PER_SESSION` | `128` | стримов на сессию |
| `TPROXY_MAX_PENDING_GLOBAL_BYTES` | `268435456` | глобальный pending-бюджет (держите ≤ ~25% mem_limit) |
| `TPROXY_MAX_SESSIONS_PER_IP` | `0` (выкл) | per-IP лимит сессий |
| `TPROXY_LONG_POLL_SECONDS` | `25` | long-poll `/down` (nginx-таймаут должен быть больше) |
| `TPROXY_RECONNECT_GRACE` | `120` | сколько секунд реапер ждёт реконнекта idle-сессии |
| `TPROXY_API_ENABLED` / `TPROXY_API_TOKEN` | `false` / — | Admin API (fail-closed) |
| `TPROXY_BOT_ENABLED` / `TPROXY_BOT_TOKEN` / `TPROXY_BOT_ADMINS` | `false` / — / — | Telegram-бот (fail-closed) |
| `MTPROXY_PUBLIC_IP` | — (обязателен) | публичный IP сервера для NAT MTProxy |

Rate-лимиты (`new_sessions/streams/bootstraps_per_minute` + burst), бюджеты
батчей и кадров, контрольный резерв — в `config.json` и `.env.example`.

## Обслуживание

```bash
# Обновление релея (сессии инвалидны, клиенты переподключаются сами):
git pull && docker compose -f docker-compose.prod.yml up -d --build relay
# Откат — на предыдущий git-тег той же командой.

# Бэкап реестра ключей (единственное состояние):
docker exec tproxy-relay sh -c 'sqlite3 /data/keys.db ".backup /tmp/keys.db"' || \
docker cp tproxy-relay:/data/keys.db backup-keys-$(date +%F).db

# Логи:
docker logs tproxy-relay              # релей (ротация 3×10 МиБ)
docker exec tproxy-mtproxy sh -c 'ls /tmp/mtproxy/'   # логи детей mtproxy
docker exec tproxy-mtproxy curl -s 127.0.0.1:8888/stats  # статистика MTProxy

# Метрики (с сервера):
curl -s 127.0.0.1:8081/metrics
```

Роутинг-конфиг MTProxy (`proxy-multi.conf`) супервизор обновляет ежедневно и
перезапускает прокси только при изменениях. Алерты минимум (Prometheus):
`tproxy_pending_bytes > 200 МиБ`, `tproxy_limit_hits_total` растёт быстрее
~10/мин, `up == 0`, память контейнера > 60% лимита.

## Диагностика

| Симптом | Причина / действие |
|---|---|
| `certbot` выдаёт 429 | rate limit Let's Encrypt; для отладки `--staging`, боевой выпуск позже |
| Порт 80/443 занят | `sudo ss -tlnp 'sport = :443'` — найдите владельца; другой vhost или сервис |
| Сайт открывается, Telegram не подключается | `TPROXY_PUBLIC_HOSTNAME` ≠ домен в ссылке; секрет/слаг от старого деплоя (capability выводится из host+path — нужен перевыпуск) |
| Подключается, но стримы висят без ошибок в логах | NAT-грабли MTProxy: `MTPROXY_PUBLIC_IP` пуст/неверен (middle-end должен представляться публичным адресом) |
| `tproxy-mtproxy` — `unhealthy` | дети мертвы и не респавнятся: `docker logs tproxy-mtproxy`, логи `/tmp/mtproxy/log-*` в контейнере |
| Бот молчит | chat-id не прописан: ищите `event=tg_stranger` в логах; polling: `docker logs tproxy-relay \| grep tg_` |
| Вечный 503 на `/api/v1/*` | бэкенд недоступен: `curl 127.0.0.1:8081/readyz` покажет причину |
| Новые сессии сразу закрываются, `tproxy_limit_hits_total` растёт | штурм или исчерпан pending-бюджет: смотрите `tproxy_pending_bytes`; при монотонном росте — рестарт relay (временная мера) и аудит лимитов |
| Контейнер релея OOMKilled | pending-бюджеты подняты выше ~25% `mem_limit` — снизьте бюджеты или поднимите лимит |

> [!IMPORTANT]
> Инструмент для личного использования; ответственность за соблюдение законов
> вашей юрисдикции — на владельце сервера. Секрет прокси = полный доступ:
> держите `.env` с правами 600 и не публикуйте ссылки в открытых чатах.
> В логи не пишутся bridge-URL и bearer-токены (`access_log off` в nginx,
> релей токены не логирует); провайдер видит только TLS-соединение к вашему
> домену. Админ-порт `8081` публикуется только на loopback хоста.

## Документация

- [docs/PROTOCOL.md](docs/PROTOCOL.md) — нормативный протокол: кадры, токены,
  курсы/гранты, коды ошибок.
- [docs/BASE_PATH.md](docs/BASE_PATH.md) — деплой под префиксом: v2-capability,
  слаг, маркированные ссылки.
- [docs/PUBLIC_SITE.md](docs/PUBLIC_SITE.md) — сайт-прикрытие: static/upstream.
- [CHANGELOG.md](CHANGELOG.md) — история версий.
- [.env.example](.env.example) — полный справочник переменных окружения.

## Разработка

Ветка **`dev`** — рабочая: тесты, dev-стеки и инструменты живут там.

```bash
git clone -b dev https://github.com/yourO2xygen/tproxy-relay
cd tproxy-relay
dotnet test tests/TproxyRelay.Tests          # юнит + интеграционные (настоящий релей на ephemeral-портах)
docker compose up -d --build                 # локальный стек: nginx-контейнер + релей + echo-заглушка
docker compose -f docker-compose.yml -f docker-compose.ws.yml up -d --build   # то же в websocket-режиме
dotnet run --project tools/TproxyTestClient  # e2e-клиент (28 проверок https / 23 ws)
```

CI собирает и тестирует обе ветки: на `dev` — полный прогон тестов + все образы,
на `main` — сборка прод-образов. Лицензия — [MIT](LICENSE).
