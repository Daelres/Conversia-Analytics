# Development workflow: TDD + isolated three-agent review

This is the mandatory process for **every** feature built in a project
of this project, human or AI-assisted. It exists to prevent
a single author (human or AI) from writing the requirement, the test,
the implementation, and the approval, all with the same blind spots.
Splitting the work across isolated roles is what makes the review
real instead of ceremonial.

## 1. Break the requirement into phases

Before writing anything, split the requirement into phases. A phase is
one process/feature slice that can be taken **completely** from empty
to done — tests and implementation both — before the next phase
starts. Do not start phase N+1 while phase N's tests are still red.

Example (this is how the initial Users/Tenants/Permissions system was
split):
1. Tenant + user registration + login (JWT issuance).
2. Permission assignment + permission-checked authorization.

## 2. Three isolated roles, per phase

Three roles touch every phase, always in this order. **Each invocation
is a fresh agent/session with no memory of the others' reasoning.**
The reviewer role only ever sees the artifact under review (tests, or
code + approved tests) and the original requirement — never the prior
agent's chain of thought. This isolation is not optional: if the same
agent writes the tests and then reviews them, it will rubber-stamp its
own blind spots.

### Role A — Test Engineer

Writes the tests for the phase before any production code exists.
Rules that are non-negotiable:

- Tests must exercise **real** conditions: a real PostgreSQL via
  Testcontainers for anything touching persistence, multiple
  concurrent/parallel users where the feature is concurrency-sensitive
  (e.g. two logins racing, two tenants writing at once), and realistic
  invalid input — not just the happy path.
- No placebo tests. A test that always passes regardless of the
  implementation (e.g. `Assert.True(true)`, asserting on a mock's own
  setup instead of real behavior, or a test that never actually
  invokes the code under test) is a defect, not a test.
- Every input must lead to a defined, controlled outcome. If the
  requirement doesn't say what happens for a given case, don't invent
  new behavior — but do write a test asserting that the system
  degrades in a controlled way (a specific handled exception / a
  specific error response), never an unhandled crash. "Undefined by
  the requirement" must still mean "safely handled," not "untested."
- Only add the minimal interfaces/method signatures needed for the
  test project to compile (red state). No production logic.
- Commit when done: `test(<phase>): add tests for <phase description>`.

### Role B — Tech Lead (review gate #1: tests)

Fresh context. Given only the requirement and the tests from Role A.
Checks:

- Are these real tests, or placebo? Do they actually fail against a
  no-op implementation?
- Do they cover the concurrency/multi-user angle when relevant, and
  the exception/edge-case handling described above?
- Is the test code itself clean and readable enough that another
  developer can understand the intent without spelunking?

If anything fails, send the phase back with **specific, concrete**
feedback (not "make it better"). A fresh Role A instance fixes it and
commits: `test(<phase>): address review feedback — <what changed>`.
Repeat until Role B approves. Only after approval does the phase move
to Role C.

### Role C — Developer

Fresh context. Given only the requirement and the **approved** tests
(not Role A's or Role B's reasoning). Implements production code to
turn the tests green.

- Clean Code / Clean Architecture: respect the dependency direction in
  `docs/ARCHITECTURE.md`, keep methods small and single-purpose.
- Minimal comments — only where a non-obvious constraint or gotcha
  needs explaining. No restating what the code already says.
- Every exception path a user can trigger must be caught at a
  boundary (API middleware or service layer) and turned into a
  controlled, meaningful response. Nothing unhandled reaches the
  caller as a raw 500 or an unformatted stack trace.
- Commit when done: `feat(<phase>): implement <phase description>`.

### Role B again — Tech Lead (review gate #2: implementation)

Fresh context (a new instance of the reviewer role, not a continuation
of gate #1). Given the requirement, the approved tests, and the
implementation. Checks:

- All phase tests pass, and no unrelated tests broke.
- Clean Code / Clean Architecture conventions are respected — layer
  boundaries, naming, no leaked infrastructure concerns into
  Application/Domain.
- Current framework/library best practices and official guidance are
  followed (do not accept deprecated APIs or anti-patterns just
  because they compile).
- **Query efficiency**: no N+1 query patterns, no redundant
  round-trips to the database that a single query or a batched
  operation could replace. This is checked explicitly, not left to
  chance — resource cost matters.
- Security: no secrets in code, input validated at the boundary,
  authorization checked where required.

If anything fails, send it back with specific feedback. A fresh Role C
instance fixes it and commits:
`fix(<phase>): address audit feedback — <what changed>`. Repeat until
Role B approves.

## 3. Commit discipline

Every phase produces **at least two commits** (Role A's test commit,
Role C's implementation commit), plus one additional commit per
correction round from either review gate. Never squash a phase's
history into one commit — the point is traceability of who found what
and when.

## 4. What this buys you

A requirement is never interpreted, tested, implemented, and approved
by the same blind spot. If you are an AI agent reading this file to
continue development on a project of this project: follow
this process for any new feature, and do not skip a role or merge two
roles into one invocation, even when it feels faster.
