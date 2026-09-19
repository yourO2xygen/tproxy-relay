#!/bin/sh
set -e
CFG=/etc/mtproxy
mkdir -p "$CFG"
if [ ! -s "$CFG/proxy-secret" ]; then
    curl -fsSL https://core.telegram.org/getProxySecret -o "$CFG/proxy-secret"
fi
if [ ! -s "$CFG/proxy-multi.conf" ]; then
    curl -fsSL https://core.telegram.org/getProxyConfig -o "$CFG/proxy-multi.conf"
fi
exec mtproto-proxy \
    -u nobody \
    -p 8888 \
    -H 2398 \
    -S "$MTPROXY_SECRET" \
    $MTPROXY_NAT_ARGS \
    --aes-pwd "$CFG/proxy-secret" \
    "$CFG/proxy-multi.conf" \
    -M 1 -C 4096
