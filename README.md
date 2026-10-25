# Conversia

Conversia reads the WhatsApp sales conversations between advisors and customers in the
background and turns them into metrics and an executive dashboard, so the managers of a
small or medium company can see why customers do not buy. Advisors keep answering
manually; Conversia only reads, classifies, stores and analyzes. It does not reply to
anyone.

> **Test version.** This is the pilot under construction (first customer: a laser
> hair-removal company with four branches). Behavior, screens and data may change, and
> it is not a certified product. Open questions and scope live in [`CLAUDE.md`](CLAUDE.md).

Provenance: the codebase started from an internal .NET template and has since been
adapted to PostgreSQL and rewritten around this product.

## What it does

- **Ingestion** — receives the incoming and outgoing messages of a WhatsApp Business number
  through the webhook of the provider (360dialog), stores them idempotently and processes
  them in a background worker ([`docs/WEBHOOKS.md`](docs/WEBHOOKS.md)).
- **Classification** — a rule-based classifier (Spanish rules, no AI model) reads each
  conversation and extracts the service asked about, the reason for interest, the
  objection and the outcome: won, lost or in follow-up
  ([`docs/CLASSIFICATION.md`](docs/CLASSIFICATION.md)).
- **Metrics** — conversion by branch, advisor and source; first-response time;
  unanswered conversations; top reasons for not closing; volume by day and hour; and
  alerts ([`docs/METRICS.md`](docs/METRICS.md)).
- **Executive dashboard** — `/panel` shows six headline numbers, alerts and the breakdowns.
  `/conversaciones` lists the chats and shows each thread with its classification;
  `/mensajes` ("Mensajes recibidos") and `/mensajes/eventos` ("Entregas del webhook") show
  what the webhook received, to check that messages are arriving;
  `/organizacion` manages branches, advisors and WhatsApp numbers (and shows the lines
  used out of the company's quota); `/users` manages users and their permissions.
  A company that is still pending or suspended sees `/cuenta-pendiente` instead.
- **Platform administration** — `/admin/login` is the administrator's own sign-in;
  `/admin` lists the companies, `/admin/empresas/{id}` approves, suspends and sets the line
  quota of one, and `/admin/auditoria` shows every change
  ([`docs/PLATFORM_ADMIN.md`](docs/PLATFORM_ADMIN.md)).
- **Data separation** — every record belongs to a company (tenant); access is controlled
  per company and per permission. The design takes the Colombian data-protection law
  (Ley 1581 de 2012) into account; the project makes no certification claim.
- **Spanish UI** — the interface and the messages the API returns to users are in Spanish;
  code, logs, configuration keys, routes and JSON property names are in English.
- **Not included** — automatic replies or a chatbot, integrations with external CRM/ERP
  systems, and a mobile app.

## Stack

- .NET 10 (LTS)
- ASP.NET Core Web API (Controllers) + Blazor Web App (static server-side
  rendering, cookie sign-in) as the front end
- EF Core 10 + PostgreSQL 17 (Npgsql)
- xUnit, Moq (unit tests), Testcontainers.PostgreSql (integration tests —
  real PostgreSQL in Docker, spun up and torn down per test run)
- GitHub Actions (`.github/workflows/tests.yml`) builds the solution and
  runs all six test projects (and the Docker smoke test) on every push/PR to `main`

## Solution layout

Four layers plus the web front end, a background worker inside the API, and a
development-only simulator:

```
WhatsApp -> 360dialog -> webhook (API) -> stored events -> background worker
                                  |                              |
                                  v                              v
                             PostgreSQL <---- classification (rules) ----> metrics API -> Web dashboard
```

```
src/
  Conversia.Domain          entities, enums, domain interfaces — no dependencies
  Conversia.Application     use-case services, DTOs, validation — depends on Domain
  Conversia.Infrastructure  EF Core, repositories, JWT, auth handler — depends on Domain + Application
  Conversia.API             ASP.NET Core Web API — depends on all three
  Conversia.Web             Blazor front end — depends on Application only, talks to the API over HTTP
tests/
  Conversia.Domain.UnitTests
  Conversia.Application.UnitTests
  Conversia.Infrastructure.UnitTests   permission authorization handler
  Conversia.IntegrationTests           the API against real PostgreSQL (Testcontainers)
  Conversia.Web.IntegrationTests       the Web against the real API + PostgreSQL (Testcontainers)
  Conversia.WebhookSimulator.UnitTests the simulator's chats, plan, payloads and commands (no network)
tools/
  Conversia.WebhookSimulator           dev-only: fills a local API with believable WhatsApp conversations (docs/SIMULATOR.md)
docs/                                    ARCHITECTURE, WEBHOOKS, METRICS, CLASSIFICATION, SIMULATOR, DOCKER, ...
docker-compose.yml, .env.example         full local stack
docker/healthcheck.sh                    container health probe (bash /dev/tcp, no curl in the image)
scripts/docker-build.sh                  builds the conversia-api, -migrator and -web images
scripts/docker-smoke-test.sh             image acceptance test: builds and exercises the real stack
```

Read `docs/ARCHITECTURE.md` before adding anything — it defines the
dependency direction and per-layer conventions of the project. Background work (webhook
event processing, classification, closing idle conversations, retention) runs in hosted
services inside the API process.

## Quick start with Docker Compose

Prerequisite: Docker with Compose v2. Nothing else is needed — no .NET SDK.

```bash
cp .env.example .env     # then edit the required secrets
docker compose up -d --build
```

- `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` — database, user and
  password of the PostgreSQL container; the API and the migrator get the
  same values in their connection string. Avoid `;`, `=` and `"` in the
  password.
- `JWT_SIGNING_KEY` — at least 32 bytes (`openssl rand -base64 48`).
- Compose fails immediately with a message if any of them is missing. `.env`
  is git-ignored.

Services: `postgres` (`postgres:17-alpine`, with a `pg_isready` healthcheck
and the `postgres-data` volume), `migrator` (one-shot job that applies the EF Core migrations
through a migrations bundle and exits), `api` and `web`. The API and Web
start only after the migrator completed successfully.

| What | URL |
|---|---|
| Web | http://localhost:5112 |
| API | http://localhost:5030 |
| PostgreSQL | `127.0.0.1:5432` (`POSTGRES_USER` / `POSTGRES_PASSWORD`) |

Host ports can be changed with `WEB_PORT`, `API_PORT` and `POSTGRES_PORT` in
`.env`. Open the Web, register a company and sign in. A new company is
**Pending** and cannot register or activate WhatsApp numbers until a platform
administrator activates it and sets its line quota: add `PLATFORM_ADMIN_EMAIL` and
`PLATFORM_ADMIN_PASSWORD` to `.env` (not in `.env.example` yet) before the first
start and use `/admin/login` ([`docs/PLATFORM_ADMIN.md`](docs/PLATFORM_ADMIN.md)).
Stop with `docker compose down`; add `-v` to also delete the database volume.

To test with real WhatsApp messages through the 360dialog sandbox, follow
[`docs/PRUEBAS_SANDBOX.md`](docs/PRUEBAS_SANDBOX.md) (in Spanish): step by step, with an
acceptance checklist. The 360dialog setup for sandbox and production is summarized in
[`docs/CONFIGURACION_360DIALOG.md`](docs/CONFIGURACION_360DIALOG.md) (in Spanish).

To fill the stack with believable WhatsApp conversations (branches, advisors, numbers
and chats the classifier and the dashboard can show), use the webhook simulator:
[`docs/SIMULATOR.md`](docs/SIMULATOR.md). If a port is already in use, you get
`password authentication failed`, or forms fail after a restart, see
[Docker troubleshooting](docs/DOCKER.md#troubleshooting).

The images, their build arguments and labels, health checks, multi-arch builds
and the smoke test (`scripts/docker-smoke-test.sh`) are described in
[docs/DOCKER.md](docs/DOCKER.md).

Notes: the containers listen on plain HTTP (port 8080 inside), so put a
TLS-terminating reverse proxy in front for anything beyond local use.
Behind a proxy or load balancer, configure the API's forwarded headers with
trusted proxies: otherwise the per-IP rate limits (login and the error
report endpoint) see the proxy's IP, so all clients, including the Web's
error reports, share one budget (see [docs/ERRORS.md](docs/ERRORS.md)).
The Web's auth cookie keys persist in the `web-keys` volume, so restarting `web`
keeps users signed in. The Docker builds restore NuGet packages from nuget.org, so
the build needs internet access.

## Quick start without Docker (for development)

Prerequisites: .NET 10 SDK and a PostgreSQL 17 you can reach (Docker is
needed for the integration tests; `docker compose up -d postgres` is the
quickest way to get a database).

```bash
dotnet restore
dotnet build
```

Configure the API (user secrets or environment variables, never in
committed files):

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=conversia;Username=conversia;Password=<your password>" --project src/Conversia.API
dotnet user-secrets set "Jwt:SigningKey" "<random-secret-of-32+-bytes>" --project src/Conversia.API
# or: export Jwt__SigningKey="<random-secret-of-32+-bytes>"
```

Apply migrations:

```bash
dotnet ef database update --project src/Conversia.Infrastructure --startup-project src/Conversia.API
```

Run the API (https://localhost:7199, http://localhost:5030):

```bash
dotnet run --project src/Conversia.API --launch-profile https
```

Run the Web app (https://localhost:7221, http://localhost:5112) in a second
terminal; it needs the API to be running:

```bash
dotnet run --project src/Conversia.Web --launch-profile https
```

The Web's default `Api:BaseUrl` is the API's https launch URL. Trust the
dev certificate once with `dotnet dev-certs https --trust`, then open
https://localhost:7221, register a company and sign in. Use an https
`Api:BaseUrl` wherever the API redirects http to https (as it does with
the https launch profile), because the bearer token would not survive
the redirect.

## Configuration reference

Every key can be set in `appsettings*.json`, user secrets, or as an
environment variable (`:` becomes `__`, e.g. `Jwt__SigningKey`).

| Key | App | Default | Description |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | API | `Host=localhost;Port=5432;Database=conversia;...` (placeholder) | PostgreSQL (Npgsql) connection string. Set it per environment. |
| `Jwt:SigningKey` | API | placeholder (rejected at startup) | HS256 signing secret, at least 32 bytes (UTF-8). Required. |
| `Jwt:Issuer` | API | `Conversia` | Token issuer, validated on every request. |
| `Jwt:Audience` | API | `Conversia` | Token audience, validated on every request. |
| `PlatformAdmin:Email` | API | empty | With `PlatformAdmin:Password`, creates the first platform administrator at startup when none has that email (never overwrites one). Empty = no bootstrap. Compose: `PLATFORM_ADMIN_EMAIL`. `docs/PLATFORM_ADMIN.md`. |
| `PlatformAdmin:Password` | API | empty | At least 12 characters, not a template placeholder, otherwise the API will not start (the error names the key only). Compose: `PLATFORM_ADMIN_PASSWORD`. It is only needed until the account exists: **remove it from your `.env` after the first start** (the API forgets it in memory right after the bootstrap, but the container's environment keeps it as long as the variable is set). |
| `PlatformAdmin:PasswordFile` | API | empty | Instead of `PlatformAdmin:Password`: the path of a file that holds the password (for example a Docker secret mounted at `/run/secrets/...`); a trailing newline is dropped. Setting both is a startup error, and so is a file that is unreadable or empty (the error names the key, never the path). Compose: `PLATFORM_ADMIN_PASSWORD_FILE`. |
| `PlatformAdmin:TokenMinutes` | API | `60` | Lifetime of the administrator's token (1 to 720). |
| `RateLimiting:Login:PermitLimit` | API | `10` | Login requests allowed per IP per window. Whole number >= 1, otherwise the API will not start. |
| `RateLimiting:Login:WindowSeconds` | API | `60` | Length of the login rate-limit window in seconds. Whole number >= 1. |
| `ErrorLogging:Enabled` | API | `true` | Store unexpected errors. `false`: the code is still generated, returned and logged, nothing is stored. `true`/`false` only, otherwise the API will not start. |
| `ErrorLogging:RetentionDays` | API | `90` | Error rows older than this are purged. 1 to 3650. |
| `ErrorLogging:MaxPerFingerprintPerMinute` | API | `20` | Rows stored per defect and minute; beyond it an error still gets a code and a log entry. Whole number >= 1. |
| `ErrorLogging:PersistTimeoutSeconds` | API | `3` | How long a failing request waits for its error row to be written. 1 to 30. |
| `ErrorLogging:ReportRateLimitPerMinute` | API | `60` | Reports per client IP and minute accepted by `POST /api/diagnostics/errors`. Whole number >= 1. |
| `ErrorLogging:ReportKey` | API | *(unset)* | Shared secret (>= 32 characters) of `POST /api/diagnostics/errors`; **unset turns the endpoint off** (`404`). Set it as a secret, never in a committed file. |
| `Dialog360:ApiKey` | API | *(empty)* | The 360dialog API key. Bound but **not used yet**: nothing calls 360dialog in this phase. In compose it comes from `D360_API_KEY`. Set it as a secret, never in a committed file. |
| `Webhooks:MaxBodyBytes` | API | `1048576` | Largest accepted body of `POST /api/webhooks/360dialog/{channelKey}`; more is `413`. 1024 to 16777216. |
| `Webhooks:FailedAuthRateLimitPerMinute` | API | `60` | Failed webhook authentications per client IP and minute; beyond it the address gets `429` before being authenticated. Whole number >= 1. |
| `Webhooks:ChannelRateLimitPerMinute` | API | `6000` | Authenticated deliveries per channel and minute; beyond it `429`. Whole number >= 1. |
| `ForwardedHeaders:KnownProxies:N` | API | *(none: off)* | Addresses of the reverse proxies whose `X-Forwarded-For` is trusted (one hop). With neither this nor `KnownNetworks` set the header is ignored. |
| `ForwardedHeaders:KnownNetworks:N` | API | *(none)* | CIDR ranges (`10.0.0.0/8`) of trusted proxies. Invalid entries stop the API at startup. |
| `Webhooks:Processing:Enabled` | API | `true` | Runs the background worker that processes stored webhook events. `false`: events are stored and stay pending. |
| `Webhooks:Processing:PollIntervalMilliseconds` | API | `2000` | Pause of the worker when the queue is empty. 10 to 600000. |
| `Webhooks:Processing:BatchSize` | API | `20` | Events processed per cycle before the worker looks at the clock again. 1 to 1000. |
| `Webhooks:Processing:MaxAttempts` | API | `8` | Attempts after which an event is marked `Failed` (counted when the event is claimed). 1 to 100. |
| `Webhooks:Processing:RetryBaseDelaySeconds` | API | `30` | Delay before the first retry; it doubles each time (cap 6 h). 0 to 86400. |
| `Webhooks:Processing:ProcessingTimeoutSeconds` | API | `60` | `statement_timeout`, `idle_in_transaction_session_timeout` and total budget of processing one event; the lease is twice this plus 10 s. 1 to 3600. |
| `Webhooks:Processing:MaxMessagesPerDelivery` | API | `500` | A delivery with more messages is `Failed` at once, without retries. 1 to 100000. |
| `Webhooks:Processing:TenantBusyDelaySeconds` | API | `10` | Wait (jittered) of an event whose tenant another worker is processing; it does not use up an attempt. 1 to 3600. |
| `Webhooks:Processing:TenantLockWaitMilliseconds` | API | `2000` | How long a worker spins trying to get the tenant lock before deferring the event. 0 to 60000. |
| `Webhooks:Retention:Enabled` | API | `true` | Runs the purge of old webhook events (they hold customers' phone numbers and message text). |
| `Webhooks:Retention:RetentionDays` | API | `14` | Processed webhook events older than this are deleted. 1 to 3650. |
| `Webhooks:Retention:FailedRetentionDays` | API | `60` | Failed webhook events older than this are deleted (kept longer to investigate and requeue). 1 to 3650. |
| `Webhooks:Health:MaxPendingAgeMinutes` | API | `10` | `GET /health` reports Degraded when the oldest pending event has waited longer. 1 to 10080. |
| `Webhooks:Health:MaxFailedPerHour` | API | `5` | `GET /health` reports Degraded when more events than this became Failed in the last hour. Whole number >= 0. |
| `Conversations:AutoCloseEnabled` | API | `true` | Runs the job that closes conversations nobody has written in for a while. |
| `Conversations:AutoCloseAfterDays` | API | `7` | A conversation whose last message is older than this is closed (the customer's next message then opens a new one). 1 to 365. |
| `Conversations:AutoCloseIntervalMinutes` | API | `15` | How often the job runs (it also runs once at startup). 1 to 1440. |
| `Conversations:AutoCloseBatchSize` | API | `200` | Conversations closed per transaction. 1 to 5000. |
| `Metrics:TimeZone` | API, Web | `America/Bogota` | IANA time zone of the business: date ranges of the conversation list and the metrics (`docs/METRICS.md`) are local dates of this zone, and days and hours are grouped in it. Must be known to the machine, otherwise the API (or the Web, which reads the same key to know what "today" is for the dashboard's periods) will not start. |
| `Classification:Enabled` | API | `true` | Runs the background worker that classifies conversations (service, reason, objection, outcome). `false`: conversations stay flagged. See `docs/CLASSIFICATION.md`. |
| `Classification:Provider` | API | `Rules` | The classifier behind the port: only `Rules` (rule-based Spanish) exists; any other value stops the API at startup. |
| `Classification:RulesPath` | API | *(embedded rules)* | Path of a JSON file with the classifier's dictionaries (another vertical or country). Validated at startup. |
| `Classification:DebounceSeconds` | API | `60` | A conversation is classified once its classification request (the arrival of its last message) is at least this old. 0 to 86400. |
| `Classification:PollIntervalMilliseconds` | API | `2000` | Pause of the classification worker when nothing is due. 10 to 600000. |
| `Classification:BatchSize` | API | `20` | Conversations classified per cycle. 1 to 1000. |
| `Classification:MaxFailures` | API | `5` | Consecutive failures after which a conversation is no longer tried, until a new message arrives. 1 to 100. |
| `Classification:RetryDelaySeconds` | API | `60` | Wait before a failed conversation is tried again. 0 to 86400. |
| `Classification:ProcessingTimeoutSeconds` | API | `30` | Time budget of one classification (also its `statement_timeout`). 1 to 3600. |
| `Metrics:FirstResponseSlaMinutes` | API | `30` | First-response SLA of the metrics and the unanswered alert. 1 to 1440. |
| `Metrics:ConversionMaturityDays` | API | `7` | A conversation enters the conversion rate once it started this many days ago (an undecided one idle that long counts against it). 1 to 365; at least `Conversations:AutoCloseAfterDays` while the closing job is on, otherwise the API does not start. |
| `Metrics:TopLossReasons` | API | `5` | Loss reasons listed by `/api/metrics/loss-reasons`. 1 to 20. |
| `Metrics:MinSampleSize` | API | `20` | Decided conversations needed on both sides of a rate comparison for an alert. 1 to 100000. |
| `Metrics:ConversionDropAlertPoints` | API | `10` | Points of conversion drop against the previous period that raise `CONVERSION_DROP`. 1 to 100. |
| `Metrics:BranchBelowAveragePoints` | API | `15` | Points below the tenant's conversion that raise `BRANCH_BELOW_AVERAGE`. 1 to 100. |
| `Metrics:RateLimitPerMinute` | API | `60` | Requests per minute and authenticated user for `/api/metrics/*`, `GET /api/conversations`, `/api/messages/*` and `GET /api/webhook-events` (one shared budget); the next one answers `429` with `Retry-After`. 1 to 100000. |
| `Metrics:QueryTimeoutSeconds` | API | `15` | Time limit of each SQL statement of the metrics; a slower one is cancelled and answers `503`. 1 to 300. |
| `ErrorReporting:Enabled` | Web | `true` | `false`: the Web still shows and logs the code of its errors, it does not send them to the API. `true`/`false` only. |
| `ErrorReporting:ApiKey` | Web | *(unset)* | The API's `ErrorLogging:ReportKey` (>= 32 characters), sent as `X-Error-Report-Key`. **Unset: the Web only logs its codes.** In compose both come from `ERROR_REPORT_KEY`. Set it as a secret, never in a committed file. |
| `ErrorReporting:TimeoutSeconds` | Web | `2` | How long the Web waits for the API to take a report (one attempt, no retries). 1 to 30. |
| `ErrorReporting:MaxPerFingerprintPerMinute` | Web | `20` | Reports sent per defect and minute; beyond it an error still gets a code and a log entry. Whole number >= 1. |
| `Support:ContactText` | Web | *(unset)* | Optional, one line of plain text (up to 300 characters, no control characters; longer or invalid stops the Web at startup) shown on the pending and suspended page as "Cómo contactar al equipo", for example `Escríbenos a soporte@empresa.co o al +57 300 000 0000`. Shown as text, never as markup. Compose: `SUPPORT_CONTACT_TEXT`. |
| `Api:BaseUrl` | Web | `https://localhost:7199/` | Absolute http(s) URL of the API. Required. |
| `Api:TimeoutSeconds` | Web | `10` | Timeout of each API call, 1 to 300. |

## API endpoints

Errors are `application/problem+json`; an unexpected `500` also carries
`errorId` and the `X-Error-Id` header (`docs/ERRORS.md`). `401` means a missing/invalid
token, `403` a missing permission, or a company that is not Active (`"code": "tenant_not_active"`, `docs/PLATFORM_ADMIN.md`). Owners satisfy every permission
policy in their own tenant.

| Method | Path | Auth / permission | Success | Errors |
|---|---|---|---|---|
| POST | `/api/auth/register-tenant` | anonymous | `201` token | `400` invalid input / weak password, `409` email already registered |
| POST | `/api/auth/login` | anonymous, rate limited | `200` token | `400` invalid input, `401` invalid credentials, `429` too many attempts (`Retry-After`) |
| GET | `/api/tenant` | any signed-in user, in every status | `200` name, status (`Pending`/`Active`/`Suspended`), `maxActiveLines`, `activeLines`, `registeredLines` | `401` |
| POST | `/api/admin/auth/login` | anonymous, rate limited | `200` platform administrator token (60 min, own audience) | `400`, `401`, `429` |
| GET | `/api/admin/tenants`, `/api/admin/tenants/{id}`, `/api/admin/audit` | platform administrator only | `200` companies (filter by status and name), one company with its lines and recent audit, the audit trail | `400`, `401`, `403`, `404` |
| PUT | `/api/admin/tenants/{id}/status`, `/line-quota` | platform administrator only | `200` the company | `400`, `401`, `404`, `409` invalid transition / quota below the active lines |
| POST | `/api/admin/tenants/{id}/channels/{channelId}/deactivate` | platform administrator only | `200` the line, off | `401`, `404` |
| GET | `/api/users/me` | any authenticated user, in every status | `200` profile (id, tenant, owner flag, permissions) | `401`, `404` |
| GET | `/api/users` | `UsersView` | `200` users of the caller's tenant | `401`, `403` |
| POST | `/api/users` | `UsersManage` | `201` created user | `400`, `401`, `403`, `409` email already registered |
| POST | `/api/users/{userId}/permissions` | `UsersManage` | `204` (idempotent) | `400`, `401`, `403`, `404` unknown or other-tenant user |
| DELETE | `/api/users/{userId}/permissions/{permission}` | `UsersManage` | `204` (idempotent) | `400` undefined permission, `401`, `403`, `404` unknown or other-tenant user |
| GET | `/api/branches`, `/api/branches/{id}` | `OrganizationView` | `200` branches of the tenant / one branch | `401`, `403`, `404` unknown or other-tenant branch |
| POST | `/api/branches` | `OrganizationManage` | `201` branch + `Location` | `400`, `401`, `403`, `409` name already in the tenant (any letter case) |
| GET | `/api/advisors`, `/api/advisors/{id}` | `OrganizationView` | `200` advisors / one advisor | `401`, `403`, `404` |
| POST | `/api/advisors` | `OrganizationManage` | `201` advisor + `Location` | `400` (also unknown branch or user), `401`, `403` |
| GET | `/api/channels`, `/api/channels/{id}` | `OrganizationView` | `200` WhatsApp channels; key and webhook path only with `OrganizationManage`, never the secret | `401`, `403`, `404` |
| POST | `/api/channels/{id}/deactivate`, `/activate` | `OrganizationManage` | `200` channel (webhook answers 401 while inactive); `activate` also needs an Active company and a free slot of the line quota | `401`, `403`, `404`, `409` quota full (`activate`) |
| POST | `/api/channels/{id}/rotate-secret` | `OrganizationManage` | `200` channel + the NEW `webhookSecret` (once); the old one stops at once | `401`, `403`, `404` |
| POST | `/api/channels` | `OrganizationManage` | `201` channel, **inactive**, + `webhookSecret` (shown only once) | `400` (also unknown branch), `401`, `403` |
| GET | `/api/conversations` | `ConversationsView` | `200` page of conversations, newest first (filters `branchId`, `advisorId`, `unassigned`, `stage`, `source`, `status`, `from`, `to`, `unanswered` (open, no reply yet), `closedUnanswered` (closed, never replied); page size 1 to 100); no message text | `400`, `401`, `403` |
| GET | `/api/messages/recent` | `ConversationsView` | `200` page of the newest messages (filters `channelId`, `direction`, `since`, `afterId` for "newer than the last one I saw"; page size 1 to 100, default 50) with line, branch, customer name and masked number (`…1234`), arrival time and a Spanish type label; same rate limit as the metrics | `400`, `401`, `403`, `404` unknown or other-tenant `afterId` |
| GET | `/api/messages/summary` | `ConversationsView` | `200` messages today (`Metrics:TimeZone`), last inbound/outbound, last 24 hours and, per line, the last webhook delivery that arrived | `401`, `403` |
| GET | `/api/webhook-events` | `ConversationsView` | `200` page of webhook deliveries, newest first (filters `channelId`, `status`): counts, outcome in Spanish and the sanitized last error; never the raw body | `400`, `401`, `403` |
| GET | `/api/conversations/{id}` | `ConversationsView` | `200` conversation + opportunity (stage, outcome source, service, reason, objection, loss reason, classifier confidence and rule codes, `stageChangedBy`) + `advisorAssignedBy`/`advisorAssignedAt` (user ids only; null when the system did it) + messages (text included) | `401`, `403`, `404` unknown or other-tenant conversation |
| PUT | `/api/conversations/{id}/advisor` | `ConversationsManage` | `200` conversation (`{"advisorId": null}` unassigns); records who did it (`advisorAssignedBy`, `advisorAssignedAt`) | `400` unknown/other-tenant/inactive advisor or one from another branch, `401`, `403`, `404` |
| PUT | `/api/conversations/{id}/opportunity` | `ConversationsManage` | `200` opportunity; a manual `Won`, `Lost` (needs `lossReason`) or `InFollowUp`, which the classifier never overwrites; records who decided (`stageChangedBy`) | `400`, `401`, `403`, `404` |
| GET | `/api/metrics/summary`, `/conversion`, `/first-response`, `/loss-reasons`, `/volume`, `/alerts` | `MetricsView` | `200` the numbers of `docs/METRICS.md` for `from`/`to` (local dates between 2015-01-01 and today + 366 days, `to` exclusive, at most 366 days, default the last 30) and the filters `branchId`, `advisorId`, `unassigned`, `source` | `400`, `401`, `403`, `429` (per-user limit), `503` (a query ran out of time) |
| POST | `/api/webhooks/360dialog/{channelKey}` | anonymous; the channel's secret in the header `X-Webhook-Secret` or as the password of `Authorization: Basic` (never in the URL); rate limited (`docs/WEBHOOKS.md`) | `200` (also for a body already received) | `400` not a JSON object, `401` unknown/inactive channel, wrong secret or malformed `Authorization` (same answer), `413`, `429` |
| GET | `/health` | anonymous | `200` `Healthy` / `Degraded`, `503` `Unhealthy` (webhook queue; status word only) | |
| POST | `/api/diagnostics/errors` | header `X-Error-Report-Key`, rate limited; off without `ErrorLogging:ReportKey` | `201` `{code,persisted}` | `400`, `401`, `404` (off), `413` over 64 KB, `429` (`Retry-After`) |

The Web exposes `/`, `/login`, `/register`, `/cuenta-pendiente`, `/users`, `/panel`, `/conversaciones`,
`/conversaciones/{id}`, `/mensajes` and `/mensajes/eventos` (the messages the service stored and the webhook deliveries it received), `/organizacion` (and `/organizacion/canales/{id}/{activar|desactivar|rotar-secreto}`), `POST /logout` and the form endpoints
`POST /users/permissions/grant` and `POST /users/permissions/revoke`. The platform administrator has its own pages: `/admin/login`, `/admin`,
`/admin/empresas/{id}` (with `/estado/{accion}` and `/lineas/{id}/desactivar`), `/admin/auditoria` and `POST /admin/logout`, with a session of its own
(`docs/PLATFORM_ADMIN.md`).
Details and the full error contract: `docs/USERS_TENANTS_PERMISSIONS.md`.

## Testing

```bash
dotnet test tests/Conversia.Domain.UnitTests
dotnet test tests/Conversia.Application.UnitTests
dotnet test tests/Conversia.Infrastructure.UnitTests
dotnet test tests/Conversia.WebhookSimulator.UnitTests
dotnet test tests/Conversia.IntegrationTests
dotnet test tests/Conversia.Web.IntegrationTests
```

The four unit-test projects need nothing else. The two integration
projects require a running Docker daemon: they start their own
disposable PostgreSQL container via Testcontainers (`postgres:17-alpine`,
pulled on first use) and destroy it when the run ends — they never touch
a local or shared database, and no other container is affected. The
same six projects run in CI (`.github/workflows/tests.yml`), followed by the
Docker smoke test.

## Documentation

- `docs/PLATFORM_ADMIN.md` — the platform administrator: company approval, per-line quota, admin API and Web area, sessions, bootstrap, security model, runbook.
- `docs/WEBHOOKS.md` — the 360dialog webhook: endpoint, mapping, retries, closing of conversations.
- `docs/METRICS.md` — the exact definition of every dashboard metric, the alerts and their settings.
- `docs/CLASSIFICATION.md` — the rule-based classifier and how to plug in another one.
- `docs/DOCKER.md` — the three images (api, migrator, web): build, variables, health checks, troubleshooting.
- `docs/decisiones.md` — project decision log (in Spanish).
- `docs/ARCHITECTURE.md` — layers, dependency rules, per-project
  conventions.
- `docs/WORKFLOW.md` — the mandatory TDD + isolated three-agent review
  process used to build every feature in this codebase.
- `docs/USERS_TENANTS_PERMISSIONS.md` — the concrete data model and
  auth flow already implemented (3 tables: `Tenants`, `Users`,
  `UserPermissions`) and the permission catalog.
- `docs/ERRORS.md` — the error registry: codes, what is stored and what is
  not, sanitization, storm control, the report endpoint, retention, SQL to
  investigate an error.
- `docs/PRUEBAS_SANDBOX.md` — test-phase guide (Spanish): run the stack, register the company and the line, connect the 360dialog sandbox through a tunnel, check the messages in the UI, problems table and acceptance checklist.
- `docs/CONFIGURACION_360DIALOG.md` — 360dialog configuration runbook (Spanish): sandbox (verified) versus production (documented, not yet verified), prerequisites, registering and rotating the webhook, troubleshooting.
- `docs/SIMULATOR.md` — the webhook simulator: fill a local stack with believable
  conversations (setup, populate, play), the safety rules, and how to point the
  360dialog sandbox at your machine (documentation only).
- `docs/EXTENDING.md` — add a feature the way Branches are built, layer by
  layer, with the pitfalls hit so far.

## Status

Test version, pilot in progress. Product scope, open questions (how messages sent from the
advisors' phones are captured, dashboard technology, classification engine, final name)
and working conventions are in [`CLAUDE.md`](CLAUDE.md).
