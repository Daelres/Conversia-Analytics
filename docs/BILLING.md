# Billing: the recurring charge of the subscription (Wompi)

Conversia charges each company a monthly subscription through **Wompi** (Colombia). Only the **charge** is in scope: electronic invoicing
(DIAN) is handled outside Conversia, so a "charge" here is never a tax invoice.

- Card data never reaches our servers: the browser tokenizes the card (or the Nequi number) with Wompi's public key, and the server only sees
  the token and, from then on, Wompi's payment source id.
- Every money state change is audited in `PlatformAuditLogs` (the system with a `billing.*` reason code, or the administrator).
- The gateway is never trusted for what we did not ask: every answer (a poll or an event) is the transaction as Wompi has it when we re-read
  it, and it only pays a charge when its amount, currency (`COP`) and reference match the attempt.

## Data model

| Table | Scope | What it holds |
| --- | --- | --- |
| `BillingPlans` | platform | Catalogue: code (unique), name, monthly price per billed line (COP in cents, whole pesos), AI conversations a month included, active flag. Prices are the owner's (admin UI), never hard-coded. |
| `BillingAddOns` | platform | Optional AI packages of a plan: conversations per package and price per period. |
| `BillingSubscriptions` | tenant | One **live** per company (partial unique index on non-cancelled ones; cancelled ones stay as history, so a company reactivated after a cancellation gets a new subscription): plan, status, current period (local dates, America/Bogota), anchor day, billed lines override, add-on and quantity, `CancelAtPeriodEnd`, payment method (Wompi source id, type, masked label, billing email), `BillingSuspendedAt`, `NextRunAt` (the job's queue). |
| `BillingCharges` | tenant | What is owed for one period, with a snapshot of plan, lines, unit price and add-on. Unique `(SubscriptionId, PeriodStart)` and at most one `Open` per subscription. |
| `BillingPaymentAttempts` | tenant | One try to collect: our reference (unique, 128 random bits, `cv_` + 32 hex), Wompi transaction id (unique once known), kind, status, amount, decline reason. At most one pending attempt we started per charge (partial unique index). |
| `BillingEvents` | platform | Verified Wompi events, stored once by body hash and processed by the job. Ids only (event type, transaction id, payment link id): no customer data. Processed ones are purged after 30 days. |
| `AiQuotaGrants` | tenant | The AI conversations a paid add-on gives the company for the period it paid. |
| `BillingReviewItems` | platform | Money a person must look at that no attempt carries: an approved transaction that matches nothing of ours (`unmatched_payment`), an approval with a reference that is not ours (`reference_mismatch`), a company cancelled while a payment landed (`cancelled_after_payment`). Durable: never purged with the events, kept when a company's data is deleted (ids, amounts, a reason code), one open item per transaction. |

Tenant tables carry the tenant query filter and are deleted with the company's data (`TenantDataDeletionPlan`); the audit trail keeps the
money history (amounts, references, statuses).

### Billed lines

A charge bills `BilledLinesOverride` when the administrator set one, else the company's **line quota** (`MaxActiveLines`, the contracted number
of lines, at least 1), whatever is switched on. The quota is the commercial agreement; switching a line off does not lower the bill.

### No proration, no permanence

- Billed **in advance**: the charge of a period is created when the period starts and is due that day.
- A change of plan, billed lines or add-on is read when the **next** period's charge is created. Nothing is prorated or refunded.
- The owner can cancel any time: effective at the end of the paid period (undoable until then). The administrator can also cancel at period
  end or **now** (unpaid charges voided; the company's status is not changed: the administrator changes it separately).
- A subscription that ends at period end cancels the company too (`ITenantBillingLifecycle.CancelForNonPaymentAsync` with
  `billing.subscription_ended`): its data is then kept for the retention window (docs/PLATFORM_ADMIN.md) and it can be reactivated meanwhile.
- When the administrator cancels a **company** (only the company row changes; billing rows are never touched from the company's lock), the
  billing job follows on its next run: the live subscription ends (`billing.subscription_ended`), its open charges are **voided**, nothing is
  ever charged or attempted again, and a payment still pending keeps being asked about (the cancelled subscription stays scheduled until no
  attempt is pending) so an approval is flagged for review (`paid_after_void`). If the company is reactivated before its data is deleted,
  the administrator starts a new subscription (the cancelled one stays as history).

## States

### Subscription

```
            start (trial days > 0)          trial ends: first charge
  (none) ──────────────────────────► Trialing ──────────────────────► Active
     │  start (no trial)                                               │  ▲
     └──────────────────────────────────────────────────────────────► │  │ charge paid / voided
                                                                       ▼  │
                                          due date passed or attempt failed  PastDue
  Active / Trialing / PastDue ──(end asked and period ended | non-payment | admin cancels now | company cancelled)──► Cancelled
```

### Charge

```
  Open ──paid (gateway / manual / free plan)──► Paid
  Open ──void (administrator)─────────────────► Void
  Open ──subscription ended unpaid────────────► Failed ──marked paid by the administrator──► Paid
```

### Attempt

`Pending → Approved | Declined | Voided | Error`, once. `Error` also covers our own outcomes: `amount_mismatch` (an answer for another amount:
nothing is paid, a person reviews it), `verified_no_transaction` (the administrator checked the dashboard) and Wompi's refusal type.

**Review flags.** An attempt is flagged for a person (`ReviewReason`, shown on the admin billing page with our reference, counted by the
monitoring flag `billing_payment_needs_review`, audited as `billing.payment_needs_review`, logged as event 4301) when money may have moved
that the charge does not show: `duplicate_payment` (approved for a charge already paid), `paid_after_void`, `amount_mismatch`,
`approved_after_resolution` (Wompi reports APPROVED for an attempt we had closed), `voided_after_approval`, `unknown_outcome` and
`pending_too_long`. One flag at a time per attempt (a replay never piles them up). What no attempt can carry becomes a review item
(`BillingReviewItems`), listed on `/admin/facturacion` ("Pagos por revisar", `GET /api/admin/billing/review-items`) and closed with
`POST /api/admin/billing/review-items/{id}/review` (`{reason}`); the monitoring flag counts both. The administrator resolves it with
`POST /api/admin/tenants/{id}/billing/attempts/{attemptId}/review` (`{markFailed, reason}`): `markFailed` closes a pending attempt as
`verified_no_transaction` so the dunning goes on.

**Unknown outcomes.** A charge call that timed out has no transaction id. It is **never** closed automatically: nothing found by our reference
(an undocumented lookup) is not proof that nothing was charged. It stays pending, so no second attempt can charge the same period; it is
looked up again with backoff (2, 4, 8, 16, 32 minutes, then hourly) and, after `UnknownAttemptTimeout` (24 h), flagged `unknown_outcome` for a
person. Pending attempts of **every** charge are polled, including charges paid or voided by other means meanwhile.

## Dunning timeline (defaults)

All dates are local (America/Bogota); automatic attempts are made at `Billing:Dunning:AttemptHourLocal` (09:00), never at night (a Nequi
approval would wake the payer).

| Day (after the due date) | What happens |
| --- | --- |
| 0 | Charge created and due; first automatic attempt (if there is a payment method). |
| 1, 3, 5 | Automatic retries after a decline or error (`Billing:Dunning:RetryDays`; at least 12 h between two). From the day after the due date the subscription is `PastDue` and the owner sees "update your payment method". |
| 7 | Still unpaid: the company is **suspended for billing** (`SuspendForBillingAsync`, `billing.past_due`); its messages keep being received; a paused company gets a billing hold. |
| 7 + 30 | Still unpaid: the subscription and the company are **cancelled for non-payment** (`CancelForNonPaymentAsync`, `billing.non_payment`). `Billing:Dunning:CancelAfterSuspendedDays=0` means never. |
| any | Paid (card, Nequi, payment link or marked by the administrator): the charge is `Paid`, the subscription `Active`, a billing suspension is lifted at once (`ResumeFromBillingAsync`, `billing.paid`). |

**The dunning step** decides from the state as it is at that moment and acts in the same step: the subscription's row stays locked from the
decision through the lifecycle call (made in a DI scope of its own, so its own connection and transaction) to the record of what it did.
A payment applied by another instance or an event waits for the step and then finds the result (a payment after a cancellation is a
`paid` charge of a cancelled subscription and a review item). **Lock order**: the subscription row, then the company row (the 1b lifecycle
locks only the company row and never takes a subscription lock), so the two cannot deadlock each other. **Nothing is suspended or cancelled
while an attempt of the overdue charge is pending or of unknown outcome**; when a run may suspend or cancel, it first asks the gateway about
every pending attempt of that charge, whatever its backoff. The step runs it runs at the end of every job run
(after this run's gateway answers were applied) and after anything that settles money. So a payment approved in the same run is never followed
by a suspension or a cancellation, and a payment that lands between the decision and the lifecycle call is healed at once. It follows the
company's real status, not only our record:

- a company suspended for billing (or paused with a billing hold) with nothing owed is lifted, whatever our record says (self-healing);
  also when its subscription is **cancelled**: nothing can be owed any more, so it is resumed to Active and not billed (the administrator
  decides what else to do with the company); the job looks for such companies on every run;
- only an Active company, or a Paused one without a hold, is suspended; a company suspended by the administrator, or still Pending, is
  never suspended nor cancelled for billing (it is looked at again every hour); a suspension the lifecycle does not apply is not recorded;
- when the administrator lifts a billing suspension while the charge is still unpaid, the grace period starts again from that day
  (`DunningRestartedOn`), and the cancellation counts from the next suspension, never from the old one.

While a charge is overdue no new period starts. When it is paid, the next period starts on the old period end, or **today** if that was
more than 3 days ago: the time the company spent suspended is not billed.

Never twice: a period has one charge (unique index), a new attempt never starts while one of the charge is pending (checked under the
subscription's row lock and enforced by a partial unique index), each attempt has its own unique reference and a transaction id applies to
one attempt only.

Notification to the owner: there is no email channel in Conversia yet, so "send the owner to update the payment method" is the alert on the
billing page and the link on the "pending account" page; an email reminder is left for when an email provider exists.

## How it works

- **The billing job** (`BillingJobService`, every `Billing:Job:IntervalSeconds` = 60 s, and at once when an event arrives). Each run:
  1. processes stored events (each claimed with `FOR UPDATE SKIP LOCKED`, transaction re-read from Wompi, applied; failures retried with
     back-off up to 8 times, then `Failed`);
  2. takes the subscriptions whose `NextRunAt` passed and runs `BillingEngine.RunAsync` for each in its own scope. The engine locks the
     subscription with `FOR UPDATE SKIP LOCKED` (another instance holding it is skipped), decides (new period and charge, attempt to start,
     pending ones to poll, dunning step), commits, then calls Wompi **with no lock or transaction open**, then applies the answer under the lock.
     Lifecycle changes of the company go through `ITenantBillingLifecycle` after the commit and are retried by the next run until recorded.
- **Pending transactions** (Nequi waiting for the payer, PSE in progress) are polled every 2 minutes and also resolved by the events. A
  transaction whose creation timed out (no id) is looked up by reference after 2 minutes; after 24 h without a trace it counts as failed.
- **Payment links** (PSE and every other method): one single-use Wompi link per open charge, valid 3 days (`Billing:PaymentLinkValidDays`),
  reused while valid. Its payment arrives as an event with `payment_link_id` and settles the same charge (amount checked).
- **Free plans**: a charge of 0 is settled at once (`Free`), with no gateway.
- **AI add-ons**: a paid charge with add-ons writes an `AiQuotaGrant` for `[period start, period end)`. The AI meter (docs/CLASSIFICATION.md,
  "Usage and quota") uses as the company's quota: its own quota set by the administrator, else the plan's included conversations (while the
  subscription is not cancelled), else the deployment default; **plus** every grant valid at that instant. The administrator's "limits" panel
  still shows the company's own/default quota; the plan and the add-ons are on the billing page. The monitoring "AI limited" list counts them.

## The company's billing page

`/organizacion/facturacion` (owner or holders of the new permission **`BillingManage`**, docs/USERS_TENANTS_PERMISSIONS.md). It is reachable
while the company is not Active (a company suspended for billing must be able to pay): the API's billing endpoints carry
`[AllowInactiveTenant]`. It shows plan, status and dunning stage, current period, next charge date and amount, payment method (masked),
the open charge with "Pagar ahora" and "Pagar con PSE u otro medio" (redirect to Wompi's checkout), cancel at period end / undo, and the
history of charges with their attempts.

`/organizacion/facturacion/medio-de-pago` adds or replaces the card or Nequi. The page gets the public key, the API URL and the two terms
(end-user policy and personal data authorization, with their links) from `GET /api/billing/payment-method/setup`. `wwwroot/js/billing.js`
tokenizes in the browser (`POST /tokens/cards`, or `POST /tokens/nequi` and then polls `GET /tokens/nequi/{id}` until the payer approves in
the app), the inputs have no `name` (nothing of the card or the number is posted), and only the token, the acceptance tokens, the ticked
consent and, for a card, brand and last four are posted. The server creates the Wompi payment source with the private key.

## Browser security

The Web sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and a CSP with `frame-ancestors 'none'`,
`base-uri 'self'`, `object-src 'none'` and `form-action 'self'` on every page (plus `https://checkout.wompi.co` on the billing page, whose
"PSE" button redirects there). The payment-method page, where card data is typed, adds `script-src 'self' 'nonce-…'` (the one inline theme
script carries a per-request nonce) and `connect-src 'self' https://sandbox.wompi.co https://production.wompi.co`. The page renders the form
only when the API's base URL is one of the two Wompi API bases, and `billing.js` refuses any other `data-api-base`.

## Deployment requirements

- Behind a reverse proxy, configure the forwarded headers (docs/DOCKER.md) so the failed-events limit counts real client addresses; without
  them every caller shares the proxy's address and only the failed-events budget is shared (verified events are never blocked by it).
- The image must have the `America/Bogota` zone data (the Debian-based `aspnet` image does; the smoke test checks it and the API refuses to
  start without it, naming `Metrics:TimeZone`).
- A company reactivated after its subscription was cancelled needs a new subscription started by the administrator (the old one stays as history).

## Administration

- `/admin/facturacion`: plan catalogue (create; same code updates name, price, included AI and active flag; code immutable) and AI add-ons.
- `/admin/empresas/{id}/facturacion`: start the subscription (plan, free days 0 to 90, billed lines override), change plan / lines / add-on
  (from the next period), mark a charge paid manually (reason required, e.g. bank transfer), void a charge (reason), cancel at period end
  or now (reason from the closed `admin.*` list), undo. Starting runs the engine at once, so the first charge exists immediately.
- The company list and page show the billing status and the dunning stage; the monitoring backlog counts past due and billing-suspended
  subscriptions, and two flags: `billing_events_failed` (critical) and `billing_attempt_stuck` (pending over a day).
- The administrator never sees card data: only the masked label and Wompi's source id.

## API

| Method and path | Who | What |
| --- | --- | --- |
| `GET /api/billing` | `BillingManage` | The company's subscription, open charge and history. 404 `subscription_not_found`. |
| `GET /api/billing/payment-method/setup` | `BillingManage` | Public key, API URL, acceptance tokens and links. 503 `billing_not_configured`. |
| `PUT /api/billing/payment-method` | `BillingManage` | `{type: Card|Nequi, token, acceptanceToken, personalDataToken, acceptedTerms: true, cardBrand?, cardLastFour?}`. 422 `payment_method_rejected`. |
| `POST /api/billing/charges/{id}/pay` | `BillingManage` | Attempt now with the stored method: `{status}`. 409 `payment_attempt_pending`, `payment_method_missing`, `charge_not_open`. |
| `POST /api/billing/charges/{id}/payment-link` | `BillingManage` | `{url}` of the single-use checkout link. |
| `POST /api/billing/cancel`, `POST /api/billing/cancel/undo` | `BillingManage` | Cancel at period end / undo. |
| `POST /api/billing/wompi/events` | anonymous | Wompi's events. Body at most 64 KB, checksum verified (body and `X-Event-Checksum`), environment checked, signed timestamp no older than `Billing:Events:MaxAgeHours` (72, covers Wompi's 24 h retries) and at most 10 minutes ahead; stored once **by its verified checksum** (the same event re-serialized is the same event); 200. A forged, altered or stale one gets 401 and counts against its address (`Billing:Events:FailedRateLimitPerMinute`, 30, then 429); verified events have their own platform-wide budget (`Billing:Events:RateLimitPerMinute`, 600), so anonymous junk cannot starve Wompi even behind a proxy that hides the client addresses. A payment through a link is matched only by the `payment_link_id` of the **re-read** transaction (never the event's). 404 when the gateway is not configured. |
| `POST /api/admin/tenants/{id}/billing/attempts/{attemptId}/review` | platform admin | `{markFailed, reason}`: the verdict on an attempt flagged for review. |
| `GET/POST /api/admin/billing/plans`, `PUT /api/admin/billing/plans/{id}`, `POST /api/admin/billing/plans/{id}/add-ons`, `PUT /api/admin/billing/add-ons/{id}` | platform admin | Catalogue. 409 `billing_code_taken`. |
| `GET /api/admin/tenants/{id}/billing`, `POST .../billing/start`, `PUT .../billing`, `POST .../billing/charges/{cid}/mark-paid`, `POST .../billing/charges/{cid}/void`, `POST .../billing/cancel` (`{atPeriodEnd, reasonCode}`), `POST .../billing/cancel/undo` | platform admin | A company's subscription. |

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Billing:Wompi:Environment` | (unset) | `Sandbox` or `Production`. Unset: no gateway (charges are created, nothing is sent, payment buttons hidden, events endpoint 404). Base URL fixed by it: `https://sandbox.wompi.co/v1` or `https://production.wompi.co/v1`. |
| `Billing:Wompi:PublicKey`, `PrivateKey`, `EventsSecret`, `IntegritySecret` | | Required with the environment; each must carry its environment's prefix (`pub_test_`, `prv_test_`, `test_events_`, `test_integrity_` or the `_prod_` ones) or the API does not start (the error names the key, never the value). The private key and both secrets are removed from the configuration once read and never leave the server. |
| `Billing:Wompi:TimeoutSeconds` | `15` | Per call, 2 to 60. One attempt per call. |
| `Billing:Dunning:RetryDays` | `1,3,5` | Increasing days after the due date, at most 10, each 1 to 90. Empty list in configuration is not possible; use one day. |
| `Billing:Dunning:GraceDays` | `7` | Days after the due date before the billing suspension (1 to 90). |
| `Billing:Dunning:CancelAfterSuspendedDays` | `30` | Days suspended before cancelling for non-payment (0 = never, max 365). |
| `Billing:Dunning:AttemptHourLocal` | `9` | Local hour of the automatic attempts. |
| `Billing:PaymentLinkValidDays` | `3` | Validity of a payment link (1 to 30). |
| `Billing:Job:Enabled`, `IntervalSeconds`, `BatchSize` | `true`, `60`, `100` | The job (off in the test hosts, which run it by hand). |
| `Billing:Events:RateLimitPerMinute` | `600` | Verified events per minute, platform-wide. |
| `Billing:Events:FailedRateLimitPerMinute` | `30` | Forged or stale events per client address per minute before 429. |
| `Billing:Events:MaxAgeHours` | `72` | How old a signed event may be (1 to 720). |

In the Wompi dashboard (Desarrolladores → Seguimiento de transacciones / URL de eventos) set the events URL to
`https://<api-host>/api/billing/wompi/events`, one per environment.

## Wompi facts (verified 2026-11-12 against docs.wompi.co, Colombia)

| Fact | Source |
| --- | --- |
| Environments Sandbox and Production, base URLs `https://sandbox.wompi.co/v1` and `https://production.wompi.co/v1`; keys `pub_test_`/`pub_prod_`, `prv_test_`/`prv_prod_`, events secret `test_events_`/`prod_events_`, integrity secret `test_integrity_`/`prod_integrity_`. | https://docs.wompi.co/docs/colombia/ambientes-y-llaves/ |
| Acceptance tokens: `GET /merchants/info` with the public key in `x-merchant-public-key` (the old `GET /merchants/{public_key}` stopped on 2026-10-31); `data.presigned_acceptance.{acceptance_token, permalink}` and `data.presigned_personal_data_auth.{acceptance_token, permalink}`, sent as `acceptance_token` and `accept_personal_auth`. | https://docs.wompi.co/docs/colombia/tokens-de-aceptacion/ |
| Card tokenization `POST /v1/tokens/cards` with the public key: `number`, `cvc`, `exp_month`, `exp_year`, `card_holder` → `data.id`, `brand`, `last_four`... | https://docs.wompi.co/docs/colombia/metodos-de-pago/ |
| Nequi: `POST /v1/tokens/nequi {phone_number}` (public key) → `data.id`, `status`; `GET /v1/tokens/nequi/{token}` goes `PENDING` → `APPROVED` when the payer accepts the subscription in the app. | https://docs.wompi.co/docs/colombia/fuentes-de-pago/ |
| Payment source `POST /v1/payment_sources` (private key): `type` (`CARD`/`NEQUI`), `token`, `customer_email`, `acceptance_token`, `accept_personal_auth` → `data.id`, `status` (`AVAILABLE`), `public_data` (Nequi: `phone_number`). | https://docs.wompi.co/docs/colombia/fuentes-de-pago/ |
| Transaction `POST /v1/transactions` (private key): `amount_in_cents`, `currency`, `signature`, `customer_email`, `reference`, `payment_source_id`, `payment_method.installments` (cards), `recurrent: true` (Credential on File, VISA/Mastercard through RBM). | https://docs.wompi.co/docs/colombia/fuentes-de-pago/ |
| Integrity signature: SHA-256 of reference + amount in cents + currency + integrity secret (no separators). The documented vector (`sk8-438k4-xmxm392-sn2m`, `2490000`, `COP` → `37c84077...bf5`) is a unit test. | https://docs.wompi.co/docs/colombia/widget-checkout-web/ |
| Statuses `PENDING`, `APPROVED`, `DECLINED`, `VOIDED` (cards only), `ERROR`; every transaction starts `PENDING`; `GET /v1/transactions/{id}` needs the private key; reference unique per transaction, at most 255 characters. | https://docs.wompi.co/docs/colombia/transacciones/ |
| Events: `transaction.updated` (also `nequi_token.updated`, ...); body `event`, `data.transaction`, `environment` (`test`/`prod`), `signature.properties`, `signature.checksum`, `timestamp`; checksum = SHA-256 of the listed property values + `timestamp` + events secret; same value in `X-Event-Checksum`; answer 200; retries up to 3 times (30 min, 3 h, 24 h). | https://docs.wompi.co/docs/colombia/eventos/ |
| Payment links `POST /v1/payment_links` (private key): `name`, `description`, `single_use`, `collect_shipping`, `currency`, `amount_in_cents`, `expires_at`... → `data.id`; checkout `https://checkout.wompi.co/l/{id}`. | https://docs.wompi.co/docs/colombia/links-de-pago/ |
| Amounts in cents, currency `COP`. Sandbox test data: card `4242 4242 4242 4242` approved, `4111 1111 1111 1111` declined; Nequi `3991111111` approved, `3992222222` declined; PSE `financial_institution_code` `1` approved, `2` declined. | https://docs.wompi.co/docs/colombia/datos-de-prueba-en-sandbox/ |

### Unverified (isolated in `WompiGateway`, covered by the opt-in sandbox test)

- The event page's printed checksum does not match its own example string (our SHA-256 of that string is `5a18ec5e...efbe`); we implement
  the documented algorithm and pin it on that string.
- `GET /v1/transactions?reference=` to find a transaction after a timeout: not documented.
- `public_data.brand` / `last_four` of a CARD payment source: not documented; the label falls back to what the browser's tokenization
  returned (brand and last four only).
- Whether every monthly Nequi charge needs an approval in the app (we handle it as pending either way) and how long acceptance tokens stay
  valid (the page fetches fresh ones each time).
- Whether a transaction from a payment link carries our data (we match it by `payment_link_id` and check the amount).

Opt-in sandbox test: `tests/Conversia.Infrastructure.UnitTests/Billing/WompiSandboxTests.cs`, runs only with `WOMPI_SANDBOX_PUBLIC_KEY`,
`WOMPI_SANDBOX_PRIVATE_KEY`, `WOMPI_SANDBOX_EVENTS_SECRET` and `WOMPI_SANDBOX_INTEGRITY_SECRET` set (sandbox keys only).

## Manual test in the Wompi sandbox

1. Set the four sandbox keys and `Billing:Wompi:Environment=Sandbox`; expose the API (a tunnel) and set the events URL in the sandbox dashboard.
2. Admin: create a plan, start a subscription for a company without trial. Owner: `/organizacion/facturacion` shows the open charge.
3. Add the card `4242 4242 4242 4242` (any future date, any CVC), "Pagar ahora": approved, paid. Repeat on another company with
   `4111 1111 1111 1111`: declined, retries scheduled, "update the method" alert.
4. Nequi `3991111111`: approve in the sandbox flow; the payment stays pending until the event or the poll resolves it.
5. "Pagar con PSE": pay with the approving bank; the event settles the charge.

## Runbook

- **A failed event** (`billing_events_failed`): the API log has the event id and the error code. Usually the transaction could not be read
  (Wompi down, wrong private key). Fix the cause and set the row back: `UPDATE "BillingEvents" SET "Status"='Pending', "Attempts"=0,
  "NextAttemptAt"=now() WHERE "Id"=...`. Polling also resolves pending attempts on its own.
- **A stuck pending attempt** (`billing_attempt_stuck`, or flagged `unknown_outcome`): look the transaction up in the Wompi dashboard by our
  reference (`cv_...`, on the admin billing page). If Wompi has it, the next poll applies it (or set its id). If Wompi never created it,
  resolve the review with "mark failed": the next retry may then start. Never charge by hand while one is pending.
- **Manual payment** (transfer, cash): admin billing page, "Marcar como pagado" with the reason. A billing suspension is lifted at once.
- **Payment needing review** (log event 4301 `BillingPaymentNeedsReview`; 4302 `BillingLifecycleChangeFailed` for a lifecycle change retried, audit `billing.payment_needs_review`): a duplicate payment, a
  payment after a void, an amount that does not match, or an approved link payment that matches no charge. Refund it in the Wompi dashboard
  (refunds are manual) or mark the right charge paid.
- **Refunds** are always made by hand in the Wompi dashboard; Conversia does not call any refund API.
- **Rotating keys**: change them in configuration and restart. The events secret must match the dashboard's.
