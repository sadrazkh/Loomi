#!/usr/bin/env bash
set -euo pipefail
umask 077
mkdir -p /data/profiles /data/images /data/keys
Xvfb :99 -screen 0 1440x1000x24 -nolisten tcp &
xvfb_pid=$!
trap 'kill $(jobs -pr) 2>/dev/null || true' EXIT INT TERM
for i in $(seq 1 50); do
  [ -S /tmp/.X11-unix/X99 ] && break
  kill -0 "$xvfb_pid"
  sleep 0.1
done
fluxbox -display :99 >/dev/null 2>&1 &
x11vnc -display :99 -localhost -rfbport 5900 -forever -shared -nopw -quiet >/dev/null 2>&1 &
websockify --web=/usr/share/novnc 127.0.0.1:6080 127.0.0.1:5900 >/dev/null 2>&1 &
dotnet Loomi.dll &
wait -n
exit 1
