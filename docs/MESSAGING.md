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
`Messaging:SendRateLimitPerMinute` (30) per user and `Messaging:TenantSendRateLimitPerMinute` (120) per company, whoever sends.

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
| `422` | `recipient_not_allowed` | 360dialog answered `403`: this key may not write to this number |
| `422` | `provider_rejected` | Any other `4xx` of 360dialog (for example a free-form text outside WhatsApp's 24-hour window) |
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
- Only plain text and the sandbox's three predefined templates. Conversia sends text only.
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
- **Not handled:** a failure of the record step after the provider accepted (`message_sent_not_recorded`) leaves the message
  out of the conversation unless an echo brings it. It is logged as an error.

## Configuration

| Setting | Default | |
|---|---|---|
| `Messaging:CredentialEncryptionKey` | *(unset)* | Master key: base64 of exactly 32 random bytes (`openssl rand -base64 32`). A secret: environment variable or Key Vault, never a committed file. In compose it comes from `CREDENTIAL_ENCRYPTION_KEY` |
| `Messaging:CredentialEncryptionKeyId` | `1` | Written in front of every value the key encrypts. 1 to 32 letters, digits, `-` or `_` |
| `Messaging:SendRateLimitPerMinute` | `30` | Sends per user and minute (sliding window). 1 to 100000 |
| `Messaging:TenantSendRateLimitPerMinute` | `120` | Sends per company and minute, all its users together (sliding window). 1 to 1000000 |
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

`IWhatsAppMessageSender` (Application) is the port; `Dialog360MessageSender` (Infrastructure) is the only adapter and the only
class that knows the 360dialog call. The sender is chosen **per channel, per call** by `IWhatsAppMessageSenderResolver`, so another
way of sending is a new adapter plus a case in `WhatsAppMessageSenderResolver`, with no change to the use case.
