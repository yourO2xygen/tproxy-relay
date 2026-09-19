#!/bin/sh
set -e
CERT_DIR=/etc/nginx/certs
mkdir -p "$CERT_DIR"
if [ ! -f "$CERT_DIR/proxy.example.com.crt" ]; then
    echo "generating self-signed certificate for proxy.example.com"
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
        -keyout "$CERT_DIR/proxy.example.com.key" \
        -out "$CERT_DIR/proxy.example.com.crt" \
        -subj "/CN=proxy.example.com" \
        -addext "subjectAltName=DNS:proxy.example.com" \
        >/dev/null 2>&1
fi
exec nginx -g "daemon off;"
