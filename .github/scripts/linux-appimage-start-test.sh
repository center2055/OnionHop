#!/bin/bash
# Start test for a built AppImage on a machine of its architecture (#95): it unpacks with its own
# runtime, every bundled program runs, and the desktop app starts on a virtual X display, opens its
# window and is still running 45 seconds later, as a user who never ran it before. Saves a screenshot.
#
# Usage: linux-appimage-start-test.sh <AppImage> <screenshot.png>
# Needs Xvfb, xwininfo (x11-utils) and ImageMagick's import.
set -u

APPIMAGE="$(readlink -f "$1")"
SHOT="$(readlink -f "$2")"
SCRIPTS="$(cd "$(dirname "$0")" && pwd)"

fail() {
    echo "::error::$1"
    exit 1
}

chmod +x "$APPIMAGE"
WORK="$(mktemp -d)"
(cd "$WORK" && "$APPIMAGE" --appimage-extract >/dev/null) || fail "the AppImage does not unpack on $(uname -m)"
APPDIR="$WORK/squashfs-root"
echo "ok  unpacked with the AppImage's own runtime on $(uname -m)"

bash "$SCRIPTS/linux-cli-smoke.sh" "$APPDIR/usr/bin" runtime || exit 1

export DISPLAY=:99
Xvfb :99 -screen 0 1280x800x24 >/tmp/xvfb.log 2>&1 &
XVFB=$!
sleep 3
kill -0 "$XVFB" 2>/dev/null || fail "Xvfb did not start"

# A home of its own, like a first start, so nothing from an earlier run is reused.
export HOME="$(mktemp -d)"
"$APPDIR/AppRun" >/tmp/onionhop-app.log 2>&1 &
APP=$!

show_logs() {
    echo "--- app output"
    tail -n 60 /tmp/onionhop-app.log
    echo "--- startup.log"
    tail -n 60 "$HOME/.local/share/OnionHop/startup.log" 2>/dev/null
}

window=""
for _ in $(seq 1 45); do
    kill -0 "$APP" 2>/dev/null || break
    if [ -z "$window" ]; then
        window="$(xwininfo -root -tree 2>/dev/null | grep -m 1 '"OnionHop"' || true)"
    fi
    sleep 1
done

if ! kill -0 "$APP" 2>/dev/null; then
    show_logs
    fail "OnionHop exited during startup"
fi
if [ -z "$window" ]; then
    show_logs
    fail "OnionHop is running but opened no window"
fi
echo "ok  window: $(echo "$window" | sed 's/^ *//')"

import -window root "$SHOT" || fail "could not take a screenshot"
echo "ok  OnionHop is still running after 45 s; screenshot saved"

kill "$APP" 2>/dev/null
for _ in $(seq 1 10); do
    kill -0 "$APP" 2>/dev/null || break
    sleep 1
done
kill -9 "$APP" 2>/dev/null
kill "$XVFB" 2>/dev/null
exit 0
