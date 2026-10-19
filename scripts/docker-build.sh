#!/usr/bin/env bash
# Builds the three Conversia images: conversia-api, conversia-migrator and conversia-web.
#
#   scripts/docker-build.sh [--tag TAG] [--platform linux/amd64|linux/arm64] [--no-cache]
#
# --tag        image tag (default: $CONVERSIA_TAG, else "local")
# --platform   build for another architecture through buildx (default: the Docker host's)
# --no-cache   ignore the build cache
#
# Version metadata (image labels only, never secrets) comes from git when it is available:
#   APP_VERSION  `git describe --tags --always --dirty`, else 0.0.0-local   (override: APP_VERSION=1.2.3)
#   GIT_SHA      `git rev-parse HEAD`, else unknown
# The images are loaded into the local Docker store; nothing is pushed.
set -euo pipefail

tag="${CONVERSIA_TAG:-local}"
platform=""
no_cache=()

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --tag) tag="${2:?--tag needs a value}"; shift 2 ;;
    --platform) platform="${2:?--platform needs a value}"; shift 2 ;;
    --no-cache) no_cache=(--no-cache); shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$platform" in
  ""|linux/amd64|linux/arm64) ;;
  *) echo "Unsupported platform: $platform (use linux/amd64 or linux/arm64)" >&2; exit 2 ;;
esac

cd "$(dirname "$0")/.."

app_version="${APP_VERSION:-}"
git_sha="${GIT_SHA:-}"
if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
  [ -n "$app_version" ] || app_version="$(git describe --tags --always --dirty 2>/dev/null || true)"
  [ -n "$git_sha" ] || git_sha="$(git rev-parse HEAD 2>/dev/null || true)"
fi
app_version="${app_version:-0.0.0-local}"
git_sha="${git_sha:-unknown}"
build_date="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

build_cmd=(docker build)
if [ -n "$platform" ]; then
  build_cmd=(docker buildx build --platform "$platform" --load)
fi

build_image() { # <image name> <dockerfile> [extra build args...]
  local name="$1" dockerfile="$2"
  shift 2
  echo ">> Building $name:$tag (version $app_version, revision ${git_sha:0:12}${platform:+, $platform})"
  "${build_cmd[@]}" ${no_cache[@]+"${no_cache[@]}"} \
    --file "$dockerfile" \
    --build-arg "APP_VERSION=$app_version" \
    --build-arg "GIT_SHA=$git_sha" \
    --build-arg "BUILD_DATE=$build_date" \
    --tag "$name:$tag" \
    "$@" .
}

started=$SECONDS
build_image conversia-api src/Conversia.API/Dockerfile
build_image conversia-migrator src/Conversia.API/Dockerfile --target migrator
build_image conversia-web src/Conversia.Web/Dockerfile

echo
echo "Built in $((SECONDS - started))s:"
for name in conversia-api conversia-migrator conversia-web; do
  # `docker image ls` reports the unpacked (disk) size, which is the number that matters on the host.
  size="$(docker image ls --format '{{.Size}}' "$name:$tag")"
  printf '  %-40s %s\n' "$name:$tag" "$size"
done
