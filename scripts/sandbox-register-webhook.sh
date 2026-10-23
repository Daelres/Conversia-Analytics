#!/usr/bin/env bash
# Registers the Conversia webhook in the 360dialog SANDBOX and checks the tunnel first.
#
# Run it in a terminal while the tunnel is open (see docs/PRUEBAS_SANDBOX.md, step 4).
# Every secret is read silently from the terminal (never from arguments), is never printed
# and is never written to disk.
#
# Usage: bash scripts/sandbox-register-webhook.sh [--basic]
#   --basic  send the secret as "Authorization: Basic" instead of "X-Webhook-Secret"
set -euo pipefail

variant="header"
if [[ "${1:-}" == "--basic" ]]; then
  variant="basic"
fi

read -r -p "Tunnel address (for example https://something.trycloudflare.com): " tunnel_host
tunnel_host="${tunnel_host%/}"
if [[ "$tunnel_host" != https://* ]]; then
  echo "The tunnel address must start with https://" >&2
  exit 1
fi

read -r -p "Channel key (the last part of /api/webhooks/360dialog/<key>): " channel_key
if [[ ! "$channel_key" =~ ^[A-Za-z0-9_-]+$ ]]; then
  echo "The channel key may only contain letters, digits, '-' and '_'." >&2
  exit 1
fi

read -r -s -p "Webhook secret of the line (not shown): " webhook_secret
echo
read -r -s -p "360dialog sandbox API key (not shown): " d360_api_key
echo

webhook_url="${tunnel_host}/api/webhooks/360dialog/${channel_key}"

auth_header_name="X-Webhook-Secret"
auth_header_value="$webhook_secret"
if [[ "$variant" == "basic" ]]; then
  auth_header_name="Authorization"
  auth_header_value="Basic $(printf 'conversia:%s' "$webhook_secret" | base64 | tr -d '\n')"
fi

echo
echo "1) Checking the tunnel and the credentials against your API..."
# The header travels through stdin so the secret does not appear in the process list.
check_status="$(printf '%s: %s\n' "$auth_header_name" "$auth_header_value" |
  curl -s -m 15 -o /dev/null -w '%{http_code}' -X POST "$webhook_url" -H @- \
    -H 'Content-Type: application/json' -d '{}')" || check_status="000"

case "$check_status" in
  200) echo "   OK (200): the tunnel reaches the API and the secret is valid." ;;
  401) echo "   401: wrong channel key or secret, the line is inactive, or the company is not active." >&2; exit 1 ;;
  429) echo "   429: too many failed attempts from this network; wait 60 seconds and retry." >&2; exit 1 ;;
  *)   echo "   HTTP $check_status: the tunnel is not reaching the API (is the tunnel pointing at the API port?)." >&2; exit 1 ;;
esac

echo
echo "2) Registering the webhook in the 360dialog sandbox (variant: $variant)..."
body="$(printf '{"url":"%s","headers":{"%s":"%s"}}' "$webhook_url" "$auth_header_name" "$auth_header_value")"
response_file="$(mktemp)"
trap 'rm -f "$response_file"' EXIT
register_status="$(printf 'D360-API-KEY: %s\n' "$d360_api_key" |
  curl -s -m 20 -o "$response_file" -w '%{http_code}' -X POST \
    'https://waba-sandbox.360dialog.io/v1/configs/webhook' -H @- \
    -H 'Content-Type: application/json' -d "$body")" || register_status="000"

echo "   360dialog answered HTTP $register_status"
case "$register_status" in
  200|201) echo "   Registered. Now send a text message from your WhatsApp to the sandbox number" \
                "and open /mensajes in the Web." ;;
  400)     echo "   400: 360dialog rejected the request (the address must be https, or it does not accept 'headers')." >&2
           echo "   Try again with: bash scripts/sandbox-register-webhook.sh --basic" >&2
           exit 1 ;;
  401|403) echo "   $register_status: the sandbox API key is wrong or was revoked by a newer START." >&2; exit 1 ;;
  *)       echo "   Unexpected answer; check your connection and the 360dialog sandbox page." >&2; exit 1 ;;
esac
