#!/bin/bash
# Smoke test for a published Linux CLI folder: every bundled binary must start on this machine's
# architecture, and with "connect" the CLI must reach Tor and stop cleanly on SIGTERM (#89).
#
# Usage: linux-cli-smoke.sh <cli dir> [binaries|connect]
set -u

DIR="$1"
MODE="${2:-binaries}"

fail() {
    echo "::error::$1"
    exit 1
}

# 126/127: the file could not be executed at all; above 128: killed by a signal (SIGILL, SIGSEGV...).
# A tool that is still running when the timeout ends it (124) did start.
starts() {
    local name="$1"
    shift
    timeout 120 "$@" >/tmp/smoke-out.txt 2>&1
    local code=$?
    if [ "$code" -eq 126 ] || [ "$code" -eq 127 ] || [ "$code" -gt 128 ]; then
        cat /tmp/smoke-out.txt
        fail "$name does not run here (exit $code)"
    fi
    echo "ok  $name: $(head -n 1 /tmp/smoke-out.txt)"
}

check_binaries() {
    starts "OnionHop CLI" "$DIR/OnionHopV3.Cli" version
    starts tor "$DIR/tor/tor" --version
    starts lyrebird "$DIR/tor/pluggable_transports/lyrebird" -version
    starts conjure-client "$DIR/tor/pluggable_transports/conjure-client" -h
    starts webtunnel-client "$DIR/tor/pluggable_transports/webtunnel-client" -h
    starts dnstt-client "$DIR/tor/pluggable_transports/dnstt-client" -h
    starts artihop "$DIR/artihop/artihop" --version
    starts snowflake-proxy "$DIR/snowflake/snowflake-proxy" -version
    starts sing-box "$DIR/vpn/sing-box" version
    starts xray "$DIR/vpn/xray" version
}

tor_running() {
    local p
    for p in /proc/[0-9]*; do
        if [ "$(cat "$p/comm" 2>/dev/null)" = "tor" ]; then
            return 0
        fi
    done
    return 1
}

connect_and_terminate() {
    local log cli code i
    log="$(mktemp)"
    "$DIR/OnionHopV3.Cli" connect --smart off --proxy-scope local --lan on >"$log" 2>&1 &
    cli=$!

    # Emulated ARM is slow: give Tor up to six minutes.
    for i in $(seq 1 120); do
        grep -q "disconnect and exit" "$log" && break
        kill -0 "$cli" 2>/dev/null || break
        sleep 3
    done
    cat "$log"
    if ! grep -q "Connected.  IP" "$log"; then
        kill -9 "$cli" 2>/dev/null
        fail "the CLI did not connect"
    fi
    grep -q "Shared with your network" "$log" || fail "--lan on did not report the LAN endpoints"
    tor_running || fail "tor is not running while connected"

    kill -TERM "$cli"
    for i in $(seq 1 60); do
        kill -0 "$cli" 2>/dev/null || break
        sleep 1
    done
    if kill -0 "$cli" 2>/dev/null; then
        kill -9 "$cli"
        fail "the CLI was still running 60 s after SIGTERM"
    fi
    wait "$cli"
    code=$?
    tail -n 3 "$log"
    [ "$code" -eq 0 ] || fail "the CLI exited with $code after SIGTERM (expected 0)"
    sleep 2
    if tor_running; then
        fail "tor was left running after SIGTERM"
    fi
    echo "ok  connected, then SIGTERM disconnected cleanly and stopped tor"
}

case "$MODE" in
    binaries) check_binaries ;;
    connect) connect_and_terminate ;;
    *) fail "unknown mode $MODE" ;;
esac
