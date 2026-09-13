# DevSup

**AI-driven API failure detection & auto-repair.** DevSup watches your production API,
captures failures as they happen, dispatches an AI agent to investigate your code and
push a fix, and emails you at every step — so you get notified of the error and its fix,
instead of digging through logs.

> Project status: **v0.28** — the **webhook retry-all** release.
> `POST /api/webhooks/{id}/deliveries/retry-all` re-queues every failed delivery for an
> endpoint in one call (audited `webhook.retryAll`), and the dashboard delivery log
> gains a **Retry all failed** button. 240 tests passing.

---

## Table of contents

1. [What it does](#1-what-it-does)
2. [The problem](#2-the-problem)
3. [How it works end-to-end](#3-how-it-works-end-to-end)
4. [Architecture](#4-architecture)
5. [Harry vs. machine-readable errors](#5-code-errors-vs-not-code-errors)
6. [Bring-your-own AI keys](#6-bring-your-own-ai-keys)
7. [Email notifications](#7-email-notifications)
8. [Webhooks, channels & health checks](#8-webhooks-channels--health-checks)
9. [Event replay & re-dispatch](#9-event-replay--re-dispatch)
10. [Platform admin & data retention](#10-platform-admin--data-retention)
11. [Audit & failure history](#11-audit--failure-history)
12. [Security & sanitization](#12-security--sanitization)
13. [Repository layout](#13-repository-layout)
14. [Data model](#14-data-model)
15. [Local development](#15-local-development)
16. [Roadmap](#16-roadmap)

---

## 1. What it does

- **Users create an account** and connect their git repositories (GitHub or GitLab)
  via OAuth.
- For each repo the user picks a **branch**, and optionally provides the **URL of the
  app/backend** the source is linked to.
- The user instruments their app with the **DevSup middleware** (a NuGet package).
- The middleware **detects API failures** — exceptions and 4xx/5xx responses — and logs
  the request, response, payload, and stack trace to the DevSup database.
- An **AI agent is prompted immediately**: it inspects the failing code, diagnoses the
  root cause, applies a patch, and runs `git add`/`commit`/`push`.
- The database entry is marked **fixed** on success, with a status and the commit SHA.
- **Emails** keep the user informed: error detected → recommended fix → fix pushed.
- A **browser dashboard** at `/dashboard/` shows every repository, its health, and all
  repair tickets in one place.

## 2. The problem

Production API errors are repetitive and noisy: null references, DB timeouts, bad
credentials, config drift, flaky downstream services. Developers spend hours triaging
issues that an AI with repo access can diagnose and often fix in minutes. DevSup
automates the loop while keeping a human in control — it never silently approves or
merges without visibility.

## 3. How it works end-to-end

```
 app (instrumented)                 DevSup platform                git provider          smart agent
 -----------------                 ----------------                ------------         -----------
 request -> 200/4xx/5xx
        |__ DevSup middleware captures failure
             |_ sanitized event POST /ingest
                               |_ persisted (FailureEvent)
                               |_ classified (code vs not-code)
                               |_ RepairTicket created (status=New)
                               |_ email: "failure #123 detected"
                               |_ agent dispatched
                                     |_ clone repo @ branch (or use app URL for live checks)
                                     |_ investigate failing code
                                     |_ propose patch -> status=PatchProposed
                                     |_ git add/commit/push -> commit SHA, status=FixPushed
                                     |_ email: "fix pushed (sha …) — status updated"
                                 Feedback loop
```

### Instrumenting your app

**ASP.NET Core (.NET):** add the `DevSup.Instrumentation` package, register an HTTP
client, then put the middleware near the top of the pipeline:

```csharp
builder.Services.AddHttpClient();

var app = builder.Build();
app.UseDevSup(new DevSupInstrumentationOptions
{
    IngestEndpoint = new Uri("https://your-devsup/api/ingest"),
    ApiToken       = "<your DevSup JWT>",
    RepositoryId   = Guid.Parse("<repository id from GET /api/repositories>")
});
```

`DevSupMiddleware` buffers the request body, and on an unhandled exception or any
status code in `FailureStatusCodes` (default 4xx/5xx) it redacts secrets and POSTs the
event to `/api/ingest` — **fire-and-forget**, so a DevSup outage never breaks your app.
Useful options: `MaxCapturedPayloadLength`, `SensitiveHeaderNames` (stripped before
sending), `FailureStatusCodes`, and `SchemaVersion`.

**Any other language:** there is no SDK, but the wire contract is just an HTTP POST —
an interceptor is ~20 lines. Send:

- `POST /api/ingest`
- `Authorization: Bearer <your DevSup JWT>` (the endpoint verifies the repository
  belongs to that account)
- `X-DevSup-Schema-Version: 1`
- JSON body matching `IngestFailureRequest`:

```json
{
  "repositoryId": "00000000-0000-0000-0000-000000000000",
  "statusCode": 500,
  "method": "GET",
  "path": "/api/orders",
  "requestPayload": "...",
  "responsePayload": "...",
  "exceptionMessage": "...",
  "stackTrace": "..."
}
```

```js
// Node/Express sketch
app.use(async (req, res, next) => {
  try {
    await next();
    if (res.statusCode >= 400) report(req, res.statusCode);
  } catch (e) { report(req, 500, e); throw e; }
});

function report(req, statusCode, e) {
  fetch(process.env.DEVSUP_URL + "/api/ingest", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Authorization": "Bearer " + process.env.DEVSUP_TOKEN,
      "X-DevSup-Schema-Version": "1"
    },
    body: JSON.stringify({
      repositoryId: process.env.DEVSUP_REPO_ID,
      statusCode, method: req.method, path: req.originalUrl,
      exceptionMessage: e?.message, stackTrace: e?.stack
    })
  }).catch(() => {}); // never let reporting break the request
}
```

Strip `Authorization`, `Cookie` and `X-Api-Key` before sending (the .NET SDK does this
for you). If your reported `X-DevSup-Schema-Version` is newer than the platform
supports, ingest replies `426 Upgrade Required` rather than misinterpreting the event.

## 4. Architecture

DevSup is split by responsibility so the platform, the agent, and the instrumentation
package stay independently deployable and testable.

| Component | Project | Responsibility |
|---|---|---|
| **Web platform / API** | `DevSup.Api` | Accounts, OAuth connections, repo & branch configuration, AI key management, failure ingest endpoint, email dispatch |
| **Domain core** | `DevSup.Core` | Entities, enums, and pure logic/triage (classifier) — zero infra dependencies |
| **Infrastructure** | `DevSup.Infrastructure` | EF Core + SQLite (dev; PostgreSQL later), git provider adapters (GitHub/GitLab), AI model clients (BYO key), SMTP transport, key encryption at rest |
| **Agent / repair engine** | `DevSup.Agent` | Background worker: consumes triaged failures, drives the investigate → patch → push loop |
| **Instrumentation package** | `DevSup.Instrumentation` | NuGet middleware dropped into the customer's app; captures failures and reports them (separate repo in the future if needed) |
| **Tests** | `DevSup.Tests` | Unit + integration tests |

**Concurrency & durability:** failure ingest is fire-and-forget from the app's
perspective (a broken DevSup must never break the customer's app). Ingested events land
in the DB; the agent worker adopts them idempotently (one ticket per failure), retries
with backoff, and surfaces status changes as email outbox rows.

## 5. Code errors vs. not-code errors

Not every failure should trigger a code fix. The classifier (`FailureClassifier` in
`DevSup.Core`) decides whether to engage the agent or skip and flag:

| Category | Examples | Action |
|---|---|---|
| **Code error** | `NullReferenceException`, SQL/DB timeouts, misconfigured access, missing records, invalid business logic | Agent investigates & fixes |
| **Not-code error** | Wrong login credentials (401), unsupported client, corporate proxy/TLS, API rate limiting (429), downstream 5xx | **Skipped** + flagged, emailed as informational |

The classifier signals `SkippedNotCodeError` for those ticket statuses so the user sees
"this isn't a code bug" instead of a useless attempted patch.

## 6. Bring-your-own AI keys

Users supply their own model API keys — **Claude (Anthropic), Gemini (Google),
DeepSeek, OpenAI, or a local Ollama endpoint**. Keys are bound per
user/provider/model, encrypted at rest under `Security:DataProtectionKey`, and never
stored in plain text or surfaced to the UI — listing shows only a `••••abcd`-style
mask. The agent resolves the repo owner's key binding, decrypts it in memory only, and
favours the model for investigation and patch generation.

The repair loop tries **model first**: if the model returns a patch suggestion, the
same verified-verbatim gate as templates applies (exact fragment → replacement, no
trusted-in-broken-diffs). When the model returns nothing usable, DevSup falls back to
`.devsup/repairs.json` template repairs, and only then escalates to a human.

- `GET /api/ai-keys` — list your key bindings (masked, never plaintext).
- `POST /api/ai-keys` — add or update a binding (`provider`, `model`, `key`).
- `DELETE /api/ai-keys?provider=&model=` — remove a binding.

Provider wire shapes (OpenAI-compatible chat for OpenAI/DeepSeek/Ollama, Anthropic
Messages, Gemini `generateContent`) and default endpoints are built in; the
`AiModels:<Provider>:BaseUrl/Model/TimeoutSeconds` config section overrides them — the
default `Ollama` entry already points at `http://localhost:11434`.

## 7. Email notifications

An email outbox (`EmailMessage`) decouples notification from transport:

1. **Error detected** — status code, endpoint, brief context, and a recommended path forward.
2. **Fix ready / pushed** — commit SHA, patch summary, and the ticket link.
3. **Not a code error** — why it was skipped.

Sending is the responsibility of `DevSup.Infrastructure` (SMTP first; transactional
providers later). Failed sends are retried, never silently dropped.

### Accounts & passwords (v0.12)

Self-service is self-service with a paper trail. `GET /api/account` returns your profile
(email, display name, admin/active flags, digest opt-out state, digest cadence, created
at); `PUT /api/account` updates your display name and optionally toggles
`digestEnabled` and/or sets `digestFrequency` (`"daily"` / `"weekly"`); `POST
/api/account/password` verifies the current password and sets a new
one (min 8 characters). Both write operations are persisted to the audit trail
(`account.profileUpdate`, `account.passwordChange`).

### Email outbox visibility, retry & daily digests (v0.13)

The email outbox is no longer a black box:

- `GET /api/emails` — your messages, newest first, paginated
  (`page` / `pageSize` ≤ 100), optionally filtered with `sent=true|false`.
- `POST /api/emails/{id}/retry` — re-queues a failed message (resets attempt count and
  last error). Already-sent messages reject retry with `409`. Audited as `email.retry`.
- **Daily digests** — a background worker (`Digests:Enabled` default on, `Digests:IntervalHours`
  default 24) mails each **active** user one summary per interval: failures detected,
  open repair tickets (listed), and fixes pushed within the window. Users with repos but
  no activity get nothing — no empty digests. Digests ride the normal email outbox, so
  they obey SMTP retries like everything else. Digests are **global opt-out per user**:
  `PUT /api/account` with `digestEnabled: false` silences the daily summary while
  leaving transactional incident emails untouched. Since v0.23 a digest that includes
  shared repositories splits its counts into **Your repositories** and **Shared with
  you** sections, so it's clear where the activity came from. Since v0.24 each user
  picks a **cadence** — `digestFrequency: "daily"` (24 h) or `"weekly"` (168 h). The
  worker tracks `LastDigestSentAt` per user, skips anyone whose interval hasn't
  elapsed, and widens the reporting window to match the cadence (a weekly digest looks
  back seven days).

The dashboard **Delivery center** renders the email outbox with one-click retry, and a
`Ping` button per webhook endpoint fires a signed `devsup.ping`.

### Fix verification (v0.14)

`FixVerified` status is earned, not assumed. The **verification worker** (`Verification:`
config, default on) sweeps tickets that reach `FixPushed` and re-probes the repository's
app URL once the fix has had time to deploy (`Verification:ProbeDelayMinutes` default 2).
A healthy probe flips the ticket to `FixVerified`, stamps its updated time and mails the
owner ("fix verified on `GET /api/orders`"). Unhealthy or unprobeable — no change, and a
slow deploy keeps its window (`Verification:WindowMinutes` default 30) to come up. The
dashboard casts `fixVerified` as **Fixed**.

### Per-webhook delivery log (v0.14)

Each webhook endpoint in the dashboard now has a **Log** button. It expands the delivery
history (event, state, attempts, created time, last error) straight from
`GET /api/webhooks/{id}/deliveries`, and failed deliveries carry a one-click **Retry**
button pointed at `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry` — the same
audited operation available via the API.

### Notification preferences (v0.11)

Email is opt-out per repository. `GET /api/notification-preferences` lists your
preferences; `PUT /api/notification-preferences` upserts one. Each preference has an
`emailEnabled` master switch (default on) and a `mutedEvents` list that mutes specific
events *per event* — `failureDetected`, `notCodeError`, `fixPushed`, `fixPendingReview`,
`needsHumanReview`. A missing preference row means "all emails on". Preferences gate
**email only**; webhook fan-out is governed by each endpoint's own `events` mask, so you
can silence your inbox without silencing your Slack channel. The dashboard's
**Preferences** panel edits the same settings with checkboxes.

## 8. Webhooks, channels & health checks

### Webhook notifications & channels

Every ticket transition can be mirrored to your own HTTP endpoints. Register an
endpoint with `POST /api/webhooks` and receive an **HMAC-SHA256 signature** you must
store — the signing secret is returned once and never re-exposed; it is stored
encrypted at rest.

Each delivery `POST`s a JSON payload with headers:

- `X-DevSup-Signature` — `sha256=<hex>` HMAC over the raw body using your secret
- `X-DevSup-Event` — camelCase event name (`failureDetected`, `fixPushed`,
  `fixPendingReview`, `needsHumanReview`, `notCodeError`), or `ping` for test payloads

Endpoints declare a `channel`:

| Channel | Payload |
|---|---|
| `http` (default) | The raw DevSup event JSON, byte-for-byte as queued |
| `slack` | Incoming-webhook message with mrkdwn `blocks` (text `*DevSup {event}*`, failure/repo/ticket fields) |
| `teams` | Office 365 **MessageCard** (`@type: MessageCard`, theme color, facts table) |

Slack/Teams bodies are formatted at delivery time from the same stored event payload;
the HMAC signature is always computed over the body actually sent. Give each endpoint
a friendly `name` (e.g. `#incidents on Slack`) and it shows up formatted everywhere.
Events are opt-in per endpoint (`events` list on create, all by default). Failed
deliveries are retried from an outbox (`Webhooks:` interval / `MaxAttempts`, default
8 tries); deleted or deactivated endpoints are drained silently.

#### Repository-scoped subscriptions (v0.27)

By default an endpoint hears about **every** repository you own. Pass a
`repositoryIds` list on create to scope it: the endpoint then only receives events
whose repository is in the list (a scoped endpoint receives nothing for repo-less
events). Scope is stored per endpoint, echoed on `GET /api/webhooks`, and only
repositories you own may be listed (`400` otherwise). The dashboard's webhook form has
a multi-select for this — leave it empty for all repositories — and each endpoint's row
shows its scope (`all repositories` / `N repo(s)`).

### External app-URL health checks

For each repository you optionally provide the URL of the app/backend it serves
(`appUrl`). A background prober (`HealthChecks:` interval, default 300 s) does a
`GET` on that URL: a non-2xx, timeout, or transport error marks the repository
**unhealthy**, and the first transition into an unhealthy state is ingested as a
failure event (`Source=appHealthCheck`) — classified, ticketed, emailed, and mirrored
to webhooks exactly like an SDK-reported failure. Consecutive failures don't create
duplicate tickets; recovery to healthy is recorded silently.

The `GET /api/overview` endpoint (and the dashboard) aggregates this health state and
your ticket counts across **all** repositories, so one home screen covers the whole
fleet.

### Pausing repository monitoring (v0.16)

Sometimes you need a repo to stop raising noise — a hotfix window, an upstream outage,
a repo you no longer care about but want to keep listed. `POST
/api/repositories/{id}/pause` flips a repository to **paused** (`Paused` flag +
`PausedAt`, migration `AddRepositoryPaused`); `POST /api/repositories/{id}/unpause`
flips it back. While paused, three pipelines go quiet: the health checker skips the
repo's app URL, `POST /api/ingest` rejects new failures with `409`, and the repair
worker won't pick up its tickets. Existing tickets, failures and webhook history stay
visible, and the digest excludes paused repositories. Both transitions are audited
(`repository.pause` / `repository.unpause`), the dashboard shows a **Paused** badge
with **Pause / Resume** buttons per repository.

### Webhook pause & resume (v0.16)

The dashboard and API let you take an endpoint out of rotation without deleting it.
`POST /api/webhooks/{id}/deactivate` marks it inactive and `POST
/api/webhooks/{id}/activate` restores it (each audited; double toggles return `409`).
Inactive endpoints stay listed and keep their history but receive nothing — the
delivery outbox drains them silently, exactly like deleted endpoints.

### Archiving repositories (v0.20)

Pause is temporary; **archive** is retirement. `POST /api/repositories/{id}/archive`
sets `Archived` + `ArchivedAt` (migration `AddRepositoryArchived`) and takes the
repository off the active surface: it disappears from the repository list, the
overview and the failure/ticket feeds, and every background pipeline skips it (health
probes, repair pick-up, fix verification and digests). `POST
/api/repositories/{id}/unarchive` brings it back exactly as it was — tickets, failures
and health state are retained, never deleted. `GET /api/repositories?archived=true`
lists retired repos so the owner can restore them. Ingest to an archived repo is
rejected with `409`, and the dashboard keeps an **Archived** section with one-click
**Restore**. Both transitions are audited (`repository.archive` /
`repository.unarchive`) and are owner-only. Archived repositories are hidden from
**shared members** too — only the owner sees the archived list.

### Bulk repository actions (v0.22)

Fleet-scale housekeeping: `POST /api/repositories/bulk` applies one lifecycle action —
`pause`, `unpause`, `archive` or `unarchive` — to up to 100 repositories in a single
call (`{ action, repositoryIds }`). It returns a **per-repository result**: `ok`
(changed), `unchanged` (already in the desired state — bulk operations are forgiving,
not `409`s), `forbidden` (you don't own it) or `notFound`. Only repositories you own
are acted on; shared repositories you merely belong to are reported `forbidden` and
left alone. Each change is audited exactly like the single-repository endpoint, so it
shows up in that repository's activity feed. The dashboard's repositories table gains
selection checkboxes and a **Pause / Resume / Archive** bulk bar (only owned rows are
selectable).

## 9. Event replay & re-dispatch

Some failures deserve a second look — a transient SMTP outage, a receiver that was
down at notify time, an agent that stalled mid-repair.

- `POST /api/failures/{failureId}/replay` — re-runs the full notification fan-out
  (email + webhook) for an existing failure event from your own repositories, exactly
  once per call, using the same templates and payloads as the original.
- `POST /api/tickets/{ticketId}/redispatch` — returns a `new` or `needsHumanReview`
  repair ticket to the **New** state so the repair worker picks it up again. Tickets
  that are already fixed, pending review, or closed are rejected with `409`.

Both are scoped to the authenticated user's repositories — their own or, since v0.17,
ones shared with them.

### Ticket detail & triage (v0.15)

Tickets are inspectable and actionable without guessing:

- `GET /api/tickets/{ticketId}` — full incident detail: repository, `METHOD path` with
  HTTP status, exception message, category/kind, agent analysis, patch summary, commit
  /pull-request links, occurrence and last-updated timestamps.
- `POST /api/tickets/{ticketId}/close` — marks a ticket `closed` (409 when it already
  is), audited as `ticket.close`.
- `POST /api/tickets/{ticketId}/reopen` — returns a closed ticket to `new` so the repair
  worker can attack it again (409 unless closed), audited as `ticket.reopen`.

Scoped to the authenticated user's accessible repositories (own or shared since v0.17)
like every ticket route. The dashboard's **View** button on each ticket row opens the
full incident; **Close ticket** / **Reopen ticket** act on it immediately, and each
change is written to the audit trail.

### Teams & shared repositories (v0.17)

Repositories can be shared with other DevSup users. The owner invites a teammate by
email with a role — `operator` (full triage access) or `observer` (read access):

- `POST /api/repositories/{id}/members` — invite (`{ email, role }`); re-inviting an
  existing member **updates their role**. Unknown or unregistered emails return `404`
  (you can't invite ghosts; you also can't share with yourself).
- `GET /api/repositories/{id}/members` — the member list (needs any access; includes
  `owner: true/false` so the dashboard can show controls only to the owner).
- `DELETE /api/repositories/{id}/members/{userId}` — revoke access.

A shared member sees the repository everywhere a team member should: the overview,
repository list, tickets (including detail/close/reopen), failure history + CSV export,
notification preferences, and the daily digest. Because a shared repository is still
*one* repo, two safety rails hold: **failure ingest** (`POST /api/ingest`) and the
**repair agent's pick-up** remain owner-only, and — like every tenant boundary in
DevSup — these read paths are cross-tenant views, not cross-tenant writes (a member
triaging a ticket still uses their own identity and the repo's real owner is the one
notified). The audit trail records `repository.share` / `repository.unshare` with the
invited email and role.

### Role enforcement & collaborative notifications (v0.18)

Roles are **enforced at the API boundary**, not just stored. Read access is universal
across a shared repository, but triage writes are gated: `POST
/api/tickets/{id}/close`, `/reopen`, and `/redispatch` succeed for the owner **or
operator members** and return `403` for **observers**. The ticket-detail response
carries `canTriage: true/false` so the dashboard only renders **Close ticket** /
**Reopen ticket** actions the caller is allowed to perform.

Collaboration means being kept in the loop. **Operator members are copied on incident
emails**: failure-detected and not-a-code-error notices (the ingest and replay fan-out)
plus fix-pushed / pull-request-opened / needs-review messages from the repair agent —
each member's copy rides their own `emailEnabled` / `mutedEvents` notification
preferences. Observers stay quiet, and webhook fan-out remains scoped to the owner's
endpoints.

### Ownership transfer & member self-service (v0.19)

Teams change, and access should follow. Two routes keep sharing honest:

- `POST /api/repositories/{id}/transfer` — the owner hands the repository to an
  **existing operator member** (`{ email }`). The new owner takes over monitoring,
  member management and app configuration; the **previous owner is demoted to an
  operator member** so they keep working until they choose to leave. Transferring to an
  observer, a non-member, or someone who already owns a repo with the same clone URL is
  rejected (`400` / `409`). The new owner gets a "you now own …" email reminding them to
  link a git provider token.
- `POST /api/repositories/{id}/leave` — any member removes **themselves** from a shared
  repository (audited as `repository.leave`). Owners can't leave their own repo — they
  must transfer it first (`409`).

Both transitions are audited (`repository.transfer` / `repository.leave`). In the
dashboard's **Members** panel the owner sees a **Transfer** button on each operator and
a **Leave repository** button appears for members viewing a repo they don't own.

## 10. Platform admin & data retention

### Platform admin (multi-tenant)

Admins are a special class of user who can see and manage the whole fleet. On startup,
DevSup **idempotently promotes** every account whose email appears in the
comma-separated `Admin:Emails` configuration to `IsAdmin = true`, so the first admin is
always created the moment that account registers.

- `GET /api/admin/overview` — platform totals: users (total/active), repositories,
  failures, open tickets, webhook endpoints, plus a **health breakdown**
  (healthy / unhealthy / unchecked / paused / archived repositories).
- `GET /api/admin/users` — every user with `isAdmin`, `active`, and per-user
  repository/ticket counts.
- `GET /api/admin/repositories` — cross-tenant fleet view (v0.25): every repository
  with its owner email, provider, app-health state, pause/archive flags, open-ticket
  count and total failures. Paginated (`page` / `pageSize` ≤ 200), filterable by
  `health` (`healthy` / `unhealthy` / `unchecked`), `owner` email, `paused` and
  `archived`.
- `POST /api/admin/users/{id}/deactivate` and `.../activate` — suspend / restore a
  tenant. A deactivated account can no longer sign in (`403` at login).

Admin endpoints are authorization-guarded per request (a user must be `IsAdmin`), so a
non-admin always gets `403` regardless of route knowledge. In v0.11 the **admin console**
moved into the dashboard: an admin sees an `Admin` section in `/dashboard/` to
suspend/restore users, page through the audit trail, and review + CSV-export failure
history without touching the API directly. Since v0.25 the console also carries a
**Fleet health** panel — a health summary plus the per-repository table described above.

### Event retention & archiving

Historical failure data grows forever unless pruned. A background worker
(`Retention:`) sweeps on `IntervalHours` (default 24) and deletes records older than
`WindowDays` (default 365) in batches of `BatchSize` (default 500):

- **Expired** `FailureEvents` and their `RepairTickets` are removed entirely.
- **Webhook deliveries** older than the window are removed (endpoints themselves stay).
- **Emails** older than the window are removed only if already sent — unsent outbox rows
  are never deleted, so a genuinely pending notification is never dropped.

Setting `Retention:WindowDays = 0` disables purging entirely.

#### Per-repository overrides (v0.26)

The global window is a default, not a mandate. `PUT /api/repositories/{id}/retention`
(`{ retentionDays }`, owner-only, audited as `repository.retention`) sets a per-repo
policy:

- `null` — **inherit** the global `Retention:WindowDays`.
- `0` — **keep forever**; this repository's failures/tickets are never purged.
- `> 0` — **custom window**; history older than that many days is purged even if the
  global window is longer (or shorter).

The override governs that repository's `FailureEvents`/`RepairTickets`; webhook
deliveries and sent emails remain global (they aren't repository-scoped). A negative
value is rejected with `400`. The dashboard's repositories table exposes a
**Retention** button per owned repository.

## 11. Audit & failure history

### Write-audit trail

Every sensitive action is persisted to an `AuditEntry` (actor email, action, entity,
before/after summary, timestamp) so platform admins can answer "who did what, when":

- `user.login` — every successful account sign-in
- `repository.connect`, `webhook.create`, `webhook.delete`, `webhook.rotate`,
  `aiKey.create`, `aiKey.update`, `aiKey.delete` — configuration writes
- `user.deactivate`, `user.activate` — admin account changes
- `account.profileUpdate`, `account.passwordChange` — self-service profile/password changes
- `webhook.test`, `webhook.retry`, `webhook.retryAll` — delivery operations
- `webhook.activate`, `webhook.deactivate` — endpoint pause/resume
- `repository.pause`, `repository.unpause` — monitoring pause/resume
- `repository.archive`, `repository.unarchive` — repository retirement/restore
- `repository.share`, `repository.unshare` — repository sharing (invite / revoke)
- `repository.transfer`, `repository.leave` — ownership hand-off and member self-removal
- `email.retry` — re-queuing a failed outbound email
- `ticket.close`, `ticket.reopen` — ticket lifecycle (triage) operations

`GET /api/admin/audit` (admin-only) lists the latest 200 entries, filterable by
`actor`, `action`, and `entityType`. Failure ingestion is deliberately **not** audited
to avoid noise.

### Repository activity feed (v0.21)

The audit trail isn't just for admins. `GET /api/repositories/{id}/activity` returns a
repository-scoped timeline (`limit`, default 50, max 200) that any member — owner or
shared observer/operator — can read. It gathers the repo's own lifecycle entries
(`repository.connect` / `pause` / `unpause` / `archive` / `unarchive` / `transfer`), its
member changes (`repository.share` / `repository.unshare` / `repository.leave`), and
ticket triage on its incidents (`ticket.close` / `ticket.reopen`). Each item carries the
**actor email**, action, before/after detail and timestamp, newest first. Entries are
strictly filtered to the requested repository, so a member never sees activity from
other repositories. The dashboard's **Activity** button opens the same timeline, and
`GET /api/repositories/{id}/activity/export` streams it as a UTF-8 CSV
(`timestampUtc,action,actorEmail,before,after`) — the **Export CSV** button in the
panel downloads it.

### Failure history & CSV export

`GET /api/failures` returns a paginated history of your failure events joined to their
repair tickets, newest first. Filters: `repositoryId`, `from` / `to` (UTC timestamps),
`status` (ticket status), `page`, `pageSize` (max 100). `GET /api/failures/export`
streams the same filtered view as a UTF-8 CSV (RFC-4180 escaping) with one row per
failure — ideal for reviewing or archiving before retention purges it.

### Webhook secret rotation

Suspect a leaked signing secret? `POST /api/webhooks/{id}/rotate` replaces the stored
secret and returns the new value — once, exactly like creation — without touching the
endpoint's URL, channel, or event subscriptions. All subsequent deliveries are signed
with the new secret.

### Webhook delivery log

`GET /api/webhooks/{id}/deliveries` (ownership-scoped) returns the delivery history for
one endpoint — every event fanned out to it with its status (`queued` / `delivered` /
`failed`), attempt count, last HTTP status/error, HMAC signature, and timestamps.
Newest first, paginated (`page` / `pageSize`, max 100). The same data the admin console
and email outbox draw on, per endpoint.

### Testing & retrying (v0.12)

- `POST /api/webhooks/{id}/test` — fires a synthetic **`devsup.ping`** through the full
  delivery path: queued as a normal outbox row, signed with the endpoint secret, POSTed
  to the URL exactly like a real event (Slack/Teams formatted per channel). Great for
  confirming the receiver parses your HMAC headers.
- `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry` — re-queues a failed delivery:
  resets its attempt count and last error so the outbox worker picks it up again, without
  touching the endpoint or its secret. Already-sent deliveries reject retry with `409`.
- `POST /api/webhooks/{id}/deliveries/retry-all` (v0.28) — the bulk form: re-queues
  **every** failed delivery for the endpoint in one call, returning the count re-queued.
  Handy after an outage at the receiver. Ownership-scoped and audited as
  `webhook.retryAll`.

All three are ownership-scoped like every other webhook operation, and all are audited
(`webhook.test`, `webhook.retry`, `webhook.retryAll`). The dashboard delivery log shows
a **Retry all failed** button whenever the endpoint has pending deliveries.

## 12. Security & sanitization

- **Payload scrubbing** at capture time: `Authorization`, `X-Api-Key`, `Cookie`,
  `Set-Cookie` headers and secrets stripped before a failure event is stored.
- **Key encryption**: AI keys and provider tokens encrypted at rest (DPAPI / envelope
  encryption; bring your own KMS later).
- **Repo access**: the agent uses a scoped token for the connected repo only
  (fine-grained PAT/per-deploy key), never full account access.
- **Human approval surface**: statuses flow *New → Triaged → Investigating →
  PatchProposed → FixPushed → FixVerified → Closed* so every auto-mutation is visible
  and reversible.

## 13. Repository layout

```
devsup/
├─ src/
│  ├─ DevSup.Api/            # ASP.NET Core web API + ingest endpoint
│  ├─ DevSup.Core/           # domain models, enums, failure classifier
│  ├─ DevSup.Infrastructure/ # EF Core, git/AI/email adapters
│  ├─ DevSup.Agent/          # background repair worker
│  └─ DevSup.Instrumentation/# NuGet middleware for customer apps
├─ tests/
│  └─ DevSup.Tests/          # xunit tests
└─ README.md
```

## 14. Data model (EF Core + SQLite default / PostgreSQL optional, migrations applied at startup)

- `Users` — account, email, display name, **PBKDF2 password hash**, `IsAdmin` flag, `Active` (suspension) flag, digest opt-out (`DigestEnabled`) + cadence (`DigestFrequency`, `LastDigestSentAt`), created timestamp
- `ConnectedRepositories` — provider, clone URL (unique per user), branch, optional app URL, live app-health state (`AppHealthy`, `AppHealthCheckedAt`, `AppHealthLastError`), pause state (`Paused`, `PausedAt`), archive state (`Archived`, `ArchivedAt`), retention override (`RetentionDays`)
- `RepositoryMembers` — cross-tenant shares (repository + user composite key, role `observer`/`operator`, created at)
- `AiModelKeyBindings` — user, provider, model, encrypted key, display mask**
- `FailureEvents` — method, path, status, request/response payload, exception, stack, timestamp
- `RepairTickets` — category, kind, status, analysis, patch summary, commit SHA, last agent error, optional PR/MR URL
- `EmailMessages` — outbox (to, subject, html body, sent, created at)
- `WebhookEndpoints` — user, destination URL, channel (`http`/`slack`/`teams`), event mask, **encrypted signing secret**, repository scope (`RepositoryIds`), active
- `WebhookDeliveries` — outbox (webhook, event, payload, attempts, last error, sent)
- `AuditEntries` — write-audit trail (actor, action, entity, before/after, IP, timestamp)
- `NotificationPreferences` — per-user, per-repository email delivery preferences (`EmailEnabled` master switch + per-event `MutedEmailEvents` bitmask)

Every table is mapped in `DevSup.Infrastructure/Persistence/DevSupDbContext.cs` with the
schema shipped as EF Core migrations (`InitialCreate`,
`AddEmailOutboxRetriesAndOAuthTokens`, `AddRepairTicketLastError`,
`AddAiModelKeyMaskUpdatedAtUniqueIndex`, `AddRepairTicketPullRequestUrl`,
`AddWebhookNotifications`, `AddRepositoryHealthChecks`, `AddWebhookChannelAndName`, `AddUserAdminAndActive`, `AddAuditEntries`, `AddNotificationPreferences`, `AddRepositoryPaused`, `AddRepositoryMembers`, `AddRepositoryArchived`, `AddUserDigestFrequency`, `AddRepositoryRetention`, `AddWebhookRepositoryScope`).

### The repair agent (v0.4)

A background worker (`DevSup.Agent/Repair/RepairWorker`) sweeps on an interval (default
20 s, configurable under `Repairing:`) and hands each **New, code-error** ticket to
`RepairProcessor`, which walks it through:

1. **Adopt** — only tickets still `New` are picked up (idempotent; one claim per ticket).
2. **Investigate** — resolves the owner's linked provider token (decrypted in memory
   only), clones the connected repo into a temp workspace, and runs the repair chain.
3. **Patch (AI first, templates second)** — if the owner has bound a model key, the
   provider for that binding is asked for a surgical patch; otherwise, or when the model
   returns nothing usable, the default `HeuristicRepairProvider` reads
   `.devsup/repairs.json` from the checked-out repo and applies a single,
   *verified-verbatim* string replacement in the targeted file (path-traversal guarded).
   It refuses ambiguous, missing, or unparsable repairs rather than risk a broken diff.
4. **Commit & push** — the change is committed and pushed to the repo's default branch
   under the token the user linked during OAuth (`https://x-access-token:<token>` on the
   clone URL, never persisted or logged). The short checkout is cleaned up afterwards.
5. **Report** — the ticket moves to `FixPushed` (with commit SHA + patch summary) or
   `NeedsHumanReview` (with the agent's analysis), and the email outbox gets a
   "fix pushed" or "needs your review" message.

Security: only https clone URLs are eligible, the resolved file must stay inside the
workspace, and no failure is auto-retried forever — anything unexpected lands in
`NeedsHumanReview` with a `LastError`. GitLab clones use the `oauth2:` credential user
(GitHub uses `x-access-token:`), and the token is never written to disk or logs.

### Emails (outbox + SMTP)

Every detected failure enqueues an `EmailMessage` outbox row. A background worker
(`EmailOutboxWorker`) drains the outbox on an interval (default 15 s, configurable under
`Emailing:`), attempts delivery via SMTP (`Smtp:` section in appsettings), and marks the
message `Sent` only on success. Failed sends bump `Attempts` and store `LastError`;
delivery gives up after `Emailing:MaxAttempts` (default 5). Non-code errors get an
explanatory "not a code error — no patch scheduled" email instead of a repair notice.

### GitHub OAuth

- `GET /api/auth/github/login` — redirects to GitHub with a signed state cookie.
- `GET /api/auth/github/callback` — exchanges the code, resolves the profile email,
  creates or links the account, stores the access token encrypted at rest (AES-256-GCM,
  key from `Security:DataProtectionKey`), and returns a JWT.

OAuth only works when the provider's `ClientId` and `ClientSecret` are configured
(`GitHub:` / `GitLab:`); authorize/token/user URLs are overridable per environment.

## 15. Local development

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/DevSup.Api
```

The API exposes `/` as a health check and OpenAPI in Development. SQLite migrations run
automatically at startup (a `devsup.db` file is created next to the repo). Set
`Database:Provider=postgresql` and a `ConnectionStrings:DevSup` PostgreSQL URL to run
the same migration set on PostgreSQL via Npgsql instead.

### API endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET` | `/` | — | Health check |
| `POST` | `/api/users/register` | — | Create account (email, display name, password ≥ 8 chars) |
| `POST` | `/api/users/login` | — | Exchange credentials for a JWT |
| `GET` | `/api/account` | Bearer | View your profile (email, name, admin/active flags) |
| `PUT` | `/api/account` | Bearer | Update your display name and/or digest settings (`digestEnabled`, `digestFrequency`) |
| `POST` | `/api/account/password` | Bearer | Change your password (current password required) |
| `GET` | `/api/auth/github/login` | — | Start GitHub OAuth (redirects to GitHub) |
| `GET` | `/api/auth/github/callback` | — | GitHub OAuth callback → links account, returns JWT |
| `GET` | `/api/auth/gitlab/login` | — | Start GitLab OAuth (redirects to GitLab) |
| `GET` | `/api/auth/gitlab/callback` | — | GitLab OAuth callback → links account, returns JWT |
| `GET` | `/api/repositories` | Bearer | List connected repositories (add `?archived=true` for retired ones) |
| `POST` | `/api/repositories` | Bearer | Connect a repository (provider, clone URL, branch) |
| `POST` | `/api/repositories/{id}/pause` | Bearer | Pause monitoring (health checks, ingest, repair) |
| `POST` | `/api/repositories/{id}/unpause` | Bearer | Resume monitoring |
| `POST` | `/api/repositories/{id}/archive` | Bearer | Retire a repository (hidden from feeds, pipelines skip it) |
| `POST` | `/api/repositories/{id}/unarchive` | Bearer | Restore an archived repository |
| `PUT` | `/api/repositories/{id}/retention` | Bearer | Set history retention (`null` inherit / `0` forever / positive days) |
| `POST` | `/api/repositories/bulk` | Bearer | Apply `pause`/`unpause`/`archive`/`unarchive` to up to 100 owned repos (per-repo results) |
| `GET` | `/api/repositories/{id}/members` | Bearer | List repository members (`owner` flag) |
| `POST` | `/api/repositories/{id}/members` | Bearer | Invite a member (`{ email, role }`; reshare updates role) |
| `DELETE` | `/api/repositories/{id}/members/{userId}` | Bearer | Revoke a member's access |
| `POST` | `/api/repositories/{id}/transfer` | Bearer | Transfer ownership to an operator member (owner becomes operator) |
| `POST` | `/api/repositories/{id}/leave` | Bearer | Leave a shared repository (members only; owners must transfer first) |
| `GET` | `/api/repositories/{id}/activity` | Bearer | Repo-scoped activity timeline (members can read; `limit` ≤ 200) |
| `GET` | `/api/repositories/{id}/activity/export` | Bearer | Activity timeline as UTF-8 CSV |
| `POST` | `/api/ingest` | Bearer | Report a failure; triaged into a repair ticket |
| `GET` | `/api/tickets` | Bearer | List repair tickets for your repositories |
| `GET` | `/api/ai-keys` | Bearer | List your AI key bindings (masked) |
| `POST` | `/api/ai-keys` | Bearer | Add or update an AI key binding |
| `DELETE` | `/api/ai-keys` | Bearer | Delete an AI key binding |
| `GET` | `/api/overview` | Bearer | Cross-repo health + ticket summary (dashboard feed) |
| `GET` | `/api/tickets?repositoryId=` | Bearer | Filter tickets to one repository |
| `POST` | `/api/failures/{failureId}/replay` | Bearer | Re-send email + webhook notifications for a past failure |
| `POST` | `/api/tickets/{ticketId}/redispatch` | Bearer | Return a new/needs-review ticket to the repair queue |
| `GET` | `/api/tickets/{ticketId}` | Bearer | Full incident detail for one ticket (incl. `canTriage`) |
| `POST` | `/api/tickets/{ticketId}/close` | Bearer | Mark a ticket closed (audited) |
| `POST` | `/api/tickets/{ticketId}/reopen` | Bearer | Reopen a closed ticket into the repair queue (audited) |
| `GET` | `/api/failures` | Bearer | Paginated failure history joined to tickets (`repositoryId`, `from`, `to`, `status`, `page`, `pageSize`) |
| `GET` | `/api/failures/export` | Bearer | CSV export of the same filtered failure history |
| `POST` | `/api/webhooks` | Bearer | Register a webhook endpoint (returns the signing secret once; `channel` = http/slack/teams; optional `repositoryIds` scope) |
| `GET` | `/api/webhooks` | Bearer | List webhook endpoints |
| `DELETE` | `/api/webhooks/{id}` | Bearer | Remove a webhook endpoint |
| `POST` | `/api/webhooks/{id}/rotate` | Bearer | Replace the signing secret and receive the new value once |
| `POST` | `/api/webhooks/{id}/deactivate` | Bearer | Pause the endpoint (stops deliveries) |
| `POST` | `/api/webhooks/{id}/activate` | Bearer | Resume the endpoint |
| `GET` | `/api/webhooks/{id}/deliveries` | Bearer | Per-endpoint webhook delivery log (paginated) |
| `POST` | `/api/webhooks/{id}/test` | Bearer | Queue a signed `devsup.ping` test delivery |
| `POST` | `/api/webhooks/{id}/deliveries/{deliveryId}/retry` | Bearer | Re-queue a failed delivery |
| `POST` | `/api/webhooks/{id}/deliveries/retry-all` | Bearer | Re-queue every failed delivery for the endpoint |
| `GET` | `/api/emails` | Bearer | Your email outbox, newest first (`sent` filter, paginated) |
| `POST` | `/api/emails/{id}/retry` | Bearer | Re-queue a failed email message |
| `GET` | `/api/notification-preferences` | Bearer | List your per-repository email delivery preferences |
| `PUT` | `/api/notification-preferences` | Bearer | Upsert a repository's preference (`emailEnabled`, `mutedEvents`) |
| `GET` | `/dashboard/` | — | Self-contained dashboard UI (open in a browser) |
| `GET` | `/api/admin/overview` | Bearer + admin | Platform-wide totals + repository health breakdown |
| `GET` | `/api/admin/repositories` | Bearer + admin | Cross-tenant fleet view (`health`, `owner`, `paused`, `archived` filters; paginated) |
| `GET` | `/api/admin/users` | Bearer + admin | List every user with admin/active flags and per-user counts |
| `POST` | `/api/admin/users/{id}/deactivate` | Bearer + admin | Suspend an account (blocks future sign-in) |
| `POST` | `/api/admin/users/{id}/activate` | Bearer + admin | Restore a suspended account |
| `GET` | `/api/admin/audit` | Bearer + admin | Audit trail, filterable by `actor`, `action`, `entityType` |

Secrets at rest (GitHub access tokens) are encrypted with AES-256-GCM under
`Security:DataProtectionKey`. JWT settings live under `Jwt`. Agent cadence and the commit
identity used for pushes live under `Repairing:` (`IntervalSeconds`, `BatchSize`,
`GitUserName`, `GitUserEmail`). Retention sweep cadence lives under `Retention:`
(`WindowDays`, `IntervalHours`, `BatchSize`) and admin bootstrapping under `Admin:Emails`.
Daily email digests are configured under `Digests:` (`Enabled`, `IntervalHours`,
`MaxOpenTickets`), and fix verification under `Verification:` (`Enabled`,
`IntervalSeconds`, `ProbeDelayMinutes`, `WindowMinutes`, `ProbeTimeoutSeconds`).
Override any of these via
configuration/environment in a real deployment — the checked-in values are for
development only.

### Docker

```bash
docker build -t devsup-api .
docker run --rm -p 8080:8080 devsup-api
```

### CI

`.github/workflows/ci.yml` runs `restore` → `build` → `test` in Release on every
push/PR to `master`.

## 16. Roadmap

- **v0.1** — solution scaffold, domain model, failure classifier + tests
- **v0.2** *(done)* — SQLite persistence + migrations, JWT accounts, connect repo, ingest endpoint, classifier triage, tickets, email outbox
- **v0.3** *(done)* — SMTP outbox delivery with retries, GitHub OAuth + encrypted token storage, not-a-code-error emails
- **v0.4** *(done)* — agent repair loop: investigate → template patch → commit → push, ticket status updates, "fixed"/"needs review" emails
- **v0.5** *(done)* — BYO AI keys (Claude/Gemini/DeepSeek/OpenAI/Ollama), GitLab support, key management API
- **v0.6** *(done)* — consumer versioning of the middleware (`X-DevSup-Schema-Version`), payload sanitization hardening, PR-based (opt-in) repair flow
- **v0.7** *(done)* — webhook notifications (HMAC-signed), external app-URL health checks, cross-repository `/api/overview`, dashboard UI at `/dashboard/`
- **v0.8** *(done)* — event replay + ticket re-dispatch, Slack/Teams notification channels, configurable PostgreSQL provider
- **v0.9** *(done)* — platform admin role + account suspension, dashboard webhook channel/name routing, event retention worker
- **v0.10** *(done)* — write-audit trail, paginated failure history with date/repo/status filters and CSV export, webhook secret rotation
- **v0.11** *(done)* — operator console: admin console in the dashboard, per-endpoint webhook delivery log, per-repository notification preferences
- **v0.12** *(done)* — self-service & delivery ops: account profile/password API (audited), webhook ping test + failed-delivery retry, notification-preferences panel in the dashboard
- **v0.13** *(done)* — delivery center: email outbox log + retry API, scheduled daily digest emails, Delivery center panel with outbox retry and webhook ping in the dashboard
- **v0.14** *(done)* — repair verification & delivery deep-dive: probe-confirmed `FixVerified` with confirmation emails, per-user digest opt-out, per-webhook delivery log with retry in the dashboard
- **v0.15** *(done)* — incident triage & account controls: ticket detail + close/reopen API (audited), dashboard incident view with triage actions, Account panel with daily-digest toggle
- **v0.16** *(done)* — monitoring controls: pause/unpause repositories (health checks, ingest, repair skip; migration `AddRepositoryPaused`), webhook activate/deactivate API + dashboard Pause/Resume buttons per repo and webhook
- **v0.17** *(done)* — teams & shared repositories: owners invite/revoke members by role, shared members see the repo across overview/tickets/failures/preferences/digest while ingest + repair stay owner-only; dashboard Members panel (invite form + revoke), migration `AddRepositoryMembers`
- **v0.18** *(done)* — role enforcement & collaborative notifications: operator members triage (close/reopen/redispatch) while observers get `403`; ticket detail reports `canTriage` and the dashboard hides triage actions accordingly; operator members are copied on incident + fix-status emails (per their own notification preferences)
- **v0.19** *(done)* — ownership transfer & member self-service: owner hands a repo to an operator member (previous owner demoted to operator, new owner emailed), members can leave a shared repo; both audited, dashboard Transfer/Leave controls
- **v0.20** *(done)* — repository archiving: archive retires a repo from the repository list, overview, failure/ticket feeds and all background pipelines (health, repair, verification, digest) while retaining history; ingest returns `409`, `?archived=true` lists retired repos, dashboard Archived section restores them; audited, migration `AddRepositoryArchived`
- **v0.21** *(done)* — repository activity feed: `GET /api/repositories/{id}/activity` returns a repo-scoped audit timeline (lifecycle, member changes, ticket triage) with actor/before/after/timestamp, readable by owners and shared members and strictly isolated per repository; dashboard Activity panel
- **v0.22** *(done)* — bulk repository actions: `POST /api/repositories/bulk` applies pause/unpause/archive/unarchive to up to 100 owned repos with per-repo results (ok/unchanged/forbidden/notFound) and per-repo audits; dashboard selection checkboxes + bulk action bar
- **v0.23** *(done)* — reporting: daily digests group activity into "Your repositories" vs "Shared with you" sections; repository activity feed gains a CSV export (`GET /api/repositories/{id}/activity/export`) with a dashboard Export CSV button
- **v0.24** *(done)* — digest cadence: per-user `digestFrequency` (daily/weekly) with `LastDigestSentAt` tracking; the worker skips users whose interval hasn't elapsed and widens the window to the cadence (weekly = 7 days); dashboard Account panel cadence selector; migration `AddUserDigestFrequency`
- **v0.25** *(done)* — admin fleet health: `GET /api/admin/repositories` cross-tenant view (owner, health, pause/archive, open tickets, failures; health/owner/paused/archived filters) and a health breakdown on `GET /api/admin/overview`; dashboard admin console Fleet health panel
- **v0.26** *(done)* — per-repository retention overrides: `PUT /api/repositories/{id}/retention` (inherit / keep-forever / custom days), the retention worker honours each policy for that repo's failures + tickets, audited `repository.retention`, dashboard Retention control; migration `AddRepositoryRetention`
- **v0.27** *(done)* — repository-scoped webhooks: optional `repositoryIds` scope on `POST /api/webhooks`; scoped endpoints receive only their repositories' events (unscoped = all), scope echoed on list, validated to owned repos, dashboard multi-select; migration `AddWebhookRepositoryScope`
- **v0.28** *(done)* — webhook retry-all: `POST /api/webhooks/{id}/deliveries/retry-all` re-queues every failed delivery for an endpoint (audited `webhook.retryAll`), dashboard Retry all failed button

---

Built with **.NET 10**, **ASP.NET Core**, **EF Core + SQLite** (dev; PostgreSQL planned),
**JWT auth (PBKDF2)**, **AES-256-GCM secret encryption**, **GitHub OAuth**, **SMTP**,
**the git CLI for agent pushes**, **xUnit**, and **GitHub Actions**.