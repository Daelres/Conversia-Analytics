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
| `429` | More than `Webhooks:RateLimitPerMinute` calls (default 600) from one client IP in a minute; has `Retry-After`. This is a policy of its own and does not spend the login budget. |

The channel (key and secret) is created with `POST /api/channels` (permission
`OrganizationManage`). The `201` answer holds the relative `webhookPath` and the
`webhookSecret`, **shown only once**: only its SHA-256 hash is stored. The key is
256 random bits (43 URL-safe characters), unique across all tenants; the secret is
another 256 random bits. The call compares `SHA-256(received secret)` with the
stored hash using `CryptographicOperations.FixedTimeEquals`.

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
default) polls the queue. `WebhookEventRunner` handles one event per call:

1. In a transaction: `SELECT ... WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now ORDER BY ... LIMIT 1 FOR UPDATE SKIP LOCKED`.
   A row another worker holds is skipped, never waited for, so any number of workers
   and API instances can run; a crash releases the lock and the event is claimable again.
2. Sets the tenant of the event, then takes a transaction-level advisory lock for
   that tenant, so two workers never race to create the same contact or
   conversation. Different tenants run in parallel.
3. Processes the delivery in a savepoint. On success the event becomes `Processed`.
   On any exception the work is rolled back to the savepoint (nothing partial
   survives), the failure is recorded on the still-locked event and committed.

Rules applied when a message is stored:

- **Contact**: upsert by normalized `WaId` within the tenant.
- **Conversation**: the contact's `Open` conversation on that channel is reused;
  otherwise one is created with `StartedAt` = the message time, `Source` = `Ads`
  if the message has a referral, else `Organic`. A new conversation always gets
  one `Opportunity` (`Stage = New`). The database allows at most one open
  conversation per contact and channel, and one opportunity per conversation.
  Nothing closes a conversation yet.
- **Message**: unique on `(ChannelId, ProviderMessageId)`. A message that is already
  stored (the same message inside another delivery) is skipped.
- **Times** are recomputed from the stored message times once per conversation after a
  delivery's messages are in, never from arrival order: `StartedAt` is the earliest
  message, `LastMessageAt` the latest, `FirstInboundAt` the earliest customer message,
  and `FirstResponseAt` the earliest business message **at or after** `FirstInboundAt`
  (timestamps have one-second resolution, so a same-second reply counts; a business
  message sent *before* the customer's first one is not a response). A delivery that
  arrives late or out of order therefore fixes the markers instead of breaking them.
- **Display name**: the profile name of the latest message (by message time) wins; an
  older delivery processed late never overwrites a newer name.
- **Skipped messages are visible**: `WebhookEvents.SkippedMessageCount` (customer or
  advisor messages that could not be stored: no usable phone number or id, implausible
  timestamp, group, ambiguous recipient, another business number) and `StatusCount` are
  filled when the event is processed, and a Warning (without any customer data) is logged
  when the skipped count is above zero.

### Idempotency, in layers

1. Same body twice on a channel: one `WebhookEvents` row (`(ChannelId, BodyHash)` unique); `200` both times.
2. Same message inside different bodies: one `Messages` row (`(ChannelId, ProviderMessageId)` unique).
3. Processing an event twice: the second pass finds every message already stored.

### Retries

A failed attempt increments `Attempts` and sets `NextAttemptAt = now + RetryBaseDelaySeconds * 2^(attempts-1)`
(30 s, 1 min, 2 min, ... capped at 6 h). At `MaxAttempts` (default 5) the event is
`Failed`, nothing retries it, and it is also registered in the error registry
(`ErrorLogs`, `docs/ERRORS.md`). `LastError` holds the exception type and its
innermost message, sanitized with `ErrorSanitizer` (secrets redacted, cut to 1000
characters) and with digit runs of 7 or more masked, because database errors can
quote values such as phone numbers. To retry a `Failed` event by hand, set it back to
`Pending` with `Attempts = 0` and a past `NextAttemptAt`.

## Configuration

See the README configuration table: `Webhooks:MaxBodyBytes`,
`Webhooks:RateLimitPerMinute`, `Webhooks:Processing:*` and `Dialog360:ApiKey` (bound,
not used yet). Invalid values stop the API at startup naming the key.

## Privacy and retention

`WebhookEvents.RawPayload` holds the delivery as received: customers' phone numbers,
names and message text, besides what is also stored normalized. **There is no retention
policy or purge job yet** (open item in `docs/decisiones.md`); the Ley 1581 de 2012
purposes, retention period and deletion requests must be decided before real customer
traffic is stored. Secrets never reach the repository: the webhook secret is stored as
a hash, the 360dialog key only comes from configuration.
