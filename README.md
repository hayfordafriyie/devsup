# DevSup

**AI-driven API failure detection & auto-repair.** DevSup watches your production API,
captures failures as they happen, dispatches an AI agent to investigate your code and
push a fix, and emails you at every step — so you get notified of the error and its fix,
instead of digging through logs.

> Project status: **scaffold / v0.1** — solution layout, domain model and failure
> classifier in place. Remaining roadmap at the bottom.

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

- **Users create an account** and connect their git repositories (GitHub, and later
  GitLab) via OAuth.
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
| **Infrastructure** | `DevSup.Infrastructure` | EF Core + PostgreSQL, git provider adapters (GitHub/GitLab), AI model clients (BYO key), SMTP transport, key encryption at rest |
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
user/provider/model, encrypted at rest, and never stored in plain text or surfaced to
the UI. The agent resolves the key per repair ticket and uses that provider for
investigation, patch generation, and commit-message drafting.

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

## 10. Data model (planned EF Core + PostgreSQL)

- `Users` — account, email, display name
- `ConnectedRepositories` — provider, clone URL, branch, optional app URL
- `OAuthTokens` — encrypted provider credentials per integration
- `AiModelKeyBindings` — user, provider, model, encrypted key
- `FailureEvents` — method, path, status, request/response payload, exception, stack, timestamp
- `RepairTickets` — category, kind, status, analysis, patch summary, commit SHA
- `EmailMessages` — outbox (to, subject, body, sent)

## 11. Local development

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/DevSup.Api
```

The API exposes `/` as a health check and OpenAPI in Development.

### Docker

```bash
docker build -t devsup-api .
docker run --rm -p 8080:8080 devsup-api
```

### CI

`.github/workflows/ci.yml` runs `restore` → `build` → `test` in Release on every
push/PR to `master`.

## 12. Roadmap

- **v0.1** *(this commit)* — solution scaffold, domain model, failure classifier + tests
- **v0.2** — accounts + GitHub OAuth, connect repo + branch, ingest endpoint, log failures
- **v0.3** — classifier-driven triage, email alerts (error + recommended fix)
- **v0.4** — AI repair loop: investigate → patch → commit → push, status updates, "fixed" emails
- **v0.5** — BYO AI keys (Claude/Gemini/DeepSeek/OpenAI/Ollama), GitLab support, not-code-error skip flows
- **v0.6** — consumer versioning of the middleware, payload sanitization hardening, PR-based (opt-in) flow
- **v0.7** — multi-repo, dashboards, Slack/webhook notifications, external app-URL checks

---

Built with **.NET 10**, **ASP.NET Core**, **EF Core + PostgreSQL** (planned), **xUnit**,
and **GitHub Actions**.