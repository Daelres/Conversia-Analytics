# Manager reports

Each company can send its managers a **short, automatic summary** of the period by email and/or WhatsApp: about three indicators, a short
diagnosis and up to five concrete actions, in plain Spanish, readable in a few minutes. It is not a dashboard: the dashboard is `/panel`; the
report says what happened and what to do. Orchestration is in .NET (a background job), no external workflow tool.

```
Definition (who, how often, which branches, which parts)
   │  due? (America/Bogota, Reports:Job)
   ▼
Data pack (aggregates from the metrics queries) ──► Writer: model (validated) │ template (fallback)
   │                                                         │
   ▼                                                         ▼
ManagerReports (content, pack, HTML/text, generator, cost) ──► ReportDeliveries (one per person and channel) ──► email (SMTP/TLS) · WhatsApp (template)
```

## Definitions

`ReportDefinitions` + `ReportRecipients` (tenant tables, query filter, deleted with the company's data).

| Field | Rule |
| --- | --- |
| Name | 1 to 100 characters. |
| Active | A paused definition generates nothing. |
| Frequency | `Daily` (previous day), `Weekly` (previous Monday–Sunday week, sent on `SendDayOfWeek`, Monday by default), `Monthly` (previous month, sent on the 1st). |
| Send hour | 0–23, local time of `Metrics:TimeZone` (America/Bogota). Default 07:00. |
| Branches | Empty = all (also branches added later); otherwise only those (the metrics filter `branchIds`). |
| Sections | Closed list, at least one: `Headline`, `Conversion` (by branch, advisor, source), `DropOff` (closing point), `LossReasons` (reasons and objections), `Response` (first response and unanswered), `AiCoverage`. |
| Recipients | Users of the company (by id; the email is the user's, read at send time), each by email, WhatsApp or both. At most `Reports:MaxRecipientsPerDefinition` (10). |
| WhatsApp | Line + approved **utility** template with exactly three body placeholders, in this order: period, headline, link. Required when someone receives it by WhatsApp. |

Limits: `Reports:MaxDefinitionsPerCompany` (5; `409 report_limit`). Per-plan limits are not implemented (deployment-wide only).

**Permissions.** New permission `ReportsManage = 12` ("Gestionar informes"): definitions, options and preview. The history needs `MetricsView`.
A report page is readable with `MetricsView` or `ReportsManage`, or by a person who receives that definition (without the delivery list).
**Trail:** as for contact suppression there is no tenant audit table: every create/update/delete/preview/opt-in/unsubscribe writes a structured
log line, event `4301 ReportTrail` (ids, action, counts; never an address or number).

## Schedule

`ReportSchedule` (pure, tested over thousands of random instants): the job runs every `Reports:Job:IntervalSeconds` (60) and, for every active
definition of an **Active** company, takes the latest occurrence whose send instant passed. It is generated only when it is not older than
`Reports:CatchUpHours` (24: after a long outage a stale period is skipped, not sent late) and not earlier than the definition's creation (a new
definition never sends a past period at once). **Idempotency:** a unique index `(DefinitionId, PeriodFrom)`; a second instance gets a unique
violation and drops its copy. The key is `(DefinitionId, Frequency, PeriodFrom)`: changing a definition from weekly to daily never makes a day look
already reported. A period missed by a longer outage is **skipped**, with a warning (event `4307 ReportPeriodSkipped`, once per period and instance).

## The data pack

`ReportDataPack` (version `1`), built by `ReportDataPackBuilder` from `IMetricsService` for the period and scope (same cohort, maturity,
sandbox and imported-history rules as docs/METRICS.md):

- period (`from`, `until` inclusive, days, label in words), company name, scope ("Todas las sucursales", "Sucursal Norte" — a name that already
  says "Sucursal" is not repeated — or the names), sections;
- `headline`: the period's conversations and sales so far, the previous period's conversations, and the **conversion of the latest matured
  window** (see below) against the window right before it, with the change in points only when both rates are stated;
- `conversion`: rates by branch (up to 12), advisor (top 8 by volume) and source over the same matured window; a group below the sample has no rate;
- `dropOff`: stopped, read, main stage (in words), its count and share, moved off the chat;
- `losses`: total lost, "no response", top 3 stated reasons, top 3 objections;
- `response`: SLA, median minutes, % within SLA and answered of the period; and the **open conversations waiting for a reply now**, whatever
  their start date (last 90 days), how many over the SLA and how long the oldest has waited: what "responder hoy" needs; the 3 slowest branches;
- `coverage`: AI on, stopped vs. not read, % read, last AI reading date.

**Privacy.** Aggregates only, plus company-internal names (company, branches, advisors, labels). No customer name, number or message: the pack's
types have no field that could hold one (`ReportDataPackGuardTests` walks them) and an integration test seeds a customer name, number and text and
checks none reaches the stored pack, content, HTML or text. The pack is stored with the report so every number can be checked.

### Conversion: only matured, enough, comparable

A conversation counts in a rate once it is `Metrics:ConversionMaturityDays` (7) old (docs/METRICS.md). A weekly report sent on Monday covers a week
whose conversations are almost all younger than that, so its own rate would come from a handful of early sales. The report therefore measures
conversion over the **latest window of the period's length that has fully matured** — the period itself when it ended at least the maturity ago,
otherwise the period moved back just enough (a weekly report on Monday measures the week before it) — and says which dates it measured
("Conversión del 2 al 8 de noviembre"). The rate is stated only with at least `Reports:MinConversionSample` (20) decided conversations and no
immature one; otherwise the report says **"Sin datos suficientes"** with how many there were. The change against the window before is stated only
when both windows passed that guard, under the same maturity rule.

The JSON of the pack escapes `<`, `>`, `&` and quotes (letters of every language stay readable), so no name can close the `<data_pack>` block of
the prompt.

## Generation and grounding

`ReportComposer`:

1. No model configured (`Classification:Provider=Llm` is Development-only until the classification gate is lifted, docs/CLASSIFICATION.md) or
   `Reports:Ai:Enabled=false` → template (`ai_not_configured`).
2. Admission (`ReportAiAdmission`): the company's AI switch (`ai_disabled`), the real provider's sandbox-only gate (`sandbox_only`), the company's
   model-written reports this month (`Reports:Ai:MonthlyReportsPerCompany`, 62 → `monthly_limit`), the deployment's report spend this month
   (`Reports:Ai:DeploymentMonthlyUsdCap`, 5 USD → `deployment_cap`).
3. The model: static cacheable system prompt and field guide (`ReportPrompt`, no tenant data), the pack as JSON in `<data_pack>`, structured
   output (`title`, `indicators[]`, `diagnosis`, `actions[]`, `coverage_note`, `mentioned_names[]`).
4. **Grounding** (`ReportGroundingValidator`): every number of the text must be a number of the pack that **rounds (half away from zero) or
   truncates** to what is written at its precision (`23,5 %`, `24 %` and `23 %` match 23.5; `82 %` does not match 81.3; `1.234` and `1,234`
   match 1234), **of the kind the words after it say** (`%` a percent, `puntos` a change in points, `minutos`, `horas`, `días`, `conversaciones` or
   `ventas` a count); a day, month or year of the pack's dates only **inside a written date** ("9 al 15 de noviembre de 2026"); a direction word
   (`subió`, `bajó`, `mejoró`, `empeoró`, `aumentó`, `disminuyó`...) must match the sign of the change its sentence speaks of (the conversion, or the
   volume of conversations); a capitalized word
   mid-sentence must belong to a pack name or a short product list (WhatsApp, IA...); `mentioned_names` must be pack names; no other branch or
   advisor name of the company may appear; 1–3 indicators, 1–5 actions, field limits and 4,000 characters in total; no links, `@` or markup.
5. Unusable answer (refusal, truncation, invalid JSON, not grounded) → **one** regeneration; then the template (`invalid_output`). Provider down
   (timeout, 429, 5xx, bad key) → template at once (`provider_unavailable`). The cost of every attempt is stored.

The **template** (`TemplateReportWriter`) uses only the pack's numbers and names with fixed sentences; a property test runs the validator over
thousands of random packs. It is always available.

**Cost and quota.** Reports are a category of AI use **apart** from the conversation analysis: they never consume the conversation quota
(`AiUsageMonths` is not touched). Every written report — scheduled or **preview** — goes into the ledger `ReportLedgerEntries` (kind, generator,
attempts, tokens, estimated USD; no text) **as soon as it is written**, before it is rendered, stored or sent. The monthly limit per company and the
deployment cap count the ledger, previews included, and the cost of a call stays counted even if storing the report then fails. If a run fails
after calling the model, the next run of that period finds the ledger entry and writes the report with the **template** (`model_already_called`)
instead of calling the model every minute. Previews are also bounded by `Reports:PreviewsPerHour` (6 per user) and
`Reports:PreviewsPerCompanyPerDay` (10): past it the template writes them (`preview_budget`). A Haiku report is ~3–5k input and ~600 output tokens.

## Storage and retention

`ManagerReports`: structured content (jsonb), data pack (jsonb), email HTML and text (without per-person links), headline, generator,
fallback reason, attempts, model, tokens, estimated USD, sandbox flag. Kept `Reports:RetentionDays` (180) and deleted by the job (deliveries
cascade); deleted with the company's data (`TenantDataDeletionPlan`) and with its definition.

## Delivery

`ReportDeliveries`: one row per report, person and channel (unique: never sent twice), with status (`Pending`, `Sent`, `Failed`, `Skipped`),
attempts, next attempt and a stable code. The job claims due rows with `FOR UPDATE SKIP LOCKED` and a 10-minute lease; before sending, the
instance renews the lease with a fresh clock **only if the delivery still carries its claim** (same attempt number): when the lease expired and
another instance claimed it again, the first one stops. Backoff and `SentAt` use the clock at the moment of the send.

- **Held, not failed**: a company that is not Active (pending, paused, suspended, cancelled) sends nothing; its deliveries wait (an hour at a time,
  the attempt is not spent) and fail with `company_inactive` once the report is older than `Reports:CatchUpHours`. The same for
  `email_not_configured`: configuring email within that window sends them.
- **The person's current choice**: a WhatsApp delivery is sent only if the recipient still has WhatsApp chosen (`channel_removed`) and a verified
  number; an email only if not unsubscribed.
- **Duplicates that remain**: an instance that dies after the server accepted the message and before recording it; and an SMTP connection lost
  while the message was being handed over (`smtp_unknown_outcome`, **not retried**, like a WhatsApp timeout). Every attempt of a delivery
  carries the same Message-Id (`report-delivery-<id>@<sender domain>`), so a mail program can recognise a repeat.

- **Email** (`IEmailSender`): subject "Company: title", HTML (inline styles only: no images, fonts, scripts or trackers) and plain-text parts,
  link to `/informes/{id}`, the person's unsubscribe link and the RFC 8058 headers `List-Unsubscribe: <…/informes/baja/un-clic?t=…>` and
  `List-Unsubscribe-Post: List-Unsubscribe=One-Click`. Temporary failures are retried after 1 min, 5 min, 30 min, 2 h, 6 h up to
  `Reports:MaxDeliveryAttempts` (5); permanent ones fail at once. `Email:Provider=None` → `email_not_configured`.
- **WhatsApp**: only to people with a recorded opt-in, through the definition's line and template, via the sender resolver (a company in
  sandbox mode uses the internal sender and reaches nobody). Parameters: period in words, headline, link. `LimitReached` is retried; a timeout
  (`provider_unreachable`) is **not** repeated, like manual sends (docs/MESSAGING.md): the outcome is unknown and a duplicate costs. Meta charges
  each template message; the settings page says so.
- **Unsubscribe**: the token is the recipient row id plus an HMAC-SHA256 tag, keyed by `Reports:UnsubscribeKey` (base64 of 32+ random bytes)
  or, when unset, by a key derived (HKDF) from `Jwt:SigningKey`. `/informes/baja?t=…` asks for a click (link scanners unsubscribe nobody);
  `POST /informes/baja/un-clic` is the one-click endpoint. It stops the emails of that definition only. **Availability**: the API checks the
  signature in the rate limiter, before any database read: a valid link has its own budget (`Reports:Unsubscribe:PerLinkPerMinute`, 10), every
  invalid token shares another (`Reports:Unsubscribe:InvalidPerMinute`, 20), so guessing never blocks a real unsubscribe and no client address
  needs to be forwarded by the Web. The Web limits both pages to 30 a minute per client address.
- **WhatsApp opt-in, verified**: `/informes/whatsapp`, the person's own number with an explicit consent checkbox (instant recorded). A one-time
  6-digit code (15 minutes, 5 tries, only its hash stored) is sent to the number through the company's line and the template of one of its
  report definitions (or its first approved utility template with three fields): "verificación de tu número…", "tu código es 123456", the page
  link. Only a **verified** number receives reports. Without a line or template the consent is kept, the page explains that no code could be
  sent, and nothing is sent to the number. Withdrawing deletes the number and the verification and keeps the instant of the withdrawal.

### Why MailKit

`System.Net.Mail.SmtpClient` is marked by Microsoft as not recommended for new development and cannot require TLS reliably. MailKit
(4.18.2) is what that documentation recommends; TLS is mandatory (`StartTls` or `SslOnConnect`, never plain), the certificate is validated by
the system. Azure Communication Services is not implemented: add an `IEmailSender` adapter (its Email SDK `EmailClient.SendAsync`) and a
case in `ReportsServiceCollectionExtensions`.

## API

| Method | Path | Permission |
| --- | --- | --- |
| GET, POST | `/api/reports/definitions` | ReportsManage |
| GET, PUT, DELETE | `/api/reports/definitions/{id}` | ReportsManage |
| POST | `/api/reports/definitions/{id}/preview` | ReportsManage (rate limited) |
| GET | `/api/reports/options` | ReportsManage |
| GET | `/api/reports?page=` | MetricsView |
| GET | `/api/reports/{id}` | MetricsView, ReportsManage or recipient |
| GET, PUT, DELETE | `/api/reports/whatsapp-opt-in` | the caller |
| POST | `/api/reports/unsubscribe` | anonymous (token; 20/min per address) |
| GET | `/api/admin/monitoring/reports` | platform administrator |

## Web

`/informes` (definitions and history), `/informes/nuevo`, `/informes/definiciones/{id}` (edit, delete), `/informes/definiciones/{id}/vista-previa`,
`/informes/{id}` (mobile-friendly, prints cleanly), `/informes/whatsapp`, `/informes/baja` (anonymous). The report page renders the structured
content (never stored HTML).

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Reports:Job:Enabled` / `IntervalSeconds` | `true` / `60` | The job. |
| `Reports:WebBaseUrl` | `http://localhost:5112` | Public address of the Web for links. Outside Development: https (loopback http only while no email is sent); **required** when an email provider is set. |
| `Reports:MinConversionSample` | `20` | Decided conversations a window needs for its rate. |
| `Reports:PreviewsPerCompanyPerDay` | `10` | Previews a company may have written by the model a day. |
| `Reports:Unsubscribe:PerLinkPerMinute` / `InvalidPerMinute` | `10` / `20` | Unsubscribe budgets. |
| `Reports:UnsubscribeKey` | — | Optional dedicated key of the unsubscribe links (base64, 32+ bytes). |
| `Reports:MaxDefinitionsPerCompany` / `MaxRecipientsPerDefinition` | `5` / `10` | Limits. |
| `Reports:RetentionDays` | `180` | Retention of generated reports. |
| `Reports:CatchUpHours` | `24` | How late a report may still be generated. |
| `Reports:MaxDeliveryAttempts` | `5` | Email retries. |
| `Reports:PreviewsPerHour` | `6` | Previews per user. |
| `Reports:Ai:Enabled` / `MonthlyReportsPerCompany` / `DeploymentMonthlyUsdCap` | `true` / `62` / `5` | The model's use for reports. |
| `Email:Provider` | `None` | `None`, `Smtp` or `Pickup` (Development: `.eml` files in `Email:Pickup:Directory`). |
| `Email:FromAddress`, `Email:FromName` | — | Sender identity, validated at startup. |
| `Email:Smtp:Host`, `Port`, `Security`, `UserName`, `Password`, `TimeoutSeconds` | —, 587, `StartTls`, —, —, 30 | SMTP; the password only from the environment (`Email__Smtp__Password`). |

## Runbook

- **Nothing arrives by email**: `/admin/monitoreo` › "Informes a gerentes" shows the provider and sender; the report page lists each delivery
  and its reason. `email_not_configured` = set `Email:*`. `smtp_*` = server unreachable, TLS or credentials (retried).
- **WhatsApp not sent**: `no_opt_in` (the person must accept at `/informes/whatsapp`), `template_unavailable` (approve a utility template with 3
  fields and sync), `credentials_missing`, `provider_unreachable` (not retried; check 360dialog).
- **Report written by the template when AI was expected**: the history shows the generator; the log line `4303 ReportGenerated` has the
  fallback reason; `4302 ReportAnswerRejected` lists the grounding problem codes.
- **Rotating the unsubscribe key**: links of emails already sent stop working (the landing says the link is not valid). With a dedicated
  `Reports:UnsubscribeKey` the JWT key can rotate freely; rotating the dedicated key has the same effect on old links, so do it only after a leak.
- **Manual test**: Development with `Email:Provider=Pickup`, create a definition, open the preview, then wait for the hour (or set it to the
  current one) and open the `.eml` files.
