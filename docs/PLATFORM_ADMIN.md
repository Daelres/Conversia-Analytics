# Platform administration

Conversia bills **per WhatsApp line** (a channel). A **platform administrator**, who belongs to no client company, approves each company
and sets how many lines it may have switched on, according to its commercial agreement. This document is the contract of that feature.

## Roles

| Role | Belongs to | Signs in at | Can |
|---|---|---|---|
| Company user (owner or member) | one company | `POST /api/auth/login` | Everything its permissions allow **inside its company**, while the company is Active. |
| Platform administrator | no company | `POST /api/admin/auth/login` | List companies, read their business summary, change their status (activate, pause, suspend, cancel, reactivate), delete a cancelled company's data, set their limits (lines, AI, sends), switch a line off, manage the 360dialog line licence inventory, monitor the service with counts only, read the audit trail. **Nothing else**: no conversations, messages, contacts, a company's metrics or users. |

The two never cross: each has its own token audience and signing key, and a token of one kind is a `401` on the endpoints of the other (see
[Security model](#security-model)).

## Lifecycle of a company

```
register-tenant
      |
      v
  Pending --(Active)--> Active --(Paused)--> Paused --(Active: resume)--> Active
      |                  |  ^                   |
      |                  |  +---(Active)---- Suspended (Manual | Billing) <--- Active, Paused, Pending
      |                  |                      |
      +------------------+----------------------+--(Cancelled)--> Cancelled --(Active, before the deletion starts)--> Active
                                                                     |
                                                                     +--(retention window passed, or "delete now")--> data deleted (tombstone)
```

### Transitions

An administrator moves a company with `PUT /api/admin/tenants/{id}/status` (`Suspended` from there is always **Manual**). The billing job of
unit 1c moves it through the hooks below (always audited as **the system**, with a reason code). Anything not in the table, including staying
where it is and going back to Pending, is a `409`.

| From \ to | Active | Paused | Suspended | Cancelled |
|---|---|---|---|---|
| **Pending** | admin | | admin (Manual) | admin |
| **Active** | | admin | admin (Manual); billing (`SuspendForBillingAsync`, Billing) | admin; billing (`CancelForNonPaymentAsync`) |
| **Paused** | admin ("Reanudar"; lands in Suspended/Billing while a billing hold is set) | | admin (Manual) | admin; billing |
| **Suspended, Manual** | admin ("Reactivar") | | | admin; billing |
| **Suspended, Billing** | admin ("Reactivar"); billing (`ResumeFromBillingAsync`) | | admin: makes it **Manual** (audited with `previousCause`), so a later payment no longer lifts it | admin; billing |
| **Cancelled** | admin ("Reactivar"), only while the data deletion has not started (`409` `tenant_deletion_started` after) | | | |

The billing hooks (`ITenantBillingLifecycle`, Application) are idempotent and answer `Changed`, `Unchanged` (already there: nothing written)
or `NotApplicable` (nothing written): `SuspendForBillingAsync` suspends an Active company; for a **Paused** one it records a **billing hold**
(`BillingHoldSince`, audited with `billingHold: true`) and the company stays paused, so its users keep seeing the pause; resuming the pause
while the hold is set lands in Suspended/Billing, and `ResumeFromBillingAsync` clears the hold (a cancellation does too); `ResumeFromBillingAsync` never lifts an
administrator's suspension nor a cancellation; `CancelForNonPaymentAsync` leaves a Pending company alone. Each takes a **reason code**
(`billing.past_due`, `billing.paid`, `billing.non_payment`, ...: lowercase segments joined by dots, at most 64 characters; free text is refused).
Reason codes are kept apart from the free-text reason an administrator types. An administrator can also pick one from a **closed list**
(`reasonCode` in `PUT .../status`: `admin.contract_signed`, `admin.agreed_pause`, `admin.customer_request`, `admin.contract_ended`,
`admin.payment_issue`, `admin.abuse`, `admin.other`; anything else is `400`); the form offers it first and the free text becomes the optional
detail. Every free-text reason (status, sandbox, AI, limits, deletion, licence notes) is capped (500 or 200 characters) and its form says not
to write personal data, since the audit trail keeps it forever.

### What each status does

| Status | Company users | Webhooks of its lines | Deliveries turned into messages, auto-close | Classification and LLM | Outbound sends |
|---|---|---|---|---|---|
| **Pending** (quota 0) | Can sign in, `GET /api/tenant`, `GET /api/users/me`. Every other endpoint: `403` `tenant_not_active` ("Tu empresa está pendiente de activación."). | `401`, nothing stored | No | No | No |
| **Active** | Everything their permissions allow. | Accepted on active lines | Yes | Yes | Yes |
| **Paused** (agreed pause) | As Pending ("Tu empresa está en pausa."); lands on `/cuenta-pendiente` with the pause wording. | **Accepted and stored**: no message is lost | Yes (a long pause must not merge unrelated chats into one conversation) | No: the conversations wait flagged and are analysed on resume | No (users are blocked) |
| **Suspended, Manual** (the team) | As Pending ("Tu empresa está suspendida."). Tokens already issued stop working on the very next request. | `401`, nothing stored (unchanged: an administrator's stop is a full stop, for abuse or a dispute) | No | No | No |
| **Suspended, Billing** (late payment) | As Pending ("Tu empresa está suspendida por un pago pendiente."), `suspensionCause: "Billing"` in the `403`; lands on the "pago pendiente" page. | **Accepted and stored** | Yes | No, until the payment | No |
| **Cancelled** | As Pending ("El servicio de tu empresa fue cancelado."); the landing page shows the deletion date. | `401`, nothing stored | No | No | No |

The `403` of a company that is not Active always carries `code` and `tenantStatus`, and `suspensionCause` (`Manual` or `Billing`) for a
suspension. `GET /api/tenant` also returns `suspensionCause` and, for a cancelled company, `dataDeletionDueAt`.

**Cancelling** (administrator or billing) does, in the same transaction under the company lock: switch every line off, release every licence
assigned to it (each audited as `license.released`), set `cancelledAt` and `dataDeletionDueAt = cancelledAt + Tenants:Deletion:RetentionDays`
(default **30**, 1 to 365, validated at startup), and audit `tenant.status_changed` with `deletionDueAt`, `linesDeactivated` and
`licensesReleased`. A cancelled company cannot be given a licence (`409` `tenant_cancelled`). **Reactivating** it before the deletion
starts clears the dates; its lines stay off until the company switches them on (assign its licences again first).

Existing companies at the time this feature was introduced were migrated to **Active** with `MaxActiveLines = max(4, their active
channels)`: 4 because the pilot customer has four branches, and never below what they already had switched on. Their channels keep their
`IsActive`.

## Deleting a company's data

Ley 1581 de 2012: once the service ends, the company's data (its customers' numbers, names and messages above all) must not be kept longer
than needed. A **background job** (`TenantDataDeletionService`, `Tenants:Deletion:*`) deletes everything of a cancelled company once its
retention window passed; the administrator can **delete now** from `/admin/empresas/{id}/eliminar-datos` (or
`POST /api/admin/tenants/{id}/data-deletion` `{confirmName, reason}`): only for a cancelled company (`409` `tenant_not_cancelled` otherwise),
typing the company's name (case and outer spaces aside, `400` otherwise) and a reason (1 to 200 characters, audited). The answer is `202`: the
job does it in the background (it is woken at once on that instance). Asking again changes nothing.

**How it runs.** Starting a deletion (the job when the window passed, or the administrator) locks the company row, marks
`DataDeletionStartedAt` (from then on the company **cannot be reactivated**), releases any licence still assigned, writes the tombstone and
audits `tenant.data_deletion_started` (`trigger` `Scheduled`/`Manual`; system with `deletion.retention_expired`, or the administrator with
`deletion.requested_by_admin` and the reason). Then, table by table in dependency order, it deletes **batches** of
`Tenants:Deletion:BatchSize` rows (default 1000), each in its own short transaction (30 s statement and lock limits) that also adds the
batch to the tombstone's counts. Last, in one transaction, it checks that nothing is left, deletes the company row, completes the tombstone and
audits `tenant.data_deleted` (system, `deletion.completed`, with the rows per table). No long lock; a run stopped half way (crash, restart) is
resumed by the next run from the first table, the emptied ones answer 0 rows, and the counts stay exact because each batch and its count
commit together; two instances serialize on the tombstone row (the one that arrives second answers `CompletedElsewhere`). A run makes at most
5 passes over the plan: if rows keep appearing (a defect), it stops, marks the tombstone **stuck** (`StuckSince`), logs an Error, and the next
run retries; the monitoring overview raises `tenant_deletion_stuck` (critical) with a count of stuck deletions (or started more than a day ago),
never a company. The window is due from its exact end (`DataDeletionDueAt <= now`, the job and the domain alike).

**Order** (children first; every foreign key is `RESTRICT`): `Messages`, `Opportunities`, `AiUsageConversations`, `Conversations`,
`ContactSuppressions`, `Contacts`, `WebhookEvents` (raw payloads), `AiUsageMonths`, `MessageSendOutcomes`, the assistant's `AssistantKnowledgeVersions`, `AssistantSettings` and `AssistantActivityCounts` (and
`ConversationAssistants` before `Conversations`), `WhatsAppChannels` (with the
encrypted provider key), `Advisors`, `Branches`, `UserPermissions`, `Users`, `ErrorLogs` (the company's error registry rows), then `Tenants`.
The plan is a list of constant table names in `TenantDataDeletionPlan`; `TenantDataDeletionTests` compares it with every table of the EF model
that has a `TenantId` (and every table that points at one of them), so a new tenant table fails the tests until the plan covers it, and proves
that after a deletion no table holds a row of that company and nothing of another company changed.

**What is kept.**

| Kept | Why |
|---|---|
| `PlatformAuditLogs` rows of the company | The evidence of who approved, stopped, cancelled and deleted it. They hold ids, statuses, counts, masked licence numbers, the company's own line numbers (`channel.deactivated`) and the reasons administrators typed: no customer data. |
| `TenantDeletions` (the tombstone) | One row per deleted company: its id, an **HMAC-SHA256 of its name** (trimmed, upper-cased) with the privacy key (`Privacy:HashKey`) and that key's id, the cancellation, start and completion dates, the trigger, the administrator who asked (null for the job), `StuckSince` and the rows deleted per table. The name itself is not kept (data minimisation: a company name can be a person's name for a sole trader); whoever holds the key can still check whether a given company was deleted, nobody else can. |

After the deletion the administrator's `GET /api/admin/tenants/{id}` is `404`, its webhook path `401`, and an old user token `401` with
`"code": "tenant_gone"` ("Tu empresa ya no existe en Conversia..."). The audit page shows the company by a short id.

**Runbook.** Cancel (`PUT .../status` `{"status":"Cancelled","reason":"..."}`); the company page shows the deletion date. To delete before
it: `/admin/empresas/{id}/eliminar-datos`. To follow a deletion: the company page says "La eliminación de los datos empezó ..." until it is
gone; the API log has "The data of tenant {id} was deleted: N rows." (Information) or "The data deletion of tenant {id} stopped and will resume
at the next run" (Error, with the exception type only). The job runs at startup, every `Tenants:Deletion:IntervalMinutes` (60) and when
woken; `Tenants:Deletion:Enabled=false` switches it off (tests only: a cancelled company would then keep its data past its window).

## Billing

The subscription of each company (plan, charges, dunning, payment methods through Wompi) is in [docs/BILLING.md](BILLING.md). For the
administrator: `/admin/facturacion` is the plan catalogue (price per line, AI conversations included, AI add-ons) and
`/admin/empresas/{id}/facturacion` starts a company's subscription (with free days if agreed), changes plan, billed lines or add-on (from the
next period, no proration), marks a charge paid by hand (bank transfer) or voids it, and cancels at period end or now. The company list and
page show the billing status; the billing job suspends a company for billing (`billing.past_due`), lifts it once paid (`billing.paid`) and
cancels it for non-payment (`billing.non_payment`) through the hooks of the lifecycle above, all as the system. The monitoring backlog counts
past-due and billing-suspended subscriptions and raises `billing_events_failed` and `billing_attempt_stuck`.

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

## Limits

Each company has its limits; every one has a deployment default, and the administrator can give a company its own value (and take it back).
The company detail (`GET /api/admin/tenants/{id}`) returns them as they apply now in `limits`: each effective value and whether it is the
default.

| Limit | Default | Company value | Where it is enforced |
|---|---|---|---|
| Active lines (`maxActiveLines`) | 0 at registration | 0 to 1000 (`PUT .../line-quota`) | Switching a line on (above). |
| AI switch (`aiEnabled`) | **off** | on/off with a reason; switching on needs the version of the AI terms the company accepted (`PUT .../ai-enabled`). New terms while it is on are a change too (audited with the previous version); the same terms again write nothing | The classifier's admission (docs/CLASSIFICATION.md, "Usage and quota"). |
| AI monthly quota (`aiMonthlyConversationQuota`) | `Classification:Quota:DefaultMonthlyConversations` (500) | 0 to 1,000,000 distinct conversations a month (America/Bogota); `null` = default; a reason is required (`PUT .../ai-quota`) | The same admission. The company's USD ceiling is the quota times `Classification:Quota:TenantMonthlyUsdPerQuotaUnit` (0.02: 500 = 10 USD). |
| When the AI reads (`aiAnalysisMode`, `aiAnalysisNightlyHour`, `aiAnalysisIntervalHours`, `nextAiAnalysisWindowAt`) | **`Nightly`** from `Classification:Schedule:DefaultNightlyHour` (01:00 America/Bogota) | `Nightly` with an optional local start hour (0 to 23), `EveryHours` with an interval (1 to 24), or `NearRealTime` (special cases); a reason is required (`PUT .../ai-schedule`) | The LLM scheduler (docs/CLASSIFICATION.md, "When the AI reads"): the rules keep reading every conversation near real time whatever the mode. Takes effect from the next window. |
| Sends per minute (`sendRateLimitPerMinute`) | `Messaging:TenantSendRateLimitPerMinute` (120) | 1 to `Messaging:TenantSendRateLimitMaxPerMinute` (default 10 times the deployment limit, 1,200; at most 1,000,000), all its users together; `null` = default; a reason is required (`PUT .../send-rate-limit`) | The company send limiter (docs/MESSAGING.md). The instance that saved it applies it on the next send; every other instance reloads the company values every 30 s. A new value starts a fresh one-minute window. |

| Media retention (`fileRetentionDays`, `fileRetentionIsDefault`, `defaultFileRetentionDays`) | `Media:RetentionDays` (90) | 1 to 3650 days; `null` = default; a reason is required (`PUT .../media-retention`, audited as `tenant.media_retention_changed` with from, to and the reason) | The media retention job (docs/MESSAGING.md, "Media retention"): stored copies older than this are deleted from the store; the messages stay. Applies at the job's next run (hourly). |

### Media retention

How long a company's stored WhatsApp files (received and sent) are kept. The platform default is `Media:RetentionDays` (90); the
administrator sets a company's own value in `/admin/empresas/{id}` ("Retención de archivos"). A shorter value deletes the older copies at the
next run; a longer one keeps future copies longer (already purged copies do not come back). Deleting a company's data deletes every stored
object of it (its whole prefix of the store), and a contact suppression deletes the contact's objects (docs/MESSAGING.md).

A company's **sandbox** conversations (docs/SANDBOX.md) are outside its quota and ceiling but have their own bound,
`Classification:Quota:SandboxMonthlyUsdPerCompany` (2 USD a month by default): one company's tests cannot spend the deployment cap.

The deployment's monthly AI cap (`Classification:Quota:DeploymentMonthlyUsdCap`, 20 USD) is not per company: when every company's estimated
USD of the month (sandbox included) reaches it, every company is read by the rules until the month ends.

## Line licences

The platform keeps an **inventory of 360dialog line licences**: one per number contracted with the provider, with what it costs per month (for
reporting, not a bill). Who owns the numbers is not decided yet, so the model works both ways: a licence Conversia resells has its cost; one
the customer owns (it pays 360dialog itself) is marked `customerOwned` with cost 0.

```
create ──> Available ──(assign to a company)──> Assigned ──(release)──> Available ──(retire)──> Retired
```

* Only the administrator creates, assigns, releases and retires licences; every change is audited (`license.created`, `license.assigned`,
  `license.released`, `license.retired`). A licence must be released before it is retired; one number has at most one live licence (a
  retired one is history, and the number can be licensed again).
* **The number is masked**: after creation the API, the Web and the audit trail show only its last 4 digits (`•••• 1234`). The full digits are
  stored (they are what matches the company's line) and never leave the API.
* **Linking**: a licence assigned to a company is linked to the company's line with the same number (same digits, or the same 360dialog id)
  when the administrator assigns it (if the company already registered the number), when the company registers it, or when the company
  switches it on. One licence backs one line that is on.
* **The switch `Licensing:RequireAssignedLicense`** (default `false`):
  * `false` (today's deployments): lines work exactly as before; licences are linked only for reporting and releasing one leaves its line as
    it is.
  * `true`: a company can switch a line on only if a licence assigned to it matches that number and is not backing another line that is on
    (`409` `license_required`: "Esta línea no tiene una licencia de 360dialog asignada a tu empresa..."). Releasing a licence switches its
    linked line off in the same transaction (the audit entry says `lineDeactivated: true`). Lines already on when the switch is turned on
    keep working until someone switches them off: assign their licences first.
* Assigning and releasing lock the company's row like every change to its lines, so they never interleave with a line being switched on; the
  licence row also carries a concurrency token, so two administrators changing the same licence at once get one success and one `409`
  `concurrent_change`.

## Monitoring

`GET /api/admin/monitoring` (platform) and `GET /api/admin/tenants/{id}/monitoring` (one company) say how the service runs **with counts only**:
no message text, no number, no contact, no conversation, no webhook payload, no error message. `/admin/monitoreo` and the company page show
them.

| Section | Platform | One company |
|---|---|---|
| Health | The `/health` checks of the instance that answered (name and status). | |
| Backlog | Pending webhook deliveries and the oldest one's wait; conversations waiting for classification, the oldest wait, and the ones stopped after repeated failures (Active companies only). | The same, for the company. |
| Deliveries | Last 24 hours and 7 days: deliveries, messages stored, duplicates, skipped, failed deliveries. **Real and sandbox apart.** | The same, plus the last real delivery. |
| AI | This month: conversations analysed, calls, failed calls, tokens, estimated USD, real and sandbox apart; the deployment USD against its cap; the provider and the circuit breaker **of the instance that answered** (it is per process; its transitions are also logged as Warnings); the night analysis's **Message Batches** by state (`Preparing`, `Submitted` with the oldest one's time, `Ended` waiting to be collected, collected and failed in the last 24 hours). | The company's state (`Disabled`, `Ok`, `Warning` at 80 %, `Exhausted`), its quota and ceiling, this month real and sandbox, the last three months; its **schedule** (`aiAnalysis`: mode, hour or interval, the next window), the conversations waiting for the LLM, and the **last window's counts** (claimed, analysed, unchanged, skipped, discarded, failed, deferred, batched; `AiAnalysisRuns`, counts only). |
| Errors | Errors of the last 24 hours and the 10 most frequent fingerprints (count and last time). | The company's. |
| Sends | Manual sends of the last 7 days by outcome (`sent` or the problem code, docs/MESSAGING.md), real and sandbox. | The company's. |
| Days | | Last 14 days (America/Bogota): messages in and out, conversations opened and closed, real and sandbox apart. |
| Lines | | Active lines against the quota, assigned licences, the last real activity. |

### Threshold flags

The overview raises a flag (with a Spanish message and its numbers) when a threshold is reached. No email or notification is sent: the
administrator looks at `/admin/monitoreo`. Thresholds are configuration (`PlatformMonitoring:*`, validated at startup):

| Flag | Raised when | Key (default) |
|---|---|---|
| `health` | A health check is Degraded (warning) or Unhealthy (critical). | |
| `webhook_backlog` | Pending deliveries reach the threshold. | `WebhookBacklog` (500) |
| `webhook_backlog_age` | The oldest pending delivery waits this many minutes. | `WebhookOldestPendingMinutes` (10) |
| `classification_backlog` | Waiting classifications reach the threshold. | `ClassificationBacklog` (500) |
| `classification_backlog_age` | The oldest waits this many minutes. | `ClassificationOldestMinutes` (60) |
| `classification_stopped` | Any conversation stopped after repeated failures (critical). | |
| `failed_deliveries` | Failed real deliveries of the last 24 hours reach this percentage, with at least `MinDeliveriesForRate` deliveries (critical). | `FailedDeliveryRatePercent` (5), `MinDeliveriesForRate` (20) |
| `errors` | Errors of the last 24 hours reach the threshold. | `ErrorsLast24h` (20) |
| `send_failures` | Real sends of the last 7 days that did not go out reach this percentage, with at least `MinSendsForRate` sends. | `SendFailureRatePercent` (20), `MinSendsForRate` (10) |
| `silent_companies` | An Active company, out of sandbox mode, with real lines on, had no real delivery for this many hours. | `SilentLineHours` (24) |
| `ai_limited_companies` | A company with the AI on used up its quota or its USD ceiling this month. | |
| `ai_deployment_cap` | The deployment's monthly AI cap is reached (critical). | |
| `ai_usage_lost` | The answering instance could not write a usage record since it started (critical): the cost is only in its log, event id 4101 `AiUsageNotRecorded` (Error). Alert on that event. | |
| `tenant_deletion_stuck` | A company's data deletion was marked stuck, or started more than a day ago and has not finished (critical). | |
| `llm_breaker_open` | The LLM circuit breaker of the answering instance is open or probing. | |
| `coexistence_disconnected` | A line of an Active company was disconnected from the API by a coexistence notification (`PARTNER_REMOVED`, `ACCOUNT_OFFBOARDED`; for example the WhatsApp Business app not opened for about 14 days). The message reminds the 13-day rule. Count in `backlog.disconnectedChannels` (platform and per company). | |
| `media_failures` | Received media whose every download attempt failed in the last 24 hours. Counts in `backlog.filesPending` and `backlog.fileFailures24h`; the deletion queue of stored objects in `backlog.fileDeletionsQueued` and `backlog.oldestFileDeletionSeconds` (counts only). | |
| `llm_batches_failed` | A Message Batch of the night analysis failed in the last 24 hours (could not be sent, abandoned, or unreadable after its expiry): its conversations take the synchronous path. | |

Every monitoring statement runs with a 10-second command timeout, and each list (silent companies, companies with the AI used up, errors)
is one statement that also returns its total. The platform overview is cached in each API process for `PlatformMonitoring:OverviewCacheSeconds`
(20 s by default, 0 to 300; 0 turns it off): it is the same for every administrator and holds nothing of a single company. A company's view is
never cached.

The send counters (`MessageSendOutcomes`, one row per company, day, sandbox flag and outcome) are kept 90 days; the overview lists at most 20
companies per list (the totals are exact). Every list is one grouped query, whatever the number of companies.

### Manager reports

`GET /api/admin/monitoring/reports` and the card "Informes a gerentes este mes" of `/admin/monitoreo` (docs/REPORTS.md): per company, the
reports generated this month (America/Bogota), how many the model wrote and how many the template, the deliveries sent, failed and pending,
and the estimated USD of the model; the totals; the email provider and the **sender identity** (`Email:FromAddress`, validated at startup:
a bad address, a missing SMTP host, a security other than TLS or a user without password stop the API naming the key). Counts and money only:
never a report's text, a recipient or an address. The model's use for reports is bounded apart from the conversation quota
(`Reports:Ai:MonthlyReportsPerCompany`, `Reports:Ai:DeploymentMonthlyUsdCap`) and follows the company's AI switch.

### Assistant

The AI sales assistant (docs/ASSISTANT.md) has two kill switches of the administrator, both with a required reason (1 to 200 characters) and
audited: **per company** (`PUT /api/admin/tenants/{id}/assistant` `{enabled, reason}`, `Tenants.AssistantBlocked`,
`tenant.assistant_blocked_changed`) and **global** (`PUT /api/admin/assistant`, `PlatformSwitches`, `platform.assistant_changed`; on by
default). The company's AI switch (`aiEnabled`) gates it too. `GET /api/admin/assistant` and `GET /api/admin/tenants/{id}/assistant` answer
counts only: the switches, the companies (or lines) with it on, timers waiting now, being answered and pending review, and the last 7 days of
`AssistantActivityCounts` by metric, real and sandbox apart. In the Web: `/admin/asistente` (from `/admin/monitoreo`) and
`/admin/empresas/{id}/asistente` (from the company page).

## Endpoints

All under `/api/admin`, policy `PlatformAdmin`, Spanish `application/problem+json` errors, an unknown id is always the same `404`
("Empresa no encontrada."). Paging: `page` (from 1), `pageSize` (1 to 100, default 20).

| Method | Path | Notes |
|---|---|---|
| POST | `/api/admin/auth/login` | `{email, password}` to `{token, adminId, expiresAt}`. Anonymous, rate limited. Wrong email, wrong password and a deactivated administrator are the **same** `401`. |
| GET | `/api/admin/tenants` | Query `status` (`Pending`/`Active`/`Paused`/`Suspended`/`Cancelled`), `search` (part of the name, case-insensitive), `page`, `pageSize`. Items: `id, name, status, maxActiveLines, activeLines, registeredLines, ownerEmail, registeredAt, lastActivityAt` (time of the latest real message, null if none: sandbox messages are not activity, [SANDBOX.md](SANDBOX.md)), `statusChangedAt, statusReason, sandboxMode`, and the lifecycle: `suspensionCause` (`Manual`/`Billing`), `statusReasonCode`, `cancelledAt`, `dataDeletionDueAt`, `dataDeletionStartedAt`. |
| GET | `/api/admin/tenants/{id}` | The same plus `lines` (`channelId, displayPhoneNumber, branchName, isActive`: business facts only, no webhook key), the last 10 `recentAudit` entries, `limits` (see [Limits](#limits)) and the assigned `licenses` (masked). The listing also carries `aiEnabled`. |
| PUT | `/api/admin/tenants/{id}/status` | `{status, reason?}` (`Active`, `Paused`, `Suspended`, `Cancelled`; reason up to 500 characters). `409` for an invalid transition, `409` `tenant_deletion_started` to reactivate a company whose deletion started. See [Lifecycle](#lifecycle-of-a-company). |
| POST | `/api/admin/tenants/{id}/data-deletion` | `{confirmName, reason}`: deletes a cancelled company's data now (`202`). See [Deleting a company's data](#deleting-a-companys-data). |
| GET | `/api/admin/tenants/{id}/lifecycle` | Its status changes and data deletion from the audit trail, newest first (`page`, `pageSize`). |
| PUT | `/api/admin/tenants/{id}/line-quota` | `{maxActiveLines}` (0 to 1000). `409` below the active lines. |
| PUT | `/api/admin/tenants/{id}/sandbox-mode` | `{enabled, reason}` (reason **required**, 1 to 200 characters). Switches the internal sandbox of the company on or off ([SANDBOX.md](SANDBOX.md)). Idempotent: the value it already has writes nothing. |
| PUT | `/api/admin/tenants/{id}/ai-enabled` | `{enabled, reason, termsVersion}`: reason required (1 to 200 characters); `termsVersion` required to switch on (up to 50). Idempotent. |
| PUT | `/api/admin/tenants/{id}/ai-quota` | `{monthlyConversations, reason}` (0 to 1,000,000, or `null` for the default; reason 1 to 200 characters). |
| PUT | `/api/admin/tenants/{id}/ai-schedule` | `{mode, nightlyHour, intervalHours, reason}`: `Nightly` (hour 0 to 23 or `null` for the default, no interval), `EveryHours` (interval 1 to 24, no hour) or `NearRealTime` (neither); reason 1 to 200 characters. The same schedule again writes nothing. |
| PUT | `/api/admin/tenants/{id}/send-rate-limit` | `{perMinute, reason}` (1 to `Messaging:TenantSendRateLimitMaxPerMinute`, or `null` for the default; reason 1 to 200 characters). Above the maximum: `400` naming it. |
| GET | `/api/admin/tenants/{id}/monitoring` | The company's counts (see [Monitoring](#monitoring)). |
| POST | `/api/admin/tenants/{id}/channels/{channelId}/deactivate` | Forces a line off. Idempotent. A channel of another company is `404`. |
| GET | `/api/admin/monitoring` | The platform overview and its flags. |
| GET | `/api/admin/licenses` | Query `status` (`Available`/`Assigned`/`Retired`), `tenantId`, `page`, `pageSize`. Numbers masked. |
| GET | `/api/admin/licenses/{id}` | One licence (`404` "Licencia no encontrada."). |
| POST | `/api/admin/licenses` | `{phoneNumber, providerChannelId?, monthlyCostUsd, customerOwned, notes?}` to `201`. `409` `license_duplicate` for a number that has a live licence. |
| POST | `/api/admin/licenses/{id}/assign` | `{tenantId}`. `409` `license_not_available` unless Available. |
| POST | `/api/admin/licenses/{id}/release` | `409` `license_not_assigned` unless Assigned. With `RequireAssignedLicense`, switches the linked line off. |
| POST | `/api/admin/licenses/{id}/retire` | `409` `license_assigned` while Assigned. |
| GET | `/api/admin/audit` | Query `tenantId?`, `page`, `pageSize`. Newest first. |

For the companies' side: `GET /api/tenant` (any signed-in user, allowed in every status) returns `name, status, maxActiveLines, activeLines, registeredLines, sandboxMode`.

The `403` of a company that is not Active carries the stable machine code (`"code": "tenant_not_active"`, plus `"tenantStatus"`):

```json
{ "status": 403, "title": "Empresa no activa", "detail": "Tu empresa está suspendida.", "code": "tenant_not_active", "tenantStatus": "Suspended" }
```

## Audit

Every change writes a row to `PlatformAuditLogs`: the administrator's id (**null when the system made the change**: the billing hooks and the
deletion job), the **reason code** (always for the system, optional for an administrator), an action code, the company, a small JSON of the old
and new values and the time. A database check refuses a row with neither an administrator nor a reason code. Actions: `tenant.status_changed` (`from`, `to`, `reason`, `cause` for a suspension; a cancellation adds `deletionDueAt`, `linesDeactivated`, `licensesReleased`),
`tenant.data_deletion_started` (`trigger`, `reason`, `cancelledAt`, `deletionDueAt`), `tenant.data_deleted` (`trigger`, `nameHash`, `hashKeyId`, `rows` per table), a status change by an administrator also `previousCause`, a billing hold `billingHold`, `tenant.quota_changed` (`from`, `to`), `tenant.sandbox_mode_changed` (`from`, `to`, `reason`), `channel.deactivated`
(`channelId`, `displayPhoneNumber`), `tenant.ai_enabled_changed` (`from`, `to`, `reason`, `termsVersion`: the evidence of the company's consent),
`tenant.ai_quota_changed` and `tenant.send_rate_limit_changed` (`from`, `to`, `reason`; `null` = default), `tenant.ai_schedule_changed` (`from` and `to` as
`{mode, nightlyHour, intervalHours}`, `reason`), the AI switch also with `previousTermsVersion`, `license.created` (`licenseId`, `number` masked,
`customerOwned`, `monthlyCostUsd`), `license.assigned` (`licenseId`, `number`, `channelId`), `license.released` (`licenseId`, `number`, `channelId`,
`lineDeactivated`) and `license.retired` (`licenseId`, `number`), `tenant.assistant_blocked_changed` (`from`, `to`, `reason`) and `platform.assistant_changed`
(`switch`, `from`, `to`, `reason`; no company). Refused changes and no-ops (same quota, line already off) write nothing. Rows hold ids, statuses, numbers and
the reason the administrator typed: never a secret, a message or customer data. There is no foreign key on the company, so the trail survives it.

**Personal data and retention.** The reason is free text, so it is the one place a person could type something personal. The status form says so
("No incluyas datos personales de clientes"); the rule for administrators is to write the business reason only ("pago pendiente", "solicitud de la
empresa"), never a customer's name, number or message. The trail has no purge: `PlatformAuditLogs` rows are kept for as long as the platform keeps its
own records, because they are the evidence of who approved or stopped a company. If a reason ever holds personal data, it is corrected in the database
by hand (the column is the only field of the row a person writes). The schema is unchanged.

## Sandbox mode

The administrator can put a company in the **internal sandbox mode**: nothing of the company goes to 360dialog, its users can simulate
customers, and its metrics show sandbox data only. It is the administrator's switch and nobody else's: a company's own API has no way to
change it. Each change needs a reason and is audited. What the mode does, its data rules and its limits are in [SANDBOX.md](SANDBOX.md).

The administrator still sees **no** conversation, message, contact or metric of the company, sandbox or real: the switch and the audit
entry are all there is on this side.

### Numbers: in full on a company's lines, masked in the licence inventory

The company detail shows each of its **lines** with the full number (`displayPhoneNumber`), as it did before this feature: it is the company's
own business number, the administrator needs it to talk to the company and to 360dialog about that line, and it is the number the company
itself registered. The **licence inventory** masks the number to its last 4 digits everywhere (API, Web, audit): a licence can sit unassigned
in the inventory, can be assigned to any company and outlives it, so the full number is kept only where it is needed (to match a line) and the
lists and the audit trail never repeat it.

## Upgrade notes

* **AI off by default.** With this version every company has `AiEnabled = false`, existing ones included (the migration adds the column with
  that default), and in **every environment, Development too**. While it is off the rules read everything and no LLM call is made, so a test
  against the real API (or over Replay, docs/DOCKER.md) shows no call. Before testing: sign in at `/admin`, open the company, "Activar la IA",
  give a reason and the terms version (`PUT /api/admin/tenants/{id}/ai-enabled` `{"enabled": true, "reason": "...", "termsVersion": "..."}`),
  and, with the real client, keep `Classification:Llm:AllowRealClientNonSandbox=false` unless the privacy review allows real conversations.
* **Limits need a reason.** `PUT .../ai-quota` and `PUT .../send-rate-limit` now refuse a body without `reason`.
* **Index builds.** The migration builds two indexes `CONCURRENTLY` (docs/DOCKER.md, "Upgrade notes").

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
  and `PlatformMonitoringQueries` (and the pre-existing webhook lookup, login, workers and the AI usage meter); they project scalars (counts,
  the owner's email, the latest message time, a branch name, tokens and USD, error fingerprints). `WebhookEvents.RawPayload`, message text,
  contacts and error messages are never selected. `AdminDtoGuardTests` pins that every admin answer (`*Response` of `DTOs.Admin`) holds
  scalars only and no member named like private content; the licence inventory (`LineLicenses`) is a platform table whose company is data
  (`AssignedTenantId`), not a tenant column.
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
| `/cuenta-pendiente` | company user, any status | Where a company that is not Active lands, with the words of its status: **Paused** ("Tu empresa está en pausa": messages keep being saved), **Suspended for a payment** ("Tu empresa tiene un pago pendiente"), **Cancelled** ("El servicio de tu empresa fue cancelado", with the date its data is deleted), **Pending** or **Suspended** by the team: "Tu empresa está pendiente de activación" (what happens next: the Conversia team activates it and sets the number of WhatsApp lines of the commercial agreement) or "Tu empresa está suspendida" (data is kept; contact the team). Reads `GET /api/tenant`. A company that is Active is sent on to `/panel`. After registering it shows "Tu empresa quedó registrada y está pendiente de activación" (`?registrado=1`). |
| `/organizacion` | company user | Shows "Líneas activas X de Y" (chip, bar and words), the state of each line (Activa / Inactiva), "Activar línea" / "Desactivar" (each on a confirmation page) and, when the quota refuses (409), the API's message plus who can help. |
| `/admin/login` | anyone | The administrator's own sign-in ("Administración de Conversia"): `POST /api/admin/auth/login`. One message for every failure; the API's rate limit is shown in words. |
| `/admin` | administrator | Companies: filters (`estado`: `pendiente`, `activa`, `en-pausa`, `suspendida`, `cancelada`; `buscar`), 20 per page (`pagina`), status chip, "Líneas activas X de Y", owner email, registration date, last activity ("Sin actividad" when none) and a banner with the companies waiting for activation. |
| `/admin/empresas/{id}` | administrator | One company: status (with the cause of a suspension, the cancellation and deletion dates) and the moves that make sense (Activar, Suspender and Cancelar servicio when Pending; Pausar, Suspender and Cancelar servicio when Active; Reanudar, Suspender and Cancelar servicio when Paused; Reactivar and Cancelar servicio when Suspended; Reactivar when Cancelled, none once its deletion started), "Eliminar los datos ahora" for a cancelled company, the "Historial de estados" (lifecycle from the audit trail, the system shown as "el sistema" with its reason in words), the quota form (0 to 1000), its lines (number, branch, state, "Desactivar") and the latest changes. |
| `/admin/empresas/{id}/estado/{activar\|reanudar\|reactivar\|pausar\|suspender\|cancelar}` | administrator | The confirmation of a status change, with an optional reason (up to 500 characters). Cancelling shows the date the data would be deleted. |
| `/admin/empresas/{id}/eliminar-datos` | administrator | Deleting a cancelled company's data now: what goes and what is kept, the company's name to type and a required reason. |
| `/admin/empresas/{id}/lineas/{channelId}/desactivar` | administrator | The confirmation of switching a line off. |
| `/admin/empresas/{id}/sandbox/{activar\|desactivar}` | administrator | The confirmation of switching the internal sandbox mode, with a **required** reason (up to 200 characters). The company page has the card "Modo sandbox" and the listing marks the companies that are in it ([SANDBOX.md](SANDBOX.md)). |
| `/admin/auditoria` | administrator | The audit trail in words, newest first, 20 per page, optionally for one company (`empresa`). |
| `/admin/monitoreo` | administrator | The overview: flags, health, backlogs, deliveries, AI this month, sends by outcome, frequent errors, silent companies, companies with the AI used up. |
| `/admin/empresas/{id}` (sections) | administrator | Besides the above: "Límites" (each effective limit and whether it is the default, the AI schedule and the next window; AI quota, AI schedule and send limit forms, empty = default), "Inteligencia artificial" (state, accepted terms, usage this month, real and sandbox, the last analysis window's counts and the pending conversations), "Licencias de 360dialog" and "Monitoreo" (counts and the last 14 days). |
| `/admin/empresas/{id}/ia/{activar\|desactivar}` | administrator | The confirmation of switching the AI, with a required reason and, to switch on, the version of the accepted terms. |
| `/admin/licencias`, `/admin/licencias/nueva`, `/admin/licencias/{id}` | administrator | The licence inventory (filter by state), the new licence form and one licence with its assign, release and retire forms. |
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
* **Agreed pause** (the company asked to stop for a while): `{"status":"Paused","reason":"..."}`. Nothing is lost: deliveries keep being
  stored; the analysis catches up on "Reanudar" (`{"status":"Active"}`).
* **End the service**: `{"status":"Cancelled","reason":"..."}` (lines off, licences released, deletion scheduled). Reactivate before the date if
  the company comes back; delete now if it asked for it (see [Deleting a company's data](#deleting-a-companys-data)).
* **See what changed**: `GET /api/admin/audit?tenantId=...`.
* **Switch the AI on for a company**: only after it accepted the AI terms. `/admin/empresas/{id}/ia/activar` (or `PUT .../ai-enabled`) with the
  reason and the terms version; set its quota if it is not the default.
* **Give a company a line**: create the licence (`/admin/licencias/nueva`), assign it to the company; the company registers the number and
  switches it on (with `RequireAssignedLicense=true`, only then can it). To take it back: release (the line goes off when licences are
  required), then retire if the number was cancelled with 360dialog.
* **Turning `Licensing:RequireAssignedLicense` on**: assign a licence to every line already on first (the flag does not switch off lines that
  are on; they would just not come back on after being switched off).
* **A flag on `/admin/monitoreo`**: `webhook_backlog`/`_age` or `health`: check the API is running its worker and the database (docs/WEBHOOKS.md);
  `failed_deliveries`: look at `/mensajes/eventos` of the company and the error registry; `classification_backlog`/`_stopped`: see
  docs/CLASSIFICATION.md, "The worker"; `silent_companies`: confirm with the company that its numbers still point to Conversia (360dialog
  webhook) and are not suspended; `ai_limited_companies`: raise the company's quota or wait for the month to end; `ai_deployment_cap`: raise
  `Classification:Quota:DeploymentMonthlyUsdCap` (restart) if the spend is expected; `llm_breaker_open`: the provider fails, the rules read
  meanwhile; batches `Submitted` for many hours or `failed` in the last 24 h: the provider is slow or refuses batches; their conversations take
  the synchronous path at the next window (docs/CLASSIFICATION.md, "Batch mode"); a company that needs fresher AI fields: change its schedule
  (`EveryHours`), not the deployment default; `send_failures`: look at the outcome codes (docs/MESSAGING.md).

## Limits

* No endpoint to create, list or deactivate administrators, or to change their password: the bootstrap and the database are the only ways.
* The administrator's Web area is deliberately small (companies, one company, monitoring, licences, audit). There is no page to create administrators or change their password (see above).
* Monitoring is read on demand (no history of its own, no notifications); the breaker shown is the answering instance's.
* The company list cannot be filtered by AI state yet; the overview lists the companies that used it up.
* The Web shows the audit trail with the company names of the first 100 companies; beyond that a company is shown by a short id.
* While a company is suspended by the team (Manual) or cancelled, its provider (360dialog) keeps getting `401` for its webhooks and may retry or disable the callback by its own rules;
  messages sent in that period are not captured.
* A suspended company with a very large backlog of pending webhook events makes each worker claim scan past them (the claim filters by status);
  reactivate or purge if that ever shows up in the queue health.
