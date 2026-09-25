#!/usr/bin/env bash
# Loomi API v1 with curl and jq: start a chat, wait for its picture, download it, then continue the chat.
# Run: LOOMI_TOKEN=lm_... bash loomi.sh
set -euo pipefail
BASE="https://loomi.example.com"
AUTH="Authorization: Bearer $LOOMI_TOKEN"

# call METHOD PATH [JSON]: prints the response body, or stops with the status and the error code.
call() {
  local args=(-sS -m 90 -w '\n%{http_code}' -X "$1" -H "$AUTH") out status
  if [ $# -ge 3 ]; then args+=(-H "Content-Type: application/json" -d "$3"); fi
  # A GET is safe to repeat, so curl may retry it after a timeout or a 429 (it honours Retry-After).
  if [ "$1" = GET ]; then args+=(--retry 5); fi
  out=$(curl "${args[@]}" "$BASE$2") || return 1
  status=${out##*$'\n'}; out=${out%$'\n'*}
  if [ "$status" -ge 400 ]; then echo "HTTP $status $out" >&2; return 1; fi
  printf '%s\n' "$out"
}

# wait_for ID: long-polls until the message is finished, then prints it.
wait_for() {
  local reply
  while :; do
    reply=$(call GET "/api/v1/messages/$1/wait?timeout=60") || return 1
    if [ "$(jq -r .done <<<"$reply")" = true ]; then jq .message <<<"$reply"; return; fi
    echo "still $(jq -r .message.stage <<<"$reply")" >&2
  done
}

# save MESSAGE FILE: downloads a finished message's picture, or says why there is none.
save() {
  if [ "$(jq -r .status <<<"$1")" != completed ]; then echo "no picture: $1" >&2; return 1; fi
  curl -sS --fail --retry 5 -H "$AUTH" -o "$2" "$BASE$(jq -r .imageUrl <<<"$1")"
  echo "saved $2"
}

# 1. Start a chat with its first message.
start=$(call POST /api/v1/chats '{"prompt":"a lighthouse at dusk, film grain"}')
chat=$(jq -r .chat.id <<<"$start")
# 2. Wait for the answer and download the picture.
first=$(wait_for "$(jq -r .message.id <<<"$start")")
save "$first" lighthouse.png
# 3. The next message in the same chat, then wait and download again.
next=$(call POST "/api/v1/chats/$chat/messages" '{"prompt":"the same scene in winter"}')
second=$(wait_for "$(jq -r .message.id <<<"$next")")
save "$second" lighthouse-winter.png
