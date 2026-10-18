# 360dialog webhook ingestion

How Conversia receives WhatsApp messages: 360dialog calls a webhook, the API stores
the delivery untouched and answers `200`, and a background worker turns stored
deliveries into contacts, conversations, messages and opportunities.

```
360dialog ──POST──► /api/webhooks/360dialog/{channelKey}
                      │ authenticate (channel key + X-Webhook-Secret)
                      │ validate (one JSON object, size limit), SHA-256 of the body
                      ▼
                  WebhookEvents (Pending, jsonb)  ──► 200 immediately
                      │
        background worker (FOR UPDATE SKIP LOCKED, one transaction per event)
                      ▼
        Contacts · Conversations · Messages · Opportunities
```

The request path never parses the payload and never does business work. Whatever
360dialog sends that is a JSON object is stored first; interpreting it is the
worker's job, so a parsing bug can never lose a message.

## Endpoint

`POST /api/webhooks/360dialog/{channelKey}`

| | |
|---|---|
| Authentication | None (no JWT). The channel is identified by `{channelKey}` and authenticated by the header `X-Webhook-Secret`. |
| Body | One JSON object, at most `Webhooks:MaxBodyBytes` (default 1 MiB). Any content type. |
| `200` | Stored as a new pending event, **or** an event with the same body hash already existed for the channel (nothing stored). 360dialog must always see `200` for a delivery we have, otherwise it retries. Empty body. |
| `400` | The body is empty, not a single JSON object, or JSON that PostgreSQL `jsonb` cannot hold (a `\u0000` escape, an unpaired surrogate). `application/problem+json`. |
| `401` | Unknown key, malformed key, inactive channel, missing secret or wrong secret. **One identical answer** for all of them, so nothing tells an attacker which part failed. The body is not read. |
| `413` | Body over the limit (checked on `Content-Length` and, for chunked bodies, while reading). |
| `429` | The client IP failed to authenticate more than `Webhooks:FailedAuthRateLimitPerMinute` times (default 60) in a minute (it is then refused before being authenticated), or the channel received more than `Webhooks:ChannelRateLimitPerMinute` deliveries (default 6000). Has `Retry-After`. See [Rate limits](#rate-limits-and-proxies). |

The channel (key and secret) is created with `POST /api/channels` (permission
`OrganizationManage`). The `201` answer holds the relative `webhookPath` and the
`webhookSecret`, **shown only once**: only its SHA-256 hash is stored. The key is
256 random bits (43 URL-safe characters), unique across all tenants; the secret is
another 256 random bits. The call compares `SHA-256(received secret)` with the
stored hash using `CryptographicOperations.FixedTimeEquals`.

### Rate limits and proxies

Two limits, both fixed one-minute windows without queueing, kept in memory per instance (idle partitions are evicted
by the limiter, so memory stays bounded):

- **Unauthenticated junk, per client IP**: only *failed* authentications count. An address over its budget gets `429` before it
  is authenticated, so guessing keys and secrets is throttled and a legitimate provider never spends this budget.
- **Authenticated deliveries, per channel**: generous, counted after the secret matched. One misbehaving channel cannot starve the others,
  and tenants behind the same proxy IP do not compete.

Behind a reverse proxy or tunnel every request arrives from the proxy's address, so all failed attempts would share one IP
budget. `X-Forwarded-For` is therefore supported but **off by default and never trusted blindly**: name the proxies explicitly with
`ForwardedHeaders:KnownProxies:0=10.0.0.5` (addresses) and/or `ForwardedHeaders:KnownNetworks:0=10.0.0.0/8` (CIDR). The header is
honored only when the immediate sender is one of them, one hop (`ForwardLimit = 1`); otherwise it is ignored. Invalid entries stop
the API at startup. It applies to the whole API (the login limiter too), as the template documents.

### Managing a channel

| Call | Permission | Effect |
|---|---|---|
| `POST /api/channels/{id}/deactivate` | `OrganizationManage` | The provider gets the same `401` as for a wrong secret. Idempotent. |
| `POST /api/channels/{id}/activate` | `OrganizationManage` | Turns it back on. Idempotent. |
| `POST /api/channels/{id}/rotate-secret` | `OrganizationManage` | Returns the **new** `webhookSecret` once (`Cache-Control: no-store`); the old one stops working immediately. The URL (channel key) does not change: update the secret header at 360dialog right after. |

`GET /api/channels` shows `channelKey` and `webhookPath` only to callers who hold `OrganizationManage` (the owner included); a caller with
only `OrganizationView` sees the channel without them, because the key is half of the webhook credential. Another tenant's channel is a `404`.
The channel key of the URL is also redacted (`/api/webhooks/360dialog/{channelKey}`) from the paths and route values stored in the error
registry and from the log lines of unexpected errors.

### Tenant context

The webhook has no JWT, so the tenant is not known until the channel is. The only
lookup that ignores the tenant filter is `GetByChannelKeyAsync`; once the secret
matches, `ITenantContext.SetTenant(channel.TenantId)` fixes the tenant of the DI
scope, and everything afterwards (the insert, and in the worker every query) runs
under the normal tenant query filters. The worker claims across tenants (the queue
is shared) and then sets the tenant of the claimed event on a scope of its own
before it touches any tenant data. Every Conversia table carries `TenantId`, and
`ConversationModelSchemaTests` fails if an entity with a `TenantId` has no filter.

## Registering the webhook with 360dialog (sandbox)

**Documentation only. Nothing in this repository calls 360dialog.**

1. Create a branch and a channel through the API (`POST /api/branches`,
   `POST /api/channels`) and keep the `webhookPath` and the `webhookSecret`.
   If you know the number's `phone_number_id`, send it as `providerPhoneNumberId`;
   deliveries naming a different number are then ignored.
2. The sandbox must reach the API from the internet, so a public tunnel (ngrok,
   Cloudflare Tunnel, ...) in front of the local API is needed. Use its HTTPS URL.
3. Register the URL with your sandbox API key (never commit the key; the app reads
   it from `Dialog360:ApiKey` / the environment variable `Dialog360__ApiKey`, and
   compose passes `D360_API_KEY` through):

```bash
curl -X POST "https://waba-sandbox.360dialog.io/v1/configs/webhook" \
  -H "D360-API-KEY: $D360_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "url": "https://<your-tunnel-host>/api/webhooks/360dialog/<channelKey>",
        "headers": { "X-Webhook-Secret": "<webhookSecret>" }
      }'
```

360dialog then sends the secret back in the `X-Webhook-Secret` header of every call.

## Payload mapping

`Dialog360PayloadParser` (`Conversia.Application/Ingestion`) is the only class that
knows the shape of a delivery. It never throws on content: what it cannot use is
skipped and counted, and a delivery with nothing to store is processed as
"ignored", not failed.

The WhatsApp Cloud API envelope is `entry[].changes[].value`. The older on-premise
shape, with `contacts`, `messages` and `statuses` at the root, is read too.

| Payload | Becomes |
|---|---|
| `value.messages[]` | An **Inbound** message. |
| `messages[].from` | The customer: `Contact.WaId`. **Strict**: after trimming and an optional leading `+`, only ASCII digits, 7 to 15 of them (E.164). Spaces, dashes, dots and letters are *not* repaired: such a message is skipped and counted. In particular a business-scoped user id (`CO.13491208655302741918`) never becomes a phone number, so different customers cannot merge. |
| `value.contacts[].profile.name` (matched by `wa_id`) | `Contact.DisplayName`; the latest non-blank name wins. |
| `messages[].id` | `Message.ProviderMessageId` (case-sensitive; unique per channel). |
| `messages[].timestamp` (unix seconds, string or number; decimals and exponents such as `1792324800.0` or `1.7923248e9` are truncated to the second) | `Message.SentAt`, UTC. Only 2015-01-01 up to one day after now is plausible; anything else is skipped and counted. |
| `messages[].type` | `Message.Type` in lower case (`text`, `image`, `audio`, ...); an odd value becomes `unknown`. |
| `text.body`, or `button.text`, or the title of an `interactive` reply, or the `caption` of a media message | `Message.Text` (4096 characters at most, NUL removed). Null for media without caption. |
| `messages[].referral` (click-to-WhatsApp ad) | `Conversation.Source = Ads`; `SourceReference` is the first *non-blank* of `headline`, `source_url`, `source_id`, `body`; a later referral fills a reference the ad conversation lacked. |
| `value.statuses[]` | Ignored (delivery receipts); counted in `WebhookEvents.StatusCount`. |
| `messages[]` with a `group_id` (or `recipient_type: group`) | Skipped and counted: group chats are not customer conversations. |
| `messages[]` of type `reaction` or `system` | Ignored: they never create a conversation nor set `FirstInboundAt`/`FirstResponseAt`. |
| `value.message_echoes[]` (`field: smb_message_echoes`) | An **Outbound** message; the customer is `to`. **Unverified**, see below. |
| A `messages[]` entry whose `from` is the business number (`metadata.display_phone_number`, compared by its digits) | Also **Outbound**, customer = `to`. Defensive fallback, **unverified**. |
| An outbound entry with no `to` | Belongs to `contacts[0]` only when the delivery has exactly one contact; otherwise skipped and counted (never a guess). |
| `metadata.phone_number_id` | Not used to find the channel (the URL does). If the channel has a `ProviderPhoneNumberId` and the delivery names another one, its messages are ignored. |
| Anything else (templates, history sync, unknown fields) | Ignored. |

### The unverified part: messages sent from the phone

Advisors keep answering from the WhatsApp Business app, so the product depends on
receiving those outgoing messages (coexistence "echoes"). The format implemented
follows the public documentation (360dialog "Coexistence Webhooks" and Meta's
`smb_message_echoes` reference: `message_echoes[]` with `from`, `to`, `id`,
`timestamp`, `type` and a type-named object) but **has not been verified against a
real 360dialog sandbox**. Until it is, treat first-response metrics as unproven.
If the real shape differs, only `Dialog360PayloadParser` has to change; the tests
build their payloads from `tests/Shared/Dialog360Samples.cs`. Also open: newer API
versions may identify users by a business-scoped id (BSUID) and omit the phone
number; such messages are skipped and counted in `SkippedMessageCount`, never stored under a fake number. Supporting them needs a decision about the contact key.

## Processing

`WebhookEventProcessingService` (hosted service, `Webhooks:Processing:Enabled`, on by
default) polls the queue. `WebhookEventRunner` handles one event per call, in three steps:

1. **Claim** (short transaction): `SELECT ... WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now ORDER BY ... LIMIT 1 FOR UPDATE SKIP LOCKED`.
   A row another worker is claiming is skipped, never waited for. In that same committed step
   the **attempt is counted** (`Attempts + 1`) and the event is **leased**: `NextAttemptAt` moves to
   now + `2 * ProcessingTimeoutSeconds + 10 s`. Nobody else can take it meanwhile.
   If the process dies, the lease expires, the event is claimed again and the attempt it used
   stays counted, so a payload that kills the worker ends up `Failed` (an event found with all its
   attempts used and never finished is marked `Failed` on the claim, not processed again).
   *Design choice*: counting at claim time on `Attempts`/`NextAttemptAt` needs no extra column and no
   clock-skew-sensitive "locked until" field; the cost is that `Attempts` means "attempts started".
2. **Process** (second transaction, bounded): `statement_timeout` and
   `idle_in_transaction_session_timeout` are set `LOCAL` to `ProcessingTimeoutSeconds` (60 s) and
   the whole step has that same total budget. The tenant is set, then a per-tenant advisory lock is
   **tried** (`pg_try_advisory_xact_lock`, spinning at most `TenantLockWaitMilliseconds`, 2 s): if
   another worker holds the tenant, the event is **deferred** `TenantBusyDelaySeconds` (10 s, jittered
   50-150 %) *without consuming the attempt*, and the worker moves on to other events and tenants.
   Otherwise the delivery is processed and the event marked `Processed` in one commit.
   A delivery with more than `MaxMessagesPerDelivery` (500) messages is a permanent failure: `Failed`
   at once, no retries, nothing stored.
3. **Failure bookkeeping**: any exception anywhere after the claim (setting the tenant, the lock, the
   processing, the commit) rolls the processing transaction back and records the attempt in a *fresh*
   transaction on a fresh context that does not depend on the tenant context or on the dead
   transaction: sanitized `LastError` and the exponential back-off, or `Failed` at the limit. If even
   that cannot be written, the lease brings the event back later. The worker loop never faults: a
   failing cycle is logged (sanitized description, never the raw exception) and the next one waits
   2 s, 4 s, ... up to 60 s, back to normal after a cycle that works; shutdown never faults it.

Rules applied when a message is stored:

- **Contact**: upsert by normalized `WaId` within the tenant.
- **Conversation**: the contact's `Open` conversation on that channel is reused;
  otherwise one is created with `StartedAt` = the message time, `Source` = `Ads`
  if the message has a referral, else `Organic`. A new conversation always gets
  one `Opportunity` (`Stage = New`). The database allows at most one open
  conversation per contact and channel, and one opportunity per conversation.
  A **closed** conversation takes no more messages: the customer's next message opens a
  new conversation (and a new opportunity), see [Closing](#closing-conversations). The
  open conversation is looked up with `SELECT ... FOR UPDATE`, so the job that closes idle
  conversations (which skips locked rows) can never close one a message is being added to.
- **Message**: unique on `(ChannelId, ProviderMessageId)`. A message that is already
  stored (the same message inside another delivery) is skipped.
- **Times** are recomputed from the stored message times once per conversation after a
  delivery's messages are in, never from arrival order: `StartedAt` is the earliest
  message, `LastMessageAt` the latest, `FirstInboundAt` the earliest customer message,
  and `FirstResponseAt` the earliest business message **at or after** `FirstInboundAt`
  (timestamps have one-second resolution, so a same-second reply counts; a business
  message sent *before* the customer's first one is not a response). A delivery that
  arrives late or out of order therefore fixes the markers instead of breaking them.
- **Classification**: every conversation that received a new message is flagged (`ClassificationDirty`, `ClassificationRequestedAt`,
  failures reset) in the same transaction; the classification worker reads it once it has been quiet (`docs/CLASSIFICATION.md`).
- **Display name**: the profile name of the latest message (by message time) wins; an
  older delivery processed late never overwrites a newer name.
- **Skipped messages are visible**: `WebhookEvents.SkippedMessageCount` (customer or
  advisor messages that could not be stored: no usable phone number or id, implausible
  timestamp, group, ambiguous recipient, another business number) and `StatusCount` are
  filled when the event is processed, and a Warning (without any customer data) is logged
  when the skipped count is above zero.

### Lock order: why two workers cannot deadlock on conversations

A review asked whether two ingestion workers that touch conversations A and B in opposite order can deadlock. They cannot, and nothing needs ordering:

- **Ingestion is serialized per tenant.** Before touching any row, the processing transaction takes the tenant's advisory lock
  (`pg_try_advisory_xact_lock(hashtextextended(tenantId))`) and keeps it until it commits or rolls back. Every ingestion transaction of a tenant therefore runs alone;
  a second worker of the same tenant never gets as far as `SELECT ... FOR UPDATE` on a conversation, it defers the event. A conversation belongs to one tenant, so two
  workers of *different* tenants never meet on the same row. (If two tenants collide on the 64-bit hash they merely wait for each other.) The `FOR UPDATE` of
  `FindOpenAsync` is not there to order workers: it is there against the jobs below.
- **Every other path that locks conversation rows never waits for one.** The job that closes idle conversations and the classification worker claim with
  `FOR UPDATE SKIP LOCKED` (a locked row is skipped, not waited for) and only write rows they hold. The manual outcome (`PUT .../opportunity`) takes one
  conversation row and then its opportunity; assigning an advisor is a single `UPDATE` of one row. All of them take the **conversation row before its opportunity**, and nobody
  takes an opportunity row without holding its conversation, so there is no cycle: a path that waits (ingestion, a person's decision) waits for a path that does not.

If a new code path ever locks several conversations and *waits* for them, it must either take the tenant lock first or lock them in `Id` order.

### Idempotency, in layers

1. Same body twice on a channel: one `WebhookEvents` row (`(ChannelId, BodyHash)` unique); `200` both times.
2. Same message inside different bodies: one `Messages` row (`(ChannelId, ProviderMessageId)` unique).
3. Processing an event twice: the second pass finds every message already stored.

### Retries

A failed attempt sets `NextAttemptAt = now + RetryBaseDelaySeconds * 2^(attempts-1)`
(30 s, 1 min, 2 min, 4 min, 8 min, 16 min, 32 min, capped at 6 h). At `MaxAttempts` (default 8,
about an hour of retries, so a short database outage does not burn the budget) the event is
`Failed`, nothing retries it, and it is also registered in the error registry (`ErrorLogs`,
`docs/ERRORS.md`). `LastError` is `WebhookErrors.Describe`: the exception type and its innermost
message, sanitized with `ErrorSanitizer` (secrets redacted, cut to 1000 characters) and with digit
runs of 7 or more masked, because database errors can quote values such as phone numbers.

### Runbook

- **See the queue**: `SELECT "Status", count(*), min("ReceivedAt") FROM "WebhookEvents" GROUP BY 1;`
  and `GET /health` (see below).
- **Why did an event fail?** `SELECT "Id", "TenantId", "Attempts", "LastError" FROM "WebhookEvents" WHERE "Status" = 'Failed' ORDER BY "UpdatedAt" DESC;`
- **Requeue failed events** once the cause is fixed. In code: `IWebhookEventMaintenance.RequeueFailedAsync(tenantId, eventId?)`
  (tenant-scoped, there is deliberately no HTTP endpoint). By SQL:

  ```sql
  UPDATE "WebhookEvents"
  SET "Status" = 'Pending', "Attempts" = 0, "NextAttemptAt" = now(), "LastError" = NULL, "ProcessedAt" = NULL
  WHERE "Status" = 'Failed' AND "TenantId" = '<tenant id>';   -- add AND "Id" = '<event id>' for one
  ```

- **Rolling back a deploy**: roll the *application* back to the previous version and leave the database
  as it is. The migrations only add tables and columns, which older code ignores. **Do not run the
  `Down` of `AddConversationsModel`** (or of any later migration): it drops the conversation tables
  and destroys every stored contact, conversation, message and webhook event. If a migration is
  wrong, fix forward with a new migration. If the webhook must stop meanwhile, deactivate the
  channels (`POST /api/channels/{id}/deactivate`) or set `Webhooks:Processing:Enabled=false`:
  deliveries keep being stored (when the endpoint is up) and are processed later.

## Closing conversations

`ConversationLifecycleService` (`Conversations:AutoCloseEnabled`, on by default) closes, once at startup and
then every `Conversations:AutoCloseIntervalMinutes` (15), every **open** conversation whose last message is
older than `Conversations:AutoCloseAfterDays` (7). It works across tenants, in batches of
`Conversations:AutoCloseBatchSize` (200), one short transaction per batch with `FOR UPDATE SKIP LOCKED`: a row an
ingestion or a person is holding is skipped and met again on the next run. A failing run is logged (masked
description, no raw exception) and retried at the next one.

- **Reopen policy**: a message that arrives after the close always starts a *new* conversation. There is no
  "reopen window": it would need a rule for the opportunity of the closed conversation (Won? Lost?) and for
  messages that arrive late and out of order, and nothing in the pilot asks for it yet. (A `Conversation.Reopen()` method that
  nothing called was removed: reopening would also have to decide what happens to the opportunity, so it belongs with the
  reopen window, if the pilot ever asks for one.)
- **Opportunity**: when the last message of the closed conversation is the business's, and the customer had
  written before (the customer went silent), the opportunity becomes `Lost` / `NoResponse` (`StageSource = Auto`).
  Nothing changes when the stage is already `Won` or `Lost`, when a person decided it (`StageSource = Manual`), or when the
  customer spoke last (or at the same second as the business: when unclear, it is not given up for lost).

## Configuration

See the README configuration table: `Webhooks:MaxBodyBytes`,
`Webhooks:FailedAuthRateLimitPerMinute`, `Webhooks:ChannelRateLimitPerMinute`, `ForwardedHeaders:*`, `Webhooks:Processing:*`, `Webhooks:Retention:*`, `Webhooks:Health:*`, `Conversations:*` and
`Dialog360:ApiKey` (bound, not used yet). Invalid values stop the API at startup naming the key.

## Logs

The ingestion never logs a raw exception nor a payload. A failing event is described by `WebhookErrors.Describe` (exception type and
innermost message, sanitized, digit runs of 7 or more masked) in the log, in `LastError` and in the error registry; an unexpected
failure of the webhook request is registered and logged the same way (no stack trace, channel key redacted). A repeated delivery is
`INSERT ... ON CONFLICT DO NOTHING`, so routine retries throw nothing. JSON that PostgreSQL `jsonb` cannot store (a `\u0000` escape, an
unpaired surrogate) is recognized and refused with `400` before the insert. Because Npgsql's error text can quote the offending value,
`appsettings.json` sets the EF Core categories `Microsoft.EntityFrameworkCore.Update` and
`Microsoft.EntityFrameworkCore.Database.Command` to `None`; keep them off in production, and never enable sensitive data logging.

## Health and backlog

`GET /health` is anonymous and answers only a status word with `200` (`Healthy`, `Degraded`) or
`503` (`Unhealthy`, the queue cannot be read). It is **Degraded** when the oldest pending event has
waited longer than `Webhooks:Health:MaxPendingAgeMinutes` (10) or more than
`Webhooks:Health:MaxFailedPerHour` (5) events became `Failed` in the last hour. It says nothing about
tenants, events or errors. The worker also logs the backlog at Information once a minute at most
(pending count, age of the oldest, failures in the last hour). No metrics system is involved: point an
uptime probe or the platform's health probe at `/health`.

## Privacy and retention

`WebhookEvents.RawPayload` holds the delivery as received: customers' phone numbers,
names and message text, besides what is also stored normalized. `WebhookEventRetentionService`
(modeled on the error-log retention) deletes, at startup and then every 24 hours, **Processed**
events older than `Webhooks:Retention:RetentionDays` (14) and **Failed** events older than
`Webhooks:Retention:FailedRetentionDays` (60, longer because an operator may still want to look at
or requeue them); `Pending` events are never purged. It can be turned off with
`Webhooks:Retention:Enabled=false`. The stored contacts, conversations and messages are *not* covered
by this purge: their retention, the Ley 1581 de 2012 purposes and deletion requests remain open
(`docs/decisiones.md`). Secrets never reach the repository: the webhook secret is stored as a hash,
the 360dialog key only comes from configuration.
