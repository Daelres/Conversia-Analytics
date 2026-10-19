#!/usr/bin/env bash
# Acceptance test of the Conversia container images (docs/DOCKER.md).
#
#   scripts/docker-smoke-test.sh [--keep] [--quick]
#
# Builds the three images with a unique throwaway tag, starts the real stack (PostgreSQL, migrator, API, Web)
# in its own compose project on free loopback ports with random one-run secrets, and checks what a
# deployment depends on: migrations applied, health checks, static assets, non-root, no secrets in the images,
# an end-to-end webhook -> conversation -> metrics flow, clean logs and the behaviour when PostgreSQL goes away.
#
# --keep    leave the stack running afterwards (prints the URLs, never the secrets)
# --quick   skip waiting for Docker to flag the API container as unhealthy while PostgreSQL is down
#
# Exit status: 0 when every check passed, 1 otherwise. Needs docker (with compose >= 2.24), curl, jq, openssl and python3.
# It never touches containers, images or volumes it did not create, and never reads the repository's .env.
set -euo pipefail

keep=0
quick=0
while [ $# -gt 0 ]; do
  case "$1" in
    --keep) keep=1 ;;
    --quick) quick=1 ;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

cd "$(dirname "$0")/.."

started=$SECONDS
passed=0
failed=0
pass() { printf 'PASS  %s\n' "$1"; passed=$((passed + 1)); }
fail() { printf 'FAIL  %s\n' "$1"; failed=$((failed + 1)); }
note() { printf '      %s\n' "$1"; }
die() { printf 'FATAL %s\n' "$*" >&2; exit 1; }
# wait_until <seconds> <command...>: polls every 2 seconds until the command succeeds or the time is up.
wait_until() {
  local deadline=$((SECONDS + $1))
  shift
  until "$@" >/dev/null 2>&1; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 2
  done
}

for tool in docker curl jq openssl python3; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
done
docker info >/dev/null 2>&1 || die "the Docker daemon is not reachable"
docker compose version >/dev/null 2>&1 || die "docker compose (v2) is required"

suffix="$(openssl rand -hex 4)"
tag="smoke-$suffix"
project="conversia-smoke-$suffix"
api_image="conversia-api:$tag"
web_image="conversia-web:$tag"
migrator_image="conversia-migrator:$tag"

umask 077
work="$(mktemp -d "${TMPDIR:-/tmp}/conversia-smoke.XXXXXX")"
envfile="$work/stack.env"
override="$work/override.yml"
secrets="$work/secrets.txt"

dc() { docker compose -p "$project" --env-file "$envfile" -f docker-compose.yml -f "$override" "$@"; }

stack_created=0
cleanup() {
  local code=$?
  set +e
  if [ "$keep" -eq 1 ] && [ "$stack_created" -eq 1 ]; then
    echo
    echo "Stack left running (--keep). Compose project: $project"
    echo "  API: http://127.0.0.1:${api_port:-?}   Web: http://127.0.0.1:${web_port:-?}"
    echo "  Working files (mode 600, contain the run's secrets): $work"
    echo "  Tear down:"
    echo "    docker compose -p $project --env-file $envfile -f docker-compose.yml -f $override down -v --rmi local"
    echo "    docker image rm $api_image $web_image $migrator_image; rm -rf $work"
  else
    if [ "$stack_created" -eq 1 ]; then
      dc down -v --rmi local --remove-orphans >/dev/null 2>&1
    fi
    docker image rm "$api_image" "$web_image" "$migrator_image" >/dev/null 2>&1
    [ -z "${work:-}" ] || rm -rf -- "$work"
  fi
  exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# ------------------------------------------------------------------ configuration of this run
read -r api_port web_port postgres_port < <(python3 - <<'PY'
import socket
sockets = [socket.socket() for _ in range(3)]
for s in sockets:
    s.bind(("127.0.0.1", 0))
print(*[s.getsockname()[1] for s in sockets])
PY
)

postgres_password="$(openssl rand -hex 16)"
jwt_key="$(openssl rand -hex 32)"
report_key="$(openssl rand -hex 24)"
owner_password="Aa1-$(openssl rand -hex 12)"
owner_email="owner-$suffix@smoke.test"
postgres_db="conversia_smoke"
postgres_user="conversia_smoke"

cat >"$envfile" <<EOF
POSTGRES_DB=$postgres_db
POSTGRES_USER=$postgres_user
POSTGRES_PASSWORD=$postgres_password
JWT_SIGNING_KEY=$jwt_key
ERROR_REPORT_KEY=$report_key
API_PORT=$api_port
WEB_PORT=$web_port
POSTGRES_PORT=$postgres_port
CONVERSIA_TAG=$tag
EOF
# The values that must never appear in an image, a container definition or a log.
printf '%s\n' "$postgres_password" "$jwt_key" "$report_key" "$owner_password" >"$secrets"

# Everything is published on loopback only, so the throwaway stack is not reachable from the network.
cat >"$override" <<'EOF'
services:
  api:
    ports: !override
      - "127.0.0.1:${API_PORT}:8080"
  web:
    ports: !override
      - "127.0.0.1:${WEB_PORT}:8080"
EOF

api_url="http://127.0.0.1:$api_port"
web_url="http://127.0.0.1:$web_port"

echo "Conversia image smoke test (project $project, tag $tag)"
echo

# ------------------------------------------------------------------ build
build_started=$SECONDS
echo ">> Building the images (log: $work/build.log)"
if ! scripts/docker-build.sh --tag "$tag" >"$work/build.log" 2>&1; then
  tail -n 30 "$work/build.log" >&2
  die "the image build failed"
fi
build_seconds=$((SECONDS - build_started))
pass "the three images build (${build_seconds}s)"
sed -n '/^Built in/,$p' "$work/build.log" | sed 's/^/      /'

# ------------------------------------------------------------------ images carry what they should
for image in "$api_image" "$web_image" "$migrator_image"; do
  title="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.title"}}' "$image")"
  revision="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")"
  version="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' "$image")"
  created="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.created"}}' "$image")"
  if [ "$title" = "${image%%:*}" ] && [ -n "$revision" ] && [ -n "$version" ] && [ "$created" != "unknown" ] && [ -n "$created" ]; then
    pass "$image has its OCI labels (version $version)"
  else
    fail "$image is missing OCI labels (title='$title' revision='$revision' version='$version' created='$created')"
  fi
done
for image in "$api_image" "$web_image"; do
  health_test="$(docker image inspect --format '{{json .Config.Healthcheck.Test}}' "$image")"
  if [ "$health_test" != "null" ] && [ -n "$health_test" ]; then pass "$image defines a HEALTHCHECK"; else fail "$image defines no HEALTHCHECK"; fi
done

leak_free() { # <image>: neither the history nor the configuration of the image contains a secret of this run
  { docker history --no-trunc "$1"; docker image inspect "$1"; } >"$work/image-metadata.txt"
  ! grep -qFf "$secrets" "$work/image-metadata.txt"
}
for image in "$api_image" "$web_image" "$migrator_image"; do
  if leak_free "$image"; then pass "no secret in the history or configuration of $image"; else fail "a secret of this run is baked into $image"; fi
done

for image in "$api_image" "$web_image" "$migrator_image"; do
  uid="$(docker run --rm --entrypoint id "$image" -u 2>/dev/null || echo error)"
  if [ "$uid" != "0" ] && [ "$uid" != "error" ]; then pass "$image runs as non-root (uid $uid)"; else fail "$image runs as root or the check failed ($uid)"; fi
done

# ------------------------------------------------------------------ the stack
echo
echo ">> Starting the stack"
run_started=$SECONDS
stack_created=1
dc up -d --no-build >"$work/up.log" 2>&1 || { tail -n 30 "$work/up.log" >&2; die "docker compose up failed"; }

cid() { dc ps -a -q "$1" | sed -n 1p; }
health_of() { docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$(cid "$1")"; }
is_healthy() { [ "$(health_of "$1")" = "healthy" ]; }
migrator_done() { [ "$(docker inspect --format '{{.State.Status}}' "$(cid migrator)")" = "exited" ]; }

if wait_until 90 is_healthy postgres; then pass "postgres becomes healthy"; else fail "postgres did not become healthy"; fi

if wait_until 120 migrator_done; then
  exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$(cid migrator)")"
  if [ "$exit_code" = "0" ]; then pass "migrator exited 0"; else fail "migrator exited with $exit_code"; dc logs migrator | tail -n 20 >&2; fi
else
  fail "migrator did not finish in time"
fi

expected_migrations="$(find src/Conversia.Infrastructure/Persistence/Migrations -name '[0-9]*_*.cs' ! -name '*.Designer.cs' | wc -l | tr -d ' ')"
applied_migrations="$(dc exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tA -c "select count(*) from \"__EFMigrationsHistory\""' 2>/dev/null | tr -d '[:space:]' || true)"
if [ -n "$applied_migrations" ] && [ "$applied_migrations" = "$expected_migrations" ]; then
  pass "all $expected_migrations migrations are applied"
else
  fail "applied migrations: '${applied_migrations:-none}', migration classes in the repo: $expected_migrations"
fi

if wait_until 120 is_healthy api; then pass "api container health is healthy"; else fail "api container never became healthy ($(health_of api))"; fi
if wait_until 120 is_healthy web; then pass "web container health is healthy"; else fail "web container never became healthy ($(health_of web))"; fi
run_wait_seconds=$((SECONDS - run_started))
note "stack ready after ${run_wait_seconds}s"

body_of() { curl -sS --max-time 15 "$@"; }
code_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$@" || true; }

if [ "$(body_of "$api_url/health")" = "Healthy" ]; then pass "api GET /health -> 200 Healthy"; else fail "api GET /health is not Healthy"; fi
if [ "$(body_of "$web_url/health")" = "Healthy" ]; then pass "web GET /health -> 200 Healthy"; else fail "web GET /health is not Healthy"; fi
if [ "$(code_of "$web_url/")" = "200" ]; then pass "web GET / -> 200"; else fail "web GET / did not return 200"; fi

# ------------------------------------------------------------------ static assets
curl -sS --max-time 15 "$web_url/login" -o "$work/login.html"
assets="$(python3 - "$work/login.html" <<'PY'
import re, sys
html = open(sys.argv[1], encoding="utf-8").read()
# The pages use <base href="/">, so the asset URLs are relative ("css/app.<hash>.css").
urls = re.findall(r'(?:src|href)="([^"#?:]+\.(?:js|css))"', html)
print("\n".join(dict.fromkeys("/" + u.lstrip("/") for u in urls)))
PY
)"
if grep -q '_framework/blazor\.web.*\.js' <<<"$assets"; then pass "the login page references blazor.web.js"; else fail "the login page does not reference blazor.web.js"; fi
asset_count=0
while IFS= read -r asset; do
  [ -n "$asset" ] || continue
  asset_count=$((asset_count + 1))
  headers="$(curl -sS --max-time 15 -D - -o /dev/null "$web_url$asset" | tr -d '\r')"
  status="$(awk 'NR == 1 {print $2}' <<<"$headers")"
  ctype="$(awk -F': ' 'tolower($1) == "content-type" {print tolower($2)}' <<<"$headers")"
  cache="$(awk -F': ' 'tolower($1) == "cache-control" {print tolower($2)}' <<<"$headers")"
  case "$asset" in
    *.js) expected_type="javascript" ;;
    *) expected_type="text/css" ;;
  esac
  if [ "$status" = "200" ] && [[ "$ctype" == *"$expected_type"* ]] && [[ "$cache" == *max-age* || "$cache" == *immutable* ]]; then
    pass "asset $asset -> 200 ($ctype; $cache)"
  else
    fail "asset $asset -> status '$status', content-type '$ctype', cache-control '$cache'"
  fi
done <<EOF
$assets
EOF
[ "$asset_count" -gt 0 ] || fail "no static asset was found in the login page"

# ------------------------------------------------------------------ runtime user and secrets at run time
for service in api web; do
  uid="$(docker exec "$(cid "$service")" id -u 2>/dev/null || echo error)"
  if [ "$uid" != "0" ] && [ "$uid" != "error" ]; then pass "running $service container is non-root (uid $uid)"; else fail "$service container runs as root or the check failed ($uid)"; fi
  if docker exec "$(cid "$service")" sh -c 'test ! -w /app'; then pass "/app is read-only for the $service process"; else fail "/app is writable for the $service process"; fi
done
if docker exec "$(cid api)" sh -c 'test -n "$Jwt__SigningKey" && test -n "$ConnectionStrings__DefaultConnection"'; then
  pass "the api receives its secrets as run-time environment"
else
  fail "the api is missing its run-time secrets"
fi
docker inspect "$(cid migrator)" >"$work/migrator-inspect.json"
if ! grep -qF "$jwt_key" "$work/migrator-inspect.json"; then pass "the migrator container never receives the real JWT signing key"; else fail "the real JWT signing key reached the migrator"; fi

# ------------------------------------------------------------------ end to end through the API
echo
echo ">> End to end: tenant, branch, advisor, channel, webhook, conversation, metrics"
json_post() { # <url> <body> [header file]: prints the response body, fails on a non-2xx answer
  local url="$1" body="$2" headers="${3:-/dev/null}"
  printf '%s' "$body" | curl -sS --fail-with-body --max-time 20 -H 'Content-Type: application/json' -H "@$headers" --data-binary @- "$url"
}
json_get() { # <url> <header file>
  curl -sS --fail-with-body --max-time 20 -H "@$1" "$2"
}

register_body="$(jq -nc --arg t "Smoke $suffix" --arg e "$owner_email" --arg p "$owner_password" '{tenantName:$t,email:$e,password:$p}')"
if json_post "$api_url/api/auth/register-tenant" "$register_body" >"$work/register.json" 2>"$work/register.err"; then pass "register a tenant"; else fail "register a tenant: $(head -c 300 "$work/register.err" "$work/register.json")"; fi

login_body="$(jq -nc --arg e "$owner_email" --arg p "$owner_password" '{email:$e,password:$p}')"
token="$(json_post "$api_url/api/auth/login" "$login_body" 2>/dev/null | jq -r '.token // empty' || true)"
if [ -n "$token" ]; then pass "log in"; else fail "log in"; die "cannot continue without a token"; fi
printf '%s\n' "$token" >>"$secrets"
auth_headers="$work/auth.headers"
printf 'Authorization: Bearer %s\n' "$token" >"$auth_headers"

branch_id="$(json_post "$api_url/api/branches" '{"name":"Sucursal Smoke"}' "$auth_headers" 2>/dev/null | jq -r '.id // empty' || true)"
if [ -n "$branch_id" ]; then pass "create a branch"; else fail "create a branch"; die "cannot continue without a branch"; fi

advisor_id="$(json_post "$api_url/api/advisors" "$(jq -nc --arg b "$branch_id" '{name:"Asesora Smoke",branchId:$b}')" "$auth_headers" 2>/dev/null | jq -r '.id // empty' || true)"
if [ -n "$advisor_id" ]; then pass "create an advisor"; else fail "create an advisor"; fi

business_number="573005550100"
customer_number="573005550199"
channel_json="$(json_post "$api_url/api/channels" "$(jq -nc --arg b "$branch_id" '{branchId:$b,displayPhoneNumber:"+57 300 555 0100"}')" "$auth_headers" 2>/dev/null || true)"
webhook_path="$(printf '%s' "$channel_json" | jq -r '.webhookPath // empty')"
webhook_secret="$(printf '%s' "$channel_json" | jq -r '.webhookSecret // empty')"
unset channel_json
if [ -n "$webhook_path" ] && [ -n "$webhook_secret" ]; then pass "create a channel (the webhook secret is returned once)"; else fail "create a channel"; die "cannot continue without a channel"; fi
printf '%s\n' "$webhook_secret" >>"$secrets"

now="$(date +%s)"
inbound_text="Hola, quiero una cotizacion de depilacion laser para cuerpo completo"
outbound_text="Con gusto, te comparto los precios de la promocion de octubre"
inbound_payload="$work/inbound.json"
outbound_payload="$work/outbound.json"
jq -nc --arg bn "$business_number" --arg cn "$customer_number" --arg text "$inbound_text" --argjson ts "$((now - 120))" --arg id "wamid.SMOKE-IN-$suffix" '
  {object:"whatsapp_business_account",entry:[{id:"102290129340398",changes:[{field:"messages",value:{messaging_product:"whatsapp",
   metadata:{display_phone_number:$bn,phone_number_id:"106540352242922"},
   contacts:[{profile:{name:"Cliente Smoke"},wa_id:$cn}],
   messages:[{from:$cn,id:$id,timestamp:($ts|tostring),type:"text",text:{body:$text}}]}}]}]}' >"$inbound_payload"
jq -nc --arg bn "$business_number" --arg cn "$customer_number" --arg text "$outbound_text" --argjson ts "$((now - 60))" --arg id "wamid.SMOKE-OUT-$suffix" '
  {object:"whatsapp_business_account",entry:[{id:"102290129340398",changes:[{field:"smb_message_echoes",value:{messaging_product:"whatsapp",
   metadata:{display_phone_number:$bn,phone_number_id:"106540352242922"},
   message_echoes:[{from:$bn,to:$cn,id:$id,timestamp:($ts|tostring),type:"text",text:{body:$text}}]}}]}]}' >"$outbound_payload"

post_webhook() { # <secret> <payload file> [path]: prints the HTTP status
  printf 'X-Webhook-Secret: %s\n' "$1" | curl -s -o /dev/null -w '%{http_code}' --max-time 20 -H 'Content-Type: application/json' -H @- --data-binary "@$2" "$api_url${3:-$webhook_path}"
}

[ "$(post_webhook "$webhook_secret" "$inbound_payload")" = "200" ] && pass "webhook: inbound message accepted (200)" || fail "webhook: inbound message was not accepted"
[ "$(post_webhook "$webhook_secret" "$outbound_payload")" = "200" ] && pass "webhook: outbound echo accepted (200)" || fail "webhook: outbound echo was not accepted"
[ "$(post_webhook "$webhook_secret" "$inbound_payload")" = "200" ] && pass "webhook: a redelivered event is accepted idempotently (200)" || fail "webhook: a redelivery was rejected"
[ "$(post_webhook "wrong-$suffix" "$inbound_payload")" = "401" ] && pass "webhook: a wrong secret is rejected (401)" || fail "webhook: a wrong secret was not rejected with 401"
[ "$(post_webhook "$webhook_secret" "$inbound_payload" "/api/webhooks/360dialog/unknown-$suffix")" = "401" ] && pass "webhook: an unknown channel key is rejected (401)" || fail "webhook: an unknown channel key was not rejected with 401"

conversation_with_two_messages() {
  local list id
  list="$(json_get "$auth_headers" "$api_url/api/conversations")" || return 1
  id="$(printf '%s' "$list" | jq -r '.items[0].id // empty')"
  [ -n "$id" ] || return 1
  conversation_id="$id"
  [ "$(json_get "$auth_headers" "$api_url/api/conversations/$id" | jq '.messages | length')" -ge 2 ]
}
conversation_id=""
if wait_until 60 conversation_with_two_messages; then
  pass "the worker turned the deliveries into a conversation with both messages"
  detail="$(json_get "$auth_headers" "$api_url/api/conversations/$conversation_id")"
  inbound_count="$(printf '%s' "$detail" | jq '[.messages[] | select(.direction == "Inbound")] | length')"
  outbound_count="$(printf '%s' "$detail" | jq '[.messages[] | select(.direction == "Outbound")] | length')"
  if [ "$inbound_count" = "1" ] && [ "$outbound_count" = "1" ]; then pass "one inbound and one outbound message (the redelivery was not duplicated)"; else fail "messages: $inbound_count inbound, $outbound_count outbound"; fi
else
  fail "no conversation with two messages appeared within 60s"
fi

for endpoint in summary conversion first-response loss-reasons volume alerts; do
  status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 -H "@$auth_headers" "$api_url/api/metrics/$endpoint" || true)"
  if [ "$status" = "200" ]; then pass "GET /api/metrics/$endpoint -> 200"; else fail "GET /api/metrics/$endpoint -> $status"; fi
done
[ "$(code_of "$api_url/api/metrics/summary")" = "401" ] && pass "metrics without a token -> 401" || fail "metrics without a token did not return 401"

# ------------------------------------------------------------------ one request through the Web
echo
echo ">> Through the Web: sign in and read the conversation list (the Web calls the API)"
login_form_from() { # <login page html> <email> <password>: the urlencoded body of the first POST form of the page (hidden fields included)
  SMOKE_EMAIL="$2" SMOKE_PASSWORD="$3" python3 - "$1" <<'PY'
import os, sys, urllib.parse
from html.parser import HTMLParser

class Hidden(HTMLParser):
    def __init__(self):
        super().__init__()
        self.fields = []
        self.form = 0
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "form" and a.get("method", "").lower() == "post":
            self.form += 1
        if tag == "input" and a.get("type") == "hidden" and a.get("name") and self.form == 1:
            self.fields.append((a["name"], a.get("value", "")))

parser = Hidden()
parser.feed(open(sys.argv[1], encoding="utf-8").read())
parser.fields += [("Input.Email", os.environ["SMOKE_EMAIL"]), ("Input.Password", os.environ["SMOKE_PASSWORD"])]
print(urllib.parse.urlencode(parser.fields), end="")
PY
}

jar="$work/web.cookies"
curl -sS --max-time 15 -c "$jar" "$web_url/login" -o "$work/login.html"
login_form_from "$work/login.html" "$owner_email" "$owner_password" >"$work/login.form"
login_status="$(curl -s -o "$work/login.out" -w '%{http_code} %{redirect_url}' --max-time 20 -b "$jar" -c "$jar" --data-binary "@$work/login.form" "$web_url/login" || true)"
if [[ "$login_status" == 30[23]* ]]; then pass "web: POST /login signs in through the API (${login_status%% *})"; else fail "web: POST /login -> $login_status"; fi
panel_code="$(code_of -b "$jar" "$web_url/panel")"
[ "$panel_code" = "200" ] && pass "web: /panel renders for the signed-in user (200)" || fail "web: /panel -> $panel_code"
conversations_html="$(curl -sS --max-time 20 -b "$jar" "$web_url/conversaciones" || true)"
if grep -q 'Cliente Smoke' <<<"$conversations_html"; then pass "web: /conversaciones lists the conversation read from the API"; else fail "web: /conversaciones does not show the conversation"; fi
[ "$(code_of "$web_url/panel")" != "200" ] && pass "web: /panel is not served without a session" || fail "web: /panel is served without a session"

# ------------------------------------------------------------------ Data Protection keys survive a restart of the Web
echo
echo ">> Recreating only the Web: a form opened before the restart must still be accepted"
# The Web protects its antiforgery tokens (and the sign-in cookie) with Data Protection keys. They live in the web-keys volume;
# without it a new container generates new keys and every form opened before fails with an antiforgery 400. The container is
# RECREATED, not just restarted: `docker compose restart` keeps the container's file system, so the default key folder would
# survive it and the check could not tell a persisted key ring from a lucky one. `up --build` recreates, which is the real case.
jar_before="$work/web-before-restart.cookies"
curl -sS --max-time 15 -c "$jar_before" "$web_url/login" -o "$work/login-before-restart.html"
login_form_from "$work/login-before-restart.html" "$owner_email" "wrong-$owner_password" >"$work/login-before-restart.form"
if [ -s "$work/login-before-restart.form" ] && [ "$(tr '&' '\n' <"$work/login-before-restart.form" | awk -F= '$1 == "__RequestVerificationToken"' | wc -l | tr -d ' ')" = "1" ]; then
  pass "web: the login page carries an antiforgery token and cookie before the restart"
else
  fail "web: the login page carries no antiforgery token"
fi
web_container_before="$(cid web)"
dc up -d --no-build --no-deps --force-recreate web >/dev/null 2>&1 || true
if [ -n "$(cid web)" ] && [ "$(cid web)" != "$web_container_before" ] && wait_until 90 is_healthy web; then pass "web container was recreated and is healthy again"; else fail "web container was not recreated or did not come back healthy ($(health_of web))"; fi
resubmit_status="$(curl -s -o "$work/login-after-restart.html" -w '%{http_code}' --max-time 20 -b "$jar_before" --data-binary "@$work/login-before-restart.form" "$web_url/login" || true)"
if [ "$resubmit_status" = "400" ]; then
  fail "web: the form opened before the restart was rejected (400, antiforgery): the Data Protection keys were not persisted"
elif [[ "$resubmit_status" == 2* || "$resubmit_status" == 30[23] ]]; then
  pass "web: a form opened before the restart is still accepted afterwards ($resubmit_status)"
else
  fail "web: the form opened before the restart got an unexpected answer ($resubmit_status)"
fi
if dc exec -T web sh -c 'ls /keys/key-*.xml' >/dev/null 2>&1; then pass "web: the Data Protection key ring is stored in the /keys volume"; else fail "web: no key file in /keys"; fi

# ------------------------------------------------------------------ logs
echo
echo ">> Logs"
dc logs --no-color api web migrator >"$work/logs.txt" 2>&1 || true
log_lines="$(wc -l <"$work/logs.txt" | tr -d ' ')"
note "$log_lines log lines captured"
if ! grep -E '(^|\| )fail: |Unhandled exception|\|\s+at [A-Za-z]' "$work/logs.txt" >"$work/log-errors.txt"; then
  pass "no Error-level entries or stack traces in the API, Web and migrator logs"
else
  # On an empty database the EF Core bundle first reads __EFMigrationsHistory, which does not exist yet, and logs that failed read at
  # Error level once. It is expected and harmless, so it is the one entry allowed (and only from the migrator).
  expected_fail="$(grep -c '^migrator-1 *| fail: Microsoft.EntityFrameworkCore.Database.Command' "$work/log-errors.txt" || true)"
  total_fail="$(wc -l <"$work/log-errors.txt" | tr -d ' ')"
  if [ "$expected_fail" = "1" ] && [ "$total_fail" = "1" ] && grep -q '__EFMigrationsHistory' "$work/logs.txt"; then
    pass "no Error-level entries or stack traces (the migrator's expected first read of __EFMigrationsHistory aside)"
  else
    fail "error-level entries or stack traces in the logs:"
    head -n 8 "$work/log-errors.txt" | cut -c 1-220 | sed 's/^/        /'
  fi
fi
leaks=0
for needle in "$customer_number" "$business_number" "$inbound_text" "$outbound_text" "Cliente Smoke"; do
  if grep -qF -- "$needle" "$work/logs.txt"; then leaks=$((leaks + 1)); fi
done
if [ "$leaks" -eq 0 ]; then pass "no phone number, contact name or message text in the logs"; else fail "$leaks personal value(s) from the sample conversation appear in the logs"; fi

# ------------------------------------------------------------------ PostgreSQL goes away
echo
echo ">> Failure mode: PostgreSQL stops"
api_container="$(cid api)"
restarts_before="$(docker inspect --format '{{.RestartCount}}' "$api_container")"
dc stop postgres >/dev/null 2>&1
unhealthy_api() { [ "$(code_of "$api_url/health")" = "503" ]; }
if wait_until 90 unhealthy_api; then pass "api GET /health -> 503 while PostgreSQL is down"; else fail "api /health did not turn unhealthy without PostgreSQL ($(code_of "$api_url/health"))"; fi
[ "$(body_of "$web_url/health")" = "Healthy" ] && pass "web /health stays Healthy (liveness does not depend on PostgreSQL)" || fail "web /health changed while PostgreSQL was down"
if [ "$quick" -eq 0 ]; then
  unhealthy_container() { [ "$(health_of api)" = "unhealthy" ]; }
  if wait_until 120 unhealthy_container; then pass "Docker flags the api container as unhealthy"; else fail "the api container was never flagged unhealthy ($(health_of api))"; fi
fi
running="$(docker inspect --format '{{.State.Running}}' "$api_container")"
restarts_after="$(docker inspect --format '{{.RestartCount}}' "$api_container")"
if [ "$running" = "true" ] && [ "$restarts_before" = "$restarts_after" ]; then pass "the api container did not crash or restart-loop"; else fail "the api container running=$running restarts $restarts_before -> $restarts_after"; fi
dc start postgres >/dev/null 2>&1
recovered() { [ "$(body_of "$api_url/health")" = "Healthy" ]; }
if wait_until 120 recovered; then pass "api /health recovers to Healthy when PostgreSQL returns"; else fail "api /health did not recover"; fi
if wait_until 90 is_healthy api; then pass "the api container is healthy again"; else fail "the api container did not recover ($(health_of api))"; fi

dc logs --no-color >"$work/logs-final.txt" 2>&1 || true
if ! grep -qFf "$secrets" "$work/logs-final.txt"; then pass "no secret of this run appears in any container log"; else fail "a secret of this run appears in a container log"; fi

# ------------------------------------------------------------------ summary
total=$((SECONDS - started))
echo
echo "Images:"
for image in "$api_image" "$web_image" "$migrator_image"; do
  printf '  %-34s %s\n' "$image" "$(docker image ls --format '{{.Size}}' "$image")"
done
echo "Build ${build_seconds}s, stack ready ${run_wait_seconds}s after start, total ${total}s"
echo "Result: $passed passed, $failed failed"
[ "$failed" -eq 0 ]
