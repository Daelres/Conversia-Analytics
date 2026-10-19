#!/usr/bin/env bash
# Container liveness probe: GET <path> on the local Kestrel port, healthy only on HTTP 200.
#
#   healthcheck.sh [/health]
#
# The aspnet runtime image has no curl or wget, so this uses bash's built-in /dev/tcp
# (no extra package, no extra process, works as the unprivileged app user). The port is
# the first entry of ASPNETCORE_HTTP_PORTS (8080 by default). A pure TCP connect would
# also pass while the app is hung or failing its checks, so the status line is read.
set -u

path="${1:-/health}"
port="${ASPNETCORE_HTTP_PORTS:-8080}"
port="${port%%[;,]*}"

exec 3<>"/dev/tcp/127.0.0.1/${port}" || exit 1
printf 'GET %s HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' "$path" >&3 || exit 1
IFS= read -r -t 3 status <&3 || exit 1
[[ "$status" == HTTP/1.?\ 200* ]]
