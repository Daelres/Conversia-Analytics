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
| `Won` | The customer **showed intent to book** (`agendar`, `reservar`, `quiero ir`, a bare `cita` outside a question, ...) **and the business confirmed**: an advisor message says the booking is made (`quedó agendada`, `cita confirmada`, or `te esperamos el sábado` with a day or an hour), or the customer answers a proposal of the advisor that names a day or an hour (`¿te agendo el sábado?` / `listo, confirmo`). | `Won` |
| `Lost` | The customer's **last stance** is a refusal (`no gracias`, `no me interesa`, `ya agendé en otro lado`) or a cancellation of the booking (`necesito cancelar mi cita`). A later request to book undoes it. | `Lost` + reason |
| `InFollowUp` | The customer said something about the service, an objection or the booking, and neither booked nor refused. | `InFollowUp` |
| `Undetermined` | Nothing can be read: a greeting, media without caption, only the business writing. | unchanged |

The loss reason comes from the **competitor** named with (or just before) the refusal, else the objection of the refusing
message, else the latest earlier objection: `Price`, `Timing`, `Location`; trust, pain or other objections give `Other`; no objection
gives `NotInterested` (`Other` for a cancellation). `NoResponse` is never produced by the classifier: the job that closes idle conversations sets it
(`docs/WEBHOOKS.md`, "Closing conversations"), and a later `InFollowUp` reading does not replace it.

### The guarantee: no Won without the business

> **A conversation ends as `Won` only if an advisor message confirms the booking, or the customer answers an advisor proposal that names a day or an hour.
> The customer's own words, alone, never produce a `Won`, whatever they are and in whatever order. When in doubt the answer is `InFollowUp`.**

The conversation is read **in order**, message by message, and the state at the end decides (not "Lost before Won by position"):

1. **Intent.** The customer asks to book with a booking verb (`agendar`, `reservar`, `separar`, `reprogramar`, ...), which counts anywhere, or with a booking noun
   (`cita`, `cupo`, `disponibilidad`), which counts only outside a question: `¿cuánto cuesta la cita?` and `¿hay cupo?` ask, they do not book. A bare confirmation
   (`confirmo`) is a weaker kind of engagement: it lets a later advisor confirmation count, and nothing more.
2. **Confirmation.** (a) An advisor message with an explicit booked statement (`quedó agendada`, `cita confirmada`, `reserva exitosa`), or a schedule statement
   (`te esperamos el`, `tu cita es el`) that has a day or an hour in the same sentence; never inside a question, never negated. Or (b) a customer message that is an
   affirmative, non-question answer (`sí`, `dale`, `listo, confirmo`) to the advisor messages right above it, when those hold a booking proposal (`¿te agendo...?`,
   or a generic `¿te parece?` with a day or an hour) **and name a day or an hour**. A customer message that carries an objection, a deferral (`yo te aviso`,
   `el próximo mes`), a refusal or a cancellation confirms nothing, whatever else it says.
3. **After the confirmation.** A booking confirmed by the advisor is ended only by an **explicit** cancellation or withdrawal by the customer: a cancel verb next to a booking noun
   (`cancelar la cita`, `anular la reserva`, `me cancelas el turno`), `ya no voy a poder ir`, `no puedo ir`, or `ya agendé en otro lado`. A bare `no gracias` / `no` /
   `mejor no, gracias` to `¿algo más?` is courtesy and keeps the `Won`. A booking that only the customer confirmed (`sí` to a proposal) is also put in doubt by a later
   deferral (`mejor lo pienso`). Cancelling and asking for another date in one breath is a reschedule: the booking is in doubt (`InFollowUp`), not `Lost`.
4. **Words that look like a cancellation but are not.** `Cancelar` is also *to pay* in Colombian Spanish: `ya cancelé el abono` and `¿puedo cancelar con tarjeta?`
   are payments (a cancel verb needs a booking noun nearby, and a clause that talks about paying is not a cancellation). A question about the policy
   (`¿puedo cancelar la cita si me enfermo?`) is not a cancellation; a request (`¿me cancelas la cita?`) is.
5. **Refusals that are not.** A refusal inside a question (`¿y si no me interesa...?`), one that the same message takes back (`no me interesa el cuerpo completo, solo las
   axilas. ¿Cuánto cuestan?`), `mejor no` followed by something (`es mejor no rasurarme`), `ya agendé en la página web` / `ya pagué en la sede` (said to us) and
   `ya agendé en otra sede` (another branch of the same business is not a competitor) are not refusals. `Ya agendé / ya pagué` is a refusal only with a cue of another
   provider in the same sentence (`otro lado`, `otra clínica`).

The guarantee is **verified, not just listed**, by `ClassifierGuaranteeTests` (the golden set only pins the readings of individual chats):

- 20,000 generated chats of customer messages only (built from every phrase of the rules file, so a new phrase is attacked the day it is added) never end as `Won`;
- 20,000 chats whose advisor only says filler, with any customer messages, never end as `Won`;
- over 40,000 generated chats with an advisor, every one that ends as `Won` stops being `Won` when each advisor message is replaced by filler (the `Won` always depends on what the business wrote);
- appending `no gracias`, `no`, `mejor no, gracias`, `ok, gracias` or `ya cancelé el abono` to any chat that ends as `Won` leaves it `Won`.

Limits of the guarantee: it is about the **text**. A booking the advisor confirmed in the chat, whose customer then cancels by phone, stays `Won` until a person fixes it;
an advisor who says `quedó agendada` by mistake makes a `Won`; and a conversation longer than 200 messages is read from its last 200 (a booking confirmed earlier,
with nothing about it afterwards, is out of the window and reads as `InFollowUp`).

A bare `dale`/`listo`/`ok` accepts a proposal **only** right after a booking proposal of the business; on its own it means nothing. The golden set
(`ConversationClassifierGoldenSetTests`, 100+ chats, among them every probe a code review found: question as confirmation, `cupón` read as `cupo`, `agenda` read as `agendar`,
`cancelé el abono` read as a cancellation, a courtesy `no gracias` turning a booking into `Lost`) pins the individual readings.

**Decision on an ambiguous case** (`A: 60 mil la sesión, ¿te parece?` / `C: Confirmo`): `InFollowUp`. The price is not a booking proposal (no day, no booking verb), so `confirmo` is
only engagement; if a later advisor message says `quedó agendada para el sábado`, it becomes `Won`.

### A person always wins

`PUT /api/conversations/{id}/opportunity` marks the stage `Manual`. From then on `ApplyClassification` leaves the stage, the loss reason and
`ClosedAt` alone. The worker and the endpoint take the lock of the conversation row before touching its opportunity, so they cannot interleave.

**Per-field provenance.** Each descriptive field carries its own source and producer (migration `AddExtractionProvenance`):

| Field | Source (who set it) | Producer (which classifier wrote it last) |
|---|---|---|
| `Opportunities.ServiceInterest` | `ServiceInterestSource`: `Auto` / `Manual` | `ServiceInterestClassifiedBy`: `Rules` / `Llm` / NULL |
| `Opportunities.InterestReason` | `InterestReasonSource` | `InterestReasonClassifiedBy` |
| `Opportunities.Objection` | `ObjectionSource` | `ObjectionClassifiedBy` |
| `Contacts.AgeRange`, `Contacts.Gender` (extracted profile, LLM only) | `AgeRangeSource`, `GenderSource` (NULL = never set) | `ProfileClassifiedBy` |

`Opportunities.ClassifiedBy` records which classifier made the last automatic stage/loss-reason write (diagnostics only).

- A `Manual` field is never overwritten by any classifier. Saving a field, even unchanged, pins it `Manual`; reverting it to `Auto` keeps
  the value until the next classification (`SetManualServiceInterest`, `SetManualInterestReason`, `SetManualObjection`, `RevertDetailToAuto`;
  `SetManualAgeRange`, `SetManualGender`, `RevertProfileFieldToAuto`). `DetailsChangedAt/By` and `ProfileChangedAt/By` record who did it.
- Every automatic write records its producer per field. An **explicit rules fallback** (`ClassificationUpdate.IsRulesFallback`: the LLM was
  meant to read the conversation and failed or was not admitted) never overwrites a non-empty value the LLM produced; stage, loss reason and
  confidence still come from the rules (an accepted mix). A plain `Provider=Rules` run is not a fallback: it overwrites `Auto` fields as it
  always did, so rolling back to the rules leaves no stale LLM values. Rows older than the columns have a NULL producer, read as `Rules`.
- The extracted profile (`Contact.ApplyExtractedProfile`) only fills fields that were stated and that no person set; it never erases a value.
- `Conversations.LlmFailures` counts LLM answers that could not be used (refused, cut off, malformed), independently of `ClassificationFailures`;
  a new message (`RequestClassification`) or an LLM success resets it. `CompleteWithRulesFallback` and `RequeueClassification` are the paths the
  LLM runner will use; with `Provider=Rules` nothing calls them.

## The rule-based classifier

`RuleBasedConversationClassifier` is pure: no clock, no network, no state; the same snapshot gives the same result.

1. **Normalize**: lower case, no accents (`ñ` becomes `n`), punctuation and emoji become spaces, a letter repeated three or more times collapses
   (`holaaaa`, `axilassss`), whole-word aliases for abbreviations (`q` is `que`, `xq` is `porque`).
2. **Split** into sentences (`. ! ?` and new lines) and clauses (commas, `pero`, `sino`, `aunque`, `porque`).
3. **Match phrases**: a phrase is a run of words; `^ok$` anchors it to a whole sentence, `mejor no,` makes it end its clause. A phrase never crosses a sentence.
   **Phrases spell every word form out: the default file has no prefix wildcard** (see below).
4. **Negation**: a negator (`no`, `nunca`, `tampoco`, `ni`, `jamas`, `sin`) among the 4 words before a phrase, in the same clause, cancels it
   (`no me parece caro` is not a price objection, `no quiero agendar` is not intent). `no, quiero agendar` is not negated: the comma ends the clause.
5. **Read the conversation** from the customer's and the advisor's messages, in order, as described above.

Work is bounded: only the last **200 messages** of a conversation and the first **1,000 characters** of each are read (`RuleBasedConversationClassifier.MaxMessages`,
`MaxCharactersPerMessage`; the worker also fetches no more than that from the database), and the classification honors its `CancellationToken` between messages.

### Prefix wildcards: why there are none

A trailing `*` lets a phrase's last word continue (`pierna*`). It is a bet that nothing else in Spanish starts the same way, and the bet lost every time it was audited:
`ingle*` read **inglés** (the language) as the groin (normalization drops the accent, so `ingles` is both), `agend*` read **agenda** as booking, `quince*` read **quincena** as a
party, `cupo*` read **cupón** (a coupon) as availability, `practic*` read **prácticamente** as comfort, and a bare `presupuesto` made every quote request a price objection.
So the default file spells out every form (`axila`, `axilas`, `axilar`, `axilares`) and uses `*` only in `dayTime`, for hour digits (`a las 1*` is 1, 10, 11, 12 and their
`am`/`pm` forms), where the stem is a digit and nothing else can follow it. A test (`TheDefaultRules_UseNoWildcardOtherThanTheHourPrefixes`) fails if another one appears.
The syntax is still supported for other rule files; use it with that bet in mind. Kept wildcards: only the ten hour prefixes of `dayTime` (`a la 1*`, `a las 1*` ... `a las 9*`).

Plural `ingles` is deliberately not a phrase on its own: `las ingles` and `mis ingles` are (an article makes it the body, not the language).

### Dictionaries are data

Everything vocabulary-specific is in `src/Conversia.Application/Classification/Rules/ClassificationRules.es-CO.json` (embedded in the
assembly): services and zones, interest reasons, objection phrases per type, competitor mentions, booking intent, days and hours, confirmation,
proposal, refusal, withdrawal and cancellation (verbs, booking nouns, payment words), abbreviations. The engine knows none of those words. The comments of the file explain the little syntax.

To serve another vertical or country, write a file of the same shape and point the API at it: `Classification:RulesPath=/etc/conversia/rules.json`.
It is parsed and validated at startup (the API refuses to start naming `Classification:RulesPath`, never quoting the file). One rules file per
deployment; per-tenant dictionaries would need a table and are not built.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Classification:Enabled` | `true` | Starts the background worker. `false`: conversations stay flagged. |
| `Classification:Provider` | `Rules` | `Rules` or `Llm` (Development only for now; see [The LLM provider](#the-llm-provider)). `Shadow` and any other value stop the API at startup. Read at startup: restart to change it. |
| `Classification:RulesPath` | *(embedded Spanish rules)* | Path of a JSON file with the dictionaries. |
| `Classification:DebounceSeconds` | `60` | A conversation is read once its classification request (the arrival of the last message) is at least this old. 0 to 86400. |
| `Classification:PollIntervalMilliseconds` | `2000` | Pause of the worker when nothing is due. |
| `Classification:BatchSize` | `20` | Conversations per cycle before the worker looks at the clock again. |
| `Classification:MaxFailures` | `5` | Consecutive failures after which a conversation is no longer tried (until a new message arrives). |
| `Classification:RetryDelaySeconds` | `60` | Wait before a failed conversation is tried again. |
| `Classification:ProcessingTimeoutSeconds` | `30` | `statement_timeout`, idle-in-transaction timeout and total budget of one classification. |

## The worker

`ConversationClassificationService` (hosted service) drains the backlog; `ConversationClassificationRunner` handles one conversation per call
and is safe to run in several workers and several API instances:

- **Claim** `WHERE "ClassificationDirty" AND failures < MaxFailures AND "ClassificationRequestedAt" <= now - debounce AND (not waiting out a retry)`, oldest
  request first, `LIMIT 1 FOR UPDATE SKIP LOCKED`: a conversation another worker, a person or an ingestion holds is skipped, never waited for.
- The quiet period counts from when ingestion **requested** the classification (`ClassificationRequestedAt`, the arrival of the message), not from the time written on
  the message (`SentAt`): a message the provider delivers late, or a replayed one, still gets its quiet period, and a replay does not re-classify the conversation over and over.
- **Read** the latest 200 messages (conversation order), each cut to its first 1,000 characters, and the branch name; **write** the opportunity; **clear** the flag; commit. One bounded transaction.
- A message that arrives meanwhile is not lost: ingestion rewrites the request on the conversation row (`ClassificationRequestedAt` changes
  every time), which waits for the worker's lock and flags the conversation again after it.
- **Failure**: everything rolls back; in a fresh transaction the failure counter and the time of the attempt are recorded, **only if the conversation was not requested
  again meanwhile** (a guarded update on `ClassificationRequestedAt`): a message that arrived during the failing attempt gave the conversation a fresh start, and that is not taken
  away. At `MaxFailures` a
  warning is logged and the conversation is skipped until a new message (`RequestClassification`) resets it. The log says the *types* of the
  failure (`InvalidOperationException caused by FormatException`), never the exception message: it could quote the customer.
- **Backlog**: once a minute at most the worker logs `Classification backlog: N waiting, oldest for S s, M stopped after repeated failures.`
  (not part of `/health`, which only watches the webhook queue).

The migration queues every stored conversation that already has a customer message, so the first runs classify the history.

## Limitations

The rules are a stopgap, and honest about it:

- Spanish only, and tuned to laser hair removal. Slang, regional words, long sentences, sarcasm and mixed intents are read badly or not at all.
- Phrase matching knows nothing about meaning: `no tengo problema en agendar` reads as negated intent; `cara` as in `la cara` (face) is a
  service, `cara` as in `muy cara` (expensive) an objection, because the dictionary lists the phrases. `cancelar` next to `cita` is a cancellation unless the clause
  talks about paying: `ya cancelé la cita con tarjeta` (I paid the appointment) is read as a payment.
- A customer who books by voice note (audio) or image is invisible: only text and captions are read.
- Won depends on the business saying it is booked, or the customer saying yes to a proposal with a day or an hour. A booking confirmed by phone leaves the chat as `InFollowUp`; a manual outcome fixes it.
  An advisor message that cancels a booking is not read (advisors often write `si necesitas cancelar tu cita, avísanos` as boilerplate).
- A refusal after a confirmed booking is not read (`ya no me interesa` to a reminder is not a cancellation); `ya no voy a poder ir` and `cancelar la cita` are.
- Several objections in one message: the order of the rules decides (price, timing, location, trust, pain, other); across messages, the latest wins.
- Echoes of messages sent from the WhatsApp Business app are **unverified** against a real 360dialog sandbox (`docs/WEBHOOKS.md`): without them the business side of every chat is missing and `Won` by advisor confirmation cannot happen.

## Preparing a conversation for the LLM (saving tokens)

The pure pieces of the LLM provider live in `Conversia.Application/Classification/Llm` and run with `Classification:Provider=Llm`
([The LLM provider](#the-llm-provider)). Before any text leaves the process the conversation goes through:

```
real snapshot ──► RuleBasedConversationClassifier ──► WonGuard (the rules have the last word on Won, both ways)
      │
      └──► Pseudonymizer ──► ConversationPreprocessor ──► LlmPromptBuilder ──► model ──► LlmResultMapper (strict) ──► WonGuard
```

The rules classifier and the `WonGuard` always read the **full real** snapshot; the preprocessor only changes what the model is sent.

**Masking comes before any cap.** On the LLM path the snapshot is read 64 characters beyond the 1,000-character cap
(`ConversationClassificationRunner.ReadCharactersPerMessage`), the `Pseudonymizer` masks the whole text, and only then the preprocessor applies
the cap, dropping a word cut in half at the end: a number straddling the cap can never leave a few of its digits behind. The Rules path reads
exactly what it read before (two queries, 1,000 characters); the known names and numbers are one more query, only on the LLM path.

**Canonical text.** Both the `Pseudonymizer` and `PromptTextNeutralizer` first run `TextSanitizer`: NFKC (fullwidth `３` and `｜` become `3`
and `|`), every decimal digit of any script to ASCII, format characters removed (zero-width spaces and joiners, soft hyphens, bidi marks) and
every Unicode space to an ASCII space. Look-alike or invisible characters cannot slip a phone number or a delimiter past the patterns.

### What the preprocessor removes, and why

| Step | Example | Why |
|---|---|---|
| Messages without text are dropped | media without caption, blank | Nothing to read; their number is simply skipped. |
| White space collapsed; a character repeated more than 3 times keeps 3; a run of emoji keeps 3; a message of only symbols keeps 3 | `siiiiii` → `siii`, `!!!!!!` → `!!!`, `👍👍👍👍👍` → `👍👍👍` | Emphasis survives, the repetition costs tokens. A thumbs-up alone is kept: it can be a confirmation. |
| Links become `[URL]` (already masked ones stay) | `https://…?utm=…` → `[URL]` | A link carries no extraction value and is token-heavy. |
| An advisor message identical to an earlier one (ignoring case, accents, punctuation, emoji, spacing) becomes `=n` | a price list sent again → `9 A: =3` | Templates, price lists and greetings are re-sent constantly. Only exact copies: a list with a different price is not a copy. |
| A long advisor message keeps its beginning, its end and every clause holding a phrase the rules read in a business message, around `[...]` | catalogue boilerplate between a greeting and `quedó agendada…` | The confirmation usually sits at the end; services, days and booking statements are never cut. |
| Consecutive messages of one side share a line | `1 C: hola \| quiero info \|4 ¿cuánto vale?` | One prefix instead of three. Each segment keeps its number (implicit when consecutive, `\|4` when not). |
| Over the token budget the oldest middle lines are left out and marked | `[... 37 messages omitted]` | Long chats; the first customer message (origin, interest) and the latest lines are always kept. A back-reference to a message the budget left out shows that message's text instead. |

The line format is `n C: text` / `n A: text`, where `n` is the message's position in the snapshot, so a line the model cites
(`WonEvidenceIndex`) maps straight back to the message; the mapper rejects a cited line that is not shown in the block (a dropped message or
one the budget left out). `PromptTextNeutralizer` guarantees no message text can break a line, imitate a prefix (`9 A:` becomes `9 A;`,
`[#9][A]` becomes `(#9)(A)`), use the segment separator (`|` becomes `/`), fake a back-reference (`=3` becomes `= 3`), imitate the gap marker
(`[... 3 messages omitted]` becomes `(... 3 messages omitted)`) or close the block (`</conversation>` becomes `[tag]`). The markers are ASCII
(`[...]`), because NFKC turns `…` into `...`.

### What it never removes

- Customer messages are never collapsed as repeats and never cut beyond the existing 1,000-character cap.
- No phrase the rules classifier relies on: `PreprocessorQualityGuardTests` runs the pseudonymizer and the preprocessor over the golden set,
  4,000 generated chats and 200 long synthetic chats and checks, **in the same message** (back-references resolved), that every phrase of the
  rules file that the classifier reads on that side (all of them for the customer; services, proposals, days and hours, confirmations for the
  business) and that appears in the pseudonymized message still appears in what is shown for it. A phrase lost from one message fails even if it
  survives in another. Messages left out by the budget are reported separately (none in that corpus).

### Measured reduction

Estimated tokens are characters / 3, rounded up (conservative for Spanish); "before" is the same messages as one `[#n][C] text` line each.

| Corpus | Characters before → after | Estimated tokens before → after | Reduction |
|---|---|---|---|
| Golden set (111 short chats) | 63.8 → 58.7 | 21.6 → 19.9 | 7.9% |
| Long synthetic chats (200, 30 to 90 messages, re-sent price lists, emoji, media, links) | 5,238 → 1,847 | 1,746 → 616 | 64.7% |
| Whole corpus (4,311 chats) | 394.7 → 220.8 | 131.9 → 73.9 | 44.1% |

Short chats gain little (the compact line prefix and merged lines); the savings are in long chats with templates. All chats are synthetic;
measure again on pilot data before tuning the defaults.

### Configuration

The preprocessor's settings (`ConversationPreprocessorOptions`) are read from configuration with `Classification:Provider=Llm` and validated
at startup (and again at construction), naming the key and its range; with `Rules` they are not read.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Classification:Llm:Preprocessing:AdvisorMaxCharacters` | `400` | 120 to 1000 | Longest advisor message kept whole. |
| `Classification:Llm:Preprocessing:TokenBudget` | `4000` | 300 to 50,000 | Estimated tokens of the conversation block. |
| `Classification:Llm:Preprocessing:KeepLastLines` | `6` | 1 to 50 | Latest lines always kept. |

`ConversationPreprocessor.Version` is part of the prompt version (`LlmPromptBuilder.PromptVersion`), and the settings' fingerprint is an input of
the snapshot hash (`SnapshotHasher`): changing either invalidates the reuse of stored results.

## The LLM provider

`Classification:Provider=Llm` reads conversations with Claude Haiku 5.5 (`claude-haiku-5-5`) through the official Anthropic C# SDK, with the
rules as fallback and as the Won guard. **Stage A: Development only.** The API refuses `Llm` in any other environment until the release gate
of the design is lifted (the hardening slice, the golden-set evaluation, the per-tenant switch and quota with their screens, the privacy
entry). `Shadow` (rules and LLM side by side) is not available yet.

### End to end

```
worker claims a conversation (FOR UPDATE SKIP LOCKED, after Classification:Llm:DebounceSeconds)
  └─ snapshot WITH the known values (contact name and number, advisor, branch, channel number)
       └─ ClassificationCoordinator
            1. rules read the REAL snapshot (always: fallback + Won guard reference)
            2. sandbox conversation and IncludeSandbox=false ............ plain rules, no call
               real client and a NON-sandbox conversation, unless
               AllowRealClientNonSandbox=true ............................ plain rules, no call (Information line with ids)
            3. tenant admission (Stage A: NullAiUsageMeter admits everyone) . not admitted: rules, fallback protection, cleared
            4. Pseudonymizer -> ConversationPreprocessor -> LlmPromptBuilder
               no customer line left ..................................... plain rules, no call
               snapshot hash == Conversation.LastLlmSnapshotHash .......... nothing changed: no call, stored reading kept
            5. circuit breaker open / probe in flight .................... rules (fallback), deferred, no call
            6. ONE call (ILlmClient, <= 8 s, no retry) -> LlmResultMapper (strict) -> WonGuard
  └─ apply to the opportunity + complete the conversation (+ profile marked PENDING on it), commit (still the claim transaction)
  └─ log line of the call, with the claim-transaction duration (Warning above ClaimWarningMilliseconds)
  └─ AFTER the commit: contact profile (age range, gender) in its own short transaction, Contact row only; then the pending marker cleared
when nothing is due: write one profile left pending by a failed write or a crash (no model call)
```

`LlmConversationClassifier` (Application) composes the pure pieces; `AnthropicLlmClient` (Infrastructure) is the only class that knows the
SDK; `ReplayLlmClient` (Infrastructure, Development only) answers without a key. `IConversationClassifier` stays the rules' port: the LLM path
needs the failure kind, the usage and the hash, which a `ClassificationResult` cannot carry.

**Stage A limit.** The call runs inside the claim transaction (one worker, one attempt, at most `TimeoutSeconds`): ingestion, a manual edit
or the auto-closer touching that conversation waits up to the call time. The hardening slice moves the call out of the transaction before the
gate is lifted (docs/WEBHOOKS.md, "Lock order").

### What is applied

| Decision | Opportunity | Conversation |
|---|---|---|
| LLM reading | guarded LLM reading; each written field's producer is `Llm` | flag cleared, `LlmFailures = 0`, `LastLlmSnapshotHash` stored; then the contact profile |
| Content failure (`Refused`, `Truncated`, `InvalidOutput`) | rules reading as an explicit fallback: a non-empty LLM-sourced service, reason or objection is kept; stage, loss reason and confidence come from the rules | `LlmFailures + 1`; stays flagged while `LlmFailures <= MaxFallbackRetries`, then cleared with the rules standing |
| Transport failure (`Timeout`, `RateLimited`, `Unavailable`) | rules reading as a fallback | `LlmFailures` unchanged; stays flagged, retried after `RetryDelaySeconds`; feeds the breaker |
| Misconfiguration (`Misconfigured`: 401 authentication, 403 permission, 404 model not found) | rules reading as a fallback | `LlmFailures` unchanged; stays flagged; opens the breaker at once (one Error line per window naming the condition, never the key) |
| Breaker open | rules reading as a fallback, no call | stays flagged, `AttemptedAt = reopen time - RetryDelay + 0..30 s` (claimable about the reopen time, spread out) |
| Breaker half-open, another conversation is the probe | rules reading as a fallback, no call | stays flagged, claimable again in 30 s |
| Unchanged snapshot hash | nothing | flag cleared, hash kept |
| No customer text, sandbox excluded, real conversation kept from the real client | plain rules reading (no fallback flag), no failure counted | flag cleared, hash forgotten |

A rules reading (plain or fallback) forgets the stored hash, so the next claim calls the model again. The profile is written only from an
LLM reading, never erases a value and never overwrites a manual one. It is recorded on the conversation as **pending** (`ProfilePending`,
`PendingAgeRange`, `PendingGender`) in the classification commit and written afterwards in its own Contact-only transaction; the marker is
cleared in a separate Conversation-only write, only if it still holds those values. If the write fails, or the process dies in between, the
marker stays: when no conversation is due, the worker claims one pending profile (not flagged, past `RetryDelaySeconds`, `SKIP LOCKED`),
releases the conversation and writes the contact from the stored values, **without calling the model again**. A contact that no longer
exists clears the marker.

**Real data and the real client.** With `Classification:Llm:Client=Anthropic` only **sandbox** conversations (docs/SANDBOX.md) reach the
provider until `Classification:Llm:AllowRealClientNonSandbox=true` is set, and that is set only after the privacy review and the processor
agreement (docs/decisiones.md). Sandbox text is typed by staff and may still contain real data (a real name, a real number): it goes through the
same pseudonymizer as any conversation, so never paste real customer chats into the sandbox.

**Endpoint and environment.** The SDK reads `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, profiles, federation and OpenTelemetry variables
from the environment. The client is built with the key, the base URL (`Classification:Llm:BaseUrl`, https only, default
`https://api.anthropic.com`) and telemetry (off) set explicitly, so no environment variable can redirect the prompts and the key, change the
credential or export prompt content (pinned by `AnthropicEnvironmentTests`).

**Circuit breaker.** Process-local. Five consecutive transport failures open it for 300 s, counted from the time each failed call ENDED; a
misconfiguration opens it at once. Only a **usable** answer closes it and resets the count; a refused, truncated or invalid answer neither
counts nor resets (so alternating timeouts and invalid answers still open it, and ten refusals in a row never do). After the cooldown one probe
call at a time is admitted. Transitions (opened, half-open, closed) are Warning lines with the cause, threshold and cooldown; denied calls are
one Information line per window, not one per conversation.

### Cost controls (lowest cost without lowering quality)

| Control | Saves | Trade-off |
|---|---|---|
| Preprocessor (pseudonymize, drop media, collapse repeats, back-references, budget) | 8% on short chats, 65% on long chats (measured above) | none on the rules' phrases (quality guard) |
| Cached prefix: the system prompt and the catalog are identical for every conversation of a deployment; the cache breakpoint is on the second system block | cache reads cost 0.1x the input price | the first call of each 5-minute window pays a 1.25x write |
| `Classification:Llm:DebounceSeconds` (300 s, separate from the rules' 60 s) | a burst of messages costs one call | results appear about 5 minutes after the last message (the simulator overlay shortens it to 5 s) |
| No call without customer text after preprocessing (only advisor messages, only media) | every such conversation | none: the model would have nothing of the customer to read |
| No call when the snapshot hash equals `Conversation.LastLlmSnapshotHash` (requeues with nothing new) | every such claim | none: the hash covers every message id and text, the known names and numbers, the prompt (which includes the catalog), model, schema, age switch, preprocessing settings and the rules file's fingerprint (`ClassificationRuleSet.Fingerprint`), so editing the rules or the catalog invalidates stored hashes. With `Classification:Llm:HashKey` the stored value is HMAC-SHA256(key, hash); without it (Development) the plain SHA-256. This is the cheap part of the design's reuse (H2); reusing a stored run after a rollback needs the runs table |
| `Effort=low`, structured output, `MaxTokens=2048` | output tokens | `Thinking=Disabled` (effort high or below) saves more but is unmeasured: compare on the golden set first |
| Breaker, one attempt, no HTTP retry | no retry storms during an outage | the rules decide meanwhile; the conversation is read again after recovery |

### Logs

One Information line per call, starting with `LLM classification`, with structured properties only: `ConversationId`, `TenantId`, `Sandbox`
(true for internal sandbox traffic: filter it out of real usage), `Provider`, `Model`, `Status` (`succeeded`, `timeout`, `rate_limited`,
`unavailable`, `refused`, `truncated`, `invalid_output`), `Completion`, `Error` (a mapper code or the SDK exception type), `InputTokens`,
`OutputTokens`, `CacheReadTokens`, `CacheWriteTokens`, `EstimatedUsd` (from `Classification:Llm:Pricing:*`), `LatencyMs`, `ClaimTransactionMs`
(how long the conversation row was held: ingestion and sends on it waited that long; a Warning above `ClaimWarningMilliseconds`), `WonDowngraded`, and
the preprocessor's `MessagesIn`, `CharactersBefore`, `CharactersAfter`, `TokensBefore`, `TokensAfter`, `Dropped`, `Collapsed`, `Deduplicated`,
`Truncated`, `Merged`, `OmittedByBudget`. Never message text, names or numbers. A decision without a call is a Debug line with its reason
(a real conversation kept from the real client is an Information line with ids). `Status` also takes `misconfigured`.
Before the runs table exists (design PR 3) this line is the only record of a call.

### Configuration

Read only with `Classification:Provider=Llm`; every value is validated at startup naming the key, never printing a value.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Classification:Llm:Client` | `Anthropic` | `Anthropic`, `Replay` | `Replay`: canned answers, no key, no network, Development only. |
| `Anthropic:ApiKey` | *(none)* | required with `Anthropic` | From the environment (`Anthropic__ApiKey`) or Key Vault only. Never logged. |
| `Classification:Llm:PrivacyApproved` | `false` | must be `true` | Records the privacy approval (docs/decisiones.md). |
| `Classification:Llm:Model` | `claude-haiku-5-5` | model id | Sent as a plain string. |
| `Classification:Llm:Effort` | `low` | `low` to `max` | Thinking depth and token spend. |
| `Classification:Llm:Thinking` | `Adaptive` | `Adaptive`, `Disabled` | `Disabled` only with effort `low`, `medium` or `high`. |
| `Classification:Llm:MaxTokens` | `2048` | 256 to 16,000 | Ceiling of the answer, thinking included. Hitting it is a `Truncated` failure. |
| `Classification:Llm:TimeoutSeconds` | `8` | 1 to 8 (Stage A) | HTTP timeout of the one call. |
| `Classification:Llm:MaxRetries` | `0` | 0 (Stage A) | HTTP retries inside a call. |
| `Classification:Llm:OtherWaitsSeconds` | `5` | 0 to 60 | Other waits of the claim transaction. `TotalBudget = TimeoutSeconds x (MaxRetries + 1) + OtherWaitsSeconds` (13 s) must be shorter than `ProcessingTimeoutSeconds` and `RetryDelaySeconds`; no relation with any debounce. |
| `Classification:Llm:MaxFallbackRetries` | `2` | 0 to 10 | Counted content failures allowed after the first attempt. |
| `Classification:Llm:Breaker:Failures` | `5` | 1 to 100 | Consecutive transport failures that open the breaker. |
| `Classification:Llm:Breaker:CooldownSeconds` | `300` | 1 to 3600 | How long it stays open. |
| `Classification:Llm:DebounceSeconds` | `300` | 0 to 86,400 | Quiet period before a conversation is read on the LLM path (`Classification:DebounceSeconds` keeps applying to the rules). |
| `Classification:Llm:IncludeSandbox` | `true` in Development, else `false` | bool | Sandbox conversations (docs/SANDBOX.md) go through the LLM, so tests with the internal sandbox exercise it. Their log lines say `Sandbox=True`. |
| `Classification:Llm:ExtractAgeRange` | `false` | bool | Ask the age range. `true` requires the next key. |
| `Classification:Llm:AgeRangeLegalRef` | *(empty)* | text | Reference of the legal sign-off (the range includes `under_18`, Ley 1581 art. 7). |
| `Classification:Llm:AllowRealClientNonSandbox` | `false` | bool | With the real client: let non-sandbox conversations reach it. Only after the privacy review and the processor agreement. |
| `Classification:Llm:BaseUrl` | `https://api.anthropic.com` | https URL | Endpoint of the real client; never taken from `ANTHROPIC_BASE_URL`. No credentials, query or fragment. |
| `Classification:Llm:HashKey` | *(none)* | base64, 32+ bytes | Secret of the stored snapshot hash (HMAC-SHA256), like `Messaging:CredentialEncryptionKey`: `openssl rand -base64 32`. Without it the plain hash is stored. Changing it invalidates stored hashes (one extra call per conversation). |
| `Classification:Llm:ClaimWarningMilliseconds` | `5000` | 100 to 60,000 | A claim transaction on the LLM path longer than this is logged as a Warning. |
| `Classification:Llm:ReplayPath` | *(none)* | file | Canned answers of the replay client: `{"responses": {"<snapshot hash>": {<answer>}}}`. Checked at startup. |
| `Classification:Llm:Pricing:InputPerMTok` / `OutputPerMTok` / `CacheReadPerMTok` / `CacheWritePerMTok` | `0.10` / `0.50` / `0.01` / `0.125` | 0 to 1000 | USD per million tokens for `EstimatedUsd` (Haiku 5.5 up to 100K-token prompts). An estimate: check the price page before budgeting. |
| `Classification:Llm:Preprocessing:*` | see above | see above | The preprocessor. |

### Running it locally

`docker-compose.llm-dev.yml` (Replay, no key) and `docker-compose.llm-real.yml` (the real API, key from `ANTHROPIC_API_KEY` in `.env`) are
described in docs/DOCKER.md. The automated tests never call the real API: `RealAnthropicTests` runs only with
`CONVERSIA_REAL_LLM_API_KEY` set (`dotnet test --filter "Category=RealLlm"`), and it costs money.

### SDK answers (design spikes S1 to S8)

Verified against the `Anthropic` NuGet package (XML docs, compiler and request/response tests through a fake HTTP handler under the SDK);
the real API was not called.

| Spike | Answer |
|---|---|
| S1 structured output | `MessageCreateParams.OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = Dictionary<string, JsonElement> } }`; on the wire `output_config.format = {type: "json_schema", schema}`. |
| S2 cached prefix | `System = new List<TextBlockParam> { prompt, new() { Text = catalog, CacheControl = new CacheControlEphemeral() } }`: one breakpoint after both static blocks. Haiku 5.5's minimum cacheable prefix is 512 tokens; the prefix is about 7,300 characters (well above, asserted in `RealAnthropicTests.Spike_S2_*`). That the second call reads from the cache is asserted only by the opt-in real test (not run). |
| S3 usage | `Message.Usage.InputTokens`, `OutputTokens`, `CacheReadInputTokens` (nullable), `CacheCreationInputTokens` (nullable). `InputTokens` excludes the cached ones. |
| S4 model | `Model = "claude-haiku-5-5"`: a string converts implicitly; the answer's model is `Message.Model.Raw()`. |
| S5 effort / thinking | `OutputConfig.Effort = Effort.Low` (`low` ... `max`). Haiku 5.5 thinks adaptively when `Thinking` is omitted (its default effort is `medium`, so `low` is set explicitly); `ThinkingConfigDisabled` is accepted at effort `high` or below. No `temperature`/`top_p`/`top_k` (rejected by Haiku 5.5), no prefill, no forced tool. |
| S6 refusal | `Message.StopReason?.Raw() == "refusal"` (`max_tokens` = truncated). Haiku 5.5 has no server-side fallback model: a refusal is a counted content failure and the rules decide. |
| S7 test objects | No hand-built `Message`: `AnthropicLlmClientTests` gives the SDK a fake `HttpMessageHandler` (`AnthropicClient.HttpClient`) and wire JSON, so the request body and the parsing are the SDK's own. Errors: `AnthropicRateLimitException` (429), `AnthropicUnauthorizedException` / `AnthropicForbiddenException` / `AnthropicNotFoundException` (`Misconfigured`), `Anthropic5xxException` and other 4xx (`Unavailable`), `AnthropicIOException`, a timeout as a cancellation not requested by the caller. With `MaxRetries=0` a 503 is sent once and a slow provider is cut at the timeout (tested through the real SDK client). |
| S8 package | `Anthropic` 13.3.0 (published 2026-10-23; newer releases exist, the pin keeps a margin of a couple of weeks before adopting a release). |
