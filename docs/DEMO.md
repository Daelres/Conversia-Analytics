# Running and presenting the demo

The template ships with a self-contained demo: two tenants (Acme Demo and
Globex Demo), five accounts with different permissions and a small product
catalog. It is **opt-in** and meant for a laptop: the accounts have a
publicly known password (see [Safety notes](#safety-notes)).

The Web UI and the messages the API returns are in Spanish, so this guide
quotes the Spanish texts you will see on screen (the guide itself stays in
English). The Web always renders in the `es` culture, whatever the browser asks
for.

| Want to... | Go to |
|---|---|
| Start it | [Quick start](#quick-start-docker-compose) |
| Log in as someone | [Demo accounts](#demo-accounts) |
| Present it | [Presenter script](#presenter-script) |
| Fix a problem | [Troubleshooting](#troubleshooting) |
| Build your own feature | [EXTENDING.md](EXTENDING.md) |

## Prerequisites

| Path | You need |
|---|---|
| Docker Compose (recommended) | Docker with Compose v2, internet access for the first build (NuGet and the base images). No .NET SDK. For the terminal commands in scenes 2, 7 and 8: `curl` and `jq`. |
| Without Docker | .NET 10 SDK, the `dotnet-ef` tool (`dotnet tool restore` installs the version pinned in `.config/dotnet-tools.json`) and a PostgreSQL you can reach (a throwaway container is fine). |

## Quick start (Docker Compose)

```bash
cp .env.example .env      # then set POSTGRES_PASSWORD and JWT_SIGNING_KEY
docker compose -f docker-compose.yml -f docker-compose.demo.yml up --build
```

- `POSTGRES_PASSWORD` is the database password (avoid `;`, `=` and `"`) and
  `JWT_SIGNING_KEY` must be at least 32 bytes (`openssl rand -base64 48`);
  Compose stops with a message if either is missing. Optional
  `DEMO_PASSWORD` in `.env` changes the demo password
  (default `Demo-Pass-2026!`, see [`.env.example`](../.env.example)).
- The overlay [`docker-compose.demo.yml`](../docker-compose.demo.yml) sets
  `ASPNETCORE_ENVIRONMENT=Demo` on the API and the Web, `Demo__Enabled=true`
  on the API and `Demo__ShowAccounts=true` on the Web, and gives both the
  same `Demo__Password`. The `migrator` job still runs first, so the tables
  exist when the API seeds the data while it starts (the log says
  `Seeded demo tenant 'Acme Demo'`).
- The first start pulls the PostgreSQL image (about 100 MB) and builds the
  images, which takes a few minutes. Add `-d` to run in the background.
- The base `docker-compose.yml` on its own never enables any demo.

## Quick start (without Docker)

You need a migrated PostgreSQL and the two apps started with the demo
settings. Example with a throwaway PostgreSQL container (bash; in
PowerShell use `$env:NAME = "value"`):

```bash
docker run -d --name dr-demo-db -e POSTGRES_DB=conversia -e POSTGRES_USER=conversia -e POSTGRES_PASSWORD=change_me \
  -p 127.0.0.1:5432:5432 postgres:17-alpine

export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=conversia;Username=conversia;Password=change_me'
export Jwt__SigningKey="$(openssl rand -base64 48)"

# apply the migrations (the API host must start, hence the signing key above)
dotnet ef database update --project src/Conversia.Infrastructure --startup-project src/Conversia.API

# terminal 1 (the shell with the two exports above): the API (the Demo environment accepts Demo__Enabled)
ASPNETCORE_ENVIRONMENT=Demo Demo__Enabled=true ASPNETCORE_URLS=http://localhost:5030 \
  dotnet run --project src/Conversia.API --no-launch-profile

# terminal 2: the Web
ASPNETCORE_ENVIRONMENT=Demo Demo__ShowAccounts=true Api__BaseUrl=http://localhost:5030/ ASPNETCORE_URLS=http://localhost:5112 \
  dotnet run --project src/Conversia.Web --no-launch-profile
```

`--no-launch-profile` matters: the launch profiles force
`ASPNETCORE_ENVIRONMENT=Development`. `Development` is also accepted by the
demo settings, and it is the one environment that serves the OpenAPI
document (`/openapi/v1.json`):

```bash
Demo__Enabled=true dotnet run --project src/Conversia.API --launch-profile http
Demo__ShowAccounts=true Api__BaseUrl=http://localhost:5030/ dotnet run --project src/Conversia.Web --launch-profile http
```

(`Api__BaseUrl` is needed because the Web's default points at the API's
https URL.) `Demo__Password` is optional on both apps; if you set it, set the
same value on both.

## URLs and ports

| What | Docker Compose | Without Docker |
|---|---|---|
| Web | <http://localhost:5112> | <http://localhost:5112> (as started above) |
| API | <http://localhost:5030> | <http://localhost:5030> |
| OpenAPI document | not served (`Demo` is not `Development`) | `/openapi/v1.json`, only in `Development` |
| PostgreSQL | `127.0.0.1:5432`, `POSTGRES_USER` / `POSTGRES_PASSWORD` | wherever you started it |

Change the Compose host ports with `WEB_PORT`, `API_PORT` and `POSTGRES_PORT` in
`.env`.

## Demo accounts

All accounts share one password: `Demo-Pass-2026!` unless you set
`DEMO_PASSWORD`. The list comes from `DemoAccounts` in
[`src/Conversia.Application/Demo/DemoAccounts.cs`](../src/Conversia.Application/Demo/DemoAccounts.cs),
which both the seeder and the Web read. With the demo on, `/demo` lists the
accounts and `/login` has one "Entrar como ..." button per account.

| Button on `/login` | Email | Tenant | Permissions |
|---|---|---|---|
| Entrar como propietario de Acme | `owner@acme-demo.example` | Acme Demo | owner: everything in its tenant |
| Entrar como gestor de Acme | `manager@acme-demo.example` | Acme Demo | `ProductsView`, `ProductsManage`, `UsersView` |
| Entrar como lector de Acme | `viewer@acme-demo.example` | Acme Demo | `ProductsView` |
| Entrar como miembro sin permisos de Acme | `noaccess@acme-demo.example` | Acme Demo | none |
| Entrar como propietario de Globex | `owner@globex-demo.example` | Globex Demo | owner: everything in its tenant |

In the rest of this guide the accounts are called owner, manager, viewer,
noaccess and Globex owner. The permission enum names (`ProductsView`, ...) stay
in routes, JSON and `data-*` attributes; the UI shows their Spanish labels:
`UsersView` is "Ver usuarios", `UsersManage` "Gestionar usuarios",
`ProductsView` "Ver productos" and `ProductsManage` "Gestionar productos".

Data: Acme Demo has 13 products (`ACME-001` to `ACME-012` and `SKU-001`),
Globex Demo has 5 (`GLX-002` to `GLX-005` and `SKU-001`). Product names are
Spanish (for example "Yunque de 50 kg", `ACME-001`). The seeding rules
are in [USERS_TENANTS_PERMISSIONS.md](USERS_TENANTS_PERMISSIONS.md#demo-data).

## Presenter script

About 15 minutes. Open the Web in a normal window, plus a second browser or
private window for scenes 2 and 5. Each scene names the template feature it
demonstrates.

| # | Scene | Template feature |
|---|---|---|
| 1 | The tour page | Demo mode, what the template contains |
| 2 | Viewer: read-only | Permission-based authorization |
| 3 | Manager: edit, and a stale edit | Optimistic concurrency |
| 4 | Globex owner: same SKU, other catalog | Tenant isolation |
| 5 | Noaccess: refused | Authorization failure handling |
| 6 | Owner: users and permissions | Per-request permission lookup |
| 7 | Login throttling | Rate limiting |
| 8 | API tour | The API as the single source of truth |

### 1. The tour page

1. Open `/demo` without signing in.
2. Show the feature list, the accounts table and the shared password.
3. Open `/login` and point at the "Cuentas de demostración" panel with its one-click buttons.

Expected: the tour lists seven features and the five accounts. Without the
overlay the page shows the feature list only and `/login` has no panel.

### 2. Viewer: read-only (authorization)

1. On `/login`, click **Entrar como lector de Acme**.
2. Open **Productos**: 13 products over two pages ("Página 1 de 2"); try the
   search box (for example `yunque`, or `SKU-001`).
3. Note what is missing: no "Agregar un producto" form, no Editar or Eliminar links.
4. Open `/demo`: the "Tú, ahora mismo" panel shows "Ver productos" and the access
   matrix.
5. Show that hiding the buttons is only a convenience. In a terminal, log in
   as the viewer and try to create a product:

```bash
TOKEN=$(curl -s -X POST http://localhost:5030/api/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"viewer@acme-demo.example","password":"Demo-Pass-2026!"}' | jq -r .token)
curl -i -X POST http://localhost:5030/api/products -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"name":"Producto falso","sku":"FORGED-1","price":1,"stock":1}'
```

Expected: `HTTP/1.1 403 Forbidden`. The same `GET /api/products` with that
token is `200`. Also, `/users` for the viewer is a 403 page (no `UsersView`).

### 3. Manager: edit and a stale edit (optimistic concurrency)

1. **Entrar como gestor de Acme** (use the second browser for the other session
   below).
2. Create a product (for example SKU `DEMO-1`, price `19,99`), then edit and
   delete it: **Editar** and **Eliminar** are on every row. The SKU is
   read-only on the edit page. The price box accepts a comma or a dot as the
   decimal mark (`19,99`, `19.99`, `1,5` is 1.5); thousands separators such as
   `1.234,50`, negatives and more than two decimals are refused with a message.
   The list shows prices the Spanish way (`1.234,50`) and the edit form fills
   the price with a comma decimal (`19,99`).
3. Stale edit: open the edit page of one product (for example `ACME-012`) in
   **both** browsers, signed in as the manager (or owner). Save a new price in
   browser A. Save a different name in browser B.

Expected: browser A returns to the list with "Producto guardado."; browser B
shows "Otra persona modificó este producto. Recarga la página para ver la
última versión." with a **Recargar el producto** link, and A's change is not
overwritten. The mechanism: every save carries the `rowVersion` read with
the form, and the API answers `409` when it is stale.

4. Optional: the manager also sees `/users`, but without the "Agregar un usuario"
   form or the permission buttons ("Ver usuarios" without "Gestionar usuarios").

### 4. Globex owner: same SKU, other catalog (tenant isolation)

1. Sign out ("Cerrar sesión"), then **Entrar como propietario de Acme** and
   search `SKU-001`: "Caja de envío estándar".
2. Sign out, **Entrar como propietario de Globex**, search `SKU-001`: "Carcasa
   de la máquina del juicio final". The list has 5 products and no `ACME-*` SKU.

Expected: both tenants hold `SKU-001` (a SKU is unique per tenant, not
globally), and each only ever sees its own catalog. No query in the code has a
`WHERE TenantId = ...`: the EF Core global query filter does it
([EXTENDING.md](EXTENDING.md#2-infrastructure)). Optional: copy a product id
from Acme's edit URL and open it as Globex owner: "Producto no encontrado.", the same
answer as for an id that does not exist.

### 5. Noaccess: refused

1. **Entrar como miembro sin permisos de Acme**. The nav shows Productos and
   Usuarios because every signed-in user gets the links.
2. Open **Productos**.

Expected: a `403` page saying "No tienes permiso para hacer eso." with no data
and no controls. `/users` is the same, while `/demo` still works and shows an
empty permission list.

### 6. Owner: users and permissions (checked on every request)

1. In the second browser sign in as **noaccess** and keep `/products`
   open (403).
2. In the first browser **Entrar como propietario de Acme**, open **Usuarios**, and create a
   user (for example `trainee@acme-demo.example` with a 12+ character password
   with upper case, lower case and a digit).
3. Next to `noaccess@acme-demo.example`, click **Conceder** at "Ver productos".
4. In the second browser reload `/products` **without signing in again**: the
   list appears. Click **Revocar** in the first browser and reload once more: 403
   again.

Expected: permissions are not stored in the token; they are read from the
database on every request, so grant and revoke apply on the very next
request. The same through the API: the new user's token goes from `403` to
`200` to `403` on `GET /api/products` while it stays the same token. Both
actions are idempotent (`204`).

### 7. Login throttling (rate limiting)

Eleven wrong logins from one address inside a minute:

```bash
for i in $(seq 1 11); do
  curl -s -o /dev/null -w "attempt $i -> %{http_code}\n" -X POST http://localhost:5030/api/auth/login \
    -H 'Content-Type: application/json' -d '{"email":"owner@acme-demo.example","password":"wrong"}'
done
curl -i -X POST http://localhost:5030/api/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"owner@acme-demo.example","password":"wrong"}'
```

Expected: attempts 1 to 10 are `401`, attempt 11 and later are `429` with
`Retry-After: 60` (seconds) and a `application/problem+json` body whose detail
is "Demasiados intentos de inicio de sesión. Inténtalo de nuevo más tarde." In
the Web the login form shows "Demasiados intentos. Vuelve a intentarlo en N
segundos." ("1 segundo" for one second).
Every login request counts, including the demo shortcut buttons and
successful ones, and the budget is per client address (10 per 60 seconds by
default, `RateLimiting:Login:*`). Wait a minute before continuing the demo.
Mind who the API sees: your `curl` calls arrive from your machine (with Docker,
from the Docker bridge address), but sign-ins made through the Web arrive
from the Web server's address, so all browsers share one budget of 10 sign-ins
per minute. Do not click through all five accounts back to back.

### 8. API tour

Everything the Web does goes through this API. Set the base URL and log in:

```bash
API=http://localhost:5030
TOKEN=$(curl -s -X POST $API/api/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"manager@acme-demo.example","password":"Demo-Pass-2026!"}' | jq -r .token)

# register a brand-new workspace (201 + a token for its owner)
curl -s -X POST $API/api/auth/register-tenant -H 'Content-Type: application/json' \
  -d '{"tenantName":"Initech","email":"owner@initech.example","password":"Initech-Pass-2026"}'

curl -s $API/api/users/me -H "Authorization: Bearer $TOKEN"
curl -s "$API/api/products?pageSize=3" -H "Authorization: Bearer $TOKEN"
curl -s "$API/api/products?search=yunque" -H "Authorization: Bearer $TOKEN"

# create (201 + Location header), keep id and rowVersion
curl -s -i -X POST $API/api/products -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"Artículo de prueba","sku":"DEMO-1","price":19.99,"stock":10}'
# the same SKU again (any letter case) -> 409
# update: send the rowVersion of the last read; a stale one -> 409
curl -s -X PUT $API/api/products/<id> -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"Artículo de prueba v2","price":24.50,"stock":8,"rowVersion":"<rowVersion>"}'
curl -s -X DELETE $API/api/products/<id> -H "Authorization: Bearer $TOKEN"    # 204, then GET -> 404
```

Expected statuses: `201`, `409` for the duplicate SKU and for the stale
`rowVersion`, `204` for the delete, `401` without a token. Error bodies are
Spanish whatever `Accept-Language` says (for example `{"title":"SKU duplicado",
"detail":"Ya existe un producto con ese SKU."}`), and invalid input answers
`400` with the title "Uno o más errores de validación.". Grant a permission
with `POST /api/users/{userId}/permissions` and body `{"permission":3}` (the
number of the `Permission` value; a name is not accepted in the body), and
revoke with `DELETE /api/users/{userId}/permissions/ProductsView` (name or
number). The full endpoint table is in the [README](../README.md#api-endpoints).

When the API runs in `Development` (the "without Docker" variant above),
`/openapi/v1.json` lists every endpoint and
[`src/Conversia.API/Conversia.API.http`](../src/Conversia.API/Conversia.API.http)
holds the same calls as ready-to-run requests for VS Code (REST Client) and Rider.

## Safety notes

- **Never use the demo in production.** Anyone can read the password on this
  page. With `Demo:ShowAccounts` on, the login page's shortcut forms even carry
  it in the HTML.
- The code enforces it: `Demo:Enabled` (API) and `Demo:ShowAccounts` (Web)
  are only accepted in the `Development` and `Demo` environments. In any other
  environment, or with a value that is not `true`/`false`, the app refuses to
  start with an error naming the key. A password that breaks the registration
  policy is refused the same way (the error never contains the password).
- Both keys are off in every shipped `appsettings*.json`. Never name a
  production environment `Demo`.
- The overlay publishes the API and the Web on all interfaces of your machine
  (PostgreSQL only on `127.0.0.1`). Do not run it on a shared network, and do
  not deploy `docker-compose.demo.yml`.
- `Demo` behaves like `Production` apart from the two demo keys: no developer
  exception page and no OpenAPI document; the containers serve plain HTTP.

## Resetting the demo

```bash
docker compose -f docker-compose.yml -f docker-compose.demo.yml down -v
docker compose -f docker-compose.yml -f docker-compose.demo.yml up --build
```

`-v` deletes the database volume, so the next start migrates and seeds from
scratch. A plain `down` / `up` keeps your changes: a demo tenant is seeded
only when its owner email does not exist yet, so a restart never duplicates
or restores anything you edited or deleted. Without Docker, drop the
`conversia` database (or remove the PostgreSQL container) and repeat the
quick start.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Set POSTGRES_PASSWORD in .env` or `Set JWT_SIGNING_KEY in .env` | There is no `.env`, or the value is empty. `cp .env.example .env` and edit it. |
| `postgres` never turns healthy, or the migrator / API report a failed login | `docker compose logs postgres`. The volume keeps the first `POSTGRES_PASSWORD` / `POSTGRES_USER` / `POSTGRES_DB`: if you changed them in `.env` afterwards, run `down -v` and start again. |
| `port is already allocated` / address in use | Set `WEB_PORT`, `API_PORT` or `POSTGRES_PORT` in `.env` (for example `POSTGRES_PORT=54320`). |
| The API container exits or the Web cannot reach it | `docker compose logs migrator api`. The migrator must have exited with code 0; the API fails fast with a message naming the setting when `Jwt__SigningKey` is short, or `Demo:Enabled` is set outside `Development`/`Demo`. |
| Demo accounts are missing, API log says `Seeding the demo data failed` | The database was unreachable or not migrated when the API started. Fix the cause and `docker compose restart api`; seeding is idempotent. |
| `/login` has no demo panel, or the demo accounts return 401 | You started without the overlay (base compose has no demo), or `DEMO_PASSWORD` was changed after the data was first seeded (existing tenants keep their old password). Use `-f docker-compose.demo.yml` and run `down -v`. |
| Signed out after restarting | `docker compose restart web` keeps the session, but a re-created `web` container (`up --build` after a change, `--force-recreate`) loses its data-protection keys, so the old cookie is invalid. Sign in again. |
| Product search does not find `Imán` when you type `iman` (`imán` does) | Search ignores letter case but is accent-sensitive. A client project that wants accent-insensitive search can enable the `unaccent` extension and compare `unaccent("Name")`, or search a computed, normalized column. |
| `429 Too Many Requests` on login | More than 10 login requests in a minute from one address (sign-ins through the Web all count against the Web server's address). Wait for `Retry-After`. |
| `docker compose up --build` fails restoring NuGet packages | The build needs internet access to nuget.org (a TLS-intercepting proxy needs its CA trusted inside the build). |
