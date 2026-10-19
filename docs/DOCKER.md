# Container images

Conversia ships as three images built from two Dockerfiles. Every command runs from the repository root, which is the build context.

| Image | Dockerfile / target | What it is |
|---|---|---|
| `conversia-api` | `src/Conversia.API/Dockerfile` (default target `final`) | ASP.NET Core API: webhook ingestion, classification, metrics. Listens on 8080. |
| `conversia-migrator` | `src/Conversia.API/Dockerfile`, `--target migrator` | One-shot job: a self-contained EF Core migrations bundle that applies the migrations and exits. |
| `conversia-web` | `src/Conversia.Web/Dockerfile` | Blazor Web dashboard. Only talks to the API over HTTP. Listens on 8080. |

They stay three images on purpose: the migrator runs once per release with database rights and exits, the API and the Web scale and restart on their own, and the Web never needs a database connection.

## Build

```bash
scripts/docker-build.sh                                   # the three images, tag "local"
scripts/docker-build.sh --tag 1.4.0 --platform linux/amd64
```

The script fills the version labels from git (`git describe --tags --always --dirty`, `git rev-parse HEAD`; both optional) and prints each image with its size. Nothing is pushed. The equivalent single commands:

```bash
docker build -f src/Conversia.API/Dockerfile -t conversia-api .
docker build -f src/Conversia.API/Dockerfile --target migrator -t conversia-migrator .
docker build -f src/Conversia.Web/Dockerfile -t conversia-web .
```

The build context is small (about 0.5 MB of sources) because `.dockerignore` excludes tests, tools, docs, scripts, build output, markdown and every `.env*` file except `.env.example`. Keep it strict: anything not needed by a `COPY` should not be sent to the daemon.

### Versioning and labels

| Build arg | Default | Label |
|---|---|---|
| `APP_VERSION` | `0.0.0-local` | `org.opencontainers.image.version` |
| `GIT_SHA` | `unknown` | `org.opencontainers.image.revision` |
| `BUILD_DATE` | `unknown` | `org.opencontainers.image.created` |
| `SOURCE_URL` | the GitHub repository | `org.opencontainers.image.source` |

`org.opencontainers.image.title` and `description` are fixed per image. Labels are metadata only, so changing them re-runs a single cheap layer. Compose names the images `conversia-api|web|migrator:${CONVERSIA_TAG:-local}` and takes the build args from `CONVERSIA_VERSION`, `CONVERSIA_GIT_SHA` and `CONVERSIA_BUILD_DATE`. Release tag convention: tag every image of a release with the same version (`1.4.0`) and also record the commit in the revision label.

### Multi-arch

The images build for `linux/arm64` and `linux/amd64` (Azure usually runs amd64; development machines are often Apple Silicon):

```bash
scripts/docker-build.sh --platform linux/amd64 --tag amd64-test
```

`--platform` uses `docker buildx build --load`, so one architecture is built and loaded per run. The migrator bundle is self-contained, so its runtime identifier must match the image architecture: the Dockerfile maps BuildKit's `TARGETARCH` (`amd64` -> `linux-x64`, `arm64` -> `linux-arm64`) and fails on anything else. Building the other architecture on a different host runs the SDK under emulation and is several times slower. Publishing a multi-platform manifest to a registry is not configured (see Known limitations).

## Run

```bash
cp .env.example .env                 # set POSTGRES_PASSWORD and JWT_SIGNING_KEY
docker compose up -d --build         # postgres -> migrator -> api -> web
```

Start order is enforced with health: the migrator waits for `postgres` to be healthy, the API waits for the migrator to finish successfully, and the Web waits for the API to be healthy. To run an image alone:

```bash
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="$CONNECTION" -e Jwt__SigningKey="$KEY" conversia-api
docker run --rm conversia-migrator --connection "$CONNECTION"
docker run --rm -p 8081:8080 -e Api__BaseUrl=http://host.docker.internal:8080/ conversia-web
```

### Environment

Nothing secret is baked into an image; everything below is supplied at run time.

| Variable | Used by | Required | Notes |
|---|---|---|---|
| `ConnectionStrings__DefaultConnection` | api | yes | Npgsql connection string. Add `GSS Encryption Mode=Disable`: the images ship no Kerberos libraries. |
| `Jwt__SigningKey` | api | yes | At least 32 bytes. The API refuses to start without it. The migrator gets a throwaway value (it boots the API host to find the `DbContext` and never signs a token). |
| `ErrorLogging__ReportKey` | api | no | At least 32 characters. Unset: the error report endpoint is off. Must equal the Web's `ErrorReporting__ApiKey`. |
| `Dialog360__ApiKey` | api | no | Passed through; nothing calls 360dialog yet. |
| `Api__BaseUrl` | web | yes | Base URL of the API as seen from the Web container. |
| `ErrorReporting__ApiKey` | web | no | See `ErrorLogging__ReportKey`. |
| `ASPNETCORE_ENVIRONMENT` | api, web | no | Defaults to Production. `Demo` is for the demo overlay only. |
| `ASPNETCORE_HTTP_PORTS` | api, web | no | Defaults to 8080 in the images. |
| `--connection` (argument) | migrator | yes | Same connection string as the API. |

Compose-level variables (`.env`, see `.env.example`): `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `JWT_SIGNING_KEY`, `ERROR_REPORT_KEY`, `D360_API_KEY`, `API_PORT`, `WEB_PORT`, `POSTGRES_PORT`, `CONVERSIA_TAG`, `CONVERSIA_VERSION`, `CONVERSIA_GIT_SHA`, `CONVERSIA_BUILD_DATE`.

## Health checks

| Image | Endpoint | Meaning |
|---|---|---|
| api | `GET /health` | The webhook queue can be read from PostgreSQL. `Healthy` and `Degraded` (old pending events, many failures) answer `200`; `Unhealthy` (database unreachable) answers `503`. |
| web | `GET /health` | The process is serving. Liveness only: it does not call the API, so a database outage does not take the Web out of rotation. |

Both are anonymous and return just the status word. The `HEALTHCHECK` lives in the image (interval 15 s, timeout 5 s, start period 20-30 s, 3 retries), so `docker run` and compose behave the same and `depends_on: condition: service_healthy` works without extra configuration.

**The probe.** The runtime image has no `curl` or `wget`. `docker/healthcheck.sh` opens a TCP connection with bash's built-in `/dev/tcp`, sends `GET /health` and requires an `HTTP/1.x 200` status line. Reasons: bash is already in the image (no package to install or patch, no larger attack surface), it runs as the unprivileged app user, it costs a few milliseconds instead of starting a second .NET runtime every 15 seconds, and reading the status line catches a hung or failing app that a bare port check would miss. If the base image ever changes to one without bash (a distroless or chiseled variant), replace the probe with a small dotnet-based one; the smoke test fails if the probe stops working.

A failing health check does not restart a container by itself (Docker only reports it). In production the orchestrator should use the same endpoint: liveness against the Web, readiness against the API.

## Smoke test

`scripts/docker-smoke-test.sh` is the acceptance test of the images. It needs `docker` (compose >= 2.24), `curl`, `jq`, `openssl` and `python3`, and takes about 80 seconds on a warm build cache (a cold native build adds roughly 30-40 seconds; the amd64 build under emulation on Apple Silicon took about 160 seconds).

```bash
scripts/docker-smoke-test.sh            # build, test, tear everything down
scripts/docker-smoke-test.sh --keep     # leave the stack running; prints URLs, never secrets
scripts/docker-smoke-test.sh --quick    # skip waiting for Docker to flag the API as unhealthy
```

It builds the three images with a throwaway tag (`smoke-<random>`), starts its own compose project on free loopback-only ports with random per-run secrets kept in a mode-600 temp directory outside the repository, and prints one `PASS`/`FAIL` line per check. The exit status is non-zero if anything failed. It checks:

- labels, `HEALTHCHECK` and a non-root user on all three images; no secret of the run in `docker history` or `docker image inspect`;
- PostgreSQL healthy, migrator exit 0, and the number of applied migrations equal to the migration classes in the repository;
- API and Web `/health`, container health `healthy`, `/` and `/login` render, every CSS and JavaScript asset the login page references (including `blazor.web.js`) answers 200 with the right content type and an immutable cache header;
- non-root and read-only `/app` in the running containers, secrets present only as run-time environment;
- end to end: register a tenant, log in, create a branch, an advisor and a channel, post an inbound message and an echo to the webhook with the secret returned once, a redelivery (idempotent), a wrong secret and an unknown key (401), wait for the conversation, all six metrics endpoints, and a sign-in and conversation list through the Web;
- logs: no Error-level entries or stack traces (the migrator's expected first read of a missing `__EFMigrationsHistory` aside), no phone number, contact name or message text, no secret;
- failure mode: with PostgreSQL stopped the API `/health` answers 503 and Docker marks the container unhealthy without a crash loop; the Web stays healthy; everything recovers when PostgreSQL returns.

It only creates and removes resources of its own project (`down -v --rmi local` plus its own tags) and never reads the repository's `.env`. CI runs it as the `docker` job of `.github/workflows/tests.yml`.

## Security properties

- All three images run as the non-root `app` user (uid 1654); `/app` is owned by root and is not writable by the process.
- No secret is baked in. Secrets arrive as environment variables or arguments at run time; the smoke test searches the image metadata for them.
- The build context is strict (`.dockerignore`), so `.env` files, tests, docs and tools never reach the daemon or an image layer.
- The healthcheck script is root-owned, mode 0555, and only talks to localhost.
- Containers listen on plain HTTP (8080). Terminate TLS in a reverse proxy and configure forwarded headers (see `docs/WEBHOOKS.md`).
- The EF Core bundle is built with a placeholder signing key that exists only for that single build command.

## Known limitations

- No registry push, image signing or SBOM/provenance configuration; the build script loads images into the local daemon only.
- Images are not scanned for vulnerabilities. Next step: `docker scout cves` or Trivy in CI (not installed here).
- The base images (`mcr.microsoft.com/dotnet/aspnet:10.0`, `sdk:10.0`, `runtime-deps:10.0`) use floating tags; pin digests for reproducible releases.
- Images are large (about 370-400 MB) because they use the full Ubuntu-based runtime and ship debug symbols; a chiseled base and `--no-symbols` publish would shrink them.
- The migrator receives the database password in its command line (visible in `docker inspect` of that container). Acceptable for local compose; use a secret store or Key Vault in production.
- `docker compose up` waits for the API to be healthy before starting the Web, so a slow API start delays the Web.
- The smoke test builds on the host architecture only; the amd64 build is verified separately (see Multi-arch).
