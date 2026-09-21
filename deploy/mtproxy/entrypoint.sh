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

# The routing config lives on the writable tmpfs so the daily refresh can
# replace it on a read-only-rootfs deployment (the baked copy stays intact).
cp "$CFG/proxy-multi.conf" "$RUN/proxy-multi.conf"

start_one() { # port secret
    mtproto-proxy -u nobody \
        -H "$1" \
        -S "$2" \
        $MTPROXY_NAT_ARGS \
        --aes-pwd "$CFG/proxy-secret" \
        "$RUN/proxy-multi.conf" \
        -M 1 -C 1024 >>"$RUN/log-$1" 2>&1 &
    echo $! > "$RUN/port-$1.pid"
}

stop_one() { # port
    if [ -f "$RUN/port-$1.pid" ]; then
        kill "$(cat "$RUN/port-$1.pid")" 2>/dev/null || true
        rm -f "$RUN/port-$1.pid"
    fi
}

# REL-002: a pid file alone proves nothing — verify the child is alive.
alive_one() { # port -> 0 when a live process holds the pid file
    [ -f "$RUN/port-$1.pid" ] || return 1
    kill -0 "$(cat "$RUN/port-$1.pid")" 2>/dev/null
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
            # start processes for new keys, and respawn any child that died
            # (a stale pid file is not "alive": verify, clean, restart)
            echo "$wanted" | while IFS=' ' read -r port secret; do
                [ -n "$port" ] || continue
                if alive_one "$port"; then
                    continue
                fi
                rm -f "$RUN/port-$port.pid"
                echo "event=child_respawn port=$port" >>"$RUN/log-$port" 2>/dev/null || true
                start_one "$port" "$secret"
            done
        fi
        sleep "$POLL"
    done
) &

# Daily routing-config refresh: fetch, and only when the routing data really
# changed, bounce every child (the supervisor respawns them; if the built-in
# proxy dies the container restarts, which is the documented MTProxy refresh).
(
    while :; do
        sleep 86400
        if curl -fsSL https://core.telegram.org/getProxyConfig -o "$RUN/proxy-multi.conf.new" 2>/dev/null; then
            if ! cmp -s "$RUN/proxy-multi.conf" "$RUN/proxy-multi.conf.new"; then
                cp "$RUN/proxy-multi.conf.new" "$RUN/proxy-multi.conf"
                for f in "$RUN"/port-*.pid; do
                    [ -e "$f" ] || continue
                    kill "$(cat "$f")" 2>/dev/null || true
                    rm -f "$f"
                done
            fi
            rm -f "$RUN/proxy-multi.conf.new"
        fi
    done
) &

# Block on the built-in proxy; if it dies the container restarts everything.
wait "$(cat "$RUN/port-2398.pid")"
