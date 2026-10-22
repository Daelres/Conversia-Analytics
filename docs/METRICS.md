# Metrics

Every number the executive dashboard shows, defined once. The API computes them in SQL (aggregation in PostgreSQL, nothing loaded
into memory) from conversations, messages and opportunities; this page is the contract the code and the tests follow.

All endpoints need the `MetricsView` permission, are scoped to the caller's tenant by the JWT and answer `application/json`.

| Endpoint | Answers |
|---|---|
| `GET /api/metrics/summary` | The headline: conversion, first response, top loss reasons |
| `GET /api/metrics/conversion` | Conversion with the raw counts, by branch, advisor and source |
| `GET /api/metrics/first-response` | First response time and SLA, by branch, advisor and source, plus the unanswered |
| `GET /api/metrics/loss-reasons` | Why sales were lost, and the objections raised |
| `GET /api/metrics/volume` | Conversations and messages per day and per hour of the week |
| `GET /api/metrics/alerts` | What needs attention now |

## Query

| Parameter | Meaning |
|---|---|
| `from`, `to` | Local dates (`yyyy-MM-dd`) of the tenant's time zone. `from` inclusive, **`to` exclusive**: the whole of October is `from=2026-10-01&to=2026-11-01`. Neither: the last 30 days **including today** (`from` = today - 29, `to` = tomorrow). Only `to`: the 30 days before it. Only `from`: the 30 days from it. `from` must be before `to` and the range at most **366 days**; otherwise `400`. |
| `branchId` | Only conversations of this branch. |
| `advisorId` | Only conversations assigned to this advisor. |
| `unassigned=true` | Only conversations with no advisor (cannot be combined with `advisorId`). |
| `source` | `Ads`, `Organic` or `Unknown`. |

**Units.** Every percentage of the metrics API is a number **from 0 to 100** with one decimal (`23.5`). The classifier's `classificationConfidence`
(`GET /api/conversations/{id}`) is a different scale on purpose: **0 to 1** (`0.85`); do not mix them. Durations are minutes with two decimals. A value that
cannot be computed (no data, an empty denominator) is `null`, never `0`: that includes every `sharePercent` over an empty set. Every answer repeats the
`period` it was computed for (`from`, `to`, `timeZone`, `days`).

Dates are validated one by one: each of `from` and `to` must fall between **2015-01-01** and **today + 366 days** (UTC today), alone or together, and the range
(`from` before `to`, at most 366 days) is checked when both are given. So `from=9999-12-31` or `to=0001-01-05`, which would overflow the 30 days an open end implies,
is a `400` with a Spanish message (`El campo Desde debe ser una fecha entre el 2015-01-01 y el ...`), never a `500`. `GET /api/conversations` takes the same window.

**Throttling.** The six endpoints and `GET /api/conversations` share one limit per authenticated user (the `sub` of the token, not the address),
`Metrics:RateLimitPerMinute` (default 60, a fixed one-minute window): the next request answers `429` with `Retry-After` and a Spanish problem. Each statement of the metrics queries has a time limit, `Metrics:QueryTimeoutSeconds` (default 15); a statement that exceeds
it answers `503` (a Spanish problem, no SQL and no stack in the answer or the logs) and the next request tries again.

## Time zone

`Metrics:TimeZone` (default `America/Bogota`, UTC-5 with no daylight saving; any IANA name the machine knows works). Dates are **local**
dates; stored instants stay UTC. A day begins at local midnight (if midnight does not exist in a daylight-saving gap, at the first valid moment).
The grouping by day and hour uses PostgreSQL's `AT TIME ZONE`, so a conversation that started at 23:30 Bogota time (04:30 UTC of the next
day) belongs to the local day it started in, and a zone with daylight saving has its 23- and 25-hour days.

## The cohort

> Conversations whose `StartedAt` falls in the period and that have **at least one inbound (customer) message** (`FirstInboundAt` is set),
> and that match the filters.

A conversation the business started (a promotion nobody answered) is not a sales conversation and is not counted. A conversation belongs
to the period it **started** in, whatever happens later; its current state (stage, first response) is what is measured, so a number
for a past period can still change when its conversations are classified, decided by hand or answered late.

## Conversion

Each cohort conversation has one opportunity, whose stage is one of **Won**, **Lost**, **InFollowUp** or **New** (not classified yet, or nothing could be read).

**The rate is computed over the matured cohort.** A conversation is *matured* when it started at least `Metrics:ConversionMaturityDays` (default **7**) before
now. Comparing a young period with an old one is biased otherwise: the decided conversations of the last days count at once (a sale is won in minutes) while the
ones that will go cold are not known yet, so the recent rate looks better than it will be and a real drop is hidden. Only matured conversations enter the rate,
in the headline, in every breakdown and in the previous-period comparison of `CONVERSION_DROP`; all the raw counts still cover the whole cohort.

| Field | Definition |
|---|---|
| `total` | Cohort size. |
| `won`, `lost`, `inFollowUp`, `new` | Conversations by stage, **all of them**; they add up to `total`. |
| `matured` | Cohort conversations that started at least the maturity ago: the ones the rate is computed over. |
| `immature` | `total - matured`: too recent. They are in every raw count and in none of the rate. |
| `maturedWon`, `maturedLost` | `won` and `lost` among the matured ones. |
| `stale` | Of `inFollowUp` and `new`, those whose last message is older than the maturity: undecided and gone cold (always matured). |
| `inProgress` | `inFollowUp + new - stale`: still alive. |
| `decided` | `maturedWon + maturedLost + stale`: the denominator of the rate. |
| **`conversionRate`** | **`maturedWon / decided * 100`**, 0 to 100; `null` when `decided` is 0. |
| `inMaturation` | `true` when the period has conversations but **none is mature yet**: there is no rate to give, not "0 %" and not "unknown", just too early. A matured period with nothing decided has a `null` rate and `inMaturation: false`. |
| `winShareOfAll` | `won / total * 100` over the whole cohort, maturity ignored; `null` when `total` is 0. |

Why this definition: `won / total` falls every time a conversation is still in progress, so the rate of the last days always looks bad; `won / (won + lost)` ignores the
conversations that went quiet without anyone deciding, which is most of the lost sales. Counting the stale ones as non-conversions, leaving the alive ones out and
rating only the conversations old enough to have settled is the simplest rate that is none of those. A dashboard shows `immature` next to the rate ("12 of 80
conversations are still maturing") and `inMaturation` as "too early". The breakdowns (`byBranch`, `byAdvisor`, `bySource`) apply the same definition per group.

`Metrics:ConversionMaturityDays` must be **at least `Conversations:AutoCloseAfterDays`** while the closing job is on (the API refuses to start otherwise, naming both keys):
the job gives the silent conversations up as lost after that many idle days, so a conversation rated earlier would still be decided afterwards and the rate of a period
would keep moving.

Groups: **branch** (the conversation's branch), **advisor** (the assigned advisor; conversations with none form an explicit bucket with
`id: null` and the name `Sin asesor`), **source** (`Ads`, `Organic`, `Unknown`). Rows are ordered by `total` descending, then name.

## First response time

Per cohort conversation: `FirstInboundAt` is the customer's first message; `FirstResponseAt` is the business's first message at or after it
(`docs/WEBHOOKS.md`). The response time is `FirstResponseAt - FirstInboundAt`.

**"Unanswered" has one definition across the API: the customer wrote and nobody replied.** It splits in two by the status of the conversation:

- **open** (`unansweredOpen`): somebody can still answer it. This is the actionable set and the one the rest of the API means by *unanswered*:
  `alerts.unanswered`, `GET /api/conversations?unanswered=true` and `oldestUnansweredOpenMinutes`.
- **closed** (`unansweredClosed`): the conversation closed (idle, or by a person) and nobody ever replied. A miss that is over: not actionable, but not forgotten either;
  `GET /api/conversations?closedUnanswered=true` lists them.

| Field | Definition |
|---|---|
| `conversations` | Cohort size. |
| `answered` | Conversations with a first response. |
| `unansweredOpen` | Open, no reply yet. **Never counted as zero** nor mixed into the statistics below. |
| `unansweredClosed` | Closed with no reply. Never part of the statistics either. `conversations = answered + unansweredOpen + unansweredClosed`. |
| `avgMinutes`, `medianMinutes`, `p90Minutes` | Over the **answered** ones. Median and p90 are `percentile_cont(0.5)` and `percentile_cont(0.9)`: linear interpolation between neighbouring values (the median of 1, 2 and 4 minutes is 2; of 1, 2, 3 and 10 it is 2.5). `null` without answered conversations. |
| `slaMinutes` | `Metrics:FirstResponseSlaMinutes` (default **30**). |
| `withinSla` | Answered in at most `slaMinutes` (inclusive). |
| `unansweredOpenOverSla` | Open, unanswered, with a customer message older than the SLA. |
| `slaEligible` | `answered + unansweredOpenOverSla + unansweredClosed`: the conversations that have had their chance. An open one still inside the SLA window is not a miss yet; a closed one is a miss for good. |
| `answeredWithinSlaPercent` | `withinSla / slaEligible * 100`; `null` when `slaEligible` is 0. |
| `oldestUnansweredOpenMinutes` | How long the oldest **open** unanswered conversation has been waiting (`now - FirstInboundAt`); `null` when none. A conversation that closed unanswered is not "waiting". |

## Loss reasons and objections

- `totalLost`: cohort conversations whose stage is **Lost**. It is `statedLost + noResponse.count`.
- `noResponse`: `{ count, sharePercent }`, the sales lost because the customer **stopped answering** (`LossReason.NoResponse`, set by the job that closes idle
  conversations). It is the system's **inference** from silence: the customer never said it, so it is shown **on its own line** and left out of the ranking below
  (otherwise, on a typical pilot, "no response" tops the list and tells the manager nothing). `sharePercent` is of `totalLost`; `null` when nothing is lost.
- `statedLost`: the rest of the losses: a reason the customer gave, the classifier read or a person set.
- `topReasons`: the loss reasons of `statedLost` (without `NoResponse`), the top `Metrics:TopLossReasons` (default **5**) by count (ties: reason name), each with `count` and
  `sharePercent` **of `statedLost`**; `otherCount` is what the rest add up to. A loss with no reason counts as `Unknown`.
- `objections`: **all** cohort conversations by the latest objection raised (`None`, `Price`, `Timing`, `Location`, `Trust`, `Pain`, `Other`), every type
  present, with `count` and `sharePercent` of the cohort (`None` included: its share says how many conversations had no objection at all); `null` over an empty cohort.

`GET /api/metrics/summary` carries the same `totalLost`, `statedLost`, `noResponse` and `topLossReasons`.

## Volume

- `byDay`: every local day of the period (zero-filled) with `conversations` (cohort conversations that **started** that day), `inboundMessages` and
  `outboundMessages` (messages **sent** that local day in conversations that match the filters, whenever those conversations started).
- `byHourOfWeek`: the 24 x 7 grid (168 cells, `dayOfWeek` 1 = Monday ... 7 = Sunday, `hour` 0 to 23, local time) with `conversations` started and
  `inboundMessages` received in that hour over the whole period: when customers write, to staff accordingly.
- `conversations`, `inboundMessages`, `outboundMessages`: the totals.

## Alerts

`GET /api/metrics/alerts` returns `alerts` (each with a `code`, a `severity` of `Warning` or `Critical`, a Spanish `message` and the `values` behind
it) and `unanswered` (see below). Thresholds are configuration; alerts that compare rates need a minimum sample (`Metrics:MinSampleSize`, default **20**
decided conversations) in **both** sides of the comparison, so a handful of conversations never raises one.

| Code | Raised when | Severity |
|---|---|---|
| `UNANSWERED_OVER_SLA` | There are **open** cohort conversations whose customer wrote more than `slaMinutes` ago and nobody answered. Values: `count`, `slaMinutes`, `oldestWaitingMinutes`. | `Critical` if the oldest waits more than 4 x the SLA, else `Warning` |
| `CONVERSION_DROP` | The `conversionRate` of the period is at least `Metrics:ConversionDropAlertPoints` (default **10**) points below that of the **previous period of the same length** (the days right before `from`, same filters), both rates over their **matured** cohort and both with `decided >= MinSampleSize`. The drop is computed from the **exact** rates and rounded only for the text: 9.96 points is not a 10-point drop. Values: `currentRate`, `previousRate`, `dropPoints`, `currentSample`, `previousSample`. | `Critical` from 2 x the threshold |
| `BRANCH_BELOW_AVERAGE` | A branch's `conversionRate` is at least `Metrics:BranchBelowAveragePoints` (default **15**) points below the tenant's (exact rates, as above), with `decided >= MinSampleSize` for that branch **and** for the tenant. One alert per branch; not raised when a `branchId` filter is given. Values: `branchId`, `branchRate`, `tenantRate`, `gapPoints`, `branchSample`. | `Critical` from 2 x the threshold |

`unanswered`: `total` (the same **open** conversations as `UNANSWERED_OVER_SLA`; closed ones are not waiting for anybody) and `items`, the **20 oldest first**: `conversationId`, `branchId`,
`branchName`, `advisorId`, `advisorName`, `firstInboundAt`, `waitingMinutes`. No customer name, number or text: the conversation is opened
with `GET /api/conversations/{id}`, which needs its own permission.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Metrics:TimeZone` | `America/Bogota` | Time zone of dates, days and hours. |
| `Metrics:FirstResponseSlaMinutes` | `30` | First-response SLA. 1 to 1440. |
| `Metrics:ConversionMaturityDays` | `7` | A conversation enters the conversion rate once it started this many days ago; an undecided one idle for that long counts against it. 1 to 365. Must be at least `Conversations:AutoCloseAfterDays` while the closing job is on. |
| `Metrics:TopLossReasons` | `5` | Loss reasons listed. 1 to 20. |
| `Metrics:MinSampleSize` | `20` | Minimum decided conversations for the rate alerts. 1 to 100000. |
| `Metrics:ConversionDropAlertPoints` | `10` | Points of drop that raise `CONVERSION_DROP`. 1 to 100. |
| `Metrics:BranchBelowAveragePoints` | `15` | Points below the tenant average that raise `BRANCH_BELOW_AVERAGE`. 1 to 100. |
| `Metrics:RateLimitPerMinute` | `60` | Requests per minute and authenticated user for the metrics endpoints, `GET /api/conversations`, `/api/messages/*` and `GET /api/webhook-events` (one shared budget). 1 to 100000. |
| `Metrics:QueryTimeoutSeconds` | `15` | Time limit of each metrics SQL statement. 1 to 300. |

## How it is computed

One cohort query per endpoint (a CTE over `Conversations` joined to `Opportunities`, filtered by tenant, period and filters) aggregated with
`GROUP BY`, `count(*) FILTER (...)` and `percentile_cont`, so the work is in the database and the rows returned are the groups, not the
conversations. The tenant predicate is written into the SQL (percentiles need raw SQL, which the EF query filter does not touch); the tests
check that no number of one tenant ever depends on another's data. Indexes: `Conversations (TenantId, StartedAt)` and
`Conversations (TenantId, Status, LastMessageAt)` for the cohort and the open conversations, `Opportunities (TenantId, Stage, ClosedAt)`,
`Messages (TenantId, SentAt)` for the message volume.
