#!/usr/bin/env bash
# Подготовка .env для прод-деплоя: секрет, публичный IP, кредиты для клиента.
# Использование: deploy/production/setup.sh proxy.yourdomain.tld [--show]
#   --show  напечатать секрет и t.me-ссылку в терминал (по умолчанию — нет)
set -euo pipefail

HOST="${1:-}"
SHOW="${2:-}"
if [ -z "$HOST" ]; then
    echo "usage: $0 proxy.yourdomain.tld [--show]" >&2
    exit 1
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
MTPROXY_PUBLIC_IP=$PUB_IP
EOF
    chmod 600 .env
    echo ".env создан (chmod 600)."
fi

echo
echo "Дальше по README:"
echo "  docker compose -f docker-compose.prod.yml up -d"
if [ "$SHOW" != "--show" ]; then
    echo
    echo "Кредиты для клиента (сервер/секрет) — в .env; показать их: $0 $HOST --show"
    exit 0
fi

echo
echo "Кредиты для клиента Telegram (Настройки -> Прокси -> WEB):"
echo "  Сервер:  $HOST"
echo "  Секрет:  $SECRET"
echo
echo "Ссылка для шаринга:"
echo "  https://t.me/webproxy?server=$HOST&secret=$SECRET"
