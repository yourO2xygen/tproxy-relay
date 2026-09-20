#!/bin/sh
set -e
CFG=/etc/mtproxy
RUN=/tmp/mtproxy
mkdir -p "$RUN"
if [ ! -s "$CFG/proxy-secret" ]; then
    curl -fsSL https://core.telegram.org/getProxySecret -o "$CFG/proxy-secret"
fi
if [ ! -s "$CFG/proxy-multi.conf" ]; then
    curl -fsSL https://core.telegram.org/getProxyConfig -o "$CFG/proxy-multi.conf"
fi

KEYS_FILE="${KEYS_FILE:-/keys/registry.txt}"
POLL="${SUPERVISOR_POLL_SECONDS:-5}"

start_one() { # port secret
    mtproto-proxy -u nobody \
        -H "$1" \
        -S "$2" \
        $MTPROXY_NAT_ARGS \
        --aes-pwd "$CFG/proxy-secret" \
        "$CFG/proxy-multi.conf" \
        -M 1 -C 1024 >>"/tmp/mtproxy/log-$1" 2>&1 &
    echo $! > "$RUN/port-$1.pid"
}

stop_one() { # port
    if [ -f "$RUN/port-$1.pid" ]; then
        kill "$(cat "$RUN/port-$1.pid")" 2>/dev/null || true
        rm -f "$RUN/port-$1.pid"
    fi
}

# Built-in profile from the environment: the default client listener.
start_one 2398 "$MTPROXY_SECRET"

# Supervisor: reconcile managed keys from the shared registry (one
# mtproto-proxy process per key) without touching the built-in listener.
(
    while :; do
        if [ -r "$KEYS_FILE" ]; then
            wanted="$(awk -F: 'NF>=2 && $1 ~ /^[0-9]+$/ && $1 != 2398 {print $1" "$2}' "$KEYS_FILE")"
            # stop processes whose key disappeared
            for f in "$RUN"/port-*.pid; do
                [ -e "$f" ] || continue
                base="$(basename "$f")"
                port="${base#port-}"; port="${port%.pid}"
                [ "$port" = 2398 ] && continue
                echo "$wanted" | grep -q "^$port " || stop_one "$port"
            done
            # start processes for new keys
            echo "$wanted" | while IFS=' ' read -r port secret; do
                [ -n "$port" ] || continue
                [ -e "$RUN/port-$port.pid" ] && continue
                start_one "$port" "$secret"
            done
        fi
        sleep "$POLL"
    done
) &

# Block on the built-in proxy; if it dies the container restarts everything.
wait "$(cat "$RUN/port-2398.pid")"
