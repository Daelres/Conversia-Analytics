# Users, Tenants & Permissions — foundational data model

This is the concrete spec for the system every project derived from
this template starts with. It is intentionally minimal: **three
tables**, enough to register a tenant, log a user in, and gate an
endpoint behind a permission. Everything client-specific gets built on
top of this, not instead of it.

## Why three tables

The requirement is "users, permissions and tenant" — three concepts,
three tables. No `Roles` table: permissions are assigned directly to a
user. This avoids the RBAC layer (and the extra table it would need)
until a real client requirement demands role-grouping — at which point
it's a deliberate addition documented in an ADR, not a default.

## Tables

All three inherit `BaseEntity` (`Id: Guid`, `CreatedAt`, `CreatedBy`,
`UpdatedAt`, `UpdatedBy`).

### `Tenants`

| Column | Type | Notes |
|---|---|---|
| Id | Guid | PK (from `BaseEntity`) |
| Name | string, required, max 200 | Display name of the tenant/workspace |

### `Users`

| Column | Type | Notes |
|---|---|---|
| Id | Guid | PK (from `BaseEntity`) |
| TenantId | Guid | FK -> `Tenants.Id`, required, `Restrict` on delete |
| Email | string, required, max 256 | **Globally unique** (unique index) — see "Login and tenant resolution" below |
| PasswordHash | string, required | Produced by `Microsoft.AspNetCore.Identity.PasswordHasher<User>` — never store or log a plaintext password |
| IsOwner | bool, required, default false | True for the user created during tenant registration. An owner bypasses permission checks *within their own tenant* — see Authorization below |

### `UserPermissions`

| Column | Type | Notes |
|---|---|---|
| Id | Guid | PK (from `BaseEntity`) |
| UserId | Guid | FK -> `Users.Id`, required, `Cascade` on delete |
| Permission | int | Stores a value from the `Permission` enum (`Domain/Enums/Permission.cs`) |

Unique index on `(UserId, Permission)` — a user cannot hold the same
permission twice.

The permission **catalog** is the `Permission` enum in code, not a
table — adding a new permission is adding an enum value, not a
migration to a lookup table.

```csharp
public enum Permission
{
    UsersView = 1,
    UsersManage = 2,
    ProductsView = 3,
    ProductsManage = 4,
    OrganizationView = 5,
    OrganizationManage = 6,
    ConversationsView = 7,
    ConversationsManage = 8,
    MetricsView = 9,
}
```

| Permission | Value | Gates |
|---|---|---|
| `UsersView` | 1 | `GET /api/users` |
| `UsersManage` | 2 | creating users, granting and revoking permissions |
| `ProductsView` | 3 | `GET /api/products`, `GET /api/products/{id}` |
| `ProductsManage` | 4 | `POST`, `PUT` and `DELETE` on `/api/products` |
| `OrganizationView` | 5 | `GET` on `/api/branches`, `/api/advisors` and `/api/channels` |
| `OrganizationManage` | 6 | `POST` on `/api/branches`, `/api/advisors` and `/api/channels` |
| `ConversationsView` | 7 | `GET /api/conversations` and `GET /api/conversations/{id}` (the detail includes message text) |
| `ConversationsManage` | 8 | `PUT /api/conversations/{id}/advisor` and `PUT /api/conversations/{id}/opportunity` |
| `MetricsView` | 9 | every `GET /api/metrics/*` endpoint (docs/METRICS.md) |

(`Organization*` gate the Conversia branches, advisors and WhatsApp channels; `Conversations*` the conversations captured from them.
`Products*` belong to the reference feature that demonstrates the
tenant-scoped pattern. Client projects add their own values as they add
features — e.g. `OrdersView`, `OrdersManage`.)

Product behaviours worth knowing:

- SKU uniqueness (per tenant) and the `search` filter ignore letter case,
  although PostgreSQL compares text case-sensitively by default: the `Sku`
  column is `citext` (and so is `Users.Email`), and the search uses `ILIKE`.
  `ABC-1` and `abc-1` are the same SKU (`409`), and a search for `abc` finds
  `ABC-1`. A plain `text` column would make both case-sensitive.
- A `PUT` that changes nothing is a no-op: no `UPDATE` is issued and
  `rowVersion` (PostgreSQL's `xmin`) and the audit fields stay unchanged. A stale `rowVersion`
  is still rejected with `409`, even when nothing else would change.
- A whitespace-only `search` means no filter.

## Login and tenant resolution

`Email` is unique **globally**, not per-tenant. This is a deliberate
simplification for the template: a login only needs `email +
password`, no separate "which workspace" step. `TenantId` is resolved
from the authenticated user's own record, not supplied by the client.

If a client project needs the same email to exist in more than one
tenant (a consultant working across workspaces, for example), that is
a breaking change to this table's unique index and to the login flow,
and must be an explicit, documented decision — not an assumption made
silently while extending the template.

## Auth flow

Two anonymous endpoints, both in `Conversia.API`, `Controllers/AuthController.cs`:

- `POST /api/auth/register-tenant` — creates a new `Tenant` and its
  first `User` in one transaction. That user is created with
  `IsOwner = true`. Request: `{ tenantName, email, password }`.
  Response: `201` with the issued JWT (the owner is logged in
  immediately — no separate login round-trip after registering).
  The password must follow the password policy below.
- `POST /api/auth/login` — `{ email, password }` -> validates the
  hash with `PasswordHasher<User>.VerifyHashedPassword`, issues a JWT
  on success (`200`, same `AuthResponse` as registration: `token`,
  `userId`, `tenantId`, `isOwner`; the token is valid for 8 hours). Wrong email or wrong password both return the same
  `401` with a generic message — never reveal which one was wrong.

Password policy (`register-tenant` only, enforced by
`[StrongPassword]` on `RegisterTenantRequest`): 12–128 characters with
at least one uppercase letter, one lowercase letter and one digit
(Unicode-aware `char.IsUpper` / `char.IsLower` / `char.IsDigit`).
Login deliberately applies no complexity rules, so accounts created
before the policy can still sign in. Validation messages never echo
the submitted password.

Startup validation: the API refuses to boot (`InvalidOperationException`
naming `Jwt:SigningKey`, never its value) when `Jwt:SigningKey` is
missing/blank, shorter than 32 bytes (UTF-8), or still the placeholder
from `appsettings.json` (also when padded with leading/trailing
whitespace). See `JwtSigningKeyValidator` in `API/Configuration`; the validated
settings are bound once into `JwtOptions` (Infrastructure) and shared by
the JWT bearer setup and `JwtTokenService`.
Supply the key via `dotnet user-secrets` or the `Jwt__SigningKey`
environment variable.

## Login rate limiting

`POST /api/auth/login` (only) is protected against brute force by
ASP.NET Core's built-in rate limiter: a fixed window per client IP
(`HttpContext.Connection.RemoteIpAddress`), no queueing. Every login
request counts, successful or not, even with a malformed body: the
limiter runs before model binding and authentication, so a throttled
request never reaches the login logic or the database. Different IPs
have independent budgets; `register-tenant` and `/api/users/*` are not
limited. A request with no resolvable remote address shares a single
"unknown client" budget instead of bypassing the limit.

Once an IP has used its budget, further logins in the window get `429`
as `application/problem+json` with a whole-second `Retry-After` (>= 1,
at most one window). After the window elapses the client can log in again.

```json
"RateLimiting": { "Login": { "PermitLimit": 10, "WindowSeconds": 60 } }
```

Shipped defaults are 10 requests per 60 s, also used when the section
or a single key is absent. A value that is not a whole number >= 1
makes the API refuse to boot with an `InvalidOperationException` naming
`RateLimiting:Login:PermitLimit` / `RateLimiting:Login:WindowSeconds`
(`LoginRateLimitOptionsValidator` in `API/Configuration`; the policy is
registered by `AddLoginRateLimiting` in `API/RateLimiting`). The limiter
is in-memory: counters are per API instance and add no database
round-trips.

Deployment note: behind a reverse proxy or load balancer,
`RemoteIpAddress` is the proxy's address unless forwarded headers are
configured, which would put every client in one shared budget. Configure
the ASP.NET Core Forwarded Headers middleware (`UseForwardedHeaders`
with `ForwardedHeaders.XForwardedFor` and `KnownProxies`/`KnownNetworks`
limited to your proxies) so the real client IP is resolved before the
rate limiter runs. The template does not add it.

## User endpoints

All live in `Controllers/UsersController.cs` (`/api/users`, class-level
`[Authorize]`, so a missing/invalid token is `401`). Owners bypass the
permission policies within their own tenant. Authorization runs before
the target is resolved, so a caller without the required permission gets
`403` regardless of whether a target exists.

- `GET /api/users/me` — any authenticated user. `200` with
  `UserProfileResponse` (`userId`, `tenantId`, `isOwner`, `permissions`).
- `GET /api/users` — requires `UsersView`. `200` with the caller's
  tenant's users as `UserSummaryResponse` (`userId`, `email`, `isOwner`).
- `POST /api/users` — requires `UsersManage`. Creates a user in the
  **caller's own tenant**.
  Body `{ email, password }`; the tenant always comes from the JWT
  `tenant_id` claim, and any `tenantId`/`isOwner` in the body is
  ignored. The new user has `IsOwner = false` and no permissions
  (assign them afterwards). Email rules are `[Required]`,
  `[EmailAddress]`, `[MaxLength(256)]`; the password follows the same
  `[StrongPassword]` policy as registration. Response: `201` with a
  `UserSummaryResponse`. The email is
  globally unique, so a duplicate from any tenant is a `409`. Under
  concurrent requests for one email the unique index decides: exactly
  one `201`, the rest `409`. `CreatedBy` is stamped centrally from the
  caller's `sub`.
- `POST /api/users/{userId}/permissions` — requires `UsersManage`.
  Body `{ permission }` (a `Permission` value). Grants one permission to
  a user of the caller's tenant: `204`. Granting a permission the target
  already holds is an idempotent `204` (the unique index decides under
  concurrency). A target that does not exist or is in another tenant is
  `404`; an undefined permission is `400`.
- `DELETE /api/users/{userId}/permissions/{permission}` — requires
  `UsersManage`. Revokes one
  permission, effective on the target's next request. `{permission}`
  is a `Permission` name (case-insensitive) or its number. The
  target lookup is tenant-scoped, then a single set-based `DELETE`
  removes the row. Revoking a permission the target does not hold is
  an idempotent `204`. A target that does not exist and one in another
  tenant both return the same `404`. A non-GUID `userId` is a `404`
  (`:guid` route constraint). A value that is not a single catalog permission
  (`0`, `999`, `-1`, `NotAPermission`, and any comma list such as
  `UsersView,UsersManage` or `1,2`, which is never combined as flags) is
  a `400`.

JWT claims: `sub` (User.Id), `tenant_id` (User.TenantId), `is_owner`.
Permissions are **not** embedded in the token — they're resolved from
`UserPermissions` per request, so revoking a permission takes effect
on the user's very next request without forcing a re-login.

## Web front end

`Conversia.Web` is a client of the endpoints above (see
`docs/ARCHITECTURE.md`). It signs in through `POST /api/auth/login` or
`register-tenant`, keeps the returned JWT only inside its encrypted auth
cookie (valid no longer than the JWT's `exp`) and replays it as the bearer
token per request. Mapping of API answers shown to the user: login `401` ->
"Correo o contraseña incorrectos.", `400` -> the per-field messages, `429` ->
"Demasiados intentos" with the `Retry-After` seconds ("1 segundo" / "N segundos"),
register `409` -> "Ese correo ya está registrado.", `403` -> "No tienes
permiso para hacer eso.", an unreachable API -> "El servicio no está disponible
...", `401` on any other call -> sign-out and redirect to `/login`. The Web never decides authorization itself:
it hides management controls from callers without `UsersManage`, but every
action is still checked by the API.

All text shown to users is Spanish, including the permission labels: the enum
names (`UsersView`, `UsersManage`, `ProductsView`, `ProductsManage`) stay in
routes, JSON, form values and `data-permission` attributes, while the pages
show "Ver usuarios", "Gestionar usuarios", "Ver productos" and "Gestionar
productos" (`Web/Services/PermissionLabels.cs`; a permission added to the enum
needs a label there). The Web always renders in the `es` culture: prices are
displayed as `1.234,50` with the raw value in `data-price` (invariant, two
decimals, `1234.50`) and the stock in `data-stock`. The price field accepts
`19,99`, `19.99` and `1,5` (1.5); thousands separators, signs, more than two
decimals, blank or non-numeric text are refused with a message and never saved.
The edit form pre-fills the price with a comma decimal (`19,99`).

The API's messages are Spanish too, whatever `Accept-Language` says: the `detail`
of the handled errors is the message of the exception (for example "Producto no
encontrado.", "Ya existe un producto con ese SKU.", "Correo o contraseña
incorrectos."), validation problems are titled "Uno o más errores de validación."
and the login `429` says "Demasiados intentos de inicio de sesión. Inténtalo de
nuevo más tarde.".

## Demo data

Opt-in sample data so the template can be shown before real use. It is off
unless `Demo:Enabled` is `true`; the shipped `appsettings*.json` never enable
it, and when it is off nothing is registered and the database is not touched.
Because the accounts have publicly known credentials, `true` is only accepted
in the `Development` and `Demo` hosting environments: in any other environment
(or with a value that is not `true`/`false`) the API refuses to start with an
`InvalidOperationException` naming `Demo:Enabled`. `Demo:Password` (default
`Demo-Pass-2026!`) must satisfy the registration password policy, otherwise the
API refuses to start naming `Demo:Password`; the value is never logged.

At startup (before the first request is served) `IDemoDataSeeder` creates:

| Tenant | Account | Permissions |
|---|---|---|
| Acme Demo | `owner@acme-demo.example` | owner |
| | `manager@acme-demo.example` | `ProductsView`, `ProductsManage`, `UsersView` |
| | `viewer@acme-demo.example` | `ProductsView` |
| | `noaccess@acme-demo.example` | none |
| Globex Demo | `owner@globex-demo.example` | owner |

Acme Demo gets 13 products (`ACME-001`..`ACME-012` and `SKU-001`), Globex Demo
5 (including `SKU-001`, to show that SKUs are unique per tenant). Product names
are Spanish ("Yunque de 50 kg", "Patines cohete", ...); the account descriptions and
shortcut labels ("Entrar como propietario de Acme", ...) come from `DemoAccounts`. Each tenant is
written in one transaction and is seeded only if its owner email does not exist
yet, so restarts, second API instances and parallel calls never duplicate, restore
or overwrite anything the user changed or deleted. A seeding failure (database
unreachable or not migrated yet) is logged at Error level and does not stop
the API; calling `IDemoDataSeeder.SeedAsync` after the migration works. Seeders are
serialized with a transaction-level PostgreSQL advisory lock (`pg_advisory_xact_lock`), so
parallel instances or callers do not race on the unique email index.

Never run a production deployment in an environment named `Demo` and never set
`Demo:Enabled` in production: the accounts have a publicly known password.

## Authorization

One ASP.NET Core authorization policy per `Permission` enum value,
named after the enum (e.g. `nameof(Permission.UsersManage)`),
enforced with `[Authorize(Policy = "...")]` on controller actions.

`PermissionAuthorizationHandler` (`Infrastructure/Authorization/`):
1. If the current user's `is_owner` claim is `true`, succeed
   immediately (an owner administers their own tenant fully).
2. Otherwise, look up whether `UserPermissions` contains a row for
   this user and the required permission (scoped to the user's own
   tenant via the global query filter — see below). Succeed or fail
   accordingly.

## Multi-tenancy enforcement

`ApplicationDbContext` applies a global query filter on `Users` and
`UserPermissions`, scoped to `CurrentTenantService.TenantId` (resolved
from the `tenant_id` claim via `IHttpContextAccessor`). `Tenants`
itself is not filtered — it is the tenant root and is only ever
queried by `Id` during registration/lookup, never listed cross-tenant
by a request.

## Error handling contract

Every input this system accepts must lead to a controlled, defined
outcome — never an unhandled exception:

- Duplicate email on `register-tenant` -> `409 Conflict`, generic
  "email already registered" message.
- Duplicate email on `POST /api/users` -> the same `409` and message,
  even when the email belongs to another tenant.
- Granting or revoking for a nonexistent or cross-tenant `userId` -> an
  identical `404` (`UserNotFoundException`); an undefined `{permission}`
  on revoke -> `400` with an error under `permission`.
- Invalid/missing fields -> `400 Bad Request` with per-field errors.
  A weak `register-tenant` password is a `400` with an error under
  `Password` that states the policy but never the submitted value.
- Unsafe or missing `Jwt:SigningKey` -> the API fails at startup
  (not a request-time error); the message never contains the key.
- More than `RateLimiting:Login:PermitLimit` login requests from one IP
  within `WindowSeconds` -> `429 Too Many Requests` (problem+json,
  `Retry-After`); invalid `RateLimiting:Login` values fail at startup.
- Login with unknown email or wrong password -> `401 Unauthorized`,
  identical generic message for both cases.
- Every error response produced by the middleware (`409`, `401`, `404`,
  `400` domain errors, `500`) and the automatic validation `400` is
  `application/problem+json`.
- Any unanticipated exception -> caught by the global exception
  middleware in `API/Middleware`, logged, and returned as a `500`
  `application/problem+json` body with **no stack trace and no
  exception message** in the response body.

This contract is what Role A (tests) writes against, Role B verifies
against, and Role C implements — see `docs/WORKFLOW.md`.
