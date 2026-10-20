# Extending Conversia: adding a feature

Branches (`Branch`, under "Organización") are the **reference feature**: a small,
tenant-scoped entity that touches every layer the way a real feature does. This
guide walks through it layer by layer with the real files, so you can copy the
shape and replace the details. Below, the new feature is called `Order` as a
placeholder. (Advisors and WhatsApp numbers follow the same pattern in the same
files; `Conversation` adds read-side projections, see
[WEBHOOKS.md](WEBHOOKS.md) and [METRICS.md](METRICS.md).)

Before you start: read [ARCHITECTURE.md](ARCHITECTURE.md) (dependency
direction, per-layer conventions) and follow the process in
[WORKFLOW.md](WORKFLOW.md) (tests first, isolated review roles, one commit
per step).

| Section | What |
|---|---|
| [The checklist](#the-checklist) | One tick-list from entity to page |
| [Layer by layer](#layer-by-layer) | The Branch files, what each one shows |
| [Migrations](#migrations) | The commands that work in this repo |
| [Tests](#tests-at-each-level) | What to write where |
| [Pitfalls](#pitfalls-hit-in-this-codebase) | Mistakes that were made once |

## The checklist

Paths are relative to `src/`. "Copy from" is the Branch file to start from.

- [ ] **Domain** `Conversia.Domain`
  - [ ] Entity inheriting `BaseEntity`, with `TenantId`, private setters, a
    `Create` factory and the validation rules (copy from `Entities/Branch.cs`)
  - [ ] New `Permission` values, numbers never reused (`Enums/Permission.cs`)
  - [ ] Repository interface (`Interfaces/IBranchRepository.cs`)
- [ ] **Infrastructure** `Conversia.Infrastructure`
  - [ ] `IEntityTypeConfiguration<T>` with required columns, max lengths,
    unique index including `TenantId`, FK to `Tenants`, `citext` for text that
    must be unique ignoring case (`BranchConfiguration` in
    `Persistence/Configurations/OrganizationConfigurations.cs`)
  - [ ] `DbSet<T>` and the global query filter in `Persistence/ApplicationDbContext.cs`
  - [ ] Migration ([commands](#migrations))
  - [ ] Repository (`Repositories/BranchRepository.cs`)
- [ ] **Application** `Conversia.Application`
  - [ ] Service interface and implementation (`Interfaces/IOrganizationService.cs`, `Services/OrganizationService.cs`)
  - [ ] DTOs with DataAnnotations (`DTOs/Organization/BranchDtos.cs`) and custom validation
    attributes that delegate to the domain rules (`Validation/TrimmedTextAttribute.cs`)
  - [ ] Exceptions for the outcomes callers must tell apart (`Exceptions/OrganizationExceptions.cs`)
- [ ] **API** `Conversia.API`
  - [ ] Controller with a policy per action (`Controllers/BranchesController.cs`)
  - [ ] One `catch` per new exception in `Middleware/ExceptionHandlingMiddleware.cs`
  - [ ] DI registration in `Program.cs`
- [ ] **Web** `Conversia.Web`
  - [ ] Members on `Services/IApiClient.cs` and `Services/ApiClient.cs`
  - [ ] The page (`Components/Pages/Organization.razor`) and its form view models (`Services/OrganizationInput.cs`)
  - [ ] Nav link in `Components/Layout/ShellHeader.razor`
- [ ] **Tests** at every level ([below](#tests-at-each-level))
- [ ] **Docs**: README endpoint table, `docs/USERS_TENANTS_PERMISSIONS.md` permission table

## Layer by layer

### 1. Domain

| File | What to copy |
|---|---|
| `Domain/Entities/Branch.cs` | Inherits `BaseEntity` (`Id` and the four audit fields; **never set audit fields by hand**, `ApplicationDbContext.SaveChangesAsync` stamps them). `TenantId` has a private setter and is passed to `Create` by the service, never taken from a request. A private parameterless constructor is only for EF. Rules live here as `const` limits plus a rule message (`NameMaxLength`, `NameRuleMessage`, built with `Common/TextRules`), and `Create` throws `DomainException` after validating everything **before** assigning anything. |
| `Domain/Enums/Permission.cs` | Add `OrdersView = 10` and `OrdersManage = 11`. The numbers are stored in `UserPermissions.Permission`, so never renumber or reuse a value (`PermissionCatalogTests` pins them; `3` and `4` are retired). No table or migration is needed for a new permission. Adding a value also creates its authorization policy (the loop over `Enum.GetValues<Permission>()` in `API/Program.cs`) and adds its grant/revoke buttons to the Users page. |
| `Domain/Interfaces/IBranchRepository.cs` | The contract, owned by Domain. The doc comments state the semantics callers rely on: reads are untracked, another tenant's row is `null`. |

### 2. Infrastructure

| File | What to copy |
|---|---|
| `Infrastructure/Persistence/Configurations/OrganizationConfigurations.cs` (`BranchConfiguration`) | `ToTable`, `IsRequired` and `HasMaxLength` taken from the domain constants, the `Name` column as `citext` (case-insensitive text), a **unique index on `(TenantId, Name)`** (unique per tenant, not globally, ignoring case, and the arbiter of concurrent creates) and the FK to `Tenants` with `DeleteBehavior.Restrict`. The class is found by `ApplyConfigurationsFromAssembly`; there is nothing to register. |
| `Infrastructure/Persistence/ApplicationDbContext.cs` | Add `public DbSet<Order> Orders => Set<Order>();` and, in `OnModelCreating`, `modelBuilder.Entity<Order>().HasQueryFilter(o => o.TenantId == _currentTenantService.TenantId);`. **Every tenant-scoped entity needs this filter**; it is what keeps tenants apart, so no query writes a `TenantId` predicate by hand. |
| `Infrastructure/Repositories/BranchRepository.cs` | Reads that only display use `AsNoTracking()`. `AddAsync` catches `DbUpdateException` through `IsUniqueViolation()` (`Persistence/DbUpdateExceptionExtensions.cs`) and throws `DuplicateBranchNameException`. |

### 3. Application

| File | What to copy |
|---|---|
| `Application/Interfaces/IOrganizationService.cs`, `Services/OrganizationService.cs` | A service talks to repository interfaces only, never to `DbContext`. The controller passes the caller's tenant id (from the JWT) to `CreateBranchAsync`. There is **no existence pre-check** before inserting: it would cost a round trip and still race, so the unique index decides. Not found and cross-tenant are the same `BranchNotFoundException`. |
| `Application/DTOs/Organization/BranchDtos.cs` | Requests are plain classes with DataAnnotations (`[Display]`, `[TrimmedText]`). `CreateBranchRequest` has no tenant property. `BranchResponse` has a static `From(entity)`. List endpoints that page return `DTOs/PagedResponse<T>`. |
| `Application/Validation/TrimmedTextAttribute.cs`, `ChannelFieldAttributes.cs` | Custom `ValidationAttribute`s that **call the domain rule** (`TextRules`, `Branch.NameRuleMessage`), so the rule exists once and the API's `400` and the entity's `DomainException` can never disagree. |
| `Application/Exceptions/OrganizationExceptions.cs` | One small exception per outcome the API maps to a status: `BranchNotFoundException` (404), `DuplicateBranchNameException` (409). Messages are safe to return to the caller. |

### 4. API

| File | What to copy |
|---|---|
| `API/Controllers/BranchesController.cs` | `[ApiController]`, `[Route("api/branches")]`, class-level `[Authorize]` (so no token is `401`) and `[Authorize(Policy = nameof(Permission.OrganizationView))]` / `OrganizationManage` per action (so a missing permission is `403`). `POST` returns `CreatedAtAction(nameof(GetBranch), ...)`, which produces the `201` and the `Location` header. Actions contain no `try/catch`. |
| `API/Middleware/ExceptionHandlingMiddleware.cs` | Add a `catch` per new exception that writes a `ProblemDetails` with the right status. Order matters: specific exceptions first, then `DomainException` (400), then the final `Exception` (logged, generic 500 with no message). |
| `API/Program.cs` | `builder.Services.AddScoped<IBranchRepository, BranchRepository>();` and `AddScoped<IOrganizationService, OrganizationService>();`. Policies need nothing: they are generated from the `Permission` enum. |

### 5. Web

The Web talks to the API only, with the DTOs from Application, and never
references Infrastructure (an architecture test enforces it).

| File | What to copy |
|---|---|
| `Web/Services/IApiClient.cs`, `ApiClient.cs` | One method per endpoint (`GetBranchesAsync`, `CreateBranchAsync`, ...), each a one-liner over `SendAsync<T>`. The token is an argument of every call. Every outcome comes back as an `ApiResult<T>` (`Success`, `ValidationFailed`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `RateLimited`, `Unavailable`); the client never throws for a backend problem. |
| `Web/Components/Pages/Organization.razor` | `/organizacion`: the lists and the create forms of branches, advisors and WhatsApp numbers on one page. Each form is an `EditForm ... method="post"` with a `FormName` and a `data-testid`; a successful post redirects to `/organizacion?notice=...` (`Services/OrganizationNotice.cs`). |
| `Web/Services/OrganizationInput.cs` | The form view models (`NewBranchInput`, ...): the same rules as the entity through `[TrimmedText]`, and a `ToRequest()` that converts only after validation. |

Rules every Web page follows:

- **Antiforgery on every POST.** `UseAntiforgery()` is in `Program.cs`.
  `EditForm ... method="post"` with a `FormName` renders the token by itself;
  a plain `<form method="post" @formname="...">` needs `<AntiforgeryToken />`.
  Minimal-API form endpoints add `.RequireValidAntiforgery()`
  (`Http/AntiforgeryEndpointExtensions.cs`).
- **The API is the authority.** Controls are shown with `profile.Can(Permission...)`
  only as a convenience. Keep the POST forms in the render tree even when the
  caller may not manage (they are `hidden`): a forged post must reach the API
  and be refused there, instead of crashing the page.
- **Post/redirect/get with a fixed code**: after a write the page redirects to
  `/organizacion?notice=created`. Never echo posted data into the URL.
- **Page order**: a POST is an action, so its handler loads the lists only when
  it has to show them again.

The Blazor traps:

1. **A page-set 404 is re-executed.** `Program.cs` uses
   `UseStatusCodePagesWithReExecute("/status/{0}")` (`/not-found` and
   `/status/404` are the same page), which replaces the body of
   any `404` with the generic Not found page, including a `404` your page set
   on purpose. A page that wants to say *what* is missing marks the request
   (`Http/NotFoundMarker.cs`, set by the conversation and number pages) and
   `Components/Pages/NotFound.razor` reads the marker.
2. **Numeric binding turns bad input into 0.** Static SSR binding of an
   `int`/`decimal` form field maps blank, unparsable or missing text to `0`,
   which would then be saved as something the user never typed. A form view
   model keeps a number as `string`, validates it itself and converts only after
   validation (`ToRequest()`), so a refused value is never bound to 0.

## Using the design system

The Web has its own small design system (no Bootstrap, no CSS framework, no
build step). Everything lives in `src/Conversia.Web`.

### A new page in the shell

```razor
@page "/widgets"
@attribute [Authorize]
@inherits ApiPageBase

<PageTitle>Widgets - Conversia</PageTitle>

<div class="page" data-page="widgets">
    <PageHeader Eyebrow="Catálogo" EyebrowIcon="box" Title="Widgets" Description="Lo que hace esta página.">
        <a class="btn btn--primary" href="/widgets/new"><Icon Name="plus" />Nuevo widget</a>
    </PageHeader>

    <Card Title="Resumen" IconName="layers">...</Card>
</div>
```

`MainLayout` is the default layout: it adds the sidebar (or the top bar for
visitors), the skip link, `<main id="main">` and the footer. Wrap the content
in one `div.page[data-page]`, put exactly one `h1` in it (`PageHeader` does)
and do not skip heading levels. To show the page in the navigation add a
`<NavItem Href=... Label=... IconName=... />` to `Components/Layout/ShellHeader.razor`;
the current entry is marked from the URL. A page that has already loaded the
caller's profile should call `Shell.Remember(profile)` so the account card can
show the role.

### Components (`Components/Shared/`)

| Component | Use it for |
|---|---|
| `PageHeader` | The `h1`, a short description, an eyebrow and the primary action(s) (child content). |
| `Card` | A surface; `Title`, `Subtitle`, `IconName` add a heading. Extra attributes (`data-testid`) go to the element. |
| `Alert` | `Kind` = `Danger`/`Warning` (announced with `role="alert"`) or `Success`/`Info` (`role="status"`); optional `Title` and `Actions`. |
| `Chip` | Badges and statuses; `Tone`, `IconName`, `Mono`. Pass `data-*` hooks straight through. |
| `FormField` + `TextInput` | A labelled control with icon, hint and error messages. It reads the `EditContext` and the `ApiErrors` dictionary, and hands the control the `aria-invalid`/`aria-describedby` attributes: `<FormField For="() => Input.Email" Id="email" Label="..." Context="field"><TextInput id="email" @bind-Value="Input.Email" @attributes="field" Invalid="field.Invalid" /></FormField>`. A plain `<input>` takes `@attributes="field"` only. |
| `EmptyState` | A list with nothing to show: `Title`, `Text`, `Art` (`box`, `search`) and `Actions`. Carries `data-testid="empty-state"`. |
| `StatusView` | A 403/404/error page: `DataPage` (`forbidden`, `not-found`, `error`), `Code`, `Title`, message and `Actions`. |
| `ForbiddenView`, `ConversationNotFoundView`, `ChannelNotFoundView` | The 403 and the "not found" pages, ready made on `StatusView`: `<ForbiddenView />` when `Denied`, `<ConversationNotFoundView />` when the conversation is missing. |
| `InPageLink` | A link that jumps inside the page: `<InPageLink Fragment="id" class="btn">...</InPageLink>` (see Tokens below for why not `href="#id"`). |
| `Avatar`, `BrandMark`, `ThemeToggle`, `Illustration`, `Icon` | Small pieces. |

CSS classes used by the markup: buttons `btn btn--primary|secondary|ghost|danger|danger-ghost` (+ `btn--sm`, `btn--block`),
`card`, `chip`, `alert`, `field`, `input`, `table-wrap > table.table` (a number
column carries `data-align="end"`), `grid-cards`, `stack`, `cluster`, `kv`.

### Tokens

`wwwroot/css/tokens.css` is the only place with raw colours, sizes and shadows.
Use the custom properties (`--brand`, `--surface`, `--text-muted`, `--border`,
`--space-4`, `--radius-md`, `--shadow-md`, `--text-h2`, ...) in your own CSS;
a colour that differs between the schemes is written once as
`var(--if-light, <light value>) var(--if-dark, <dark value>)`, so a new component
needs no dark rules (the scheme blocks at the end of `tokens.css` switch the two
variables; no `light-dark()`, so old browsers work too). Keep text at 4.5:1
contrast against its background in both schemes. Put a new component's CSS in
`components.css` and a page-only layout in `pages.css`.

A link that jumps inside the page (the skip link, a button that opens a panel via
`:target`) must use `<InPageLink Fragment="id" class="...">`, never a bare
`href="#id"`: the page has `<base href="/">`, so a bare fragment resolves to
`/#id` and leaves the page. `InPageLink` points at the current path and query
plus the fragment and opts out of Blazor's enhanced navigation, so the browser's
own jump (focus and `:target`) happens, with or without JavaScript.

### Supported browsers

The UI targets current evergreen browsers: Chrome/Edge 86+, Firefox 85+ and
Safari 15.4+ (the floor is `:focus-visible`). Newer features have a plain
fallback written first, so an older engine gets a readable, correct page that
only lacks the refinement:

| Feature | Needs | Without it |
|---|---|---|
| `color-mix()` (header and card translucency, hover and tint borders) | Chrome/Edge 111, Firefox 113, Safari 16.2 | the solid `--surface` / `--*-soft` colour |
| `oklch()` (avatar tints) | Chrome/Edge 111, Firefox 113, Safari 15.4 | the brand tint |
| `100dvh` (full-height panels) | Chrome/Edge 108, Firefox 101, Safari 15.4 | `100vh` |
| `text-wrap: balance` | Chrome/Edge 114, Firefox 121, Safari 17.5 | ordinary wrapping |

The colour tokens do not use `light-dark()` (Chrome/Edge 123, Firefox 120, Safari
17.5) so the light and dark schemes work in every browser that has custom
properties. When you add CSS with a newer feature, put the plain declaration
before it and list the feature here.

### Adding an icon

Draw it on a 24 x 24 grid with round caps and joins (no fill, the stroke is
`currentColor`) and add its inner SVG markup to `Components/Shared/IconSet.cs`
under a short name. Use it with `<Icon Name="that-name" />` (`Class="icon--sm"`
for 16 px). Icons are `aria-hidden`: the text next to them is the accessible
name, so an icon-only button needs an `aria-label`.

### How the theme works

`<html>` follows `prefers-color-scheme`. `data-theme="light"` or `"dark"` forces
a scheme. The button in the shell (`ThemeToggle`, `data-theme-toggle`) is handled
by `wwwroot/js/shell.js`, which flips `data-theme` and stores it in
`localStorage` (`dr-theme`); the inline script in `App.razor` re-applies it before
the first paint. Without JavaScript the button is hidden and the system scheme
wins. Do not add inline `style` attributes or more inline scripts.

Blazor's enhanced navigation (a click on an in-app link without a page load)
patches the `<html>` attributes with the ones of the server's page, which drops
`data-theme` and the `js` class that reveals the button. `shell.js` therefore
re-applies both (the choice is read from `data-theme` or `localStorage`) in a
`Blazor.addEventListener("enhancedload", ...)` handler; keep any other state you
keep on `<html>` or `<body>` in that handler too. The handler also moves focus to
the new page's `main h1`, which replaces `<FocusOnNavigate>`: that component
focuses the heading on a full load as well, so the skip link would never be the
first Tab stop.

## Translating / changing the UI language

The UI and the messages users read are Spanish, and the culture is fixed to `es`:
both hosts use `UseRequestLocalization` with `es` as the only supported culture and
no culture provider, so `Accept-Language`, cookies and query strings change
nothing (`Web/Program.cs`, `API/Localization/SpanishApiExtensions.cs`). The `es`
culture itself is `Domain/Common/SpanishCulture.cs`, which also fixes the number
separators (`1.234,50`) so output does not depend on the host's ICU data. To
translate the product, change the text in these places and nowhere else; code
identifiers, logs, configuration keys, routes, enum names and JSON property names
stay in English:

| Text | Where it lives |
|---|---|
| Wording used by more than one page (errors, notices, retry hints) | `Web/Services/UiMessages.cs`, `OrganizationNotice.cs`, `ConversationsNotice.cs`, `UsersNotice.cs` |
| Permission labels | `Web/Services/PermissionLabels.cs` (add one when you add a `Permission` value) |
| Static text of a page | The page's markup under `Web/Components`; `<html lang>` in `App.razor` |
| Validation messages and field names of the request DTOs | `Application/Validation/ValidationMessages.cs` and the `[Display(Name = ...)]` of each property |
| Exception messages (they become the `detail` of API errors) | The exception classes in `Application/Exceptions` and the `DomainException` calls in `Domain/Entities` |
| API problem titles, model-binding and JSON error messages | `API/Localization/ProblemTexts.cs` and `SpanishApiExtensions.cs` |

Switching to another language means replacing those strings and the culture in
`SpanishCulture`; adding a second language at run time would additionally need a
culture provider and resource files, which this project deliberately does not have.

## Migrations

There is no `IDesignTimeDbContextFactory`: `dotnet ef` boots the API host to
find the `DbContext`, and the host refuses to start without a valid
`Jwt:SigningKey` (at least 32 bytes). So give the command a throwaway key
through the environment. This is the same trick the API Dockerfile uses to
build the migrations bundle.

```bash
dotnet tool restore                                          # once; dotnet-ef 10.0.11 from .config/dotnet-tools.json (same version as the EF packages)
export Jwt__SigningKey="design-time-only-$(openssl rand -hex 24)"

EF="--project src/Conversia.Infrastructure --startup-project src/Conversia.API"
dotnet ef migrations add AddOrders $EF       # no database needed; files land in Persistence/Migrations
dotnet ef migrations list $EF --no-connect   # the migrations known to the code
dotnet ef database update $EF                # needs ConnectionStrings__DefaultConnection (or user secrets)
```

Always read the generated migration (EF warns when an operation may lose
data) and commit the `.cs`, the `.Designer.cs` and the updated
`ApplicationDbContextModelSnapshot.cs` together. Docker Compose applies new
migrations by itself: the `migrator` image bundles all migrations and runs
before the API.

## Tests at each level

Write them first, in the order and roles of [WORKFLOW.md](WORKFLOW.md). No EF
InMemory provider anywhere; persistence is tested on a real PostgreSQL.

| Level | Project and Branch examples | What it proves |
|---|---|---|
| Domain | `tests/Conversia.Domain.UnitTests/Entities/OrganizationEntitiesTests.cs`, `PermissionCatalogTests.cs` | Invariants, boundary lengths, a rejected create changes nothing, permission numbers stay put |
| Application | `Application.UnitTests/Services/OrganizationServiceTests.cs` (Moq repository), `DTOs/OrganizationRequestValidationTests.cs` | What the service returns, throws and hands to the repository; DataAnnotations and the custom attributes agree with the domain |
| Infrastructure | `Infrastructure.UnitTests` (permission handler) | Pure authorization logic. Repositories are covered by the integration tests |
| API integration | `IntegrationTests/Organization*Tests.cs` plus `TestHelpers.Organization.cs` and `TestHelpers.Tenants.cs` (`StartTenantAsync`, `TestTenant`), `[Collection(nameof(IntegrationTestCollection))]`, `CustomWebApplicationFactory` (Testcontainers PostgreSQL) | Endpoint contracts and statuses, `401`/`403` per permission, tenant isolation (cross-tenant answers equal "unknown id"), concurrent creates, persisted values |
| Web integration | `Web.IntegrationTests/Organization/OrganizationPageTests.cs` with `OrganizationStub` (a stand-in API), `[Collection(nameof(WebIntegrationCollection))]`, `TestApiFactory`, `WebTestHost` | The real Web against a stand-in or the real API: forms, antiforgery, forged posts, who sees which controls. `Infrastructure/PageContract.cs` documents the `data-testid` hooks the tests rely on, so keep them when you change the markup |

## Pitfalls hit in this codebase

| Pitfall | What happens and what to do |
|---|---|
| **Case-sensitive text in PostgreSQL** | PostgreSQL compares `text` and `varchar` case-sensitively (SQL Server's default is the opposite). This project keeps the case-insensitive behaviour on purpose: `Users.Email` and `Branches.Name` are `citext` columns (the `citext` extension is created by the first migration), so `Centro` and `centro` collide in the unique index (`409`) and an email logs in whatever its letter case. For a new column that must be unique ignoring case, use `HasColumnType("citext")`; for case-insensitive search on a plain text column, use `EF.Functions.ILike` with the wildcards of the typed text escaped. Test with letter-case variants. Sorting is a separate matter: `ORDER BY` on a plain text column follows the database collation (on the `postgres:17-alpine` image that is byte order, so uppercase letters sort before lowercase ones). |
| **Comma-list enum binding** | `Enum.TryParse("1,2")` accepts a comma list and combines the values as flags (`"1,2"` equals the defined value `3`), and it accepts out-of-range numbers. Any enum taken from a route or query string needs `Enum.IsDefined` and a comma check, as in `API/ModelBinding/DefinedPermissionModelBinder.cs`. JSON bodies use `[EnumDataType]` and carry the **number** (`{"permission":3}`); a name is not accepted there. |
| **`ExecuteDeleteAsync` and query filters** | `Branches.Where(b => b.Id == id).ExecuteDeleteAsync()` is one set-based `DELETE` and **does apply the global query filter**, so another tenant's id deletes nothing (and the controller answers the same `404` as for an unknown id). It also bypasses the change tracker: no audit stamping and no concurrency check. Fine for a delete; do not use `ExecuteUpdateAsync` for entities that need `UpdatedAt/UpdatedBy`. |
| **Optimistic concurrency** | Nothing in Conversia uses it today. If a feature needs "someone else changed this", PostgreSQL's `xmin` system column works as a row version (a `uint` property mapped with `IsRowVersion()`; Npgsql gives it no column of its own). EF's `UPDATE ... WHERE xmin = @original` uses the value **loaded** from the database, not the one the client sent, so compare the client's token with the loaded one yourself before saving, and tell "modified" (`409`) from "deleted meanwhile" (`404`) through `DbUpdateConcurrencyException` and `GetDatabaseValuesAsync`. The removed Products feature did this; see the git history before the `RemoveProducts` migration. |
| **Concurrent creates** | Do not check for a duplicate first. Insert and map the PostgreSQL unique violation (SQLSTATE `23505`, `IsUniqueViolation()`) to the domain exception. |
| **NUL characters and timestamp precision** | PostgreSQL text cannot store U+0000, so free text taken from a request is rejected when it holds one (`NoNullCharactersAttribute` on the DTOs, plus the same rule in the domain: branch names, tenant names, emails); do the same for any new text column. `timestamptz` keeps microseconds while `DateTime.UtcNow` has 100 ns ticks on Linux, so `ApplicationDbContext` stamps audit dates through `DatabaseTime.Truncate`; truncate any other date you persist and later compare with what you read back. |
| **Retrying execution strategy** | The API does not enable `EnableRetryOnFailure`. A manual transaction (`BeginTransaction`) or a transaction-level advisory lock (`pg_advisory_xact_lock`) is incompatible with it unless the whole unit of work runs inside `CreateExecutionStrategy().ExecuteAsync(...)`; rework such code before adding the option. |
| **Rate-limit partitions behind proxies** | The login limiter keys on `RemoteIpAddress`. Behind a reverse proxy, or when the Web calls the API (the API sees the Web server's address), everyone shares one budget. Configure the Forwarded Headers middleware with your known proxies before the limiter runs; the project does not add it. |
| **Inbound claim mapping** | JWT bearer is configured with `MapInboundClaims = false`; read `sub` through `JwtRegisteredClaimNames.Sub` and the tenant through `tenant_id`. Without it, `sub` becomes the legacy `NameIdentifier` URI. |
| **Records print secrets** | A `record`'s generated `ToString()` prints every property. Options that hold a secret (`Dialog360Options`, `ErrorReportingOptions`) are classes that override `ToString()`. |
| **Authorization before lookup** | Policies run before the target is resolved, so a caller without the permission gets `403` whether or not the id exists; for a caller with the permission, an id of another tenant and an unknown id are the same `404`. |
