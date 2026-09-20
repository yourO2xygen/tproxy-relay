#!/usr/bin/env bash
# Подготовка .env для прод-деплоя: секрет, публичный IP, кредиты для клиента.
# Использование:
#   deploy/production/setup.sh proxy.yourdomain.tld [--show] [--base-path <слаг|none>]
#   --show            напечатать секрет и t.me-ссылку в терминал (по умолчанию — нет)
#   --base-path слаг  все пути релея под /<слаг>/; без флага генерируется случайный
#                     слаг (16 символов base32); none = прокси в корне хоста
set -euo pipefail

HOST=""
SHOW=""
BASE_PATH=""
while [ $# -gt 0 ]; do
    case "$1" in
        --show) SHOW="--show"; shift ;;
        --base-path) BASE_PATH="${2:-}"; shift 2 ;;
        -*) echo "неизвестный флаг: $1" >&2; exit 1 ;;
        *) if [ -z "$HOST" ]; then HOST="$1"; shift; else echo "лишний аргумент: $1" >&2; exit 1; fi ;;
    esac
done
if [ -z "$HOST" ]; then
    echo "usage: $0 proxy.yourdomain.tld [--show] [--base-path <слаг|none>]" >&2
    exit 1
fi
if [ -z "$BASE_PATH" ]; then
    # lowercase RFC 4648 base32 от 10 случайных байт = 16 символов
    BASE_PATH="$(head -c 10 /dev/urandom | base32 | tr 'A-Z' 'a-z' | tr -d '\n')"
elif [ "$BASE_PATH" = "none" ]; then
    BASE_PATH=""
fi

# публичный IP сервера (для MTPROXY_NAT_ARGS)
PUB_IP="$(curl -s4 --max-time 5 ifconfig.me || curl -s4 --max-time 5 ip.sb)"
if [ -z "$PUB_IP" ]; then
    echo "не удалось определить публичный IP — заполните MTPROXY_PUBLIC_IP вручную" >&2
    exit 1
fi

SECRET="$(openssl rand -hex 16)"

cd "$(dirname "$0")/../.."
if [ -f .env ]; then
    echo ".env уже существует — не трогаю." >&2
else
    cat > .env << EOF
TPROXY_PUBLIC_HOSTNAME=$HOST
TPROXY_SECRET_HEX=$SECRET
TPROXY_CARRIER_MODE=https
TPROXY_BASE_PATH=$BASE_PATH
MTPROXY_PUBLIC_IP=$PUB_IP
EOF
    chmod 600 .env
    echo ".env создан (chmod 600)."
fi

# Адрес сервера и форма секрета для клиента зависят от base path.
if [ -n "$BASE_PATH" ]; then
    CLIENT_SERVER="$HOST/$BASE_PATH"
    # маркированный секрет: base64url(0x70 || секрет)
    MARKED="$( { printf '\x70'; printf "$(printf %s "$SECRET" | sed 's/../\\x&/g')"; } | base64 | tr '+/' '-_' | tr -d '=\n' )"
    CLIENT_SECRET="$MARKED"
    LINK_SERVER="$HOST%2F$BASE_PATH"
else
    CLIENT_SERVER="$HOST"
    CLIENT_SECRET="$SECRET"
    LINK_SERVER="$HOST"
fi

echo
echo "Дальше по README:"
echo "  docker compose -f docker-compose.prod.yml up -d"
if [ "$SHOW" != "--show" ]; then
    echo
    echo "Кредиты для клиента (сервер/секрет) — в .env; показать их: $0 $HOST --show --base-path ${BASE_PATH:-none}"
    exit 0
fi

echo
echo "Кредиты для клиента Telegram (Настройки -> Прокси -> WEB):"
echo "  Сервер:  $CLIENT_SERVER"
echo "  Секрет:  $CLIENT_SECRET"
echo
echo "Ссылки для шаринга:"
echo "  https://t.me/webproxy?server=$LINK_SERVER&secret=$CLIENT_SECRET"
echo "  tg://webproxy?server=$LINK_SERVER&secret=$CLIENT_SECRET"
