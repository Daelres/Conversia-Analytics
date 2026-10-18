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

Percentages are numbers from 0 to 100 with one decimal (`23.5`); durations are minutes with two decimals; a value that cannot be computed
(no data) is `null`, never `0`. Every answer repeats the `period` it was computed for (`from`, `to`, `timeZone`, `days`).

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

| Field | Definition |
|---|---|
| `total` | Cohort size. |
| `won`, `lost`, `inFollowUp`, `new` | Conversations by stage; they add up to `total`. |
| `stale` | Of `inFollowUp` and `new`, those whose last message is older than `Metrics:ConversionMaturityDays` (default **7**, the idle-close threshold): undecided and gone cold. |
| `inProgress` | `inFollowUp + new - stale`: still maturing. |
| `decided` | `won + lost + stale`. |
| **`conversionRate`** | **`won / decided * 100`**; `null` when `decided` is 0. |
| `winShareOfAll` | `won / total * 100`: the same, ignoring maturity; `null` when `total` is 0. |

Why this definition: `won / total` falls every time a conversation is still in progress, so the rate of the last days always looks bad
and comparing periods is unfair; `won / (won + lost)` ignores the conversations that went quiet without anyone deciding, which is most
of the lost sales. Counting the stale ones as non-conversions and leaving the fresh ones out is the simplest rate that is neither of those. The raw
counts are always returned so the dashboard can show both. The breakdowns (`byBranch`, `byAdvisor`, `bySource`) apply the same definition per group.

Groups: **branch** (the conversation's branch), **advisor** (the assigned advisor; conversations with none form an explicit bucket with
`id: null` and the name `Sin asesor`), **source** (`Ads`, `Organic`, `Unknown`). Rows are ordered by `total` descending, then name.

## First response time

Per cohort conversation: `FirstInboundAt` is the customer's first message; `FirstResponseAt` is the business's first message at or after it
(`docs/WEBHOOKS.md`). The response time is `FirstResponseAt - FirstInboundAt`.

| Field | Definition |
|---|---|
| `conversations` | Cohort size. |
| `answered` | Conversations with a first response. |
| `unanswered` | Conversations without one. **They are never counted as zero** nor mixed into the statistics below. |
| `avgMinutes`, `medianMinutes`, `p90Minutes` | Over the **answered** ones. Median and p90 are `percentile_cont(0.5)` and `percentile_cont(0.9)`: linear interpolation between neighbouring values (the median of 1, 2 and 4 minutes is 2; of 1, 2, 3 and 10 it is 2.5). `null` without answered conversations. |
| `slaMinutes` | `Metrics:FirstResponseSlaMinutes` (default **30**). |
| `withinSla` | Answered in at most `slaMinutes` (inclusive). |
| `slaEligible` | `answered + unanswered whose customer message is older than the SLA`: the conversations that have had their chance. An unanswered one still inside the SLA window is not a miss yet. |
| `answeredWithinSlaPercent` | `withinSla / slaEligible * 100`; `null` when `slaEligible` is 0. An unanswered conversation past the SLA counts as a miss. |
| `unansweredOverSla` | Unanswered with a customer message older than the SLA. |
| `oldestUnansweredMinutes` | How long the oldest unanswered conversation has been waiting (`now - FirstInboundAt`); `null` when none. |

## Loss reasons and objections

- `totalLost`: cohort conversations whose stage is **Lost**.
- `topReasons`: the loss reasons of those, the top `Metrics:TopLossReasons` (default **5**) by count (ties: reason name), each with `count` and
  `sharePercent` (of `totalLost`); `otherCount` is what the rest add up to. A loss with no reason counts as `Unknown`.
- `objections`: **all** cohort conversations by the latest objection raised (`None`, `Price`, `Timing`, `Location`, `Trust`, `Pain`, `Other`), every type
  present, with `count` and `sharePercent` of the cohort (`None` included: its share says how many conversations had no objection at all).

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
| `CONVERSION_DROP` | The `conversionRate` of the period is at least `Metrics:ConversionDropAlertPoints` (default **10**) points below that of the **previous period of the same length** (the days right before `from`, same filters), both with `decided >= MinSampleSize`. Values: `currentRate`, `previousRate`, `dropPoints`, `currentSample`, `previousSample`. | `Critical` from 2 x the threshold |
| `BRANCH_BELOW_AVERAGE` | A branch's `conversionRate` is at least `Metrics:BranchBelowAveragePoints` (default **15**) points below the tenant's, with `decided >= MinSampleSize` for that branch **and** for the tenant. One alert per branch; not raised when a `branchId` filter is given. Values: `branchId`, `branchRate`, `tenantRate`, `gapPoints`, `branchSample`. | `Critical` from 2 x the threshold |

`unanswered`: `total` (the same open conversations as `UNANSWERED_OVER_SLA`) and `items`, the **20 oldest first**: `conversationId`, `branchId`,
`branchName`, `advisorId`, `advisorName`, `firstInboundAt`, `waitingMinutes`. No customer name, number or text: the conversation is opened
with `GET /api/conversations/{id}`, which needs its own permission.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Metrics:TimeZone` | `America/Bogota` | Time zone of dates, days and hours. |
| `Metrics:FirstResponseSlaMinutes` | `30` | First-response SLA. 1 to 1440. |
| `Metrics:ConversionMaturityDays` | `7` | Age after which an undecided conversation counts against conversion. 1 to 365. |
| `Metrics:TopLossReasons` | `5` | Loss reasons listed. 1 to 20. |
| `Metrics:MinSampleSize` | `20` | Minimum decided conversations for the rate alerts. 1 to 100000. |
| `Metrics:ConversionDropAlertPoints` | `10` | Points of drop that raise `CONVERSION_DROP`. 1 to 100. |
| `Metrics:BranchBelowAveragePoints` | `15` | Points below the tenant average that raise `BRANCH_BELOW_AVERAGE`. 1 to 100. |

## How it is computed

One cohort query per endpoint (a CTE over `Conversations` joined to `Opportunities`, filtered by tenant, period and filters) aggregated with
`GROUP BY`, `count(*) FILTER (...)` and `percentile_cont`, so the work is in the database and the rows returned are the groups, not the
conversations. The tenant predicate is written into the SQL (percentiles need raw SQL, which the EF query filter does not touch); the tests
check that no number of one tenant ever depends on another's data. Indexes: `Conversations (TenantId, StartedAt)` and
`Conversations (TenantId, Status, LastMessageAt)` for the cohort and the open conversations, `Opportunities (TenantId, Stage, ClosedAt)`,
`Messages (TenantId, SentAt)` for the message volume.
