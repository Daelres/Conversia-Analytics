# The AI sales assistant

**Human first.** When a customer writes and nobody of the team answers within the company's delay (60 seconds by default), the assistant
attends the customer so the sale is not lost. Any advisor can take the conversation at any moment. It is a business-specific sales
assistant, not a general chatbot and not an option menu: it answers only from the company's published knowledge base, pushes the sale and
may close it, and when the conversation gets out of hand it leaves it **pending human review** with a natural line. It **never claims to be
a person**.

This is **unit 5a**: everything deterministic. No model is called. Real conversations get the `NullAssistantReplyGenerator` (every fired
timer becomes a silent handoff, "Pendiente de revisión"); sandbox conversations get the `ScriptedAssistantReplyGenerator`, so the whole loop
can be tried without any provider. Unit 5b plugs the model behind the same port (`IAssistantReplyGenerator`).

| Section | What |
|---|---|
| [State machine](#state-machine) | The per-conversation state and its durable timer |
| [Pipeline](#pipeline) | Claim, prepare, generate, send; every re-check |
| [Handoff](#handoff) | The deterministic triggers and the customer-facing line |
| [Settings](#settings) | Per company and per line; defaults and ranges |
| [Knowledge base](#knowledge-base) | Content, validation, versions, the Web format |
| [The reply port](#the-reply-port) | Request, result, the null and scripted generators |
| [Disclosure](#disclosure) | Modes, default, the legal-pending note |
| [Kill switches](#kill-switches) | Company, administrator per company, global |
| [Meta policy](#meta-policy) | What the WhatsApp terms allow, with links |
| [Costs](#costs) | Meta service-message fees; the model in 5b |
| [Monitoring](#monitoring) | Counters, counts only |
| [API](#api) | Endpoints and permissions |
| [Configuration](#configuration) | `Assistant:*` |
| [Runbook](#runbook) | Switching it on, stopping it, reading what happened |

## State machine

One row per conversation in `ConversationAssistants` (`ConversationAssistant`, Domain), created the first time a customer message arms a
timer. A row in `Waiting` with `DueAt` **is** the durable timer.

| From | Event | To | Notes |
|---|---|---|---|
| `Off`, `Active` | Unanswered customer message (line and company with the assistant on) | `Waiting` | `DueAt = now + delay`; the generation moves |
| `Waiting` | Another customer message | `Waiting` | Same `DueAt` (the customer waits since the first one); the generation moves, so an answer in preparation is stale |
| `Waiting` | Timer fires, every check passes, reply sent | `Active` | One more turn |
| `Waiting` | Timer fires, no longer eligible | `Off` (or `Active` after an earlier reply) | Counted `discarded.{reason}` |
| `Waiting` | A handoff trigger, a generator handoff, a closed window, a failed send | `HandedOff` | `HandoffReason` (code) |
| any (a row is created if there was none) | A person writes (Conversia send, template, attachment, or a phone-app echo that is not the assistant's own) | `HumanTookOver` | Timer cancelled; hold of `HumanCooldownMinutes`. A first customer message after the assistant was switched on, in a conversation a person already answered, starts held too |
| `Waiting` | Claimed by an instance | `Waiting` | The generation moves and an attempt is counted |
| any | "Tomar la conversación" | `HumanTookOver` | Hold until handed back (`TakenOverBy`) |
| `HumanTookOver` | Customer message after the hold | `Waiting` | A new episode: turns and misunderstandings start again |
| `HumanTookOver`, `HandedOff` | "Devolver al asistente" | `Waiting` (customer waiting, window open) or `Off` | Counters reset; from any other state it changes nothing |
| `HandedOff` | Customer message | `HandedOff` | Pending review: the assistant stays out |

- **Every change runs under the conversation's lock** in the ingestion order (tenant ingestion lock, then the conversation row, then the
  assistant row `FOR UPDATE`): ingestion, the send path's record step, take over and hand back, and the job's phases. The inbox's change token
  moves with it (`Conversations.UpdatedAt`).
- **`Generation`** changes with everything that makes an answer in preparation stale. The job remembers the generation it claimed and drops
  its answer when it no longer matches.
- Imported history and closed conversations never arm anything.

## Pipeline

`AssistantRunner` (Infrastructure), run every `Assistant:PollSeconds` (2) by `AssistantJobService`: a timer fires within about 5 seconds of
its due time.

1. **Claim** (`FOR UPDATE SKIP LOCKED` on `IX_ConversationAssistants_Due`, at most `BatchSize`): `ClaimedUntil = now + LeaseSeconds` (120).
   **Every claim moves the generation and counts an attempt**: a holder whose lease expired is stale from then on and can never send (it is
   checked again before the provider call and in the record step). Claimed timers are answered up to `MaxParallel` (8) at once, each reading the
   clock again, so one slow generation never delays the other companies.
2. **Prepare**, under the conversation lock: the row is still `Waiting` with the claimed generation (else the claim is released and, if a newer
   customer message is waiting, claimed again at once). A **send already started** by an earlier attempt (`SendingStartedAt`: a crash or a
   timeout during the provider call) is a silent handoff `send_unknown`, never a second send; more than `MaxAttempts` (3) claims is
   `timer_failed`; a timer firing more than `LateCapMinutes` (10) after its due time is `too_late`. Then the **re-checks** (`AssistantPolicy`), in this order: global switch, company
   blocked by the administrator, company `Active`, the company's AI switch (`Tenants.AiEnabled`), the company's assistant switch, the line
   active and with the assistant on, sandbox mode equal to the conversation's kind, conversation open, customer wrote last, the 24-hour window
   (closed: silent handoff `window_closed`, the assistant never sends a template), the active hours, the admission hook (quota, 5b; refused:
   handoff `quota_exhausted`; a customer who asked for a person gets the handoff line even outside the active hours). Then the **hourly
   ceilings** of assistant messages (`TenantMessagesPerHour` 300 for the company, `ConversationMessagesPerHour` 8 per conversation: silent
   handoff `rate_limited`), an **injection anywhere in the context** the generator would read (the latest `ContextMessages` customer
   messages, not only the unanswered ones), the deterministic triggers ([Handoff](#handoff)), the honest answer to "am I talking to a bot?"
   ([Disclosure](#disclosure)), and the request: the latest `ContextMessages` (30) messages, **pseudonymized** (`Pseudonymizer`), the
   published knowledge version and the settings.
3. **Generate**, with no lock and no transaction: the typing indicator first when the company has read receipts on (best effort), then the
   generator, bounded by `GenerationTimeoutSeconds` (45). A failure or a timeout is a handoff `generator_failed`. A reply is checked
   (`AssistantRules.CheckReply`): cleaned, 1 to 1000 characters, links only to the allowed domain, no forbidden phrase, **no claim to be a
   person**; a reply that fails becomes the handoff line.
4. **Send**: a short locked re-check — same generation, and **every switch and fact read again** (global, company and line switches, the
   company's own switch, status, sandbox, window, hours, quota) — a person who answered while the generator ran makes the answer stale, and it is
   **dropped** (`superseded`). Then `SendingStartedAt` and the SHA-256 of the text are **committed before the provider is called**. Then the send path (`IMessageSendingService.SendAsAssistantAsync`):
   the same checks, window, provider call (outside any lock) and record as a manual send, recorded with **`Sender = AiAssistant`**, no user, no
   advisor; its record step updates the assistant's state under the same lock as the message. The provider call is bounded by
   `SendTimeoutSeconds` (30), the typing indicator by `TypingTimeoutSeconds` (5). A refusal is a handoff `send_failed` (`window_closed` for
   Meta's 131047); a timeout, an unreachable provider or a send not recorded is `send_unknown` (never repeated).

Each phase reads in a scope of its own, so nothing tracked by an earlier phase hides what changed meanwhile.

**Own echoes.** An echo of the phone line whose text matches the assistant's pending send (same SHA-256, within the lease window) is the
assistant's own message, not a person: it neither cancels the timer nor moves the generation, and the record step claims the stored echo as
`AiAssistant` and counts the turn. An echo with another text is a person answering.

**Residual race (inherent, documented):** a person writing *during the provider call* of the assistant's message (the window is the HTTP call
itself): both messages reach the customer; the state becomes the person's. Every other race is closed: a lost lease cannot send (generation),
a crash after the provider accepted cannot resend (`send_unknown`), and the assistant's own echo is recognized.

## Handoff

Deterministic, before any model is asked (`AssistantRules.HandoffReason`), on the customer's unanswered messages (normalized: lower case, no
accents; every pattern linear, no backtracking):

| Code | When |
|---|---|
| `max_turns` | The assistant answered `MaxAssistantTurns` (8) times in this episode |
| `misunderstanding` | The generator did not understand 3 times (`NoReply("not_understood")`, 5b) |
| `human_requested` | "quiero hablar con un asesor", "pásame con una persona", "persona real"... |
| `injection_suspected` | "ignora tus instrucciones", "system prompt", "a partir de ahora eres", "modo desarrollador"... |
| `keyword` | One of the company's handoff keywords (at a word start) or patterns (`re:`), default: queja, reclamo, demanda, abogado, denuncia, superintendencia, reembolso, devolución, cancelar, estafa, fraude, pésimo, groser... |
| `media_not_understood` | An unanswered audio or file without text (no generator reads it): the handoff line |
| `window_closed`, `quota_exhausted`, `assistant_unavailable`, `generator_failed`, `generator_handoff`, `send_failed`, `send_unknown`, `too_late`, `timer_failed`, `rate_limited` | From the pipeline |

A customer asking for a person ("asesor", "representante", "ejecutivo", "vendedor", "supervisor", "llámenme", "una persona", "alguien
real"...) makes the timer fire within `HumanRequestDelaySeconds` (5) and always gets the handoff line, even outside the active hours. All
matching runs on folded text: zero-width and direction marks removed, full-width forms and common Cyrillic/Greek look-alikes mapped to Latin.

The customer gets the company's **handoff line** (default: "Ya le paso tu mensaje a un asesor para que te ayude con esto.") or the
generator's own line when it gives one; nothing for `window_closed` (it could not reach them) and `assistant_unavailable` (unit 5a: the
customer is not told anything that is not true). The inbox shows **"Pendiente de revisión"**; the conversation view shows the reason.

## Settings

`AssistantSettings`, one row per company (defaults apply until it saves any); the line flag is `WhatsAppChannels.AssistantEnabled`.

| Setting | Default | Range |
|---|---|---|
| Switch (`Enabled`) | **off** | The **owner** switches it on, accepting the terms (`Assistant:TermsVersion`) and acknowledging the legal basis and the disclosure rule; `TermsVersion`, `TermsAcceptedAt`, `TermsAcceptedBy` are kept (also when switched off) |
| Lines | none | Each line on or off |
| Reply delay | 60 s | 30 to 900 s |
| Active hours | Always | Always / outside business hours (from the published knowledge base; unknown hours count as open: the assistant stays quiet) / custom local window `HH:mm`–`HH:mm` (may cross midnight) |
| Max assistant turns | 8 | 1 to 50 |
| Hold after a person writes | 60 min | 0 to 1440 |
| Disclosure | `OnRequest` | `None` / `FirstMessage` / `OnRequest` |
| Disclosure text | "Te escribe el asistente virtual de la empresa. Si prefieres hablar con una persona, dímelo y te comunico con un asesor." | 1 to 300, no links |
| Handoff line | "Ya le paso tu mensaje a un asesor para que te ayude con esto." | 1 to 300, no links |
| Handoff keywords | the list above | ≤ 50, ≤ 120 each; `re:` patterns must compile (non-backtracking, 50 ms timeout) |
| Tone notes | none | ≤ 500, control characters removed, no links |
| Language | es-CO | es-CO only |

The tone the owner asked for (roadmap, 2026-11-11): natural, like a good salesperson; not robotic, not a form, not an option menu, not "cheap
AI". That is about tone, never about pretending to be human. Unit 5b writes the prompt for it.

## Knowledge base

`AssistantKnowledge` (Domain value): services (name, description, price or range in whole COP, duration, conditions, active, branches),
promotions (dates), opening hours per branch or for all, holidays, booking policy (how to book, deposit, cancellation), payment methods, FAQ
(question, answer, topic), allowed topics, forbidden topics/phrases, escalation rules and **one allowed link domain** (booking or payment page,
subdomains included). Business facts only, never customer data.

- **Validated** (`Validated`): lengths, lists bounded (200 services, 200 FAQ...), prices 0 to 1,000,000,000 with min ≤ max, durations 1 to 1440,
  hours within a day, branches of the company only, control and invisible characters removed, **no link but to the allowed domain**. Spanish
  errors that name the item.
- **Links** (`LinkRules`, also applied to every reply): any `scheme://` link, markdown links and bare domains on **any** alphabetic top-level
  domain (`rb.gy/x`, `x.dev`, `pagos.ru`) are found; an authority with `@`, `\` or `%` is never valid; hosts are compared in their ASCII
  (punycode) form, so a look-alike in another script never matches; only the allowed domain and its subdomains pass. The allowed domain cannot
  be a public suffix, a link shortener or a shared host (`com.co`, `bit.ly`, `linktr.ee`, `*.github.io`, `wa.me`...). A sentence written
  without a space after its period ("gracias.Te") reads as a link and is refused: the price of catching every domain.
- **Trail**: every switch on (terms version, both acknowledgements, disclosure mode), switch off, settings change (all old and new values),
  line change and publication is an **append-only** row of `AssistantAuditEntries` (`GET /api/assistant/audit`, `AssistantManage`): an earlier
  consent is never overwritten. Deleted with the company's data.
- **Versioned**: every save is a new version (`AssistantKnowledgeVersions`, number 1, 2, 3... per company, `CreatedBy`); publishing one makes it
  the version in use and archives the previous one (partial unique index: one published per company). The rows are the trail (who saved, who
  published, when); every change also writes the log line `4401 AssistantTrail` with counts.
- **Web** (`/organizacion/asistente/conocimiento`): one item per line, columns separated by `|` (or `;`, so a catalogue pasted from a
  spreadsheet works: the cheap CSV import). `KnowledgeTextFormat` parses and formats it; items of one branch only are kept as they are (the API
  edits them).

## The reply port

`IAssistantReplyGenerator.GenerateAsync(AssistantReplyRequest)` (Application). The request carries only the pseudonymized conversation, the
knowledge version id and number, the settings the generator needs (language, tone notes, disclosure, turns) and whether this answer must
disclose. The result is one of:

- `Reply(text, usedKnowledgeIds, intent)` — sent after the checks of [Pipeline](#pipeline);
- `Handoff(reason, customerLine?)` — `null` line: say nothing; empty: the company's line;
- `NoReply(reason)` — nothing now; `not_understood` counts towards the misunderstanding handoff.

Each result may carry `Usage` (model, tokens, USD) for `IAssistantAdmission.RecordUsageAsync`; `IAssistantAdmission.AdmitAsync` is the quota
hook. Unit 5a: `UnmeteredAssistantAdmission` (admits everything, records nothing). `IAssistantReplyGeneratorResolver` picks the scripted
generator for sandbox conversations and the configured one (null in 5a) for real ones.

## Disclosure

- **Never claims to be human**, whatever the mode: every reply is checked for "soy una persona", "no soy un bot", "te habla un ser humano"...
- **Always honest on request**, whatever the mode: a customer who asks — in any unanswered message — whether they are talking to a bot, a machine
  or a person gets the company's disclosure text, without asking any model. It is a reply: it counts as a turn.
- `OnRequest` (**default**): that, and no proactive notice.
- `FirstMessage`: the first assistant message starts with the disclosure text.
- `None`: no proactive notice (the honest answer to a direct question still applies).
- **Never a person**: every reply is checked (after folding) for "soy una persona", "no soy un bot", "te habla un ser humano", first-person
  name introductions ("Soy Camila, tu asesora", "soy yo, Laura", "mi nombre es", "me llamo", "te atiendo personalmente"). The generator
  contract (`AssistantReplyRequest.HardRules`, unit 5b's prompt) forbids giving itself a personal name.
- **Legal pending.** Whether Ley 1480 (consumer protection) requires telling the customer up front was **not verified** and needs a lawyer.
  SIC Circular Externa 002 de 2024 applies Ley 1581 to AI (impact assessment). Until a lawyer says otherwise the recommendation is
  `OnRequest` at minimum; the owner's acknowledgement when switching it on says the company will review it with its legal adviser.

## Kill switches

Any of them stops the assistant at the next fired timer (it is re-checked every time):

| Switch | Who | Where |
|---|---|---|
| Company switch | Owner on, `AssistantManage` off | `/organizacion/asistente`, `POST /api/assistant/enable` / `disable` |
| Line | `AssistantManage` | Same page, `PUT /api/assistant/channels/{id}` |
| Company AI switch (`aiEnabled`) | Platform administrator | Existing (docs/PLATFORM_ADMIN.md, "Limits") |
| **Company kill switch** (`Tenants.AssistantBlocked`) | Platform administrator, reason required, audited `tenant.assistant_blocked_changed` | `/admin/empresas/{id}/asistente`, `PUT /api/admin/tenants/{id}/assistant` |
| **Global kill switch** (`PlatformSwitches.assistant`, on by default) | Platform administrator, reason required, audited `platform.assistant_changed` | `/admin/asistente`, `PUT /api/admin/assistant` |
| The job itself | Deployment | `Assistant:JobEnabled=false` |

## Meta policy

From the research of 2026-11-11 (engram `roadmap/research-2026-11-11`); re-check the wording before relying on it:

- The WhatsApp Business Solution Terms (section 4.7 at the time) forbid **general-purpose** AI assistants on the Business API; a
  business-specific assistant (sales and support of the business's own offer) is allowed. <https://www.whatsapp.com/legal/business-solution-terms>
- The WhatsApp Business Messaging Policy requires a prompt, clear way to reach a human and forbids pretending to be one.
  <https://business.whatsapp.com/policy>
- Free-form messages only within 24 hours of the customer's last message; outside it only approved templates (docs/MESSAGING.md,
  "Customer service window"). The assistant never sends a template.

## Costs

- **Meta charges service messages** since 2026-10-01 (research memory; 1,000 free service conversations per number and month at the time):
  every assistant reply and handoff line is a message Meta may charge. Pricing: <https://developers.facebook.com/docs/whatsapp/pricing>.
- Unit 5a calls no model: no AI cost. Unit 5b meters the model through `IAssistantAdmission` (quota and USD like the analysis,
  docs/CLASSIFICATION.md, "Usage and quota").

## Monitoring

`AssistantActivityCounts`: per company, local day (America/Bogota), sandbox flag and metric — `timer_armed`, `timer_fired`,
`discarded.{reason}` (`answered`, `global_off`, `company_blocked`, `company_inactive`, `ai_off`, `assistant_off`, `channel_off`,
`sandbox_mismatch`, `conversation_closed`, `outside_hours`), `handoff.{reason}`, `replied`, `handoff_line_sent`, `superseded`, `send_failed`,
`taken_over`, `handed_back`. Counts only; kept 90 days. The administrator reads them (last 7 days) with the timers waiting now, the ones being
answered and the conversations pending review: `/admin/asistente` and `/admin/empresas/{id}/asistente`. Log events: 4401 `AssistantTrail`,
4402 `AssistantJobFailed`, 4403 `AssistantHandedOff`, 4404 `AssistantReplied` (ids and codes, never a text).

## API

| Method | Route | Permission |
|---|---|---|
| GET/PUT | `/api/assistant/settings` | `AssistantManage` |
| POST | `/api/assistant/enable` `{termsVersion, acceptTerms, acknowledgeLegalBasis}` | `AssistantManage` **and owner** (`403` otherwise; `409 assistant_terms_outdated`) |
| POST | `/api/assistant/disable` | `AssistantManage` |
| PUT | `/api/assistant/channels/{id}` `{enabled}` | `AssistantManage` (another company's line: `404`) |
| GET/POST | `/api/assistant/knowledge` (`{content}` → `201` a new draft) | `AssistantManage` |
| GET | `/api/assistant/knowledge/{number}` | `AssistantManage` |
| GET | `/api/assistant/audit` | `AssistantManage`: the append-only history of settings and consent |
| POST | `/api/assistant/knowledge/{number}/publish` | `AssistantManage` |
| GET | `/api/conversations/{id}/assistant` | `ConversationsView` |
| POST | `/api/conversations/{id}/assistant/take-over`, `.../hand-back` | `ConversationsManage` (`409 assistant_not_available` without the assistant on the line) |
| GET/PUT | `/api/admin/assistant`, `/api/admin/tenants/{id}/assistant` `{enabled, reason}` | Platform administrator |

`GET /api/inbox` items carry `assistantState` and `assistantGenerating`; messages carry `sender` (`AiAssistant` for the assistant's).

## Configuration

| Key | Default | |
|---|---|---|
| `Assistant:TermsVersion` | `asistente-2026-11` | The terms an owner accepts; a new version makes owners accept again to switch it on |
| `Assistant:JobEnabled` | `true` | Off in the test hosts |
| `Assistant:PollSeconds` | 2 | 1 to 60 |
| `Assistant:LeaseSeconds` | 120 | 30 to 3600 |
| `Assistant:BatchSize` | 20 | 1 to 500 |
| `Assistant:GenerationTimeoutSeconds` | 45 | 5 to 300 |
| `Assistant:ContextMessages` | 30 | 5 to 100 |
| `Assistant:LateCapMinutes` | 10 | 1 to 1440 |
| `Assistant:MaxAttempts` | 3 | 1 to 20 |
| `Assistant:MaxParallel` | 8 | 1 to 64 |
| `Assistant:SendTimeoutSeconds` | 30 | 5 to 120 |
| `Assistant:TypingTimeoutSeconds` | 5 | 1 to 30 |
| `Assistant:HumanRequestDelaySeconds` | 5 | 1 to 60 |
| `Assistant:TenantMessagesPerHour` | 300 | 1 to 1,000,000 |
| `Assistant:ConversationMessagesPerHour` | 8 | 1 to 100 |

## Runbook

- **Try it**: put the company in sandbox mode (administrator), switch the AI on, the owner switches the assistant on in
  `/organizacion/asistente` and turns a line on, then `/conversaciones/simular`: after the delay the scripted answer appears marked "Asistente
  de IA"; write "tengo una queja" as the customer: pending review with the handoff line.
- **Stop it now**: `/admin/asistente` → "Detener el asistente para todas las empresas" (or per company). Timers already armed are discarded when
  they fire.
- **Why did it not answer?** `/admin/empresas/{id}/asistente`, counters `discarded.*` and `handoff.*`; the API log events 4403 and 4402.
- **Deletion**: `ConversationAssistants` go with their conversations (contact suppression, sandbox clean-up, company deletion); settings,
  knowledge versions and counters with the company (`TenantDataDeletionPlan`).
