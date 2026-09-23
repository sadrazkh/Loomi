#!/usr/bin/env bash
set -euo pipefail

# First-run helper for a Loomi deployment. It only prepares .env — it never touches Docker itself.
#
#   - Creates .env from .env.example if .env does not exist yet.
#   - Generates a strong LOOMI_ACCESS_KEY the first time only. It never overwrites a key that is
#     already set, so re-running this after the workspace has real users in it is safe.
#   - Sets LOOMI_HOSTS and LOOMI_DOMAIN from a domain you pass as an argument, or type when asked.
#
# Usage:
#   deploy/setup.sh                                     # prompts for the domain at a terminal
#   deploy/setup.sh loomi.example.com
#   deploy/setup.sh loomi.example.com staging.example.com   # extra names for LOOMI_HOSTS
#
# Run it again any time to change the domain; it leaves everything else in .env alone.

cd "$(dirname "$0")/.."

ENV_FILE=".env"
EXAMPLE_FILE=".env.example"

if [ ! -f "$EXAMPLE_FILE" ]; then
  echo "Error: $EXAMPLE_FILE not found here. Run this script from a Loomi checkout (deploy/setup.sh)." >&2
  exit 1
fi

if [ -f "$ENV_FILE" ]; then
  echo "$ENV_FILE already exists; keeping it and only filling in what is missing or asked for."
else
  cp "$EXAMPLE_FILE" "$ENV_FILE"
  echo "Created $ENV_FILE from $EXAMPLE_FILE."
fi
chmod 600 "$ENV_FILE"

# Prints the value after the first '=' on the first matching, uncommented line — or nothing.
get_kv() {
  grep -m1 "^${1}=" "$ENV_FILE" 2>/dev/null | cut -d'=' -f2- || true
}

# Sets a key to a value, replacing an existing (possibly commented-out) line, or appending a new one.
# The pipe delimiter in sed avoids clashing with '/' in values such as a URL or a file path.
set_kv() {
  key="$1" value="$2"
  if grep -q "^${key}=" "$ENV_FILE"; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
  elif grep -q "^# ${key}=" "$ENV_FILE"; then
    sed -i "s|^# ${key}=.*|${key}=${value}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

generate_key() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
  else
    head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'
  fi
}

existing_key="$(get_kv LOOMI_ACCESS_KEY)"
if [ -n "$existing_key" ]; then
  echo "LOOMI_ACCESS_KEY is already set in $ENV_FILE; leaving it alone."
else
  set_kv LOOMI_ACCESS_KEY "$(generate_key)"
  # Deliberately not printed here, or anywhere else this script writes to: it is the owner's login
  # password until they change it. Read it from $ENV_FILE yourself when you need to hand it over.
  echo "Generated a new LOOMI_ACCESS_KEY and wrote it to $ENV_FILE (not shown here)."
fi

domain="${1:-}"
if [ "$#" -gt 0 ]; then shift; fi
extra_hosts=("$@")

if [ -z "$domain" ] && [ -t 0 ]; then
  read -rp "Domain that already points at this server (e.g. loomi.example.com), or leave blank to skip: " domain
fi

if [ -n "$domain" ]; then
  hosts="$domain;localhost;127.0.0.1"
  if [ "${#extra_hosts[@]}" -gt 0 ]; then
    for h in "${extra_hosts[@]}"; do
      [ -n "$h" ] && hosts="$hosts;$h"
    done
  fi
  set_kv LOOMI_HOSTS "$hosts"
  set_kv LOOMI_DOMAIN "$domain"
  echo "Set LOOMI_HOSTS=$hosts and LOOMI_DOMAIN=$domain in $ENV_FILE."
else
  echo "No domain given; LOOMI_HOSTS/LOOMI_DOMAIN in $ENV_FILE are unchanged. Set them yourself before serving anything but localhost."
fi

cat <<STEPS

Next steps:
  1. Review $ENV_FILE — especially the LOOMI_PROXY_IP comment, which depends on which proxy you use.
  2. Start the stack with one of:
       docker compose up -d --build                  # deploy/nginx.conf, or a proxy you already run on the host
       docker compose --profile caddy up -d --build   # automatic HTTPS via deploy/Caddyfile
  3. docker compose logs -f loomi   — watch it come up clean, then sign in as "owner" using the
     access key now sitting in $ENV_FILE.
  4. Full runbook, in Persian: docs/DEPLOY.md
STEPS
