# Webhook simulator

A development tool that plays the part of 360dialog: it fills a **local** Conversia API with believable WhatsApp sales conversations, sent
through the real webhook, so the dashboard, the conversation list and the alerts can be seen working without a phone number, a Meta account or a
customer. It lives in `tools/Conversia.WebhookSimulator` and is **not part of the product**: it is not in the Docker images
(`tools` is in `.dockerignore`), nothing in `src/` references it, and it never writes anything to disk.

```
simulator ──login──► API (/api/auth/login)            setup:     branches, advisors, one WhatsApp number per branch
          ──POST───► /api/webhooks/360dialog/{key}    populate / play: the conversations, as 360dialog would send them
          ──PUT────► /api/conversations/{id}/advisor  populate: who answered each chat
```

What it proves and what it does not: it proves the pipeline (webhook → worker → classifier → metrics → pages) with data that looks like the
pilot's. It does **not** prove what the real provider sends. The message the business sends from the WhatsApp Business app (coexistence echo,
`smb_message_echoes`) follows the public documentation and is **unverified against a real 360dialog sandbox**: see
[the limits](#limits) and `docs/WEBHOOKS.md`.

## Run it on your machine

You need Docker and the .NET 10 SDK.

```bash
# 1. The stack, with the two background workers made quicker for quick local runs (docker-compose.simulator.yml).
#    Use your own .env (cp .env.example .env, then set POSTGRES_PASSWORD and JWT_SIGNING_KEY).
docker compose -f docker-compose.yml -f docker-compose.simulator.yml up -d --build
#    API on http://localhost:5030, Web on http://localhost:5112 (API_PORT / WEB_PORT in .env change them).

# 2. A company (tenant) and its owner: open http://localhost:5112/register and create one (or POST /api/auth/register-tenant).

#    A new company is PENDING (quota 0): nothing works until a platform administrator activates it. Either do that yourself
#    (docs/PLATFORM_ADMIN.md) or let setup do it with SIM_ADMIN_EMAIL / SIM_ADMIN_PASSWORD (step 3).

# 3. Tell the simulator who you are. Credentials come from the environment only; there is no --password option.
export SIM_EMAIL='you@example.test'
export SIM_PASSWORD='the password you registered with'
#    Only if the company is not Active yet: a platform administrator (the one you bootstrapped with PLATFORM_ADMIN_EMAIL/PASSWORD).
export SIM_ADMIN_EMAIL='admin@example.test'
export SIM_ADMIN_PASSWORD='the administrator password'

# 4. Create what the simulator needs. --print-secret shows the webhook secrets ONCE (the API never shows them again).
dotnet run --project tools/Conversia.WebhookSimulator -- setup --api-url http://localhost:5030 --print-secret
#    Copy the 'export SIM_WEBHOOK_SECRETS=...' line it prints into your shell.

# 5. Fill the dashboard.
dotnet run --project tools/Conversia.WebhookSimulator -- populate --conversations 300 --days 30 --seed 42
```

Then sign in at the Web and open **Panel** (`/panel`), **Conversaciones** and **Organización**. Start over with `docker compose down -v`.

### What the overlay changes

`docker-compose.simulator.yml` sets `Classification__DebounceSeconds=5` and `Conversations__AutoCloseIntervalMinutes=1`. Without it everything
works, only slower: a chat gets its stage after a minute of quiet (default `Classification:DebounceSeconds`, 60) and conversations older than
seven days are closed, and the silent ones given up as lost, at the next run of the closing job (default every 15 minutes). Until then the
dashboard counts those conversations as still open and "unanswered". The overlay is for local use only.

## Commands

All of them take `--api-url` (default `$SIM_API_URL`, else `http://localhost:5030`). Only local addresses are accepted: `localhost`, `*.localhost`
and loopback IPs. Pointing at anything else needs `--allow-remote`, which you should only say about an environment of yours that is meant to
receive fabricated data, and a remote address must be `https`: plain `http` is accepted only for the local addresses above, even with
`--allow-remote`, because the login, the token and the webhook secrets travel in that connection. The simulator never follows redirects, and it
only sends webhook deliveries to a relative `/api/webhooks/360dialog/...` path under the address you gave. They go through a client of their
own that never carries the owner's token, so neither the token nor a webhook secret is sent to any host other than `--api-url`.

### `setup`

Idempotent: it creates what is missing and leaves the rest alone (names are matched without regard to case; things that are not the simulator's are
never touched). It makes the four branches of the pilot (Centro, Norte, Sur, Poblado), two advisors for each and **one WhatsApp number per
branch** (`+57 300 555 0101` ...), because a conversation belongs to the branch of the number it arrived on, and the dashboard needs several
branches to compare, and **switches the numbers on** (a new line starts inactive). It prints the webhook path of each number.

If the company is not Active, setup logs in as the platform administrator (`SIM_ADMIN_EMAIL` / `SIM_ADMIN_PASSWORD`, environment only), activates it and
sets the quota (`--line-quota`, default 4). The administrator's token is used for those calls only and never sent with the owner's. Without those
variables and with a company that is not Active, setup stops before touching anything and says what to do. If the quota is full it says the number stays off.

| Option | Effect |
|---|---|
| `--line-quota N` | The active-line quota the workspace needs (default 4, one per branch). With `SIM_ADMIN_*` set, setup activates a Pending or Suspended company and raises a quota below `N`. |
| `--print-secret` | Prints the secret of a number it has just created (or rotated) as a line to paste: `export SIM_WEBHOOK_SECRETS='Centro=...;Norte=...'`. Without it the secret is discarded, and the command says so. |
| `--rotate-secret` | Replaces the secret of every simulator number (the old one stops working at once). Use it when you lost them. |

### `populate`

Plans the conversations (deterministic: the same `--seed` and the same moment give the same plan), sends each one through the webhook of its
branch with that branch's secret, waits for the workers to store them (polling `/health` and the number of conversations, with a timeout) and then
assigns advisors with `PUT /api/conversations/{id}/advisor` so the per-advisor numbers exist.

| Option | Default | Meaning |
|---|---|---|
| `--conversations` | 300 | How many in all (1 to 5000). |
| `--days` | 30 | Length of the window the recent conversations fall in; the window ends now. 1 to 180. |
| `--seed` | 42 | The same seed re-sends the same message ids, so running it twice adds nothing. |
| `--previous-share` | 0.3 | Share of the conversations placed in the window right before (same length), where the business did better. It is what gives the dashboard a "worse than before" to show. 0 turns it off. |
| `--rate` | 15 | Requests per second to the webhook (the limits are in `docs/WEBHOOKS.md`; a `429` is waited out for as long as `Retry-After` says). |
| `--wait-seconds` | 180 | How long to wait for the workers. |
| `--no-assign` | | Leave every conversation without an advisor. |

What the data looks like (so the dashboard has something to say):

- **Branches differ**: Centro and Poblado win most, Norte wins little (about 16 % against 46 % in Centro) and leaves more chats unanswered, so
  `BRANCH_BELOW_AVERAGE` fires.
- **Advisors differ**: one answers in minutes, another in about an hour; 12 % of the chats have no advisor ("Sin asignar").
- **Origins**: 35 to 55 % arrive from a click-to-WhatsApp ad (the delivery carries a `referral`), the rest are organic.
- **Outcomes**: bookings confirmed by the advisor (Won), refusals (price, another clinic, not interested), "let me think", "does it hurt?",
  and chats nobody answers. Each is written to be read the same way by the rule-based classifier (`docs/CLASSIFICATION.md`); the unit tests run the
  real classifier over thousands of generated chats to keep it true.
- **Hours**: busy at midday and after work, quiet at night and on Sundays, in the business time zone (UTC-5).
- **A few chats are waiting right now** (opened in the last hours, no answer): the `UNANSWERED_OVER_SLA` alert and the red "Sin responder" card.
- **A worse period behind**: the window before has a higher conversion, so the variation card shows a drop and `CONVERSION_DROP` can fire.
- Timestamps are always in the past (nothing is dated later than "now"), spread over the window.

### `play`

Sends one scripted scenario, message by message, to one branch (`--branch Norte`, default: the first one with a secret) and says what each
delivery should do. `--scenario` is one of:

| Scenario | What it sends | What to expect |
|---|---|---|
| `booking` | A customer asks about a zone, books, and the advisor confirms | Won, once the chat has been quiet for the debounce |
| `price-objection` | "Too expensive", then "no thanks" | Lost, reason price |
| `unanswered` | A customer writes 45 minutes ago and nobody answers | An open unanswered conversation: the alert and the card |
| `ad-origin` | The first message carries a click-to-WhatsApp referral | Source "Anuncios", then Won |
| `bsuid` | A customer identified only by a business-scoped user id (`CO.1349...`), no phone number | `200`, but the message is **skipped and counted** (`SkippedMessageCount`): no contact, no conversation |
| `duplicate` | The same body twice, then the same message inside another body | One conversation with one message |
| `out-of-order` | The customer's last message arrives first, the advisor's reply second, the first message last | Times recomputed from the message times: the first response is 2 minutes, not negative |
| `all` (default) | Every scenario above, each with its own customer | |

## Environment

Credentials and secrets are read **only** from the environment. The command line refuses `--password`, `--email`, `--secret`, `--token` and the
like, because arguments end up in the shell history and in `ps`. Nothing is ever printed, logged or written, except what `setup --print-secret` shows
on purpose.

| Variable | Meaning |
|---|---|
| `SIM_EMAIL`, `SIM_PASSWORD` | The user to log in as: the owner of the company (the simulator needs to read the webhook paths and to create things, so it needs Organization management and Conversations access). |
| `SIM_ADMIN_EMAIL`, `SIM_ADMIN_PASSWORD` | Optional, `setup` only: a platform administrator who activates a Pending workspace and sets its line quota. Both or neither. |
| `SIM_WEBHOOK_SECRETS` | One secret per number: `Centro=<secret>;Norte=<secret>;Sur=<secret>;Poblado=<secret>`. `setup --print-secret` prints the line. |
| `SIM_WEBHOOK_SECRET` | One secret to try for a branch that `SIM_WEBHOOK_SECRETS` does not list. |
| `SIM_API_URL` | The API address when `--api-url` is not given. |

Why a secret per branch and not one: the secret belongs to the channel (a number), the API generates it and shows it once, and a branch has its own
number. One secret would need one channel, and then every conversation would belong to a single branch.

## Try it against the 360dialog sandbox

**Documentation only. Nothing in this repository calls 360dialog and the simulator does not need the sandbox.** This is how to point the
real provider's sandbox at your machine, once you have an account and a sandbox API key:

1. Expose the local API with a public HTTPS tunnel (ngrok, Cloudflare Tunnel, ...) and note the host it gives you. The Web does not know that
   address (it only knows the API through `Api:BaseUrl`), so the **Organización** page shows the relative path and tells you to add the host yourself.
2. Create a number on **Organización** (or with `setup`) and keep the webhook path and the secret it shows once.
3. Register the webhook with the sandbox. Keep the key in an environment variable, never in a file you commit:

   ```bash
   curl -X POST "https://waba-sandbox.360dialog.io/v1/configs/webhook" \
     -H "D360-API-KEY: $D360_API_KEY" \
     -H "Content-Type: application/json" \
     -d '{
           "url": "https://<your-tunnel-host>/api/webhooks/360dialog/<channelKey>",
           "headers": { "X-Webhook-Secret": "<webhookSecret>" }
         }'
   ```

4. Write to the sandbox number from your phone and watch **Conversaciones**. Messages **you** send from the sandbox are what tell whether the
   business-side format is the one the parser expects: until you see a first-response time appear, treat that metric as unproven.

## Limits

- **The business echo is unverified.** `smb_message_echoes` / `message_echoes[]` follows 360dialog's and Meta's public documentation. If the real
  provider sends something else, `Dialog360PayloadParser` has to change, and the simulator's `CloudApiPayloads` with it; a green run here would
  hide that. This is the open question of `docs/decisiones.md`.
- **BSUID customers are skipped**, as the product does today (`docs/WEBHOOKS.md`): the `bsuid` scenario shows exactly that.
- The chats are written in Spanish (Colombia) for laser hair removal, the pilot's vertical, and are as varied as a few dozen templates allow: the
  classifier reading them correctly says nothing about how it reads a real customer.
- The numbers it uses (`+57 300 555 xxxx`) are invented, like the ones in the repository's samples. A real person could own one; do not point the
  simulator at anything that sends messages out.

## How it is tested

`tests/Conversia.WebhookSimulator.UnitTests` (no network, no containers, no sleeping):

- the chats: the real `RuleBasedConversationClassifier` reads thousands of generated chats of every kind and must give the outcome, loss reason and
  objection they were written for; a Won always carries a message from the advisor;
- the plan: deterministic by seed, believable spread (branches, advisors, origins, hours), nothing dated in the future;
- the payloads: the product's own `Dialog360PayloadParser` accepts every delivery (and skips the `bsuid` one);
- the command line: local-only addresses, no credentials as arguments, ranges, defaults;
- the commands, against an in-memory API: `setup` is idempotent and prints secrets only on request; `populate` sends each conversation to its
  branch, waits, assigns advisors of the right branch, is a no-op the second time and never prints a secret.
