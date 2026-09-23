# tproxy-relay — Telegram MTProto over ordinary HTTPS

**English** | [Русский](README.md)

[![ci](https://github.com/yourO2xygen/tproxy-relay/actions/workflows/ci.yml/badge.svg)](https://github.com/yourO2xygen/tproxy-relay/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/tag/yourO2xygen/tproxy-relay?sort=semver&label=release)](https://github.com/yourO2xygen/tproxy-relay/releases)
[![license](https://img.shields.io/github/license/yourO2xygen/tproxy-relay)](LICENSE)

A self-hosted relay for [Telegram's WEB proxy](https://github.com/telegramdesktop/tproxy-server)
written in C#/.NET 10: MTProto traffic is disguised as ordinary HTTPS traffic of your own
website. A censor or ISP sees a routine TLS connection to a domain with a valid Let's Encrypt
certificate; a random visitor sees a normal web page. The real MTProxy is hidden inside a
docker network and is never exposed to the internet.

Compatible with Telegram Desktop 7.2+ (proxy type **WEB**) and the official MTProxy as the backend.

```
 Telegram client (proxy: type WEB)             Ordinary browser
    │  server + secret                              │
    ▼                                               ▼
┌────────────── host: nginx (systemd), :443 TLS ─────────────────┐
│          Let's Encrypt; all traffic → 127.0.0.1:8080           │
└────────────────────────────────┬───────────────────────────────┘
                                 ▼
               tproxy-relay (container, 127.0.0.1:8080)
                 bridge page, /api/v1/session|up|down|ws
                 non-proxy requests get the cover site
                                 │  docker network tproxy-net
                                 ▼
               MTProxy (container, :2398, never exposed) ──► Telegram DC
```

Contents: [How it works](#how-it-works) · [Features](#features) ·
[Requirements](#requirements) · [Quick start](#quick-start) · [Installation](#installation) ·
[Client credentials](#client-credentials) · [Key management](#key-management) ·
[Cover site](#cover-site) · [Configuration](#configuration) · [Maintenance](#maintenance) ·
[Troubleshooting](#troubleshooting) · [Development](#development)

> Project documentation and the primary README are in Russian; this file is a full English
> translation of the deployment guide. If anything is unclear, cross-check the Russian original
> or open an issue.

## How it works

1. A **capability** is derived from the secret and the hostname:
   `HMAC-SHA256(secret, "tdesktop-web-proxy-bridge-v1\n" + host)`. It is exactly what appears
   in the URL of the one-time bridge page that the client opens in its built-in browser.
2. The bridge page uses JS to establish the first carrier and exchanges the bootstrap token
   for a session token. Traffic then flows over plain HTTPS requests
   (`/api/v1/session`, `/api/v1/up`, `/api/v1/down`) or a single WebSocket — carrying
   multiplexed MTProto streams with flow control (4 MiB per stream).
3. The relay terminates the carrier and pipes the bytes to the MTProto backend (the official
   MTProxy in a neighboring container), which talks to the Telegram data centers.

The full normative protocol description (in Russian) — [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Features

- **Masking**: your domain's TLS certificate, a bridge page and a cover site — from the
  outside the connection is indistinguishable from an ordinary website (a valid certificate
  is required).
- **Four carriers**: `https` (long-poll, the default), `websocket`,
  `https-lanes` / `websocket-lanes` (one socket per stream).
- **Base path**: the relay can live under `https://host/<slug>/` — the domain root stays with
  a real site and scanners never see the proxy surface.
- **Key management**: every client gets its own secret and its own MTProxy process; the
  registry lives in SQLite with create/pause/revoke/traffic via an Admin API or a Telegram bot.
- **Budgets and limits**: sessions/streams/pending-bytes, rate buckets, per-IP limits,
  503 + Retry-After on saturation; self-DoS and OOM protection (GC limit below `mem_limit`).
- **Hardened runtime**: admin port on loopback only, fail-closed API/bot setup, read-only
  containers with `cap_drop: ALL`, unprivileged execution.
- **Metrics and health**: `/metrics` (Prometheus), `/healthz`, `/readyz`.

## Requirements

| What | Minimum |
|---|---|
| Server | VPS 1 vCPU / 1 GiB RAM (2 GiB comfortable), Ubuntu 22.04/24.04 or Debian 12 |
| Domain | a subdomain with an A record to the server IP, ports **80 and 443** free |
| Certificate | Let's Encrypt (set up in the steps below) or your own |
| Software | nginx (systemd), Docker + compose plugin, git |
| Client | Telegram Desktop 7.2+ / mobile clients with the **WEB** proxy type |

> [!NOTE]
> Everything heavy lives in containers: nginx on the host only proxies traffic to
> `127.0.0.1:8080`; the relay and MTProxy are built and run by docker compose.

## Quick start

For those who know what DNS, certbot and compose are. Every step in detail — in the
[installation guide](#installation).

```bash
# 1. DNS: A record proxy.example.com -> server IP (at your DNS provider)
dig +short proxy.example.com            # should return the server IP

# 2. nginx + certbot
sudo apt update && sudo apt install -y nginx certbot
sudo mkdir -p /var/www/certbot

# 3. Project and .env (secret, IP, slug are generated automatically)
git clone https://github.com/yourO2xygen/tproxy-relay /opt/tproxy-relay
cd /opt/tproxy-relay
deploy/production/setup.sh proxy.example.com

# 4. vhost: stage 1 (:80), certificate
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo sed -i 's/proxy\.yourdomain\.tld/proxy.example.com/g' /etc/nginx/sites-available/tproxy.conf
sudo ln -sf /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.example.com

# 5. Containers (builds the relay and MTProxy)
docker compose -f docker-compose.prod.yml up -d --build
curl -f http://127.0.0.1:8081/readyz    # ok

# 6. vhost: stage 2 — uncomment the :443 block and reload nginx
sudo nano /etc/nginx/sites-available/tproxy.conf
sudo nginx -t && sudo systemctl reload nginx

# 7. Credentials for the Telegram client
deploy/production/setup.sh proxy.example.com --show
```

## Installation

### Step 1. DNS and firewall

At your DNS provider create an A record for the subdomain pointing to the server's public IP:

```
proxy.example.com.   IN  A   203.0.113.10
```

Wait for it to resolve and open the ports:

```bash
getent hosts proxy.example.com          # expect: 203.0.113.10
sudo ufw allow 80,443/tcp               # if you use ufw
```

**Variants.** You can point the apex domain itself if you don't need a main site — but a
separate subdomain living as an "ordinary site" looks more natural for masking. IPv6 (AAAA)
works too: the relay is not bound to an address family, TLS terminates at nginx.

### Step 2. nginx (systemd)

```bash
sudo apt update && sudo apt install -y nginx
sudo systemctl enable --now nginx
systemctl status nginx --no-pager       # active (running)
```

**Variants.**

- **nginx already runs other sites** — no need to reinstall anything: the flow below only
  *adds* its own vhost file and never touches existing ones. The layout assumes
  Debian/Ubuntu (`sites-available`/`sites-enabled`); on RHEL-like systems put the config in
  `/etc/nginx/conf.d/tproxy.conf`.
- **nginx in a container instead of systemd** — works too (proxy to `relay:8080` in a shared
  docker network), but the canonical flow of this repository is host nginx as a systemd service.

### Step 3. nginx vhost, stage 1: ACME

Take the example and substitute your domain (or do the same by hand):

```bash
sudo mkdir -p /var/www/certbot
sudo cp deploy/production/nginx-vhost.conf.example /etc/nginx/sites-available/tproxy.conf
sudo nano /etc/nginx/sites-available/tproxy.conf   # replace proxy.yourdomain.tld
sudo ln -sf /etc/nginx/sites-available/tproxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

At this stage only the `:80` block is active — the webroot for certificate issuance and the
redirect to HTTPS:

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
<summary><strong>Variant: relay under a path (base path), root site stays in nginx</strong></summary>

The relay can live under `https://host/<slug>/` — then a real site can keep running at the
domain root. The simplest option is to route everything to the relay (it handles the prefix
itself, and covers the root with the cover site — see [Cover site](#cover-site)). If you want
nginx itself to serve the root, add a location that preserves the prefix in the `:443` block:

```nginx
location /your-slug/ {
    proxy_pass http://127.0.0.1:8080;    # NO trailing slash — the path is not rewritten
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_buffering off;
    proxy_read_timeout 75s;
}
```

The slug is generated by `setup.sh` (16 base32 characters); enabling a base path changes the
credentials of all clients — see [docs/BASE_PATH.md](docs/BASE_PATH.md) (in Russian).

</details>

### Step 4. Certificate

```bash
sudo apt install -y certbot
sudo certbot certonly --webroot -w /var/www/certbot -d proxy.example.com
```

Renewals are handled by the standard certbot systemd timer
(`systemctl list-timers | grep certbot`); keep the webroot block in the vhost — renewals
need it too.

**Variants.**

- **Your own / corporate certificate** — skip certbot and point `ssl_certificate` /
  `ssl_certificate_key` at your files in step 7.
- **Wildcard `*.example.com`** — point the `:443` block at its paths; the webroot block can
  be removed, leaving just the `:80` redirect.
- **certbot 429 (rate limit)** — Let's Encrypt weekly issuance limits; add `--staging` for
  debugging and issue the production certificate later.

### Step 5. Project and .env

```bash
git clone https://github.com/yourO2xygen/tproxy-relay /opt/tproxy-relay
cd /opt/tproxy-relay
deploy/production/setup.sh proxy.example.com
```

The script automatically: detects the public IP (for MTProxy NAT), generates the secret
(`openssl rand -hex 16`) and a random base-path slug, and creates `.env` with 600
permissions. The secret is **not printed** to the terminal — by default it lives only in `.env`.

**Variants.**

```bash
# Show client credentials (secret and t.me links):
deploy/production/setup.sh proxy.example.com --show

# Proxy at the domain root, no base path:
deploy/production/setup.sh proxy.example.com --base-path none

# Your own slug instead of a random one:
deploy/production/setup.sh proxy.example.com --base-path myproxy

# Manually (without the script):
cp deploy/production/.env.example .env && nano .env
# required fields: TPROXY_PUBLIC_HOSTNAME, TPROXY_SECRET_HEX, MTPROXY_PUBLIC_IP
```

- Re-running the script does not overwrite an existing `.env`.
- The secret can be supplied as a file instead of a variable: `TPROXY_SECRET_HEX_FILE`
  (docker-secrets style) — then the variable is not needed.
- Changing `TPROXY_PUBLIC_HOSTNAME` or `TPROXY_BASE_PATH` changes all capabilities — this is
  a **reissue**: clients need new links and a new secret.
- The full variable reference — [.env.example](.env.example) (comments in Russian).

### Step 6. Docker and starting the containers

```bash
curl -fsSL https://get.docker.com | sudo sh       # Docker + compose plugin
sudo usermod -aG docker $USER                     # optional: docker without sudo

docker compose -f docker-compose.prod.yml up -d --build
docker ps --format 'table {{.Names}}\t{{.Status}}' # both containers (healthy)
curl -f http://127.0.0.1:8081/readyz               # ok
```

Two containers come up: `tproxy-relay` (publishes the transport on `127.0.0.1:8080` and the
admin endpoints on `127.0.0.1:8081` — neither is exposed publicly) and `tproxy-mtproxy`
(inside the docker network, no published ports).

**Variants.**

- **Docker Hub unreachable from the server** (TLS timeouts) — build the images where it is
  reachable and transfer them:

  ```bash
  docker build -t tproxy-relay:latest -f deploy/relay/Dockerfile .
  docker build -t tproxy-mtproxy:latest deploy/mtproxy
  docker save tproxy-relay:latest tproxy-mtproxy:latest | gzip | ssh root@server 'gunzip | docker load'
  ```

- **Busy subnet** — compose uses `172.20.0.0/24`; if it is taken on your server, change it in
  `docker-compose.prod.yml` and set `MTPROXY_LOCAL_IP` in `.env`.
- **Fine-tuning limits** — container memory, pending budgets and the rest are changed via
  `.env`/`config.json` (see [Configuration](#configuration)); keep pending budgets ≤ ~25% of
  the relay's `mem_limit`.

### Step 7. nginx vhost, stage 2: the :443 block

Uncomment the `:443` block in `/etc/nginx/sites-available/tproxy.conf` (certificate paths in
the example are already the certbot defaults) and reload:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

The key directives (already in the example) and why they are there:

```nginx
server {
    listen 443 ssl http2;
    server_name proxy.example.com;

    ssl_certificate     /etc/letsencrypt/live/proxy.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/proxy.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    add_header Strict-Transport-Security "max-age=63072000" always;
    limit_conn tproxy_conn 32;      # ≤32 connections per IP
    access_log off;                 # bridge URLs and bearers must not hit the logs

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;   # exactly one value
        proxy_set_header Upgrade $http_upgrade;          # needed by the websocket carrier
        proxy_set_header Connection $connection_upgrade;

        client_max_body_size 4m;         # max batch + headers
        proxy_request_buffering off;     # uplink streaming
        proxy_buffering off;             # downlink streaming (long-poll)
        proxy_read_timeout 75s;          # > relay long-poll (25s), with headroom
        proxy_send_timeout 75s;
    }
}
```

**Variants.**

- **WebSocket carrier** — the `Upgrade`/`Connection` headers are already in the example;
  just set `TPROXY_CARRIER_MODE=websocket` in `.env` and recreate the relay
  (`docker compose -f docker-compose.prod.yml up -d relay`). There are also `https-lanes`
  and `websocket-lanes` (a socket per stream) — the two basic modes are enough for most cases.
- **Several sites on one nginx** — each gets its own vhost file in `sites-available`; the
  relay does not interfere with neighbors and never edits their files.

### Step 8. Verification and client

```bash
curl -f https://proxy.example.com/          # the cover page (not an nginx stub)
curl -f http://127.0.0.1:8081/healthz       # ok
curl -s http://127.0.0.1:8081/metrics | head # tproxy_* metrics
```

Client credentials:

```bash
deploy/production/setup.sh proxy.example.com --show
```

In Telegram: **Settings → Proxy → Add proxy**, type **WEB**, server `proxy.example.com`,
secret `<from the script output>`. A shareable link (the script and the bot print it too):

```
https://t.me/webproxy?server=proxy.example.com&secret=<secret>
tg://webproxy?server=proxy.example.com&secret=<secret>
```

<details>
<summary><strong>Variant: end-to-end check with the test client (requires .NET 10 SDK)</strong></summary>

The test client lives in the `dev` branch (28 checks for https, 23 for websocket):

```bash
git clone -b dev https://github.com/yourO2xygen/tproxy-relay /tmp/tproxy-dev
cd /tmp/tproxy-dev
dotnet run --project tools/TproxyTestClient -- --host proxy.example.com --secret <hex>
# expected: all PASS except the echo asserts (the backend is a real MTProxy, not an echo stub)
```

</details>

## Client credentials

- **Secret** — 32 hex characters (16 bytes). The `dd`-prefixed form (44 characters) is
  supported too. With a base path enabled, links use the **marked** secret
  `base64url(0x70 || secret)` — old clients reject it explicitly instead of trying to
  connect to the wrong host.
- The **capability** is derived from the secret and the hostname (+ path with a base path):
  change the domain or the slug and every link must be reissued.
- The secret grants full access to the proxy. Treat it like a password; to revoke a
  compromised key — see [Key management](#key-management).

## Key management

The built-in secret from `.env` always works (MTProxy port 2398). On top of it you can
create managed keys — each with its own secret and **its own MTProxy process**; the registry
lives in SQLite (`/data/keys.db` on the relay volume). Revoke/pause instantly closes the
key's sessions; traffic is aggregated per key and per day.

All methods are optional and combine freely; everything is disabled by default (enabling
without required parameters = startup refusal, fail-closed).

| Variant | What to set in `.env` | Access |
|---|---|---|
| Nothing (builtin) | — | the secret from `.env` |
| **Admin API** | `TPROXY_API_ENABLED=true` + `TPROXY_API_TOKEN` | loopback `:8081`, Bearer |
| **Telegram bot** | `TPROXY_BOT_ENABLED=true` + `TPROXY_BOT_TOKEN` + `TPROXY_BOT_ADMINS` | admin chats only |
| API + bot | both blocks | both channels, one registry |

After editing `.env` recreate the relay: `docker compose -f docker-compose.prod.yml up -d relay`.

### Variant: Admin API

The admin port is not published externally — work over an SSH tunnel:

```bash
ssh -L 8081:127.0.0.1:8081 root@server
# in another terminal, on your machine:
TOKEN=...   # the value of TPROXY_API_TOKEN
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8081/admin/stats
```

| Path | Response |
|---|---|
| `GET /admin/stats` | sessions, streams, traffic, pending, limit hits, keys |
| `GET /admin/keys[?reveal=1]` | key list; secrets only with `reveal=1` |
| `POST /admin/keys` | `{"name":"ivan"}` → 201, secret and a ready t.me link |
| `DELETE /admin/keys/{id}` | revoke: `{revoked, sessions_closed}` |
| `POST /admin/keys/{id}/pause` · `/resume` | pause/resume a key |
| `GET /admin/sessions` | live sessions (no tokens) |
| `GET /admin/traffic?days=N` | per-key traffic for N days (1–90) |

No token or a wrong one → 404 (no oracle). Examples:

```bash
# Create a key and get a client link:
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' -d '{"name":"ivan"}' \
     http://127.0.0.1:8081/admin/keys
# Revoke:
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8081/admin/keys/<id>
```

<details>
<summary><strong>Variant: static key import without API and bot</strong></summary>

Put a seed file on the relay volume — it is imported at startup:

```bash
cat > seed.json <<'EOF'
[{"name":"ivan","secret_hex":"<64 hex characters>"}]
EOF
docker cp seed.json tproxy-relay:/data/keys/seed.json
docker compose -f docker-compose.prod.yml restart relay
```

</details>

### Variant: Telegram bot

The token comes from [@BotFather](https://t.me/BotFather). The bot learns your chat id once:

1. Enable the bot (`TPROXY_BOT_ENABLED=true`, token in `TPROXY_BOT_TOKEN`,
   `TPROXY_BOT_ADMINS` empty for now) and recreate the relay.
2. Send the bot any message; the log will show `event=tg_stranger chat=<id>`.
3. Put `<id>` into `TPROXY_BOT_ADMINS` and recreate the relay.

```bash
docker logs tproxy-relay 2>&1 | grep tg_stranger
```

Commands (admin chats only): `/stats`, `/keys`, `/key <name>`, `/revoke`, `/pause`,
`/resume`, `/traffic`, `/help`. The bot issues a key together with a ready `t.me` link
(it respects the base path and the marked secret).

## Cover site

By default every path outside the transport API answers with a built-in neutral page.
Instead you can serve a real site — a random visitor sees normal content and the deployment
stays masked. The modes are mutually exclusive (both at once = configuration error).

**Variant 1: nothing (default).** The built-in stub, nothing to configure.

**Variant 2: static files (`TPROXY_PUBLIC_DIR`).** The directory is read into memory at
startup (changed files — `restart relay`); GET/HEAD, ETag, 304, single Range, `404.html` on
misses, `../` traversal rejected. Example via `docker-compose.override.yml` (compose picks it
up automatically):

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
mkdir -p site && echo '<h1>My site</h1>' > site/index.html
docker compose -f docker-compose.prod.yml up -d relay
```

**Variant 3: upstream (`TPROXY_PUBLIC_UPSTREAM`).** A streaming reverse proxy to your
application: original Host and path are preserved, hop-by-hop headers are stripped, a client
disconnect releases the connection:

```yaml
services:
  relay:
    environment:
      TPROXY_PUBLIC_UPSTREAM: http://site:80   # a neighboring container in tproxy-net
```

Details — [docs/PUBLIC_SITE.md](docs/PUBLIC_SITE.md) (in Russian). Transport paths
(`/api/v1/*`, the bridge) always take priority over the site.

## Configuration

Layers (highest priority first): environment variables (`TPROXY_*`) → `config.json` →
built-in defaults. The secret is never read from `config.json` — env or a file only. A
config.json example — [deploy/config.example.json](deploy/config.example.json); attach it
like this:

```yaml
# docker-compose.override.yml
services:
  relay:
    volumes:
      - ./config.json:/app/config.json:ro
```

Main variables (full reference — [.env.example](.env.example), comments in Russian):

| Variable | Default | Description |
|---|---|---|
| `TPROXY_SECRET_HEX` | — (required) | MTProxy secret, 32 hex characters; or `TPROXY_SECRET_HEX_FILE` |
| `TPROXY_PUBLIC_HOSTNAME` | — (required) | the proxy domain; part of the capability |
| `TPROXY_CARRIER_MODE` | `https` | carrier: `https` \| `websocket` \| `https-lanes` \| `websocket-lanes` |
| `TPROXY_BASE_PATH` | empty | base path slug; empty = domain root |
| `TPROXY_MAX_SESSIONS` | `128` | global session cap |
| `TPROXY_MAX_STREAMS_PER_SESSION` | `128` | streams per session |
| `TPROXY_MAX_PENDING_GLOBAL_BYTES` | `268435456` | global pending budget (keep ≤ ~25% of mem_limit) |
| `TPROXY_MAX_SESSIONS_PER_IP` | `0` (off) | per-IP session limit |
| `TPROXY_LONG_POLL_SECONDS` | `25` | `/down` long-poll (the nginx timeout must be larger) |
| `TPROXY_RECONNECT_GRACE` | `120` | how long the reaper waits for an idle session to reconnect |
| `TPROXY_API_ENABLED` / `TPROXY_API_TOKEN` | `false` / — | Admin API (fail-closed) |
| `TPROXY_BOT_ENABLED` / `TPROXY_BOT_TOKEN` / `TPROXY_BOT_ADMINS` | `false` / — / — | Telegram bot (fail-closed) |
| `MTPROXY_PUBLIC_IP` | — (required) | the server's public IP for MTProxy NAT |

Rate limits (`new_sessions/streams/bootstraps_per_minute` + burst), batch and frame budgets,
the safety margin — in `config.json` and `.env.example`.

## Maintenance

```bash
# Updating the relay (sessions are invalidated, clients reconnect on their own):
git pull && docker compose -f docker-compose.prod.yml up -d --build relay
# Rollback — to the previous git tag with the same command.

# Backing up the key registry (the only state):
docker exec tproxy-relay sh -c 'sqlite3 /data/keys.db ".backup /tmp/keys.db"' || \
docker cp tproxy-relay:/data/keys.db backup-keys-$(date +%F).db

# Logs:
docker logs tproxy-relay              # the relay (rotation 3×10 MiB)
docker exec tproxy-mtproxy sh -c 'ls /tmp/mtproxy/'   # mtproxy child logs
docker exec tproxy-mtproxy curl -s 127.0.0.1:8888/stats  # MTProxy statistics

# Metrics (on the server):
curl -s 127.0.0.1:8081/metrics
```

The MTProxy routing config (`proxy-multi.conf`) is refreshed daily by the supervisor, which
restarts the proxy only if the data changed. Minimum alerts (Prometheus):
`tproxy_pending_bytes > 200 MiB`, `tproxy_limit_hits_total` growing faster than ~10/min,
`up == 0`, container memory > 60% of the limit.

## Troubleshooting

| Symptom | Cause / action |
|---|---|
| `certbot` returns 429 | Let's Encrypt rate limit; use `--staging` for debugging, issue the production certificate later |
| Port 80/443 busy | `sudo ss -tlnp 'sport = :443'` — find the owner; another vhost or service |
| Site opens, Telegram won't connect | `TPROXY_PUBLIC_HOSTNAME` ≠ the domain in the link; secret/slug from an old deployment (capability is derived from host+path — a reissue is needed) |
| Connects, but streams hang with no log errors | MTProxy NAT pitfall: `MTPROXY_PUBLIC_IP` empty/wrong (middle-end must present the public address) |
| `tproxy-mtproxy` is `unhealthy` | children are dead and not respawning: `docker logs tproxy-mtproxy`, logs at `/tmp/mtproxy/log-*` inside the container |
| The bot is silent | chat id not set: look for `event=tg_stranger` in the logs; polling: `docker logs tproxy-relay \| grep tg_` |
| Endless 503 on `/api/v1/*` | backend unreachable: `curl 127.0.0.1:8081/readyz` shows the reason |
| New sessions close instantly, `tproxy_limit_hits_total` grows | a flood or an exhausted pending budget: watch `tproxy_pending_bytes`; with monotonic growth — restart the relay (a stopgap) and audit the limits |
| Relay container OOMKilled | pending budgets raised above ~25% of `mem_limit` — lower the budgets or raise the limit |

> [!IMPORTANT]
> A tool for personal use; the server owner is responsible for complying with the laws of
> their jurisdiction. The proxy secret grants full access: keep `.env` at 600 permissions and
> don't post links in public chats. Bridge URLs and bearer tokens are never logged
> (`access_log off` in nginx, the relay does not log tokens); the ISP sees only a TLS
> connection to your domain. The admin port `8081` is published on the host loopback only.

## Documentation

- [docs/PROTOCOL.md](docs/PROTOCOL.md) — normative protocol: frames, tokens, cursors/grants,
  error codes (in Russian).
- [docs/BASE_PATH.md](docs/BASE_PATH.md) — deployment under a path prefix: v2-capability,
  slug, marked links (in Russian).
- [docs/PUBLIC_SITE.md](docs/PUBLIC_SITE.md) — the cover site: static/upstream (in Russian).
- [CHANGELOG.md](CHANGELOG.md) — version history (in Russian).
- [.env.example](.env.example) — the full environment variable reference (in Russian).

## Development

The **`dev`** branch is the working one: tests, dev stacks and tools live there.

```bash
git clone -b dev https://github.com/yourO2xygen/tproxy-relay
cd tproxy-relay
dotnet test tests/TproxyRelay.Tests          # unit + integration (a real relay on ephemeral ports)
docker compose up -d --build                 # local stack: nginx container + relay + echo stub
docker compose -f docker-compose.yml -f docker-compose.ws.yml up -d --build   # same in websocket mode
dotnet run --project tools/TproxyTestClient  # e2e client (28 https checks / 23 ws)
```

CI builds and tests both branches: on `dev` — the full test run + all images, on `main` —
production image builds only. License — [MIT](LICENSE).
