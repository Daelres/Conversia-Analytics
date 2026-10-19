# Architecture

Conversia is a Clean Architecture solution template for building
custom line-of-business systems on .NET. It ships with a working
Users / Tenants / Permissions foundation so new client projects start
from a solid, already-tested base instead of re-implementing auth from
scratch.

## Layers and dependency direction

```
Conversia.Domain          <- no project references (pure C#)
Conversia.Application     -> Domain
Conversia.Infrastructure  -> Domain, Application
Conversia.API             -> Domain, Application, Infrastructure
Conversia.Web             -> Application  (DTOs/contracts only, talks to the API over HTTP)
```

Dependencies only ever point inward/downward. `Domain` never references
anything else in the solution. `Application` never references
`Infrastructure` or `API`. If you find yourself wanting to add a
reference that violates this direction, the design is wrong — stop and
reconsider before adding it.

### Conversia.Domain

The core business model. No framework dependencies (no EF Core, no
ASP.NET Core) beyond the BCL.

- `Common/` — `BaseEntity` (Id, CreatedAt, CreatedBy, UpdatedAt,
  UpdatedBy). **Every entity in the system inherits from `BaseEntity`.**
  This is the only place audit-trail fields are declared.
- `Entities/` — aggregate/entity classes (`Tenant`, `User`,
  `UserPermission`, `ErrorLog`, the Conversia model `Branch`,
  `Advisor`, `WhatsAppChannel`, `Contact`, `Conversation`, `Message`,
  `Opportunity`, `WebhookEvent`, and whatever business entities a client
  project adds later). `Common/TextRules` and `Common/UtcTime` hold the
  free-text and UTC/microsecond rules the Conversia entities share.
- `Common/ErrorCode` — pure generator/validator of the `ERR-yyyyMMdd-XXXXXX`
  code of a registered error (`docs/ERRORS.md`).
- `Enums/` — enumerations, including the `Permission` catalog (see
  `docs/USERS_TENANTS_PERMISSIONS.md`).
- `Interfaces/` — repository/service contracts owned by the domain
  (e.g. `IUserRepository`), implemented in `Infrastructure`.
- `Exceptions/` — domain-specific exception types.

### Conversia.Application

Business/use-case logic. Depends only on `Domain`. No EF Core, no
ASP.NET Core.

- `DTOs/<FeatureArea>/` — request/response contracts, one folder per
  feature area (e.g. `DTOs/Auth/`).
- `Interfaces/` — service contracts consumed by `API` and implemented
  here or in `Infrastructure` (e.g. `IAuthService`, `IJwtTokenService`
  is implemented in Infrastructure since it needs signing config).
- `Services/` — concrete use-case classes. A service calls repository
  interfaces (defined in `Domain`), never `DbContext` directly.
- `Exceptions/` — application-level exceptions (validation, not-found,
  conflict) that the API layer maps to HTTP responses.
- `Validation/` — reusable validation attributes (`StrongPasswordAttribute`).
- `ErrorLogging/` — pure helpers of the error registry: `ErrorSanitizer`
  (secret redaction, control characters, truncation), `ErrorFingerprint`
  (groups repeated errors), `ErrorStormGuard` (sliding window per
  fingerprint) and `ErrorReportSanitizer` (what may be stored). Its contracts
  are `Interfaces/IErrorRegistry` and `IErrorLogPurger`; its DTOs
  `DTOs/Diagnostics/` (`ErrorReport`, `ErrorRegistration`, and the
  `ReportErrorRequest`/`ReportErrorResponse` of the report endpoint).
- `Ingestion/` — `Dialog360PayloadParser` (the only class that knows the
  360dialog payload shape; pure) and `WebhookEventProcessor` (one stored delivery
  into contacts, conversations, messages and opportunities, idempotently).
  `Services/WebhookIngestionService` is the request side: authenticate by
  channel secret, validate, store.

No CQRS/MediatR pipeline is imposed by this template. Keep it simple:
one interface + one implementation class per use case. Do not
introduce a mediator/pipeline layer speculatively — only adopt one if
a client project's complexity genuinely calls for it, and document the
decision in an ADR under `docs/adr/`.

### Conversia.Infrastructure

Everything that talks to the outside world.

- `Persistence/ApplicationDbContext.cs` + `Persistence/Configurations/`
  — one `IEntityTypeConfiguration<T>` per entity. Audit fields
  (`CreatedAt`/`CreatedBy`/`UpdatedAt`/`UpdatedBy`) are populated
  centrally in `ApplicationDbContext.SaveChangesAsync` via the change
  tracker — **never set them by hand in a service**.
- `Persistence/Migrations/` — EF Core Code First migrations (Npgsql). The
  `citext` extension (case-insensitive emails) is created by the
  first one.
- `Configuration/` — options bound from configuration (`JwtOptions`,
  `ErrorLoggingOptions`).
- `ErrorLogging/` — `ErrorRegistry` (sanitizes, codes, applies the storm guard
  and writes one `ErrorLogs` row through a scope and `DbContext` of its own,
  never throwing), `ErrorLogPurger` (batched `ExecuteDelete`) and
  `ErrorLogRetentionService` (`BackgroundService`: purge at startup, then every
  24 h). `ErrorLogs` is an operations table: no tenant query filter.
- `Webhooks/` — `WebhookEventRunner`, the queue worker's unit of work: claims one
  pending `WebhookEvents` row with `FOR UPDATE SKIP LOCKED`, processes it in a
  savepoint and records failures with exponential back-off.
- `Repositories/` — concrete repository implementations of the
  `Domain.Interfaces` contracts.
- `Services/` — infrastructure-backed service implementations
  (`JwtTokenService`, `PasswordHasherService`, `CurrentTenantService`).
- `Authorization/` — the custom `IAuthorizationHandler` + requirement
  that enforce permission-based policies.

### Conversia.API

ASP.NET Core Web API, Controllers-based (not Minimal APIs), so
feature areas map 1:1 to a controller and stay easy to navigate as the
project grows.

- `Controllers/` — one controller per feature area.
- `Middleware/` — the global exception-handling middleware (every
  unhandled exception is caught here and turned into a consistent
  `application/problem+json` response — **the API must never leak an
  unhandled 500 with a stack trace**). An unexpected one is registered first
  and answered with its code (`errorId`, `X-Error-Id`); known outcomes, a
  client that went away and `BadHttpRequestException` are not registered.
- `ErrorLogging/` — `UnhandledErrorReport` (what is recorded about a failed
  request), `ErrorReportKeyVerifier` (constant-time key check) and
  `ErrorReportAccessAttribute` (the guard of the report endpoint: off without a
  key, `401`, 64 KB cap). `Controllers/DiagnosticsController` is the
  `POST /api/diagnostics/errors` endpoint the Web reports through.
- `Configuration/` — options records plus fail-fast startup validators
  (`JwtSigningKeyValidator`, `LoginRateLimitOptionsValidator`,
  `ErrorLoggingOptionsValidator`) that refuse to boot on unsafe or invalid
  settings, plus the `AddErrorLogging` registration extension.
- `HostedServices/` — background work (`WebhookEventProcessingService`
  drains the webhook queue, [docs/WEBHOOKS.md](WEBHOOKS.md)).
- `Controllers/WebhooksController` — the one anonymous endpoint besides
  login/registration: the provider webhook, authenticated by a per-channel
  secret instead of a JWT.
- `RateLimiting/` — rate-limiter policy registration
  (`AddLoginRateLimiting`, `AddErrorReportRateLimiting`) and the one 429
  rejection answer they share.
- `ModelBinding/` — custom model binders (`DefinedPermissionModelBinder`).
- `Program.cs` — composition root: DI registration, JWT/auth setup,
  middleware pipeline. No `Startup.cs` split.

### Conversia.Web

Blazor Web App using **static server-side rendering** (no interactive
render mode): every form is a plain `EditForm`/HTML form with an
antiforgery token. It talks to `Conversia.API` over HTTP only, using
the DTOs declared in `Application` as the wire contract. It
**never** references `Infrastructure` and never opens its own database
connection — this keeps the API as the single source of truth and the
only process with a connection string. (`Domain.Enums.Permission`
reaches it transitively through the Application DTOs.)

- `Services/` — `IApiClient`/`ApiClient`, the one typed `HttpClient`
  (registered with `AddApiClient`, base address and timeout from the
  `Api` config section). Every API answer, including transport errors,
  timeouts, 5xx and malformed bodies, is mapped to an `ApiResult<T>`
  (`Success`, `ValidationFailed`, `Unauthorized`, `Forbidden`,
  `NotFound`, `Conflict`, `RateLimited` + Retry-After, `Unavailable`);
  the client never throws for a backend problem. The bearer token is a
  per-call argument — no token is stored on the client or in any
  singleton/static, so concurrent users (tenants) share no state.
  The `HttpClient` handler-pipeline logging is removed so request
  headers can never be logged.
- `ErrorReporting/` — the Web's side of the error registry (`docs/ERRORS.md`).
  `IWebErrorReporter` (`AddWebErrorReporting`) turns an unexpected failure of the
  Web into a code, reports it to the API best effort (one bounded attempt, per-
  defect storm control with `ErrorStormGuard`, payload sanitized with the
  Application's `ErrorReportSanitizer`) and always returns the code.
  `WebExceptionHandler` (an `IExceptionHandler` in front of `/Error`) registers
  an unhandled exception and hands the code to the error page and to
  `X-Error-Id`; `ApiClient` gives every `Unavailable` outcome an `ErrorCode`;
  the per-request `ErrorCodeState` carries it to `Components/Shared/ErrorCodeNotice`,
  which the layouts render, so no page shows or knows about the code. The
  options are validated at startup (`ErrorReportingOptionsSetup/Validator`).
  Only `Application` (and `Domain` through it) is referenced.
- `Authentication/` — cookie authentication (`AddWebAuthentication`).
  After a successful login/registration the API's JWT is stored **inside
  the encrypted, HttpOnly, SameSite=Lax auth cookie** as an
  authentication token (`Secure` whenever the request is https). It is
  never rendered into HTML or exposed to the browser. The cookie
  expires exactly when the JWT does (`exp` is read, not trusted;
  sliding renewal is off). Pages read the token from the current
  request's cookie. `LocalReturnUrl` only lets a `returnUrl` through
  when it is a site-local path.
- `Http/` — antiforgery helpers for the form endpoints, the
  `Cache-Control: no-store` middleware, `NotFoundMarker` and
  `RejectedFormMiddleware` (turns the bare 400 of a rejected antiforgery token
  into the "La solicitud no es válida" page, see below).
- `Components/Pages/` — `/` (anonymous landing page, or the profile from
  `GET /api/users/me`), `/login`, `/register`, `/users` (requires
  authentication; user list, create-user form, per-user grant/revoke of
  every `Permission`), and the Conversia pages: `/panel` (the executive
  dashboard), `/conversaciones` and `/conversaciones/{id:guid}` (the
  conversation list and detail) and `/organizacion` (branches, advisors and
  WhatsApp numbers). Management controls are shown only to callers
  with the matching `...Manage` permission (owner or holder) but the API
  stays the authority: a forged request is refused by the API and shown
  as a "No tienes permiso" message. An API `401` (expired token) signs the
  user out and redirects to `/login`. A `404` is re-run by the status-code
  pages into the generic "Página no encontrada" page, so a page that wants
  to say *what* is missing marks the request (`NotFoundMarker`) and that
  page says "Conversación no encontrada." or "Número no encontrado.".
- `Components/Layout/` — the shell. `MainLayout` (signed-in visitors get a
  sidebar, anonymous ones a top bar; `data-shell="app|site"`) and
  `AuthLayout` (split screen for `/login` and `/register`) both render
  `ShellHeader`: one `<header>` that holds the brand, **one** `<nav>`
  (`NavItem` marks the current entry with `aria-current="page"`), the account
  card, the theme toggle and the single logout form. Below 960 px the same
  element becomes a top bar whose menu opens with a checkbox + label (no
  JavaScript). `ShellState` is a per-request bridge: a page that already loaded
  `/api/users/me` hands the profile over so the account card can show role and
  workspace without another API call.
- `Components/Shared/` — the component library of the design system:
  `Icon` (+ `IconSet`, hand-drawn 24 px / 1.75 stroke icons), `BrandMark`,
  `Avatar`, `PageHeader`, `Card`, `Alert` (`role="alert"` for problems,
  `role="status"` for confirmations), `Chip`, `EmptyState`, `Illustration`,
  `StatusView` (403/404/error pages) with the ready-made `ForbiddenView`,
  `ConversationNotFoundView` and `ChannelNotFoundView`, `InPageLink`, `FormField` + `TextInput` (label, icon,
  hint, and the local and API error messages wired with `aria-invalid` and
  `aria-describedby`), `ThemeToggle`, and the page parts `ApiFieldErrors`,
  `ErrorAlert` and `PermissionForm`.
- **Design system, no framework.** `wwwroot/css/` holds plain CSS with no build
  step, no CDN and no web fonts: `tokens.css` (custom properties: palette,
  type scale, spacing, radii, shadows; every scheme-dependent colour is a
  `var(--if-light, ...) var(--if-dark, ...)` pair, so the page follows
  `prefers-color-scheme` unless `<html data-theme="light|dark">` forces a scheme), `base.css` (reset, typography, focus rings, inputs),
  `layout.css` (shell, auth layout, page frame), `components.css` and `pages.css`.
  They are linked from `App.razor` with fingerprinted `@Assets[...]` URLs.
  Bootstrap is gone. `wwwroot/js/shell.js` wires the theme button and saves
  the choice in `localStorage`, restores the theme and the `js` class after each
  enhanced navigation and moves focus to the new page's `h1`; one tiny inline
  script in `<head>` applies the saved theme before first paint. Without
  JavaScript the page follows the system scheme and the button stays hidden.
  `InPageLink` builds the skip link and the create-panel buttons so that, under
  `<base href="/">`, they stay on the current page. `wwwroot/favicon.svg` is the favicon.
  How to build a page with it: [`docs/EXTENDING.md`](EXTENDING.md#using-the-design-system).
- `Endpoints/` and `POST /logout` — grant/revoke buttons post to small
  minimal-API endpoints that validate the posted ids locally, call the
  API and answer with a post/redirect/get carrying a fixed notice code
  (never posted data). `POST /logout` clears the cookie; `GET /logout`
  does nothing.
- Every POST requires a valid antiforgery token (400 otherwise, before
  anything reaches the API). For a Blazor form that 400 would be an empty body
  that still says `text/html`; `RejectedFormMiddleware` drops that content type
  so the status-code pages (`/status/{code}`) render the Spanish "La solicitud
  no es válida" error page inside the shell. The minimal endpoints (logout,
  permissions) keep their plain-text 400. Dynamic responses are `Cache-Control:
  no-store`. Unexpected exceptions end on `/Error`, which shows no
  exception detail.

Deployment notes: the auth cookie is protected with ASP.NET Core Data
Protection, so every instance of a scaled-out Web must share the same
key ring; behind a TLS-terminating proxy configure forwarded headers so
requests are seen as https and the cookie is marked `Secure`.

## Multi-tenancy

Tenant isolation is enforced with an EF Core global query filter on
every tenant-scoped entity, keyed off the current request's tenant
(resolved from a claim in the JWT — see
`docs/USERS_TENANTS_PERMISSIONS.md`). Code that runs without a JWT sets the
tenant of its DI scope explicitly through `ITenantContext`, and only after it
knows it from something it verified: the anonymous webhook request after the
channel's secret matched, and the background worker after claiming an event. New business entities added by a
client project must apply the same global query filter pattern; do not
rely on remembering to add a `WHERE TenantId = ...` manually in every
query.

## Testing strategy

Five test projects, all run by CI (`.github/workflows/tests.yml`: restore,
build in Release, then each project in turn, on every push/PR to `main`).

- **Unit tests** — pure logic, no database, no HTTP:
  `tests/Conversia.Domain.UnitTests` (entities),
  `tests/Conversia.Application.UnitTests` (services and DTO validation,
  repository interfaces mocked with Moq) and
  `tests/Conversia.Infrastructure.UnitTests` (the permission
  authorization handler).
- **API integration tests** (`tests/Conversia.IntegrationTests`) — the
  real API booted in-process (`WebApplicationFactory`) on a real
  PostgreSQL via **Testcontainers** (`Testcontainers.PostgreSql`), spun up once
  per test run and torn down automatically when the run ends.
  **EF Core InMemory is banned in this template** — it does not
  validate real PostgreSQL behavior (constraints, cascade rules,
  concurrency, case-insensitive columns), which is exactly what these tests exist to catch. Do
  not add it back.
- **Web integration tests** (`tests/Conversia.Web.IntegrationTests`) —
  the real Web, hosted in-process and driven over HTTP, against the real API and a real
  PostgreSQL (Testcontainers): pages, forms, antiforgery, cookie/token
  handling, multi-user isolation, and an architecture guard that fails if
  the Web references Infrastructure, the API or a data-access package.

The two integration projects need a running Docker daemon.

## Containers

`src/Conversia.API/Dockerfile` and `src/Conversia.Web/Dockerfile` are
multi-stage builds (SDK image to `aspnet` image, non-root user, port
8080, build context = repository root, no secrets baked in). The API
Dockerfile has a second target, `migrator`, that packages the EF Core
migrations as a self-contained bundle; `docker-compose.yml` runs it once
(`migrator`) before starting the API and Web, next to a PostgreSQL
container. Secrets (`POSTGRES_PASSWORD`, `JWT_SIGNING_KEY`) come from a
git-ignored `.env` (see `.env.example`). No application code knows about
Docker: the API reads `ConnectionStrings__DefaultConnection` and
`Jwt__SigningKey`, and the Web reads `Api__BaseUrl`, from the standard
configuration providers.

See `docs/WORKFLOW.md` for the mandatory process used to build every
feature in this codebase (including the initial Users/Tenants/
Permissions system) and `docs/USERS_TENANTS_PERMISSIONS.md` for the
concrete data model and auth flow already implemented.
