# Conversation classification

How Conversia turns a WhatsApp sales conversation into data: which service the customer asked about, why, what
stopped them, and how the conversation ended. Phase 1 has no LLM, so the reader is a **rule-based Spanish
(Colombia) classifier** behind an interface that a future LLM adapter will implement.

```
ingestion ──► Conversation.ClassificationDirty = true and LlmPending = true   (any conversation that received a message)
                    │   quiet for Classification:DebounceSeconds (60)
ConversationClassificationService (FOR UPDATE SKIP LOCKED, one transaction per conversation)        ← every company, near real time
                    ▼
   IConversationClassifier.ClassifyAsync(snapshot)  ──►  ClassificationResult (codes and enums only)
                    ▼
   Opportunity.ApplyClassification  (never touches a Manual stage)  ──►  flag cleared

with Classification:Provider=Llm, on each company's schedule (nightly by default):
LlmAnalysisService ──► LlmAnalysisScheduler ──► LlmAnalysisRunner (three phases, no lock during the call)   ← see "When the AI reads"
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
of the design is lifted (the golden-set evaluation, the company's own usage screen, the privacy entry; the hardening slice (three phases,
bounded concurrency, hash reuse) and the per-company switch, schedule, quota and caps with their administrator screens exist, see "When the
AI reads" and "Usage and quota"). `Shadow` (rules and LLM side by side) is not available yet.

### End to end

```
the rules pass (every company, near real time): rules reading applied as an explicit fallback (LLM-sourced fields kept), rules flag cleared
the LLM, in the company's window (see "When the AI reads"):
phase 1 (short transaction) claims a PENDING conversation of the company (FOR UPDATE SKIP LOCKED, quiet for Classification:Llm:DebounceSeconds,
        not leased, not attempted yet in this window)
  └─ snapshot WITH the known values (contact name and number, advisor, branch, channel number)
       └─ ClassificationCoordinator.PlanAsync
            1. rules read the REAL snapshot (the Won guard's reference)
            2. sandbox conversation and IncludeSandbox=false ............ skipped, no call
               real client and a NON-sandbox conversation, unless
               AllowRealClientNonSandbox=true ............................ skipped, no call (Information line with ids)
            3. tenant admission (AiUsageMeter: switch, cap, quota, ceiling) . not admitted: skipped (the rules stand)
            4. Pseudonymizer -> ConversationPreprocessor -> LlmPromptBuilder
               no customer line left ..................................... skipped, no call
               snapshot hash == Conversation.LastLlmSnapshotHash .......... nothing changed: no call, stored reading kept
            5. circuit breaker open / probe in flight .................... deferred, no call
            6. a call is needed: LEASE (LlmLeaseUntil = now + RetryDelaySeconds), commit; no lock and no connection are kept
phase 2 (no lock): ONE call (ILlmClient, <= TimeoutSeconds) -> LlmResultMapper (strict) -> WonGuard; usage recorded at once (own scope)
phase 3 (short transaction): relock by id FOR UPDATE SKIP LOCKED (3 tries, 200 ms apart; else the lease expires and it is claimed again);
        apply ONLY IF the snapshot hash, ClassificationRequestedAt and the opportunity's stage are unchanged since phase 1, else DISCARD
  └─ log line of the call (claim, call and apply durations)
  └─ AFTER the commit: contact profile (age range, gender) in its own short transaction, Contact row only; then the pending marker cleared
when nothing is due: write one profile left pending by a failed write or a crash (no model call)
```

`LlmConversationClassifier` (Application) composes the pure pieces; `AnthropicLlmClient` (Infrastructure) is the only class that knows the
SDK; `ReplayLlmClient` (Infrastructure, Development only) answers without a key. `IConversationClassifier` stays the rules' port: the LLM path
needs the failure kind, the usage and the hash, which a `ClassificationResult` cannot carry.

**No lock during the call (design H1).** Since the scheduled runner, the call is made between two short transactions: ingestion, a manual edit
or the auto-closer touching the conversation never waits for the model (docs/WEBHOOKS.md, "Lock order"). What changes meanwhile is caught in
phase 3, and the answer is then thrown away rather than applied over newer facts.

### What is applied

The rules pass has already written its reading (as an explicit fallback: a non-empty LLM-sourced service, reason or objection is kept; stage,
loss reason and confidence come from the rules). The LLM runner only writes LLM readings over it:

| Decision (phase 3, nothing changed since phase 1) | Opportunity | Conversation |
|---|---|---|
| LLM reading | guarded LLM reading overwrites the `Auto` fields (each written field's producer is `Llm`; `Manual` fields are never touched) | `LlmPending` cleared, `LlmFailures = 0`, `LastLlmSnapshotHash` and `LlmAnalyzedAt` stored; then the contact profile |
| Content failure (`Refused`, `Truncated`, `InvalidOutput`) | nothing (the rules reading stands) | `LlmFailures + 1`, attempt recorded: pending again in the next window while `LlmFailures <= MaxFallbackRetries`, then the rules stand |
| Transport failure (`Timeout`, `RateLimited`, `Unavailable`) | nothing | `LlmFailures` unchanged; leased until `now + RetryDelaySeconds` (retried in the same window if it is still open); feeds the breaker |
| Misconfiguration (`Misconfigured`: 401 authentication, 403 permission, 404 model not found) | nothing | as a transport failure; opens the breaker at once (one Error line per window naming the condition, never the key) |
| Breaker open | nothing, no call | leased until about the reopen time plus 0..30 s |
| Breaker half-open, another conversation is the probe | nothing, no call | leased 30 s |
| Unchanged snapshot hash | nothing | `LlmPending` cleared, hash and `LlmAnalyzedAt` kept |
| No customer text, sandbox excluded, kept from the real client, not admitted | nothing | `LlmPending` cleared, hash forgotten |
| **Something changed during the call** (new message, stage changed by the auto-closer or a person, hash) | nothing | DISCARDED: lease released, attempt recorded, still pending: read in the next window |

A conversation keeps its LLM fields until the next LLM reading replaces them: with the AI on, the dashboard's stage stays current (rules, near
real time) and the descriptive fields are refreshed by the window. The profile is written only from an
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

## When the AI reads (the per-company schedule)

The owner's rule: **cost and quality first.** The rules read every conversation near real time (cheap; the dashboard stays current); the
LLM reads a company's conversations **once per window** of its schedule, which the platform administrator sets per company
(`PUT /api/admin/tenants/{id}/ai-schedule`, audited `tenant.ai_schedule_changed` with the reason; docs/PLATFORM_ADMIN.md, "Limits"):

| Mode (`Tenant.AiAnalysisMode`) | Windows | When to use it |
|---|---|---|
| `Nightly` (default) | Once a day from the local hour (`Tenant.AiAnalysisNightlyHour`, default `Classification:Schedule:DefaultNightlyHour` = 01:00 America/Bogota) for `Classification:Schedule:NightlyWindowHours` (5) | Every company: spreads the server load, and goes in Message Batches (half price) |
| `EveryHours` (1 to 24) | Every N hours counted from local midnight (the last window of the day is cut at midnight: every 5 h = 00, 05, 10, 15, 20) | A company that needs fresher AI fields during the day |
| `NearRealTime` | Always open: `Classification:Llm:DebounceSeconds` (300) after the last message, like the first LLM stage | Special cases only (each burst of messages can cost a call) |

- **One analysis per window.** A scheduled window never claims a conversation already attempted inside it (`Conversation.LlmAttemptedAt`): a
  conversation that keeps receiving messages during the day is analysed once at night, not once per message. A conversation unchanged since
  its applied reading is never re-sent (`LastLlmSnapshotHash`, design H2): it is finished without a call.
- **Not admitted is not done.** A conversation the company may not analyse at claim time (AI off, quota, USD ceiling, sandbox or deployment
  cap) stays pending: a scheduled company retries it in the next window, a near-real-time one an hour later. Companies with the AI off are
  not even looked at: their pending conversations are analysed in the first window after the AI is switched on.
- **The rules-only provider.** With `Classification:Provider=Rules` the rules pass also clears `LlmPending`: nothing piles up for an LLM that
  does not run. Switching a deployment from `Rules` to `Llm` therefore queues nothing old: each conversation is analysed after its next
  message. The migration only queued the conversations still waiting under the first LLM stage (flagged ones).
- **Selection.** Companies with the AI on, active, with pending conversations (`Conversation.LlmPending`, set by every new message) and an
  open window. Admission (the switch, the deployment cap, the company's quota and USD ceiling, the sandbox rules) is checked per conversation
  in phase 1, as before.
- **Fairness and load.** Each company's windows open a stable 0..`Classification:Schedule:SpreadMinutes` (60, at most half the window)
  minutes late, so not every company starts at 01:00:00. Companies are served round robin (one conversation each in turn, the one with
  the fewest analysed in the window first), at most `Classification:Schedule:MaxConversationsPerWindow` (2,000) per company and window
  (the rest wait for the next window). The cap counts **calls made and requests batched** (`AiAnalysisRuns.Calls`): deferrals of an open
  breaker, unchanged and skipped conversations cost nothing and do not use it up. At most `Classification:Llm:MaxConcurrent` (4) analyses
  are in flight per API instance: the permit is taken BEFORE the claim (design D20) by `Classification:Workers` workers (default =
  `MaxConcurrent`); one company has at most `Classification:Llm:PerCompanyConcurrency` (2) of them.
- **A window that closes** before every pending conversation was reached leaves them pending for the next one; nothing is lost.
- **Time zone.** `Classification:Schedule:TimeZone` (America/Bogota, no daylight saving). In a zone with daylight saving, a local hour that
  does not exist opens at the first valid instant after it.
- **Changing the schedule** takes effect at the next window. Turning the AI on for a company analyses its pending conversations (those that
  received a message since the last LLM reading) in its next window, bounded by the per-window cap and the quota.
- **The company sees** only when its AI fields were last updated: "Análisis de IA: última actualización …" on the panel
  (`GET /api/tenant/ai-analysis`, `MetricsView`) and on the conversation (`ConversationDetailResponse.AiAnalyzedAt`). Never the mode, counts
  or costs.
- **The administrator sees** per company the schedule, the next window, the pending conversations and the last window's counts (claimed,
  analysed, unchanged, skipped, discarded, failed, deferred, batched; `AiAnalysisRuns`, counts only) and, platform-wide, the batches by
  state (docs/PLATFORM_ADMIN.md, "Monitoring").

### The three phases (design H1)

| Phase | Lock | What |
|---|---|---|
| 1 | Conversation row, a few ms | Claim (`FOR UPDATE SKIP LOCKED`), snapshot, plan (admission, call reduction, breaker), then a **lease** (`LlmLeaseUntil = now + Classification:RetryDelaySeconds`) and commit. Decisions without a call are applied here. |
| 2 | none | Pseudonymize, preprocess, call (`TimeoutSeconds` x (`MaxRetries` + 1)), record the usage. |
| 3 | Conversation row, a few ms | Relock by id `FOR UPDATE SKIP LOCKED`, up to 3 tries 200 ms apart (still locked: the lease expires and the conversation is claimed again). Re-read the snapshot; apply **only if** the snapshot hash, `ClassificationRequestedAt` and the opportunity's stage (`Stage`, `StageChangedAt`, `StageSource`) equal phase 1's; otherwise **discard** and leave it pending for the next window. |

Phase 3 also checks that the answer is **still this conversation's business**: the conversation must still be pending and hold the very
lease phase 1 took (a lease that expired and was replaced by another worker's, or a reading applied meanwhile, means someone else owns it:
nothing is touched), and the company must still be Active with the AI on (and, with the real client before the privacy review, the
conversation a sandbox one). Otherwise the answer is discarded (it was paid) and the conversation stays pending.

A worker that dies after phase 1 leaves only its lease: the conversation is claimable again once it expires. The lease must outlive the call:
`TotalBudget = TimeoutSeconds x (MaxRetries + 1) + OtherWaitsSeconds` must be shorter than `RetryDelaySeconds` (validated at startup; the
transaction timeout no longer bounds the call, so `TimeoutSeconds` may be 1 to 15 and `MaxRetries` 0 or 1).

**Why the same three phases for every mode.** `NearRealTime` uses them too (its claim waits for the LLM debounce and retries an attempt after
`RetryDelaySeconds` instead of once per window): one path to test, and no mode holds the row during a call. The only behaviour difference with
the first stage is that the rules reading appears first (after `Classification:DebounceSeconds`) and the LLM reading replaces its `Auto`
fields when it arrives.

**Accepted costs.** An answer discarded in phase 3 was paid for (recorded as a usable call for a synchronous one, as a failed call for a
batched one) and the conversation is analysed again in the next window. Admission is still a read without a reservation (design D12): a
company can go over its quota by the calls in flight (`MaxConcurrent` per instance, or a whole batch).

### Batch mode (Nightly companies)

With `Classification:Llm:Batch:Enabled=true` (the default), the night window's requests of `Nightly` companies are sent as Message Batches
instead of one call each:

1. **Phase 1** claims as above but, instead of calling, writes an `LlmBatchRequests` row (the conversation, the phase-1 hash, request time
   and stage; the custom id) under an `LlmBatches` row (`Preparing`), leases the conversation for 25 hours (the provider's 24-hour window
   plus a margin) and keeps the prompt in memory.
2. Up to `Classification:Llm:Batch:MaxRequests` (1,000) requests are **submitted** in one batch (`Submitted`, with the provider's id and
   expiry). A batch that cannot be sent is `Failed`: its requests are `Errored` and their conversations released at once.
3. Every `Classification:Llm:Batch:PollSeconds` (60) the scheduler **polls** submitted batches; an ended one is **collected**: each result
   goes through phase 3 with the same staleness checks (the conversation is read again; if its hash is unchanged, the prompt that was sent is
   rebuilt exactly and the answer is read like a synchronous one: mapper, Won guard). Usage is recorded at
   `Classification:Llm:Batch:DiscountFactor` (0.5) of the list price.
4. `errored`, `expired` and `canceled` results (not billed) and requests the provider returned no result for release their conversation:
   the next claim of that request takes the **synchronous path** (in the same window if it is still open). An answer for a conversation
   that changed after the batch was sent is discarded (its cost is recorded) and the conversation is read **once more, synchronously**, while
   the window is open. An answer for a conversation that no longer exists (a suppressed contact) is recorded as a failed call: it never adds
   the id back to the distinct set (design D24).
5. **Once per result, one process per batch.** Each batch is polled, collected and recovered under a PostgreSQL advisory lock on its id
   (several API instances can run the scheduler). Each request is taken by one collector with an atomic `Pending → Collecting` step; its
   usage is recorded **once**, after phase 3 (an applied reading as a success, anything else as a failed call). A request a collector took and
   never resolved (a crash) is released after 10 minutes without recording its usage again. A request whose conversation stayed locked goes
   back to `Pending` and is collected on a later poll.
6. **No duplicate batches.** Creating a batch is not idempotent, so the batch client never retries (`AnthropicLlmBatchClient.SdkMaxRetries
   = 0`); the synchronous fallback is the retry. Before the upload the batch records `SubmitStartedAt` and its final `RequestCount`; after the
   provider accepted it, its id is logged at once and saved (three tries), only onto a batch still `Preparing` (one failed meanwhile is
   canceled at the provider). The last upload of a pass gets at most 20 s of the host's shutdown.
7. **Recovery.** A batch left `Preparing` more than 10 minutes after its upload started (never while this process uploads it) is looked up
   in the provider's list back to that moment: a batch none of ours with the same number of requests (and, if it ended, only our custom ids)
   is **adopted** and collected as usual; other unknown batches of that moment are **canceled**; with no match (or never uploaded) the batch
   is failed and its conversations released (only those still holding its lease). Matching an in-progress batch by its time and size is a
   heuristic: the provider keeps no metadata of ours on a batch.
8. **Poison batches.** A batch that can no longer be polled or read (404, results gone after 29 days) is failed once its expiry plus
   `Classification:Llm:Batch:PoisonGraceHours` (48) has passed, and its conversations released; the monitoring counts failed batches.
9. **Reservation.** Until it is resolved, every batched request is **reserved** in admission: counted as an analysed conversation of its
   company and at its estimated cost (its prompt's estimated input plus 400 tokens of answer, at the batch price) for the company's USD
   ceiling, the sandbox ceiling and the deployment cap. Each phase-1 admission sees the requests already batched, so a batch never
   goes over the company's quota or the caps by its size.
10. **Retention.** Resolved batches (collected or failed) and their requests older than `Classification:Llm:Batch:RetentionDays` (30) are
    deleted by the usage retention job; the costs stay in `AiUsageMonths`.
11. **Restart.** Everything lives in the two tables: a new process polls `Submitted` batches and collects `Ended` ones, skipping requests
    already resolved (`Applied`, `Discarded`, ...).

`EveryHours` and `NearRealTime` companies always call synchronously. The Replay client has a Replay batch client (answers at once, kept in
memory) for local runs.

### Message Batches API: verified facts

Checked on 2026-11-12 against the official documentation and the installed `Anthropic` 13.3.0 package (XML documentation and a compile
plus a fake-HTTP test through the SDK, `AnthropicLlmBatchClientTests`); **the real API was not called**.

| Question | Answer | Source |
|---|---|---|
| Discount | All usage at 50% of the standard prices; Haiku 5.5 batch: $0.05 input / $0.25 output per MTok (prompts up to 100K tokens) | [Batch processing, Pricing](https://platform.claude.com/docs/en/build-with-claude/batch-processing) |
| Limits | 100,000 requests or 256 MB per batch, whichever first | same page, "Batch limitations" |
| Processing window | Most batches end within 1 hour; results when all are done or after 24 hours; unfinished requests then `expired` | same page |
| Results retention | 29 days after creation | same page |
| `custom_id` | 1 to 64 characters, `^[a-zA-Z0-9_-]{1,64}$`, unique in the batch; results come back in any order | same page, "Prepare and create your batch" |
| Haiku 5.5 | Listed in the supported models (`claude-haiku-5-5`); "All active models support the Message Batches API" | same page (supportedModels) |
| Structured output (`output_config.format`) | Supported in batches ("Process structured outputs at scale with a 50 percent discount") | [Structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs) |
| `effort`, thinking | "Almost any request" can be batched, including extended thinking; only `stream`, `speed`, `fallbacks` and `max_tokens: 0` are rejected. The SDK's batch `Params` carries `OutputConfig` (effort) and `Thinking`. Effort inside a batch is **not** named explicitly in the docs: unverified against the real API | batch page, "What can be batched"; SDK `Anthropic.Models.Messages.Batches.Params` |
| Prompt caching | Supported; batch and cache discounts stack; cache hits are best-effort (30% to 98% typical); identical `cache_control` blocks in every request help; the 1-hour TTL is suggested for long batches (we keep the 5-minute one: measure `CacheReadTokens` in the log lines first) | batch page, "Using prompt caching with Message Batches" |
| Cancellation | `processing_status` becomes `canceling`, then `ended`, with partial results; canceled requests come back `canceled` (not used by Conversia) | batch page, "Canceling a Message Batch" |
| Result types | `succeeded` (with the message), `errored` (invalid request or server error, not billed), `canceled` (not billed), `expired` (not billed). A refusal comes back `succeeded` with `stop_reason: refusal` | batch page, "Retrieving batch results"; [Refusals in Message Batches](https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback) |
| SDK | `client.Messages.Batches.Create(BatchCreateParams { Requests = [Request { CustomID, Params }] })`, `Retrieve(id)` (`ProcessingStatus`, `RequestCounts`, `ExpiresAt`), `ResultsStreaming(id)` (`MessageBatchIndividualResponse`, `Result.TryPickSucceeded/Errored/Expired/Canceled`), `Cancel(id)` | `Anthropic` 13.3.0 XML documentation |

**Our custom id** is `c` + the conversation id (32 hex) + `-` + the first 30 hex characters of the snapshot hash: 64 characters, no personal
data (the full hash is in `LlmBatchRequests`). Batches are scoped to the API key's workspace and may go slightly over a workspace spend limit
(documented by the provider): the deployment cap and the per-company quota remain ours.

### Cost estimate per conversation

`EstimatedUsd = (input x InputPerMTok + output x OutputPerMTok + cacheRead x CacheReadPerMTok + cacheWrite x CacheWritePerMTok) / 1,000,000`,
times 0.5 for a batched request. With the defaults (Haiku 5.5: 0.10 / 0.50 / 0.01 / 0.125 USD per MTok) and the measured preprocessed sizes
(about 74 estimated tokens of conversation on average, 620 for a long chat, plus the cached prefix of about 2,400 tokens (7,300 characters / 3) and an **assumed** answer of about 150 tokens with effort `low`, not
measured against the real API):

| Case | Synchronous | Batch (x 0.5) |
|---|---|---|
| Average chat, prefix read from the cache | 74 x 0.10 + 150 x 0.50 + 2,400 x 0.01 = **0.000106 USD** | **0.000053 USD** |
| Long chat, prefix read from the cache | 620 x 0.10 + 150 x 0.50 + 2,400 x 0.01 = **0.000161 USD** | **0.000081 USD** |
| Average chat, prefix written (cache miss) | 74 x 0.10 + 150 x 0.50 + 2,400 x 0.125 = **0.000382 USD** | **0.000191 USD** |

Per 1,000 conversations a month: about 0.11 to 0.38 USD synchronous, 0.05 to 0.19 USD in batches. These are estimates from synthetic chats
and list prices: measure the real tokens (`InputTokens`, `CacheReadTokens` in the log lines and `AiUsageMonths`) on pilot data before
budgeting. The nightly schedule also saves calls: a chat of 20 messages over a day is analysed once instead of up to once per burst.

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
(true for internal sandbox traffic: filter it out of real usage), `Mode` (the company's schedule), `Batch` (true for a batched answer; its
`EstimatedUsd` includes the discount), `Outcome` (`analysed`, `discarded`, `failed`, `deferred`, `left_locked`), `CallMs`, `ApplyTransactionMs`, `Provider`, `Model`, `Status` (`succeeded`, `timeout`, `rate_limited`,
`unavailable`, `refused`, `truncated`, `invalid_output`), `Completion`, `Error` (a mapper code or the SDK exception type), `InputTokens`,
`OutputTokens`, `CacheReadTokens`, `CacheWriteTokens`, `EstimatedUsd` (from `Classification:Llm:Pricing:*`), `LatencyMs`, `ClaimTransactionMs`
(phase 1; with `ApplyTransactionMs`, the only time the conversation row is held; a Warning above `ClaimWarningMilliseconds`), `WonDowngraded`, and
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
| `Classification:Llm:TimeoutSeconds` | `8` | 1 to 15 | HTTP timeout of one call (made outside any lock). |
| `Classification:Llm:MaxRetries` | `0` | 0 to 1 | HTTP retries inside a call. |
| `Classification:Llm:OtherWaitsSeconds` | `5` | 0 to 60 | Other waits of one analysis. `TotalBudget = TimeoutSeconds x (MaxRetries + 1) + OtherWaitsSeconds` (13 s) must be shorter than `RetryDelaySeconds` (the lease); no relation with the transaction timeout or any debounce. |
| `Classification:Llm:MaxConcurrent` | `4` | 1 to 16 | Analyses in flight per API instance (the permit is taken before the claim). |
| `Classification:Workers` | `MaxConcurrent` | 1 to 16 | Workers of the LLM scheduler. The connection string's `Maximum Pool Size` must be at least `MaxConcurrent x 2 + 1 + Workers + 20` (checked at startup). |
| `Classification:Llm:Batch:Enabled` | `true` | bool | Nightly companies go in Message Batches. |
| `Classification:Llm:Batch:MaxRequests` | `1000` | 1 to 100,000 | Requests per batch. |
| `Classification:Llm:Batch:PollSeconds` | `60` | 1 to 3600 | How often submitted batches are polled. |
| `Classification:Llm:Batch:DiscountFactor` | `0.5` | 0 to 1 | Share of the list price a batched request costs (the provider's 50%). |
| `Classification:Llm:Batch:PoisonGraceHours` | `48` | 1 to 1440 | After a batch's expiry, how long polling failures are retried before it is failed. |
| `Classification:Llm:Batch:RetentionDays` | `30` | 1 to 3650 | Resolved batches older than this are deleted. |
| `Classification:Llm:PerCompanyConcurrency` | `2` | 1 to 16 | Analyses of one company in flight at once (synchronous mode). |

Outside Development (once the release gate is lifted) batch mode requires `Classification:Llm:HashKey`: the custom id carries a prefix of
the snapshot hash, and only the keyed hash keeps it from being confirmed by hashing a guessed conversation.
| `Classification:Llm:MaxFallbackRetries` | `2` | 0 to 10 | Counted content failures allowed after the first attempt. |
| `Classification:Llm:Breaker:Failures` | `5` | 1 to 100 | Consecutive transport failures that open the breaker. |
| `Classification:Llm:Breaker:CooldownSeconds` | `300` | 1 to 3600 | How long it stays open. |
| `Classification:Llm:DebounceSeconds` | `300` | 0 to 86,400 | Quiet period before the LLM reads a conversation, in every mode (`NearRealTime`: the delay after the last message; `Classification:DebounceSeconds` keeps applying to the rules). |
| `Classification:Llm:IncludeSandbox` | `true` in Development, else `false` | bool | Sandbox conversations (docs/SANDBOX.md) go through the LLM, so tests with the internal sandbox exercise it. Their log lines say `Sandbox=True`. |
| `Classification:Llm:ExtractAgeRange` | `false` | bool | Ask the age range. `true` requires the next key. |
| `Classification:Llm:AgeRangeLegalRef` | *(empty)* | text | Reference of the legal sign-off (the range includes `under_18`, Ley 1581 art. 7). |
| `Classification:Llm:AllowRealClientNonSandbox` | `false` | bool | With the real client: let non-sandbox conversations reach it. Only after the privacy review and the processor agreement. |
| `Classification:Llm:BaseUrl` | `https://api.anthropic.com` | https URL | Endpoint of the real client; never taken from `ANTHROPIC_BASE_URL`. No credentials, query or fragment. |
| `Classification:Llm:HashKey` | *(none)* | base64, 32+ bytes | Secret of the stored snapshot hash (HMAC-SHA256), like `Messaging:CredentialEncryptionKey`: `openssl rand -base64 32`. Without it the plain hash is stored. Changing it invalidates stored hashes (one extra call per conversation). |
| `Classification:Llm:ClaimWarningMilliseconds` | `5000` | 100 to 60,000 | A phase 1 or phase 3 transaction longer than this is logged as a Warning. |
| `Classification:Llm:ReplayPath` | *(none)* | file | Canned answers of the replay client: `{"responses": {"<snapshot hash>": {<answer>}}}`. Checked at startup. |
| `Classification:Llm:Pricing:InputPerMTok` / `OutputPerMTok` / `CacheReadPerMTok` / `CacheWritePerMTok` | `0.10` / `0.50` / `0.01` / `0.125` | 0 to 1000 | USD per million tokens for `EstimatedUsd` (Haiku 5.5 up to 100K-token prompts). An estimate: check the price page before budgeting. |
| `Classification:Llm:Preprocessing:*` | see above | see above | The preprocessor. |

The schedule keys are read **whatever the provider** (the administrator sees each company's windows):

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Classification:Schedule:TimeZone` | `America/Bogota` | IANA id | The zone of the windows. |
| `Classification:Schedule:DefaultNightlyHour` | `1` | 0 to 23 | Local hour the night window opens for a company without its own. |
| `Classification:Schedule:NightlyWindowHours` | `5` | 1 to 12 | How long the night window stays open. |
| `Classification:Schedule:SpreadMinutes` | `60` | 0 to 180 | Each company's stable delay inside its windows (at most half the window). |
| `Classification:Schedule:MaxConversationsPerWindow` | `2000` | 1 to 1,000,000 | Conversations of one company analysed in one window, at most. |

The quota keys are read **whatever the provider** (the administrator sees each company's effective quota):

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Classification:Quota:DefaultMonthlyConversations` | `500` | 0 to 1,000,000 | The monthly quota of a company without its own. |
| `Classification:Quota:TenantMonthlyUsdPerQuotaUnit` | `0.02` | 0 to 100 | The company's USD ceiling per conversation of its quota (500 = 10 USD). |
| `Classification:Quota:DeploymentMonthlyUsdCap` | `20` | 0 to 1,000,000 | The deployment's monthly USD cap, sandbox included. 0 = no LLM at all. |
| `Classification:Quota:TimeZone` | `America/Bogota` | IANA id | The zone of the month. |
| `Classification:Quota:SandboxMonthlyUsdPerCompany` | `2` | 0 to 10,000 | The most a company's sandbox conversations may spend a month. |

### Usage and quota

Every call to the model is **recorded**, not only logged (`AiUsageMeter`, design D12, D13, D18), and every company is **admitted** before a call:

1. **The switch.** `Tenant.AiEnabled` is **off by default**; only the platform administrator turns it on, with a reason and the version of the
   AI terms the company accepted (audited as `tenant.ai_enabled_changed`, docs/PLATFORM_ADMIN.md, "Limits"). Off = the rules read everything
   (a normal state, nothing counted). This applies in Development too: to try the LLM locally, switch the AI on for the company first.
2. **The deployment cap.** When the estimated USD of every company this month (sandbox included: it is real money with the real client)
   reaches `Classification:Quota:DeploymentMonthlyUsdCap`, every company is read by the rules until the month ends (one Warning per month and
   instance).
3. **The company's quota and ceiling** (real conversations only). The quota is distinct conversations analysed successfully in the month
   (`Tenant.AiMonthlyConversationQuota`, default `Classification:Quota:DefaultMonthlyConversations`; 0 = none); the ceiling is the quota
   times `Classification:Quota:TenantMonthlyUsdPerQuotaUnit`. Reaching either = rules only until the month ends (the moment is kept and
   logged once).

The **month** is the America/Bogota month (`Classification:Quota:TimeZone`) at the moment of the increment: a call admitted at 23:59:58 on 31
October that succeeds at 00:00:02 on 1 November counts in November.

**What is recorded** (`AiUsageMonths`, one row per company, month and sandbox flag; never text):

| Event | Conversations analysed | Calls | Failed calls | Tokens and USD |
|---|---|---|---|---|
| Usable answer | +1 the first time the conversation is read that month (`AiUsageConversations` keeps the distinct set) | +1 | | the provider's usage and its estimated cost |
| Unusable answer (refused, truncated, invalid) | | +1 | +1 | the usage it billed |
| Timeout or 5xx without usage | | +1 | +1 | the **estimated input** of the prompt sent (characters / 3), since it may have been processed |
| 429 or 401/403/404 | | +1 | +1 | 0 (refused at the door) |
| Not admitted, unchanged hash, no customer text, breaker open | nothing (no call) | | | |

The record runs in a scope and a short transaction of its own (the month row is created if missing and locked, the conversation inserted
`ON CONFLICT DO NOTHING`, the counters incremented), so a rollback of the classification never loses it, concurrent calls never lose an
increment, and a failure to record is logged (ids, tokens, USD) without stopping the classification. **Sandbox usage** (docs/SANDBOX.md) is
recorded in its own row (`IsSandbox`): it never counts against the company's quota or ceiling, but it has its own per-company bound
(`Classification:Quota:SandboxMonthlyUsdPerCompany`, reached = `SandboxUsdCeiling`, the rules read the sandbox until the month ends) and it
counts against the deployment cap.

**Never lost silently.** Once the provider answered, the cost exists: the record is written whatever happened to the classification's budget
(it has its own 3-second limit, then the classification honours its cancellation), and the circuit breaker is told the outcome first. A record
that cannot be written is logged at Error with event id 4101 `AiUsageNotRecorded` (ids, tokens and USD, never text; alert on it) and counted
in the administrator's overview (`ai_usage_lost`); the classification goes on.

**Retention.** The month rows stay (they are the evidence of what was spent). The distinct conversation sets are kept for the current and
the previous month only (purged every 6 hours). Suppressing a contact deletes its conversation ids from them (design D24;
docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"), and deleting a cancelled company deletes both tables' rows of it
(docs/PLATFORM_ADMIN.md, "Deleting a company's data"); the month rows of a suppressed contact's company stay, since they hold counts only.

**Known limits.** Admission is a read without a reservation: with N instances (and `MaxConcurrent` analyses in flight each, or a whole batch), a company can go
over its quota by the calls in flight, and the deployment over its cap by those calls' cost (design D12). `NullAiUsageMeter` (admits every
company, records nothing) remains only for unit tests and the hand-built coordinators of the integration tests. The tenant's own
usage page (`GET /api/tenant/ai-usage`) is not built yet; the administrator sees each company's usage (docs/PLATFORM_ADMIN.md, "Monitoring").

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
