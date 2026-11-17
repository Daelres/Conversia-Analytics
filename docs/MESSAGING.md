# Sending WhatsApp messages

A person can send a plain-text WhatsApp message into an existing conversation. The API calls 360dialog with the API key of the
conversation's channel and, once 360dialog accepts the message, stores it as an **Outbound** message of that conversation.

**This is a manual action, for validation and operation. It is not a bot:** nothing in Conversia sends a message by itself.
Advisors still answer from their phones; this endpoint exists to test the provider end to end and to operate a line when needed.
The Web has a screen for each part: see [From the Web](#from-the-web).

```
POST /api/conversations/{id}/messages
   │ check: conversation open, channel active, channel has a key      (plain reads, no lock)
   │ 360dialog  POST {base}/v1/messages   header D360-API-KEY          (no transaction open, one attempt)
   ▼
   record: tenant ingestion lock ─► conversation row ─► Messages (Outbound), timeline, classification request
```

| Section | What |
|---|---|
| [Endpoints](#endpoints) | Sending, and setting the key of a channel |
| [From the Web](#from-the-web) | The key of a number, and the composer of a conversation |
| [Credentials](#credentials-model) | One key per channel, encrypted, write-only |
| [Errors](#errors) | Every refusal and its code |
| [Sandbox limits](#sandbox-limits) | 25 requests, your own number only |
| [Recording and echoes](#recording-and-echoes) | Locks, idempotency, what is not verified |
| [Configuration](#configuration) | Settings and the master key |
| [Internal sandbox mode](#internal-sandbox-mode) | When nothing goes to 360dialog at all |
| [Customer service window](#customer-service-window) | When free text is allowed, and the refusal outside it |
| [Templates](#templates) | Sync, status webhook, sending one, what is not supported |
| [Inbox](#inbox) | `/bandeja`, priority, the polled delta, moving to SignalR later |
| [Read receipts and typing](#read-receipts-and-typing) | The company setting, what is sent, the 20-second debounce |
| [Verified provider facts](#verified-provider-facts) | What was checked in the official docs, with links, and what was not |

## Endpoints

| Method | Route | Permission | Answer |
|---|---|---|---|
| `POST` | `/api/conversations/{id}/messages` | `ConversationsManage` | `201` the stored message (`id`, `direction: Outbound`, `type: text`, `text`, `sentAt`) |
| `PUT` | `/api/channels/{id}/provider-credentials` | `OrganizationManage` | `200` the channel (`hasProviderApiKey`, `providerApiKeySetAt`, `providerEnvironment`), `Cache-Control: no-store` |
| `DELETE` | `/api/channels/{id}/provider-credentials` | `OrganizationManage` | `200` the channel, without a key. Idempotent |

**Sending.** Body `{"text": "..."}`: 1 to 4096 characters after trimming, no U+0000. The conversation must belong to the caller's
company (another company's id is the same `404` as an unknown one), be **open**, and its channel must be **active** and have a key.
The recipient is always the contact of the conversation; a request cannot name a number. A company that is not Active (pending or
suspended) gets the same `403` `tenant_not_active` as on the rest of the API, here and on the credentials endpoints.

Two limits, both sliding windows of one minute that count every request whatever its outcome (`429` with `Retry-After`):
`Messaging:SendRateLimitPerMinute` (30) per user and `Messaging:TenantSendRateLimitPerMinute` (120) per company, whoever sends. The platform
administrator can give a company its own per-minute limit (docs/PLATFORM_ADMIN.md, "Limits"). How each send ended (`sent` or the problem
code) is counted per company and day for the administrator's monitoring: counts only, kept 90 days.

**Setting the key.** Body `{"apiKey": "...", "environment": "Sandbox" | "Production"}`. The key is 1 to 256 visible ASCII
characters without spaces. Leave `apiKey` out (or blank) to keep the stored key and change only the environment; on a channel
without a key that is a `400`. `environment` is a name, never a URL: the base URLs come from configuration only, so a request cannot
point the API (and the key) at another host.

## From the Web

| Where | Who | What |
|---|---|---|
| `/organizacion`, numbers table | `OrganizationView` | Per number: whether a key is stored, since when, and the environment (Sandbox / Producción) |
| `/organizacion/canales/{id}/clave-api` | `OrganizationManage` | Set or replace the key and choose the environment; left blank, the stored key stays and only the environment changes |
| `/organizacion/canales/{id}/quitar-clave` | `OrganizationManage` | Confirmation, then `DELETE` |
| `/conversaciones/{id}`, "Enviar un mensaje" | `ConversationsManage` | The composer: a text area (4096 characters, with a counter) and one button |

- **The key is typed and never read back.** The field is a password input that is never bound to a value, so no answer carries the key:
  not the page after a refusal, not an address (post/redirect/get with a fixed notice code), not a cookie, not a log line. Tests guard
  each (`ProviderCredentialsPageTests`, `MessagingEndToEndTests`).
- **When the composer is not offered** it says why: the conversation is closed, the number is switched off, the number has no key, or the
  account lacks `ConversationsManage`. This only decides what is shown; the API checks every one again on each send. The state of the
  number comes with the conversation itself: `GET /api/conversations/{id}` carries `channel` with `isActive`, `hasProviderApiKey` and
  `providerEnvironment`, and nothing else about the channel (no number, no webhook key, no provider key). So a person who manages
  conversations needs no `OrganizationView` to be told why, or to see the sandbox note.
- **One message per form.** Each drawn form carries a one-time id (`OneTimeSubmissions`), claimed for the signed-in user and the
  conversation: a double click or a reload of the answer sends nothing the second time, and ids made up by one user cannot use up or
  push out another user's. With JavaScript the button is also disabled while the form is on its way.
- **Every refusal has its own Spanish wording**, chosen by the `code` (`SendFailure`), never the provider's text. The typed text stays in
  the box so it can be corrected or tried again, with one exception: after `message_sent_not_recorded` the box is emptied, because the
  customer already has the message. `provider_unavailable` says the outcome is unknown and to check the phone first;
  `provider_limit_reached` explains the 25 requests of a sandbox key.
- **Only the codes of the table below are expected.** The Web's API client carries a `code` only when the call declared it
  (`SendFailure.SendCodes` for a send, `provider_credentials_unavailable` for the key). Any other `422`, `500` or `503`, with or without
  a `code`, is an unexpected failure: logged, reported and shown with its error code like on every other page.
- **A send without a usable answer is "unknown" too.** If the Web times out, the connection drops, or a gateway or the API fails
  (`502`, `504`, an unexpected `500`), the request may still have reached 360dialog: the composer shows the same "no se sabe si el mensaje
  salió, verifica en el teléfono" wording as `provider_unavailable`, keeps the text, and the failure is still reported with its error
  code. Nothing is retried.
- **The Web waits longer for a send than for anything else:** `Api:SendTimeoutSeconds` (60) instead of `Api:TimeoutSeconds` (10). It
  must outlast `Dialog360:TimeoutSeconds` (15) plus `Messaging:RecordTimeoutSeconds` (30); a test guards the defaults. If you raise
  either API setting, raise this one too, or the Web gives up on messages the API is still sending.
- **A Sandbox number shows a note above the composer** (only the number tied to the key receives the messages; 25 requests in total).
  The Web does not count the requests used.
- The composer is one component (`Components/Conversations/MessageComposer.razor`) that owns its form; the page decides its state
  (`ComposerState`) and makes the call.

## Credentials model

- **One 360dialog API key per channel** (`WhatsAppChannels.ProviderApiKeyCiphertext`). Each company brings the key of each of its
  numbers; a leaked key exposes one line.
- **Encrypted at rest** with AES-256-GCM and a master key from configuration (`Messaging:CredentialEncryptionKey`). The stored
  value is `v1.{keyId}.{base64url(nonce || ciphertext || tag)}`: a random nonce per value, and the channel id authenticated as
  associated data, so a ciphertext copied to another channel's row cannot be opened. The plaintext is never a column.
- **Write-only.** No endpoint, DTO or log line carries the key or its ciphertext. Responses say only whether a key exists
  (`hasProviderApiKey`), when it was set and the environment. Tests guard this (`MessagingDtoTests`, `ProviderCredentialsApiTests`).
- **Rotating a channel's key**: `PUT` the new one. **Rotating the master key** is not implemented: the `v1.{keyId}.` prefix is what
  it will need. Until then, changing the master key (or its id) makes every stored key unreadable (`503`) until it is set again.

## Errors

All are `application/problem+json` with a `code` extension. The `detail` is fixed Spanish text; nothing from 360dialog's answer is
ever returned or logged.

| Status | `code` | When |
|---|---|---|
| `400` | *(validation)* | Blank or too long text; invalid key or environment; no key given and none stored |
| `404` | | Unknown conversation or channel, or one of another company |
| `409` | `conversation_closed` | The conversation is closed |
| `409` | `channel_inactive` | The channel is switched off |
| `409` | `provider_credentials_missing` | The channel has no API key |
| `409` | `provider_credentials_invalid` | 360dialog answered `401`: it does not recognize the key |
| `409` | `sandbox_conversation` | The conversation is sandbox data and the company is not in sandbox mode: nothing real is ever sent into it ([SANDBOX.md](SANDBOX.md)) |
| `409` | `sandbox_mode_active` | The company is in sandbox mode and the conversation is real: nothing is sent, and nothing made up is stored in it |
| `409` | `customer_service_window_closed` | More than 24 hours passed since the customer last wrote: free text is refused **before** 360dialog is called, and Meta's own refusal (error `131047`) maps to the same code. Only a template can be sent |
| `409` | `template_unavailable` | The template does not exist for the channel in that language, is not approved, or Conversia cannot send it (media header, variable buttons, authentication) |
| `409` | `sandbox_data_deleted` | In sandbox mode, the company deleted its sandbox data while the message was being recorded. Nothing was stored and nothing left the server |
| `422` | `recipient_not_allowed` | 360dialog answered `403`: this key may not write to this number |
| `422` | `provider_rejected` | Any other `4xx` of 360dialog (only the numeric `error.code` of the body is read, to tell `131047` apart) |
| `429` | `provider_limit_reached` | 360dialog answered `429`: the quota of the key is used up. No `Retry-After` |
| `429` | | Conversia's own limit per user or per company (has `Retry-After`, no `code`) |
| `503` | `provider_unavailable` | 360dialog could not be reached, timed out, or answered `408`/`5xx`. **The outcome is unknown and nothing is retried**: see below |
| `503` | `provider_credentials_unavailable` | No master key is configured, or the stored key cannot be read with it |
| `500` | `message_sent_not_recorded` | 360dialog accepted the message but storing it failed. **Do not send it again** |

**`provider_unavailable` means "unknown", not "not sent".** After a timeout or a broken connection the request may already have
reached 360dialog, and the customer may have the message. The API makes one attempt and never retries by itself; its answer says so
("no se sabe si el mensaje salió"). **Before sending again, the operator must check on the phone of the line (or ask the customer)
whether the message arrived.** A message that did leave is not in the conversation unless 360dialog echoes it to the webhook.

Once 360dialog has answered `2xx` the message is treated as sent whatever happens next: the answer's body is read for at most two
seconds, independently of the caller, and a slow or broken body, or a caller that went away, only means the message is recorded
without the provider's id.

## Sandbox limits

With a channel in the `Sandbox` environment (`https://waba-sandbox.360dialog.io`):

- A sandbox key allows **25 requests in total**, and **rejected ones count**. Do not script against it.
- It can write only to **your own number**, the phone that sent `START` to the sandbox; any other recipient is a `403`
  (`recipient_not_allowed`).
- Only plain text and the sandbox's three predefined templates. Conversia sends text and approved templates; with a 360dialog sandbox key, only
  those three templates can be tried.
- Sending `START` again issues a new key and revokes the old one: `PUT` the new key afterwards.

The step-by-step sandbox procedure is [`PRUEBAS_SANDBOX.md`](PRUEBAS_SANDBOX.md) (Spanish).

## Recording and echoes

A sent message is stored through the same domain paths as an ingested one (`Message`, `ConversationTimeline`,
`Conversation.RequestClassification`), so first-response time, the metrics and the classifier see it. `sentAt` is the moment
360dialog accepted it; `CreatedBy` is the user who sent it.

- **No lock is held during the HTTP call.** The checks are plain reads. Only the record step opens a transaction: it takes the
  tenant's ingestion lock (waiting for it), then the conversation row, the order described in
  [WEBHOOKS.md](WEBHOOKS.md#lock-order-why-two-workers-cannot-deadlock-on-conversations).
- **The record step is bounded by the database**, not by the caller (who may be gone; the message has been sent):
  `statement_timeout`, `lock_timeout` and `idle_in_transaction_session_timeout` are set for the transaction to
  `Messaging:RecordTimeoutSeconds` (30). A limit that is hit rolls the record back and answers `message_sent_not_recorded`.
- **State that changed during the call.** The conversation may be closed, or its channel switched off, between the check and the
  record. The message reached the customer, so it is stored anyway, and one warning is logged with the conversation and channel
  ids (no number, no text).
- **Echoes do not duplicate.** `(ChannelId, ProviderMessageId)` is unique and both sides check it under the same lock: if
  360dialog delivers the sent message back to the webhook under the same id, whichever comes second stores nothing.
- **Not verified:** whether 360dialog echoes API-sent messages to the webhook at all (only statuses are documented), and under
  which id. If an answer carries no message id the message is stored under `conversia.{guid}`, and a later echo of it could not
  be matched.
- **Who sent it.** Every stored message carries `Messages.Sender` (`Customer`, `Conversia`, `PhoneApp`, `AiAssistant` reserved for
  roadmap stage 5, `Unknown` for rows older than the column) and, for outbound ones, `SentByUserId` and `SentByAdvisorId` (ids only):
  - a send from Conversia records `Conversia`, the user who sent it and the advisor **linked to that user** (`Advisors.UserId`), or no
    advisor when the user has none (a manager writing in someone else's conversation is not the assigned advisor);
  - an outbound message that arrives through the webhook is an echo of the WhatsApp Business app: `PhoneApp`, with **no author**
    (`SentByUserId` and `SentByAdvisorId` null: the app does not say who typed it, and shared phones exist). The advisor assigned to the
    conversation when it arrived is kept apart, in `AssignedAdvisorIdAtReceipt`: **context, not authorship** (docs/decisiones.md);
  - when the echo of a Conversia send is stored first, the record step finds it and **claims** it (`Conversia`, the user, the user's
    advisor; the echo context is cleared). Both orders are tested on PostgreSQL (`MessageSendingApiTests`).
  - **Known gap:** a send whose record step failed (`message_sent_not_recorded`, see below) and whose echo arrives later is stored from
    the echo as `PhoneApp`, without its user: nothing is left to claim it. The error line of the failed record has the conversation id.
  The response latency between the two sides is not stored: it is derived from `SentAt` and `Direction` when a read model needs it
  (docs/decisiones.md). A template sent from Conversia is recorded with `Type = template`, the rendered text, `TemplateName` and
  `TemplateCategory` (what Meta charges by). A template sent from the phone app arrives as an echo without those columns: the echo does
  not document them.
- **Not handled:** a failure of the record step after the provider accepted (`message_sent_not_recorded`) leaves the message
  out of the conversation unless an echo brings it. It is logged as an error.

## Configuration

| Setting | Default | |
|---|---|---|
| `Messaging:CredentialEncryptionKey` | *(unset)* | Master key: base64 of exactly 32 random bytes (`openssl rand -base64 32`). A secret: environment variable or Key Vault, never a committed file. In compose it comes from `CREDENTIAL_ENCRYPTION_KEY` |
| `Messaging:CredentialEncryptionKeyId` | `1` | Written in front of every value the key encrypts. 1 to 32 letters, digits, `-` or `_` |
| `Messaging:SendRateLimitPerMinute` | `30` | Sends per user and minute (sliding window). 1 to 100000 |
| `Messaging:TenantSendRateLimitPerMinute` | `120` | Sends per company and minute, all its users together (sliding window), unless the administrator set the company's own limit. 1 to 1000000 |
| `Messaging:RecordTimeoutSeconds` | `30` | Limit of each statement and each lock wait of the transaction that records a sent message. 1 to 300 |
| `Dialog360:SandboxBaseUrl` | `https://waba-sandbox.360dialog.io` | Absolute `https` URL (`http` only for a loopback address) |
| `Dialog360:ProductionBaseUrl` | `https://waba-v2.360dialog.io` | Same rule |
| `Dialog360:TimeoutSeconds` | `15` | Whole call to 360dialog. 1 to 120 |

**Startup rule.** The master key is **optional**: without it the API starts (an installation that only ingests does not need it,
and the migrations bundle boots the same host), and the endpoints that need a stored key answer `503`
`provider_credentials_unavailable`. A key that is present but malformed (not base64, not 32 bytes, every byte the same) **stops the
API at startup** with a message that names the setting and never prints its value, like every other invalid setting here.
`Dialog360:ApiKey` is still bound and still unused: keys are per channel.

## Runbook: the master key

- **Back it up with the database.** The stored channel keys are useless without `Messaging:CredentialEncryptionKey`.
- **Changing or losing it (or changing `Messaging:CredentialEncryptionKeyId`) makes every stored channel key unreadable.** Nothing
  is deleted, but sending answers `503` `provider_credentials_unavailable` for every channel, and the log says
  `The provider credentials are unavailable (Unreadable)`.
- **There is no re-encryption path yet.** The way back is to restore the previous key and id, or to enter each channel's API key
  again with `PUT /api/channels/{id}/provider-credentials` (`hasProviderApiKey` stays `true` meanwhile, so it does not tell which
  channels were re-entered; `providerApiKeySetAt` does).
- **A leaked master key** exposes the stored keys only together with the database. Replace it, then re-enter every channel key; if
  the database may have leaked too, have each company issue a new key at 360dialog.

## Extending

`IWhatsAppMessageSender` (Application) is the port. `Dialog360MessageSender` (Infrastructure) is the only class that knows the
360dialog call; `SandboxMessageSender` is the internal adapter of a company in sandbox mode (below). The sender is chosen **per
channel, per call** by `IWhatsAppMessageSenderResolver`, so another way of sending is a new adapter plus a case in
`WhatsAppMessageSenderResolver`, with no change to the use case.

## Internal sandbox mode

While a company is in the internal sandbox mode ([SANDBOX.md](SANDBOX.md)), this endpoint does not reach 360dialog:

- `MessageSendingService` reads the mode of the conversation's company and asks the resolver for the sender with it;
  the resolver returns `SandboxMessageSender`: **no network call**, the channel's key is never read, the message is accepted at once
  with an id that starts with `sandbox.out.`.
- **No provider key is needed**: `provider_credentials_missing` does not apply, and neither does the master key (`503`).
- Everything else is the same: the conversation must be open and its channel active, the rate limits apply, and the message is
  recorded as Outbound through the same locks and domain paths (timeline, first response, classification request).
- Only **sandbox conversations** take messages in sandbox mode, and only **real** ones outside it; the other two combinations are the
  two `409`s above. So no real message ever goes to a made-up number, and no made-up message lands in real data.

With the mode off, none of this is in the path: the checks and the 360dialog call are what they were before the mode existed.
In the Web, the composer of a sandbox conversation is offered without a key and without the 360dialog sandbox note.

## Customer service window

WhatsApp delivers free-form messages only within **24 hours of the customer's last message**; each customer message starts the 24 hours
over, and nothing the business sends opens or extends it. Outside it only an approved template can be sent.

- `Conversations.LastInboundAt` and `LastOutboundAt` are kept by the same timeline recomputation as the other markers (ingestion and the
  send path, under the ingestion lock); the migration filled them from the stored messages.
- The window is open while `now < LastInboundAt + 24 h` (the closing instant itself is outside); a conversation where the customer never
  wrote has no window. `CustomerServiceWindow` (Domain) is the one definition.
- `GET /api/conversations` and `/{id}` carry `lastInboundAt`, `lastOutboundAt`, `awaitingReply`, `windowClosesAt`, `windowOpen` and
  `windowClosingSoon` (last two hours), the last three as of the answer.
- `POST /api/conversations/{id}/messages` outside the window is `409 customer_service_window_closed` before the provider is called (a
  360dialog sandbox key would count the refused request). If 360dialog refuses with Meta's `131047` anyway (clock skew, a message that
  was not stored), the answer is the same code. Sandbox mode applies the same rule.

## Templates

Templates are **created in the 360dialog Hub** (Channels › the number › Templates; Meta reviews them in 24 to 48 hours). Conversia does not
edit them: it keeps a copy per channel (`MessageTemplates`: name, language, category, status, body text, the placeholder names, whether it
can send it) to offer and send them. The copy holds the business's own text, never customer data; the values typed for a send live only
on the message sent.

| Method | Route | Permission | |
|---|---|---|---|
| `GET` | `/api/channels/{id}/templates` | `OrganizationView` | The copy, with `lastSyncedAt`; in sandbox mode the fixed sandbox list (`sandbox: true`) |
| `POST` | `/api/channels/{id}/templates/sync` | `OrganizationManage` | Reads the list from 360dialog with the channel's key and replaces the copy (added, refreshed, removed). Rate-limited like a send. Errors: `provider_credentials_missing`, `provider_credentials_invalid`, `provider_limit_reached`, `provider_unavailable`, `provider_rejected`, `provider_credentials_unavailable` |
| `GET` | `/api/conversations/{id}/templates` | `ConversationsView` | The templates of the conversation's channel |
| `POST` | `/api/conversations/{id}/template-messages` | `ConversationsManage` | `{"templateName", "language", "parameters": [...]}` → `201` the stored message |

- **Sync.** On demand (`/organizacion/canales/{id}/plantillas`, "Sincronizar ahora") and every `Templates:SyncIntervalMinutes` (360) by
  `TemplateSyncService`, for every active channel with a key of an Active company not in sandbox mode, one channel at a time in its own
  company. The job's first run waits one interval, so a restart sends no burst to 360dialog. `Templates:SyncEnabled=false` turns it off.
  Pages are read with `limit`/`offset` until `total`; a failed page fails the whole sync and keeps the stored copy, and so does a page
  that comes empty before the announced `total` (a broken read is never taken for a shorter list).
- **Guards against losing templates.** The list is *complete* only when every page was read, nothing was skipped and the count reached
  the provider's `total`. An **empty** list never removes anything (even "complete": an account cannot be told apart from a broken read),
  and a list that is not complete may remove **at most half** of the stored templates; above that, the removals are held, logged and
  reported (`removalsHeld`). Additions and refreshes are applied either way.
- **Status webhook.** A `message_template_status_update` change in a delivery (docs/WEBHOOKS.md, "Template status") updates the status
  of the stored template by name and language, so a paused or disabled template stops being offered before the next sync.
- **Order of status changes.** A notification carries its time (`entry.time`); one older than the last applied (`StatusChangedAt`) is
  ignored, so an `APPROVED` delivered after a later `PAUSED` changes nothing. A sync keeps the status of a notification newer than the
  moment it read the list. Sync and notification both take the channel row's lock (`SELECT ... FOR UPDATE`), so they never interleave.
- **Statuses.** Only `Approved` (Meta's `APPROVED`, any "Active - … quality") is sent; `Pending` (in review or appeal), `Rejected`,
  `Paused`, `Disabled` (disabled, archived, deleted) and anything unknown are not.
- **Supported.** A body with text placeholders (`{{1}}` or `{{name}}`, at most 20), an optional text header without placeholders, a
  footer, and quick-reply, phone or fixed-URL buttons. Media headers, header placeholders, buttons with variables or codes, carousels and
  authentication templates are listed as "Conversia aún no puede enviarla" (media is the next unit).
- **Sending.** Same checks, locks, attribution, rate limits and monitoring as a text, except the window. The values are checked against
  the template: one per placeholder, trimmed, 1 to 500 characters, no line breaks, tabs or more than four spaces in a row (a `400`
  otherwise). The payload is the Cloud API template message: `type: template`, `template.name`, `template.language` (`policy:
  deterministic`, `code`), and one `body` component whose `text` parameters follow the placeholder order (`parameter_name` for named
  placeholders). The stored message is the body with the values in it.
- **Charges.** Meta charges each template message by its category (Marketing, Utility, Authentication); the composer shows the category
  and says so, without amounts.
- **Sandbox mode.** Three fixed templates (`conversia_retomar_conversacion`, `conversia_recordatorio_cita`, `conversia_promocion`) for
  every channel, never stored; the internal sender accepts them and nothing else.
- **In the Web.** With the window open the composer shows how long free text is still allowed; once closed, the text area is replaced by
  the template composer: pick a template (a GET form, so it works without JavaScript), fill one field per placeholder, send. Every
  refusal has its Spanish wording (`SendFailure`); the values stay on a refusal; one-time submission like a text.

## Inbox

`/bandeja` is the advisors' page: the **open** conversations of the company (permission `ConversationsView`, like `/conversaciones`),
the ones awaiting a reply first (the customer wrote last), the longest waiting first, then the rest by their last message. Badges:
waiting time, window open / closing soon / closed (template only), sandbox. Filters: all, awaiting reply, mine (the advisor linked to
the user), unassigned, branch. While the company is in sandbox mode it shows the sandbox conversations only, otherwise only real ones.

- **The full list.** `GET /api/inbox?mine=&branchId=&unassigned=&awaitingReply=` answers at most 200 rows (`truncated` beyond) and an
  `ETag` that is the inbox's **version**: a hash of the filters, the caller's advisor, the mode, the company's last conversation change,
  its open count and the minute (waiting times move with the clock). A request whose `If-None-Match` is still current gets a bodiless
  `304` after **one** indexed query (the change token): the list is not built. Renaming a contact, branch or advisor does not change the
  version; it shows at the next change of the company's conversations or within a minute.
- **Bounded reads, on their own indexes:** the change token reads `max(UpdatedAt)` on `IX_Conversations_Inbox_Changes` and the open count
  on `IX_Conversations_Inbox_Open`; the list is two reads with a limit, those awaiting a reply by the customer's last message
  (`IX_Conversations_Inbox_Waiting`, partial on open) and then the rest by their last message, so no request sorts every open conversation.
  Every conversation has an `UpdatedAt` (set when it is created; the migration filled the old ones), so the delta filters on it without
  `COALESCE`. An integration test reads the plans (`EXPLAIN` of the recorded commands, with sequential scans discouraged because the test
  tables are tiny): index scans on those indexes, no sequential scan of `Conversations`, at most an incremental sort bounded by the limit.
- **The delta.** `GET /api/inbox?since={cursor}` answers the conversations of the company and mode changed after the cursor, **open or not
  and whatever their advisor, branch or reply state**: `inFilter` says whether each still matches the filters, and `status` whether it is
  open, so a client removes the closed ones and those reassigned out of its filters. `fullReloadRequired: true` (more than 200 changes)
  means the delta is not the whole change: load the full list again. **Deleted** conversations (a suppressed contact, deleted sandbox data)
  never appear in a delta: a client that uses deltas must also reload the full list from time to time (the Web polls the full list with its
  `ETag`). The cursor is the **database's** time, 10 seconds back on purpose: a row stamped before a poll but committed after it still comes;
  a row can come twice and is replaced by id.
- **Budgets.** Every polled read (`GET /api/inbox`, `/api/conversations/{id}/version`, the template lists) is limited per user and endpoint
  (`Inbox:PollRateLimitPerMinute`, 60, sliding window); the Web's own polled endpoints (`/bandeja/lista`, `/conversaciones/{id}/version`,
  `/escribiendo`, `/leido`) have the same budget (the Web's `Inbox:PollRateLimitPerMinute`). A `429` makes the script back off.
- `GET /api/conversations/{id}/version` is the conversation's fingerprint (message count, last change, status **and whether the window is
  open**, which flips with the clock) with `ETag`/`304`.
- **The Web polls.** `wwwroot/js/inbox.js` asks `/bandeja/lista` every `Inbox:PollIntervalSeconds` (5) with the last `ETag`; the Web
  passes it to the API, so an unchanged inbox is a `304` end to end and nothing is rendered. An open conversation asks
  `/conversaciones/{id}/version`; when it changed (a message, the status, the window) the script refetches the page and replaces the chat
  **and both composers**, keeping what was typed. Each element is bound once per page generation (an enhanced navigation never starts a
  second loop or listener), a navigation aborts the requests in flight, errors back off up to 60 seconds, a `401`/`403` stops the loop
  with a "sign in again" note, and a hidden tab does not poll. Without JavaScript the pages are as drawn, with an "Actualizar" link.
- **Manual checklist of the script** (no JavaScript test runner in the repository; `InboxScriptContractTests` guards the contract):
  1. Open `/bandeja`, write from the customer's phone: the row moves within the poll interval; the network shows `304`s in between.
  2. Navigate to `/conversaciones` and back with the menu (enhanced navigation): one request per interval, not two.
  3. Hide the tab for a minute: no requests; show it again: polling resumes.
  4. Stop the API: requests back off (5, 10, 20, 40, 60 s); start it: the next answer resets the pace.
  5. Sign out in another tab: the list shows the "sign in again" note and stops.
  6. In a conversation, type a draft and receive a message: the chat updates and the draft stays. Wait until the window closes: the
     composer switches to the template picker without a reload.
  7. With read receipts on, open a conversation awaiting a reply: one `POST /conversaciones/{id}/leido`; type: one `/escribiendo` per 20 s.
- **Later: Blazor interactive / SignalR.** The polling contract is the seam: a hub that pushes "conversation X changed" (raised where the
  timeline is applied) can replace the timer, and the same `/bandeja/lista` and version endpoints can be fetched on push instead of on a
  timer. Interactive render mode would need a circuit per advisor; the pilot's volume does not justify it yet (docs/decisiones.md).

## Read receipts and typing

A company setting, **off by default** because it changes what the customer sees: `GET`/`PUT /api/tenant/messaging-settings`
(`{"readReceiptsEnabled": true}`); reading needs `ConversationsView`, changing it `OrganizationManage` (the owner always has it). The
profile (`GET /api/users/me`) carries `tenantReadReceipts`. In the Web: `/organizacion/mensajeria`.

- `POST /api/conversations/{id}/read` (`ConversationsManage`): marks the customer's latest message as read (WhatsApp marks the earlier ones
  too). Sent once per new customer message (`Conversations.CustomerReadMarkedAt`). After a failure the conversation's read receipts wait
  5 minutes (`throttled`), so reopening the page does not retry each time. **The page render never sends it**: the conversation page
  draws `data-read-url` (only when the company has it on, the person may write and the customer wrote last) and its script posts
  `/conversaciones/{id}/leido` once the page is visible; a prefetch, a reload or a page without JavaScript marks nothing.
- `POST /api/conversations/{id}/typing` (`ConversationsManage`): the typing indicator (it marks as read too; WhatsApp shows it up to 25
  seconds or until the reply). Only when a reply is plausible: open conversation, the customer wrote last, window open. At most once per
  conversation every 20 seconds (`TypingIndicatorThrottle`, per API instance; the page's script also waits 20 seconds between posts). The
  slot is taken when the indicator is attempted, **sent or not**: a failure does not make the next keystroke retry at once.
- Both endpoints are limited per user (`Messaging:PresenceRateLimitPerMinute`, 30): each one is a request to the provider.
- **Trail of the setting.** There is no tenant-side audit table (as for a contact suppression): switching it writes the structured log
  line event 4301 `ReadReceiptsChanged` with the company id, the user id and the new value.
- Both go through the same sender resolver as a message; in sandbox mode the internal sender accepts them (a log line, nothing reaches a
  phone). Both answer `200` with an `outcome` (`disabled`, `not_needed`, `throttled`, `sent`, `sandbox`, `failed`): a failure is logged with
  ids only and never blocks the advisor.

## Verified provider facts

Checked on 2026-11-16 in the official documentation:

| Fact | Source |
|---|---|
| The customer service window is 24 hours from the user's last message, refreshed by each one; outside it only templates | [360dialog, Messaging overview](https://docs.360dialog.com/docs/messaging/overview) |
| Template list: `GET https://waba-v2.360dialog.io/v1/configs/templates`, header `D360-API-KEY`, query `limit` (default 1000), `offset`, `sort`, `filters`; answer `count`, `limit`, `offset`, `total`, `waba_templates[]` with `name`, `language`, `category`, `status`, `components`, `rejected_reason` | [360dialog, Templates](https://docs.360dialog.com/docs/resources/templates) |
| Only Active (approved) templates can be sent; statuses In review, Rejected, Active (quality …), Paused, Disabled | [360dialog, Templates](https://docs.360dialog.com/docs/resources/templates), [Sending template messages](https://docs.360dialog.com/docs/resources/templates/sending-template-messages) |
| Template message: `type: template`, `template.name`, `template.language.code` (360dialog lists `policy` as required), body parameters `type: text`, `parameter_name` for named placeholders; values in placeholder order | [360dialog, Sending template messages](https://docs.360dialog.com/docs/resources/templates/sending-template-messages), [Meta, Template fundamentals](https://developers.facebook.com/documentation/business-messaging/whatsapp/templates/overview) |
| Error `131047`: more than 24 hours since the recipient last replied; send a template | [Meta, Error codes](https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes) |
| Typing indicator through 360dialog: `POST /messages` with `{"messaging_product":"whatsapp","status":"read","message_id":"wamid…","typing_indicator":{"type":"text"}}`, answer `{"success":true}`; dismissed after 25 s or on reply; marks the message as read | [360dialog, Typing indicators](https://docs.360dialog.com/docs/messaging/overview/typing-indicators) |
| Mark as read: the same body without `typing_indicator`; marks earlier messages too; within 30 days | [Meta, Mark messages as read](https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/mark-message-as-read) |
| Template status notification: field `message_template_status_update`, value `event`, `message_template_id`, `message_template_name`, `message_template_language`, `reason`, `message_template_category`; events APPROVED, REJECTED, DISABLED, FLAGGED, PAUSED, LOCKED, PENDING, REINSTATED, IN_APPEAL, ARCHIVED, UNARCHIVED, DELETED, PENDING_DELETION, LIMIT_EXCEEDED | [Meta, message_template_status_update](https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update) |

**Not verified** (isolated in `Dialog360MessageSender`, `Dialog360TemplateSource` and the parser; to try with the 360dialog sandbox):

- Mark-as-read **without** the typing indicator through 360dialog (documented by Meta only; 360dialog documents the typing variant).
- The path: 360dialog's pages say `POST {base}/messages`; this adapter has always used `{base}/v1/messages` (text sends), and the read
  receipt and template sends use the same path. If the sandbox refuses it, the change is one line (`MessagesUrl`).
- Whether 360dialog forwards `message_template_status_update` to a channel's webhook URL (its pages name `template_message_update` and
  `message_template_status_change` too). Until verified, the periodic sync is what keeps the statuses right.
- Whether `/v1/configs/templates` exists on `waba-sandbox.360dialog.io`, and the exact status words of the list (`approved` vs `APPROVED`;
  both are read, case-insensitively).
- The error body shape of a `131047` refusal through 360dialog (read as `error.code`).
