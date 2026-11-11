# Internal sandbox mode

A switch per company that takes 360dialog out of the picture, so the whole product can be tried end to end without the provider and
without its sandbox limits: ingestion, conversations, classification, metrics, the dashboard and sending. **It runs the real pipeline, not
a copy of it.** The only two things that are simulated are the two ends that would touch the provider.

```
                     ┌────────────── real, unchanged ──────────────┐
simulated customer ─►│ WebhookEvents queue ─► worker ─► parser      │
(API builds the      │   ─► contacts, conversations, messages       │─► metrics, dashboard, alerts
 provider's payload) │   ─► classification request ─► classifier    │   (sandbox data only, while the mode is on)
                     │   ─► close by inactivity                     │
advisor's message ──►│ MessageSendingService ─► record (same locks) │
                     └────────────────────┬────────────────────────┘
                                          ▼
                             internal sender: no network call
```

| Section | What |
|---|---|
| [Switching it](#switching-it) | Only the platform administrator, with a reason |
| [What is simulated, what is real](#what-is-simulated-what-is-real) | The two simulated ends |
| [Endpoints](#endpoints) | The simulated customer and the clean-up |
| [Data rules](#data-rules) | The sandbox mark, the metrics, the wall between both kinds of data |
| [From the Web](#from-the-web) | Banner, both composers, badge, clean-up |
| [Security](#security) | What the mode cannot be used for |
| [The other two "sandboxes"](#the-other-two-sandboxes) | The 360dialog sandbox and the development simulator |
| [Limits](#limits) | What it does not prove |

## Switching it

`Tenants.SandboxMode` (boolean, `false` for every company). Only a **platform administrator** changes it:

```
PUT /api/admin/tenants/{id}/sandbox-mode      {"enabled": true, "reason": "Demo with the pilot customer"}
```

- `reason` is **required**, 1 to 200 characters after trimming. `enabled` is required.
- The change goes through `TenantUnitOfWork` (the lock of the company's row) and writes one audit entry,
  `tenant.sandbox_mode_changed` with `from`, `to` and `reason`. Asking for the value it already has writes nothing.
- `GET /api/admin/tenants` and `GET /api/admin/tenants/{id}` carry `sandboxMode`. The company reads it in `GET /api/tenant`
  (`sandboxMode`) and in `GET /api/users/me` (`tenantSandboxMode`).
- **A company cannot switch it.** Its own API has no endpoint that writes it, and the administrator's endpoint refuses a company's token.
- The mode is read on each request (one column by primary key), never cached: a change applies to the next request on every instance.

In the Web: `/admin/empresas/{id}`, card "Modo sandbox", then the confirmation page that asks for the reason.

## What is simulated, what is real

| | In sandbox mode | |
|---|---|---|
| A customer writes | **Simulated.** The API builds the delivery 360dialog would have posted (Cloud API envelope, one text message) and hands it to `IWebhookIngestionService.StoreAsync` | Everything after that call is the real path |
| Queue, worker, parser, de-duplication, contacts, conversations, classification request, classifier, close by inactivity | Real | Same code, same tables, same locks |
| An advisor sends a message | **Simulated transport.** `WhatsAppMessageSenderResolver` returns `SandboxMessageSender`: no network call, no provider key, accepted at once with an id that starts with `sandbox.out.` | `MessageSendingService` checks and records exactly as for 360dialog |
| Metrics, dashboard, alerts | Real queries, over sandbox data only | See [Data rules](#data-rules) |
| The webhook (`POST /api/webhooks/360dialog/{channelKey}`) | Real and unchanged: same authentication, same limits | What arrives is stored as real data |

When the mode is off, nothing of this is in the path: the resolver returns the 360dialog adapter as before.

## Endpoints

All three act on the company of the token; no route or body names another company.

| Method | Route | Permission | Answer |
|---|---|---|---|
| `POST` | `/api/sandbox/conversations` | `ConversationsManage` | `202` `{customerWaId, sentAt}` |
| `POST` | `/api/sandbox/conversations/{id}/customer-messages` | `ConversationsManage` | `202` `{customerWaId, sentAt}` |
| `DELETE` | `/api/sandbox/data` | `OrganizationManage` | `200` `{conversations, messages, contacts, webhookEvents}` |

**Starting a conversation.** Body `{"channelId": "...", "customerName": "...", "text": "..."}`: an active channel of the company, a name
of 1 to 200 characters, a text of 1 to 4096. The API makes up the customer's number; a request cannot name one.

**Writing as the customer.** Body `{"text": "..."}`. The conversation must be sandbox data, open, and on an active channel.

**`202`, not `201`.** The message is in the ingestion queue, like a delivery of the provider; the worker turns it into a conversation or
a message a moment later, so the answer carries no conversation or message id.

**Limits.** Both `POST`s share the budgets of sending (`Messaging:SendRateLimitPerMinute` per user, `Messaging:TenantSendRateLimitPerMinute`
per company): `429` with `Retry-After`.

**Refusals** (`application/problem+json` with a `code`):

| Status | `code` | When |
|---|---|---|
| `400` | *(validation)* | Missing channel, name or text; too long; U+0000 |
| `404` | | Unknown channel or conversation, or one of another company |
| `409` | `sandbox_mode_off` | The company is not in sandbox mode (both `POST`s) |
| `409` | `not_sandbox_conversation` | The conversation is real |
| `409` | `conversation_closed` | The conversation is closed: start a new one |
| `409` | `channel_inactive` | The channel is switched off |

And on `POST /api/conversations/{id}/messages` (see [MESSAGING.md](MESSAGING.md#errors)):

| Status | `code` | When |
|---|---|---|
| `409` | `sandbox_conversation` | A sandbox conversation while the mode is **off**: nothing real is ever sent to a made-up number |
| `409` | `sandbox_mode_active` | A real conversation while the mode is **on**: nothing is sent, and no made-up message is stored in real data |
| `409` | `sandbox_data_deleted` | In sandbox mode, the conversation was deleted by the clean-up while the message was on its way (the record found no row, or hit the foreign key). Nothing was stored and nothing left the server, so it is a conflict, never a `500` |

**The clean-up** is allowed whatever the mode (the usual moment is after it was switched off). One transaction, under the tenant's
ingestion lock (the one in [WEBHOOKS.md](WEBHOOKS.md#lock-order-why-two-workers-cannot-deadlock-on-conversations)), with every statement
naming the tenant: the messages and opportunities of the conversations marked sandbox, those conversations, the contacts marked sandbox,
and the deliveries marked sandbox, **including the ones still waiting in the queue**, so nothing comes back afterwards. Nothing that is
not marked is touched; the foreign keys are `RESTRICT`, so a statement that ever matched a row real data points to would fail and roll
everything back.

- **Racing the worker.** A delivery being processed holds the same lock: the clean-up waits for it, then removes what it created. A
  delivery the worker had already claimed but not started is deleted under the clean-up; the worker then finds no row and drops it
  quietly (an information line, not a failure).
- **Racing a send.** See `sandbox_data_deleted` above.
- **The trail is the application log, and only the log.** Each clean-up writes one information line (`SandboxService`) with the tenant
  id, the user id and the four counts; a clean-up that fails writes none. There is no tenant-level audit table in Conversia to put it in
  (`PlatformAuditLogs` is the platform administrators' trail and needs an administrator; `ErrorLogs` is for unexpected errors), and none
  was added for this. Keep the API log as long as this trail must be kept.

## Data rules

**The mark.** Three columns, all `false` by default: `WebhookEvents.IsSandbox`, `Contacts.IsSandbox`, `Conversations.IsSandbox`.

- It is **set at creation by the pipeline itself and propagated from the delivery**: `SandboxService` stores the delivery with
  `WebhookChannelAccess.IsSandbox = true`; `WebhookEventProcessor` creates the contact with the delivery's mark and the conversation with
  the contact's. It is never changed afterwards and **never inferred from a number**.
- Messages and opportunities have no column of their own: they are sandbox data exactly when their conversation is (one source of truth).
- `AuthenticateAsync`, the only way in for the webhook, always yields `IsSandbox = false`: nothing a caller of the webhook sends (header,
  query string, JSON field) can mark a delivery.

**The simulated customer's number.** `00000` followed by ten random digits (15 in all, a valid WhatsApp id for the product). No E.164
country code begins with `0`, so such a number **can never be a real subscriber**, and a person recognizes it at a glance. The API never
hands out a number the company already has. The prefix is for people; code decides by the mark.

**The wall between both kinds of data.** In `WebhookEventProcessor`, a message whose delivery and contact disagree is not stored and is
counted as skipped: a sandbox delivery never touches a real contact, and a real delivery that names a simulated customer's number
(which no real customer can have) adds nothing to a sandbox conversation. Each such delivery leaves one warning with ids only (event,
tenant, channel, the contacts concerned; no number, name or text): it should never happen, so somebody must look.

**Metrics, dashboard and alerts.** Every aggregation of `MetricsQueries` carries one condition, written once in the shared filter:

```sql
AND c."IsSandbox" = (SELECT t."SandboxMode" FROM "Tenants" t WHERE t."Id" = @tenant)
```

- Mode **off**: sandbox data is **excluded**. Real numbers never include a test.
- Mode **on**: **only** sandbox data is counted, so a test is readable on an otherwise empty dashboard.
- Switching back restores the real numbers exactly; nothing was changed, only hidden.

There are no exports today; one that is added must read through the same filter. Details in [METRICS.md](METRICS.md#sandbox-data).

**Lists and the detail** show both kinds, with a "Sandbox" badge on the sandbox ones (`isSandbox` in `GET /api/conversations` and
`GET /api/conversations/{id}`), whatever the mode; paging and every filter work over both.

**The diagnostic views** (`/mensajes`, `/mensajes/eventos`; `GET /api/messages/recent`, `GET /api/webhook-events`) are there to check that
messages arrive, not to measure anything, so they list **both kinds in both modes**, each row marked: `isSandbox` of a message is its
conversation's mark (the same source the metrics use), `isSandbox` of a delivery is the delivery's own. The Web shows the same badge.
The counters of `GET /api/messages/summary` ("today", "last 24 hours") count both kinds too.

**The platform administrator's "last activity"** of a company is the time of its last **real** message: sandbox messages are tests, not
activity of the business, whatever the mode.

**When the mode changes, nothing is moved, closed or deleted.**

| What is open | When the mode | What happens |
|---|---|---|
| Sandbox conversations | goes **off** | They stay open, marked, listed with the badge, out of the metrics. Nobody can write into them (`sandbox_conversation`, and the simulated customer is `sandbox_mode_off`). The inactivity job closes them as it closes any conversation (and marks a sale lost for no response the same way); they stay out of the metrics, closed or not |
| Real conversations | goes **on** | They stay open and keep receiving real webhooks. Conversia does not write into them (`sandbox_mode_active`); advisors answer from their phones as always. The inactivity job closes them as usual. They are hidden from the metrics until the mode goes off, and then counted exactly as the job left them |

**Real webhooks while the mode is on** keep being authenticated, stored and processed as **real, non-sandbox data**. Nothing is dropped:
those conversations are in the list (without the badge) and simply out of the metrics until the mode is switched off. What is refused
meanwhile is *writing* into them (`sandbox_mode_active`).

## From the Web

| Where | Who | What |
|---|---|---|
| Every page of the company | anyone signed in | A banner, "Modo sandbox activo", while the mode is on |
| `/conversaciones` | `ConversationsManage` | "Simular una conversación" (only while the mode is on); a "Sandbox" badge on each sandbox row |
| `/conversaciones/simular` | `ConversationsManage` (+ `OrganizationView` to list the numbers) | Number, customer name, first message |
| `/conversaciones/{id}` | `ConversationsManage` | On a sandbox conversation: the usual composer (no provider key needed, no 360dialog sandbox note) and a second one, "Escribir como el cliente" |
| `/organizacion`, card "Datos de prueba (sandbox)" | `OrganizationManage` | Whether the mode is on, and the link to the clean-up |
| `/organizacion/sandbox/eliminar-datos` | `OrganizationManage` | The confirmation; nothing is deleted before it is submitted |
| `/admin/empresas/{id}` and `/admin/empresas/{id}/sandbox/{activar\|desactivar}` | platform administrator | The switch, with the required reason; a badge in the listing |

- **Two composers, one component.** `MessageComposer` with `Customer="true"` is the second one; the page wraps it in a
  `FormMappingScope`, so the two forms never read each other's post. Each has its own one-time id (`OneTimeSubmissions`).
- **The simulated message is not in the answer.** After "Enviar como cliente" (or after starting a conversation) the page says it is in
  the queue; it shows up within the worker's polling interval. Reload if it is not there yet.
- **The composer follows the mode** (`ComposerState`): sandbox conversation in sandbox mode, offered without a key; real conversation in
  sandbox mode, or sandbox conversation outside it, not offered, with the reason. The API checks all of it again.

## Security

- Only the platform administrator switches the mode; every switch is audited with its reason.
- The simulated-customer endpoints work on the company of the token: another company's channel or conversation is a `404`, the same as
  an unknown one. They refuse real conversations, so they cannot inject a message into real data.
- Sandbox mode does not touch the webhook: same secret, same uniform `401`, same limits. No endpoint lets the mode weaken it.
- A company that is pending or suspended gets the same `403` `tenant_not_active` here as on the rest of its API.
- The simulated customer writes into Conversia only. No message of a company in sandbox mode leaves the server: tests assert it on the
  wire (`SandboxModeTests`, with the real resolver and the real adapter over a recording transport, and a stored key).

## The other two "sandboxes"

| | Internal sandbox mode (this document) | 360dialog sandbox ([PRUEBAS_SANDBOX.md](PRUEBAS_SANDBOX.md)) | Webhook simulator ([SIMULATOR.md](SIMULATOR.md)) |
|---|---|---|---|
| What it is | A mode of the product, per company | The provider's test environment | A development tool (`tools/`) |
| Where it runs | Any installation, production included | Against 360dialog, through a public tunnel | A local API only |
| Who starts it | The platform administrator | Whoever holds a sandbox key | A developer, from the command line |
| Uses 360dialog | No | Yes: 25 requests per key, your own number only | No |
| Enters through | `IWebhookIngestionService.StoreAsync` (after authentication) | The real webhook over HTTP | The real webhook over HTTP |
| Its data | Marked sandbox, out of the real metrics | Real data | Real data (many credible chats, for a full dashboard) |
| Proves | The product end to end, both sides, repeatably | That 360dialog and Conversia talk to each other | The pipeline under volume; fills a dashboard |

A channel's `providerEnvironment` (`Sandbox` or `Production`) is about the second column: which 360dialog host a real message goes to. It
has nothing to do with this mode.

## Limits

- **It does not prove anything about 360dialog**: not the key, not the webhook registration, not the real payloads, not delivery. The
  simulated delivery has the documented Cloud API shape, the same one the parser is tested with; whether the real provider sends exactly
  that is still what [PRUEBAS_SANDBOX.md](PRUEBAS_SANDBOX.md) is for.
- Text only, from the customer and from the advisor. No media, no ad referral (every simulated conversation is `Organic`), no statuses.
- A reply to a **closed** sandbox conversation is refused instead of opening a new one; start a new simulated conversation.
- Timestamps of the customer's messages have whole seconds, like the provider's. A reply written within the same second as the last
  message takes the next second, so the order in the thread is the order of writing.
- While the mode is on, real conversations cannot be written to from Conversia (advisors still answer from their phones, as always).
- Switching the mode does not delete anything. Sandbox data stays, marked and out of the metrics, until the company deletes it.
- The clean-up has no undo and no partial form: it removes all the sandbox data of the company.
- **Rolling back the migration** `AddSandboxMode` drops the marks: sandbox rows become indistinguishable from real ones and enter every
  metric. Delete the sandbox data of every company first.
