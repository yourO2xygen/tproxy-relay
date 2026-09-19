#!/usr/bin/env bash
# Generate a fresh client-facing secret and print the client configuration.
set -euo pipefail

HOST="${1:-}"
if [ -z "$HOST" ]; then
    echo "usage: $0 proxy.yourdomain.tld" >&2
    exit 1
fi

SECRET="$(openssl rand -hex 16)"
echo "Впишите в .env:"
echo "  TPROXY_PUBLIC_HOSTNAME=$HOST"
echo "  TPROXY_SECRET_HEX=$SECRET"
echo
echo "Клиент Telegram (Настройки -> Прокси):"
echo "  Сервер:  $HOST"
echo "  Секрет:  $SECRET"
echo
echo "Ссылка для шаринга:"
echo "  https://t.me/webproxy?server=$HOST&secret=$SECRET"
