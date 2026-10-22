# Platform administration

Conversia bills **per WhatsApp line** (a channel). A **platform administrator**, who belongs to no client company, approves each company
and sets how many lines it may have switched on, according to its commercial agreement. This document is the contract of that feature.

## Roles

| Role | Belongs to | Signs in at | Can |
|---|---|---|---|
| Company user (owner or member) | one company | `POST /api/auth/login` | Everything its permissions allow **inside its company**, while the company is Active. |
| Platform administrator | no company | `POST /api/admin/auth/login` | List companies, read their business summary, change their status, set their line quota, switch a line off, read the audit trail. **Nothing else**: no conversations, messages, contacts, metrics or users. |

The two never cross: each has its own token audience and signing key, and a token of one kind is a `401` on the endpoints of the other (see
[Security model](#security-model)).

## Lifecycle of a company

```
register-tenant
      |
      v
  Pending ----(admin: Active)----> Active <---(admin: Active)--- Suspended
      |                              |                              ^
      |                              +-------(admin: Suspended)-----+
      +------------------(admin: Suspended)-------------------------+
```

Allowed transitions: Pending to Active, Pending to Suspended, Active to Suspended, Suspended to Active. Anything else (including staying
where it is, or going back to Pending) is a `409`.

| Status | Company users | Webhooks of its lines | Background workers |
|---|---|---|---|
| **Pending** (quota 0) | Can sign in, `GET /api/tenant`, `GET /api/users/me`. Every other endpoint: `403` `tenant_not_active` ("Tu empresa está pendiente de activación."). | `401`, nothing stored | Skip it |
| **Active** | Everything their permissions allow. | Accepted on active lines | Process it |
| **Suspended** | As Pending ("Tu empresa está suspendida."). Tokens already issued stop working on the very next request. | `401`, nothing stored | Skip it; the work stays pending and resumes on reactivation |

Existing companies at the time this feature was introduced were migrated to **Active** with `MaxActiveLines = max(4, their active
channels)`: 4 because the pilot customer has four branches, and never below what they already had switched on. Their channels keep their
`IsActive`.

## Line quota

* A company may **register** (create) as many lines as it wants. A new line starts **inactive** (its secret is returned once at creation).
* A line only works while **active**; the webhook of an inactive line answers `401`.
* The company switches its own lines on and off (`POST /api/channels/{id}/activate` / `deactivate`), at most `MaxActiveLines` at once. Switching on
  beyond the quota is `409`: "Tu cupo de líneas activas está completo (X de X). Contacta al administrador de Conversia." Switching off is always free.
  Switching on is idempotent (an already active line stays so even when the quota is full) and also requires the company to be Active.
* The administrator sets the quota (`0` to `1000`). Lowering it below the lines currently active is `409` (the message says how many are
  active): the company, or the administrator with `deactivate`, switches lines off first.
* **Atomic**: switching on and changing the quota both run in a transaction that locks the company's row (`SELECT ... FOR UPDATE`) and count the
  active lines under that lock, so parallel activations (and a quota change at the same moment) can never leave more active lines than the quota.
  The lock is per company; it does not slow other companies.

## Endpoints

All under `/api/admin`, policy `PlatformAdmin`, Spanish `application/problem+json` errors, an unknown id is always the same `404`
("Empresa no encontrada."). Paging: `page` (from 1), `pageSize` (1 to 100, default 20).

| Method | Path | Notes |
|---|---|---|
| POST | `/api/admin/auth/login` | `{email, password}` to `{token, adminId, expiresAt}`. Anonymous, rate limited. Wrong email, wrong password and a deactivated administrator are the **same** `401`. |
| GET | `/api/admin/tenants` | Query `status` (`Pending`/`Active`/`Suspended`), `search` (part of the name, case-insensitive), `page`, `pageSize`. Items: `id, name, status, maxActiveLines, activeLines, registeredLines, ownerEmail, registeredAt, lastActivityAt` (time of the latest message, null if none), `statusChangedAt, statusReason`. |
| GET | `/api/admin/tenants/{id}` | The same plus `lines` (`channelId, displayPhoneNumber, branchName, isActive`: business facts only, no webhook key) and the last 10 `recentAudit` entries. |
| PUT | `/api/admin/tenants/{id}/status` | `{status, reason?}` (reason up to 500 characters). `409` for an invalid transition. |
| PUT | `/api/admin/tenants/{id}/line-quota` | `{maxActiveLines}` (0 to 1000). `409` below the active lines. |
| POST | `/api/admin/tenants/{id}/channels/{channelId}/deactivate` | Forces a line off. Idempotent. A channel of another company is `404`. |
| GET | `/api/admin/audit` | Query `tenantId?`, `page`, `pageSize`. Newest first. |

For the companies' side: `GET /api/tenant` (any signed-in user, allowed in every status) returns `name, status, maxActiveLines, activeLines, registeredLines`.

The `403` of a company that is not Active carries the stable machine code (`"code": "tenant_not_active"`, plus `"tenantStatus"`):

```json
{ "status": 403, "title": "Empresa no activa", "detail": "Tu empresa está suspendida.", "code": "tenant_not_active", "tenantStatus": "Suspended" }
```

## Audit

Every change writes a row to `PlatformAuditLogs`: the administrator's id, an action code, the company, a small JSON of the old and new values and
the time. Actions: `tenant.status_changed` (`from`, `to`, `reason`), `tenant.quota_changed` (`from`, `to`), `channel.deactivated`
(`channelId`, `displayPhoneNumber`). Refused changes and no-ops (same quota, line already off) write nothing. Rows hold ids, statuses, numbers and
the reason the administrator typed: never a secret, a message or customer data. There is no foreign key on the company, so the trail survives it.

**Personal data and retention.** The reason is free text, so it is the one place a person could type something personal. The status form says so
("No incluyas datos personales de clientes"); the rule for administrators is to write the business reason only ("pago pendiente", "solicitud de la
empresa"), never a customer's name, number or message. The trail has no purge: `PlatformAuditLogs` rows are kept for as long as the platform keeps its
own records, because they are the evidence of who approved or stopped a company. If a reason ever holds personal data, it is corrected in the database
by hand (the column is the only field of the row a person writes). The schema is unchanged.

## Bootstrap of the first administrator

There is no endpoint to create administrators. At startup, if **both** are configured and no administrator with that email exists, the API creates it:

| Variable | Meaning |
|---|---|
| `PlatformAdmin__Email` (compose: `PLATFORM_ADMIN_EMAIL`) | Email of the administrator to create. |
| `PlatformAdmin__Password` (compose: `PLATFORM_ADMIN_PASSWORD`) | At least 12 characters and not a template placeholder (`change_me`, `replace...`, `placeholder`, ...). A weak value stops the API at startup with an error that names the key, never the value. |
| `PlatformAdmin__PasswordFile` (compose: `PLATFORM_ADMIN_PASSWORD_FILE`) | Instead of the variable above: the path of a file that holds the password (for example a Docker secret mounted by you). A trailing newline is dropped. Giving both is a startup error, and so is a file that cannot be read or is empty; the errors name the key, never the path or the value. |
| `PlatformAdmin__TokenMinutes` | Lifetime of the administrator's token (default 60, 1 to 720). |

Both empty (the compose default) means no bootstrap. One without the other is a startup error. An existing administrator is **never** overwritten
by configuration (the password in the variable is ignored once the account exists). The log says "Platform administrator created." and nothing
else: no email, no password.

**Remove the password after the first start.** The API forgets it right after the bootstrap (the options object is cleared and the configuration
key is blanked), and a process that is started again with it set ignores it once the account exists, but the container's environment keeps the
variable for as long as it is set: delete `PLATFORM_ADMIN_PASSWORD` from your `.env` (or use `PLATFORM_ADMIN_PASSWORD_FILE` with a mounted secret, so
the value never sits in the environment) and recreate the `api` service. To rotate the password follow the runbook below.

`.env.example` does not list these two variables yet (it could not be edited with this change); add `PLATFORM_ADMIN_EMAIL=` and
`PLATFORM_ADMIN_PASSWORD=` to your `.env` by hand.

## Security model

* **Isolation of tokens.** The administrator's token has its own audience (`<Jwt:Audience>.platform-admin`), is signed with a key *derived* from
  `Jwt:SigningKey` (HMAC with a fixed label), carries `role=platform_admin` and **no `tenant_id`**, and lives 60 minutes. The company API validates
  the company audience and key; the admin API validates the admin ones, through a second JWT scheme that only the `PlatformAdmin` policy names.
  Result: an admin token is `401` on every company endpoint and a company token is `401` on `/api/admin/*`, even if someone forges claims with the
  other key (tests do it both ways). `CurrentTenantService` returns `Guid.Empty` for a principal with no `tenant_id`, so no company data would be
  visible to it in any case.
* **Revocation.** The policy also checks, per request, that the administrator still exists and is active: deleting or deactivating one cuts its tokens
  off at once.
* **Least privilege.** The admin API returns business facts only. The only queries that look across companies are in `PlatformAdminQueries`
  (and the pre-existing webhook lookup, login and workers); they project scalars (counts, the owner's email, the latest message time, a branch name).
  `TenantFilterGuardTests` pins the exact list of source files that call `IgnoreQueryFilters()` and the only two unfiltered tables with a `TenantId`
  (`ErrorLogs`, `PlatformAuditLogs`).
* **Login.** Uniform `401`; one password verification on every failure path (a decoy hash for unknown/deactivated accounts, built at startup so even
  the first unknown email costs the same); its own per-IP rate limit with the same numbers as the companies' (`RateLimiting:Login`); a per-account
  brake besides (below); `Cache-Control: no-store`.
* **Per-account brake.** The per-IP limit does not stop a guesser who changes address, so every email typed (known or not, normalized, cut to 256
  characters) has a failure counter in memory: after 5 failures the next attempts are refused with `429` and `Retry-After` **before the password is
  looked at**, with the same Spanish text for every email; the wait starts at 30 seconds and doubles with each further failure up to 15 minutes. The
  counter is forgotten after 15 minutes without a failure and after a successful sign-in. Memory is bounded (10,000 accounts; expired ones go first,
  then the one that failed longest ago). It is per API process and lost on restart. Cost of the choice: someone who knows the administrator's email can
  keep that account waiting (the same is true of any lockout); the legitimate administrator waits at most 15 minutes, and a restart clears it.
* **Status check cost and consistency.** Every authenticated company request does one primary-key lookup of the company's status (a single column).
  It is not cached, so a suspension applies on the next request on every API instance, with nothing to invalidate. The permission check already hits
  the database per request, so this is the same order of cost.

## Web

The Web (`src/Conversia.Web`) has two separate areas. Everything is static server-side rendering in Spanish; the pages call the API above, never the database.

| Route | Who | What it is |
|---|---|---|
| `/cuenta-pendiente` | company user, any status | Where a company that is **Pending** or **Suspended** lands: "Tu empresa está pendiente de activación" (what happens next: the Conversia team activates it and sets the number of WhatsApp lines of the commercial agreement) or "Tu empresa está suspendida" (data is kept; contact the team). Reads `GET /api/tenant`. A company that is Active is sent on to `/panel`. After registering it shows "Tu empresa quedó registrada y está pendiente de activación" (`?registrado=1`). |
| `/organizacion` | company user | Shows "Líneas activas X de Y" (chip, bar and words), the state of each line (Activa / Inactiva), "Activar línea" / "Desactivar" (each on a confirmation page) and, when the quota refuses (409), the API's message plus who can help. |
| `/admin/login` | anyone | The administrator's own sign-in ("Administración de Conversia"): `POST /api/admin/auth/login`. One message for every failure; the API's rate limit is shown in words. |
| `/admin` | administrator | Companies: filters (`estado`, `buscar`), 20 per page (`pagina`), status chip, "Líneas activas X de Y", owner email, registration date, last activity ("Sin actividad" when none) and a banner with the companies waiting for activation. |
| `/admin/empresas/{id}` | administrator | One company: status and the moves that make sense (Activar and Suspender when Pending, Suspender when Active, Reactivar when Suspended), the quota form (0 to 1000), its lines (number, branch, state, "Desactivar") and the latest changes. |
| `/admin/empresas/{id}/estado/{activar\|suspender\|reactivar}` | administrator | The confirmation of a status change, with an optional reason (up to 500 characters). |
| `/admin/empresas/{id}/lineas/{channelId}/desactivar` | administrator | The confirmation of switching a line off. |
| `/admin/auditoria` | administrator | The audit trail in words, newest first, 20 per page, optionally for one company (`empresa`). |
| `POST /admin/logout` | administrator | Ends only the administrator's session. |

Every operating page of the company side (`/panel`, `/conversaciones`, `/organizacion`, `/users`, `/`, the permission forms) sends the person to
`/cuenta-pendiente` when the API answers `403` `tenant_not_active`. That is decided in one place (`AuthenticatedPageBase.TryFailAsync`, the users page and the
permission endpoints), not page by page, and it is shown as an expected state: no error code. While the company cannot operate, the navigation shows Panel,
Conversaciones, Organización and Usuarios locked (a lock icon and hidden text, not a link) and keeps "Estado de la empresa" and "Cerrar sesión".

### Sessions

The administrator has **its own session**, never shared with a company's:

| | Company user | Administrator |
|---|---|---|
| Cookie scheme | `Cookies` (default) | `ConversiaAdmin` |
| Cookie | `.Conversia.Auth` (persistent, SameSite Lax) | `.Conversia.Admin` (browser-session cookie, SameSite Strict) |
| Holds | the company API token | the administrator API token |
| Lifetime | the token's (no sliding) | the token's: 60 minutes by default (no sliding) |
| Policy of its pages | `[Authorize]` | `PlatformAdmin` (the `ConversiaAdmin` scheme, the `platform_admin` role) |
| Signs out at | `POST /logout` | `POST /admin/logout` |

Each scheme has its own data-protection purpose, so a value copied from one cookie to the other's name is not a session. A company user who opens `/admin` is
simply anonymous for the administrator scheme and goes to `/admin/login`; an administrator who opens `/panel` goes to `/login`. Neither token is ever sent to the
other API: the pages of one area call only the endpoints of that area. Tests pin it both ways (`AdminSessionTests`). The token stays inside the encrypted cookie
and is never rendered, logged or sent to the browser. The antiforgery token of an administrator page is bound to the administrator's identity, so every POST
(login, quota, status, line, logout) needs one of its own; the logout endpoint runs under the administrator policy for the same reason.

### Contact on the pending page

A person whose company is pending or suspended is told to "contact the Conversia team", which is not enough by itself. Set `Support__ContactText`
(compose: `SUPPORT_CONTACT_TEXT`) on the `web` service to one line of plain text (up to 300 characters) and `/cuenta-pendiente` shows it in a card
titled "Cómo contactar al equipo". It is HTML-encoded (nothing in it is a link or markup), validated at startup (too long or with control characters
stops the Web, naming the key) and absent when empty.

### What each role sees

* **Company user**: its own company only, and only while it is Active (otherwise the page above). The line quota on `/organizacion`.
* **Administrator**: companies and business facts (status, quota, lines, owner email, registration, last activity, the audit trail). The Web has no page, link or call
  for conversations, messages, contacts, metrics or users of a company, and the API has no endpoint for them.

## Runbook

* **Create the first administrator**: set `PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD` for the `api` service and (re)start it; check the log
  for "Platform administrator created."
* **Reset or rotate an administrator's password** (no SQL needed with the API): there is no reset endpoint. Remove the administrator's row with your
  database tooling (or deactivate it and use a different email), then restart the API with the new `PLATFORM_ADMIN_*` values: the bootstrap creates it
  again. Tokens of the removed account stop working immediately. Doing it by hand in SQL is the only way to delete a row; keep at least one other
  administrator while you do it.
* **Approve a new company**: `GET /api/admin/tenants?status=Pending`, then `PUT .../status` `{"status":"Active"}` and `PUT .../line-quota`.
* **Stop a company**: `PUT .../status` `{"status":"Suspended","reason":"..."}`. Its lines stop receiving (the provider sees `401`); events already
  stored wait and are processed after reactivation.
* **See what changed**: `GET /api/admin/audit?tenantId=...`.

## Limits

* No endpoint to create, list or deactivate administrators, or to change their password: the bootstrap and the database are the only ways.
* The administrator's Web area is deliberately small (companies, one company, audit). There is no page to create administrators or change their password (see above).
* The Web shows the audit trail with the company names of the first 100 companies; beyond that a company is shown by a short id.
* While a company is suspended, its provider (360dialog) keeps getting `401` for its webhooks and may retry or disable the callback by its own rules;
  messages sent in that period are not captured.
* A suspended company with a very large backlog of pending webhook events makes each worker claim scan past them (the claim filters by status);
  reactivate or purge if that ever shows up in the queue health.
