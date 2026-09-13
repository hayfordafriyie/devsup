# DevSup

**AI-driven API failure detection & auto-repair.** DevSup watches your production API,
captures failures as they happen, dispatches an AI agent to investigate your code and
push a fix, and emails you at every step — so you get notified of the error and its fix,
instead of digging through logs.

> Project status: **v0.5** — on top of v0.4 (the repair agent drives
> investigate → patch → commit → push) and v0.3/v0.2 (SMTP outbox, GitHub OAuth,
> SQLite, JWT accounts, tickets), you can now **bring your own AI model keys**
> (OpenAI, Anthropic Claude, Google Gemini, DeepSeek, or a local Ollama endpoint)
> through a key-management API, GitLab is supported for both OAuth connections and
> agent pushes, and the agent hands every ticket to the model first, falling back to
> `.devsup/repairs.json` templates only when the model has nothing safe to offer.
> 55 tests passing.

---

## Table of contents

1. [What it does](#1-what-it-does)
2. [The problem](#2-the-problem)
3. [How it works end-to-end](#3-how-it-works-end-to-end)
4. [Architecture](#4-architecture)
5. [Harry vs. machine-readable errors](#5-code-errors-vs-not-code-errors)
6. [Bring-your-own AI keys](#6-bring-your-own-ai-keys)
7. [Email notifications](#7-email-notifications)
8. [Security & sanitization](#8-security--sanitization)
9. [Repository layout](#9-repository-layout)
10. [Data model](#10-data-model)
11. [Local development](#11-local-development)
12. [Roadmap](#12-roadmap)

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

## 8. Security & sanitization

- **Payload scrubbing** at capture time: `Authorization`, `X-Api-Key`, `Cookie`,
  `Set-Cookie` headers and secrets stripped before a failure event is stored.
- **Key encryption**: AI keys and provider tokens encrypted at rest (DPAPI / envelope
  encryption; bring your own KMS later).
- **Repo access**: the agent uses a scoped token for the connected repo only
  (fine-grained PAT/per-deploy key), never full account access.
- **Human approval surface**: statuses flow *New → Triaged → Investigating →
  PatchProposed → FixPushed → FixVerified → Closed* so every auto-mutation is visible
  and reversible.

## 9. Repository layout

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

## 10. Data model (EF Core + SQLite, migrations applied at startup)

- `Users` — account, email, display name, **PBKDF2 password hash**, created timestamp
- `ConnectedRepositories` — provider, clone URL (unique per user), branch, optional app URL
- `AiModelKeyBindings` — user, provider, model, encrypted key, display mask**
- `FailureEvents` — method, path, status, request/response payload, exception, stack, timestamp
- `RepairTickets` — category, kind, status, analysis, patch summary, commit SHA, last agent error
- `EmailMessages` — outbox (to, subject, html body, sent, created at)

Every table is mapped in `DevSup.Infrastructure/Persistence/DevSupDbContext.cs` with the
schema shipped as EF Core migrations (`InitialCreate`,
`AddEmailOutboxRetriesAndOAuthTokens`, `AddRepairTicketLastError`,
`AddAiModelKeyMaskUpdatedAtUniqueIndex`).

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

## 11. Local development

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/DevSup.Api
```

The API exposes `/` as a health check and OpenAPI in Development. SQLite migrations run
automatically at startup (a `devsup.db` file is created next to the repo).

### API endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET` | `/` | — | Health check |
| `POST` | `/api/users/register` | — | Create account (email, display name, password ≥ 8 chars) |
| `POST` | `/api/users/login` | — | Exchange credentials for a JWT |
| `GET` | `/api/auth/github/login` | — | Start GitHub OAuth (redirects to GitHub) |
| `GET` | `/api/auth/github/callback` | — | GitHub OAuth callback → links account, returns JWT |
| `GET` | `/api/auth/gitlab/login` | — | Start GitLab OAuth (redirects to GitLab) |
| `GET` | `/api/auth/gitlab/callback` | — | GitLab OAuth callback → links account, returns JWT |
| `GET` | `/api/repositories` | Bearer | List connected repositories |
| `POST` | `/api/repositories` | Bearer | Connect a repository (provider, clone URL, branch) |
| `POST` | `/api/ingest` | Bearer | Report a failure; triaged into a repair ticket |
| `GET` | `/api/tickets` | Bearer | List repair tickets for your repositories |
| `GET` | `/api/ai-keys` | Bearer | List your AI key bindings (masked) |
| `POST` | `/api/ai-keys` | Bearer | Add or update an AI key binding |
| `DELETE` | `/api/ai-keys` | Bearer | Delete an AI key binding |

Secrets at rest (GitHub access tokens) are encrypted with AES-256-GCM under
`Security:DataProtectionKey`. JWT settings live under `Jwt`. Agent cadence and the commit
identity used for pushes live under `Repairing:` (`IntervalSeconds`, `BatchSize`,
`GitUserName`, `GitUserEmail`). Override any of these via
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

## 12. Roadmap

- **v0.1** — solution scaffold, domain model, failure classifier + tests
- **v0.2** *(done)* — SQLite persistence + migrations, JWT accounts, connect repo, ingest endpoint, classifier triage, tickets, email outbox
- **v0.3** *(done)* — SMTP outbox delivery with retries, GitHub OAuth + encrypted token storage, not-a-code-error emails
- **v0.4** *(done)* — agent repair loop: investigate → template patch → commit → push, ticket status updates, "fixed"/"needs review" emails
- **v0.5** *(done)* — BYO AI keys (Claude/Gemini/DeepSeek/OpenAI/Ollama), GitLab support, key management API
- **v0.6** — consumer versioning of the middleware, payload sanitization hardening, PR-based (opt-in) flow
- **v0.7** — multi-repo, dashboards, Slack/webhook notifications, external app-URL checks

---

Built with **.NET 10**, **ASP.NET Core**, **EF Core + SQLite** (dev; PostgreSQL planned),
**JWT auth (PBKDF2)**, **AES-256-GCM secret encryption**, **GitHub OAuth**, **SMTP**,
**the git CLI for agent pushes**, **xUnit**, and **GitHub Actions**.