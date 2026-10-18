# Conversation classification

How Conversia turns a WhatsApp sales conversation into data: which service the customer asked about, why, what
stopped them, and how the conversation ended. Phase 1 has no LLM, so the reader is a **rule-based Spanish
(Colombia) classifier** behind an interface that a future LLM adapter will implement.

```
ingestion ──► Conversation.ClassificationDirty = true   (any conversation that received a message)
                    │   quiet for Classification:DebounceSeconds (60)
ConversationClassificationService (FOR UPDATE SKIP LOCKED, one transaction per conversation)
                    ▼
   IConversationClassifier.ClassifyAsync(snapshot)  ──►  ClassificationResult (codes and enums only)
                    ▼
   Opportunity.ApplyClassification  (never touches a Manual stage)  ──►  flag cleared
```

## What a classification produces

`ClassificationResult` holds codes and enums, **never quoted customer text**, so it can be stored, logged and compared freely.

| Field | Meaning | Stored in |
|---|---|---|
| `ServiceInterest` | Normalized code(s) of the zone or service asked about: `axilas`, `piernas,bikini`, `cuerpo_completo`, `depilacion_laser` (up to 5, max 100 characters) | `Opportunities.ServiceInterest` |
| `InterestReason` | Why: `precio_promocion`, `estetica`, `comodidad`, `salud`, `evento`, `recomendacion` | `Opportunities.InterestReason` |
| `Objection` | The latest objection: `Price`, `Timing`, `Location`, `Trust`, `Pain`, `Other`, or `None` | `Opportunities.Objection` |
| `Outcome` | `Undetermined`, `InFollowUp`, `Won`, `Lost` (see below) | `Opportunities.Stage` |
| `LossReason` | With `Lost` only: `Price`, `Timing`, `Location`, `Competitor`, `NotInterested`, `Other` | `Opportunities.LossReason` |
| `Confidence` | 0 to 1 | `Opportunities.ClassificationConfidence` |
| `RuleCodes` | Short codes of the rules that fired (`outcome.won.advisor_confirmation`, `objection.price`, `service.axilas`) | `Opportunities.ClassificationRules` (200 characters, whole codes) |

`ClassifiedAt` is set on every run. A run that reads the same thing changes nothing else (the stage keeps its
`StageChangedAt` and `ClosedAt`), so re-classifying is harmless.

### Outcome semantics

| Outcome | When | Stage written |
|---|---|---|
| `Won` | The customer **showed intent to book** (`agendar`, `cita`, `reservar`, `quiero ir`, ...) **and** a confirmation follows: the business says the booking is made (`quedó agendada`, `cita confirmada`, `te esperamos el sábado`), or the customer confirms (`confirmo`, `listo el sábado`, `ahí estaré`) after showing intent or after a booking proposal of the business. | `Won` |
| `Lost` | The customer's **last stance** is a refusal (`no gracias`, `no me interesa`, `ya no`, `ya agendé en otro lado`) or a cancellation (`cancelo mi cita`). A later request to book undoes it. | `Lost` + reason |
| `InFollowUp` | The customer said something about the service, an objection or the booking, and neither booked nor refused. | `InFollowUp` |
| `Undetermined` | Nothing can be read: a greeting, media without caption, only the business writing. | unchanged |

The loss reason comes from the **competitor** named with (or just before) the refusal, else the objection of the refusing
message, else the latest earlier objection: `Price`, `Timing`, `Location`; trust, pain or other objections give `Other`; no objection
gives `NotInterested` (`Other` for a cancellation). `NoResponse` is never produced by the classifier: the job that closes idle conversations sets it
(`docs/WEBHOOKS.md`, "Closing conversations"), and a later `InFollowUp` reading does not replace it.

A bare `dale`/`listo`/`ok` accepts a proposal **only** right after a booking proposal of the business (`¿te agendo el sábado?`);
on its own it means nothing. **The classifier is tuned never to give a confident wrong Won**: whatever is ambiguous ends as
`InFollowUp` or `Undetermined`. The golden set (`ConversationClassifierGoldenSetTests`) pins this.

### A person always wins

`PUT /api/conversations/{id}/opportunity` marks the stage `Manual`. From then on `ApplyClassification` leaves the stage, the loss reason and
`ClosedAt` alone; the descriptive fields (service, reason, objection) keep following the classifier. The worker and the endpoint take the
lock of the conversation row before touching its opportunity, so they cannot interleave.

## The rule-based classifier

`RuleBasedConversationClassifier` is pure: no clock, no network, no state; the same snapshot gives the same result.

1. **Normalize**: lower case, no accents (`ñ` becomes `n`), punctuation and emoji become spaces, a letter repeated three or more times collapses
   (`holaaaa`, `axilassss`), whole-word aliases for abbreviations (`q` is `que`, `xq` is `porque`).
2. **Split** into sentences (`. ! ?` and new lines) and clauses (commas, `pero`, `sino`, `aunque`, `porque`).
3. **Match phrases**: a phrase is a run of words; `pierna*` lets the last word continue, `^ok$` anchors it to a whole sentence. A phrase never crosses a sentence.
4. **Negation**: a negator (`no`, `nunca`, `tampoco`, `ni`, `jamas`, `sin`) among the 4 words before a phrase, in the same clause, cancels it
   (`no me parece caro` is not a price objection, `no quiero agendar` is not intent). `no, quiero agendar` is not negated: the comma ends the clause.
5. **Read the conversation** from the customer's and the advisor's messages as described above.

### Dictionaries are data

Everything vocabulary-specific is in `src/Conversia.Application/Classification/Rules/ClassificationRules.es-CO.json` (embedded in the
assembly): services and zones, interest reasons, objection phrases per type, competitor mentions, booking intent, confirmation, proposal,
refusal and cancellation phrases, abbreviations. The engine knows none of those words. The comments of the file explain the little syntax.

To serve another vertical or country, write a file of the same shape and point the API at it: `Classification:RulesPath=/etc/conversia/rules.json`.
It is parsed and validated at startup (the API refuses to start naming `Classification:RulesPath`, never quoting the file). One rules file per
deployment; per-tenant dictionaries would need a table and are not built.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Classification:Enabled` | `true` | Starts the background worker. `false`: conversations stay flagged. |
| `Classification:Provider` | `Rules` | The only provider implemented; any other value stops the API at startup. |
| `Classification:RulesPath` | *(embedded Spanish rules)* | Path of a JSON file with the dictionaries. |
| `Classification:DebounceSeconds` | `60` | A conversation is read once its last message is at least this old. 0 to 86400. |
| `Classification:PollIntervalMilliseconds` | `2000` | Pause of the worker when nothing is due. |
| `Classification:BatchSize` | `20` | Conversations per cycle before the worker looks at the clock again. |
| `Classification:MaxFailures` | `5` | Consecutive failures after which a conversation is no longer tried (until a new message arrives). |
| `Classification:RetryDelaySeconds` | `60` | Wait before a failed conversation is tried again. |
| `Classification:ProcessingTimeoutSeconds` | `30` | `statement_timeout`, idle-in-transaction timeout and total budget of one classification. |

## The worker

`ConversationClassificationService` (hosted service) drains the backlog; `ConversationClassificationRunner` handles one conversation per call
and is safe to run in several workers and several API instances:

- **Claim** `WHERE "ClassificationDirty" AND failures < MaxFailures AND "LastMessageAt" <= now - debounce AND (not waiting out a retry)`, oldest
  first, `LIMIT 1 FOR UPDATE SKIP LOCKED`: a conversation another worker, a person or an ingestion holds is skipped, never waited for.
- **Read** the latest 1000 messages (conversation order) and the branch name; **write** the opportunity; **clear** the flag; commit. One bounded transaction.
- A message that arrives meanwhile is not lost: ingestion rewrites the request on the conversation row (`ClassificationRequestedAt` changes
  every time), which waits for the worker's lock and flags the conversation again after it.
- **Failure**: everything rolls back; in a fresh transaction the failure counter and the time of the attempt are recorded. At `MaxFailures` a
  warning is logged and the conversation is skipped until a new message (`RequestClassification`) resets it. The log says the *types* of the
  failure (`InvalidOperationException caused by FormatException`), never the exception message: it could quote the customer.
- **Backlog**: once a minute at most the worker logs `Classification backlog: N waiting, oldest for S s, M stopped after repeated failures.`
  (not part of `/health`, which only watches the webhook queue).

The migration queues every stored conversation that already has a customer message, so the first runs classify the history.

## Limitations

The rules are a stopgap, and honest about it:

- Spanish only, and tuned to laser hair removal. Slang, regional words, long sentences, sarcasm and mixed intents are read badly or not at all.
- Phrase matching knows nothing about meaning: `no tengo problema en agendar` reads as negated intent; `cara` as in `la cara` (face) is a
  service, `cara` as in `muy cara` (expensive) an objection, because the dictionary lists the phrases.
- A customer who books by voice note (audio) or image is invisible: only text and captions are read.
- Won depends on the business saying it is booked, or the customer saying so after a proposal. A booking confirmed by phone leaves the chat as `InFollowUp`; a manual outcome fixes it.
- Several objections in one message: the order of the rules decides (price, timing, location, trust, pain, other); across messages, the latest wins.
- Echoes of messages sent from the WhatsApp Business app are **unverified** against a real 360dialog sandbox (`docs/WEBHOOKS.md`): without them the business side of every chat is missing and `Won` by advisor confirmation cannot happen.

## Adding an LLM provider

The port is `Conversia.Application.Classification.IConversationClassifier`:

```csharp
Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default);
```

1. Implement it in `Conversia.Infrastructure` (it is the layer that may call the outside world). Map the model's answer to the closed
   vocabulary: service codes `[a-z0-9_,]`, the `ObjectionType`, `LossReason` and `ClassificationOutcome` enums, `Confidence` 0 to 1 and rule codes (use `llm.*`).
   Return codes and enums only: nothing the customer wrote.
2. Add the provider name to `ClassificationOptions` and the switch in `ClassificationServiceCollectionExtensions.AddClassification`
   (`Classification:Provider=Llm`), registering the adapter as the singleton `IConversationClassifier`. Nothing else changes: the worker, the
   opportunity rules (a manual stage wins, idempotent re-runs) and the metrics keep working.
3. Mind what the worker assumes: one call per quiet conversation (the debounce keeps the cost down), a processing budget of
   `ProcessingTimeoutSeconds`, and failures counted per conversation. The exception message of a provider must stay out of logs (`ClassificationErrors`).
4. Privacy: sending the conversation text to a model is a data-protection decision (Ley 1581 de 2012: purpose, authorization, processor
   agreement, where the data goes). Keep it opt-in and per-deployment, and record the decision in `docs/decisiones.md` before enabling it.
5. Keep the golden set: run the same cases against the new provider and compare. Its ambiguous cases are the ones the product cares about.
