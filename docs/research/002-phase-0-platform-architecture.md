# 002 — Phase 0: re-platform to React/TypeScript + .NET 10 with Postgres (pgvector) and Redis

**Status:** proposal only (nothing implemented). Companion to `001-auto-script-pipeline-proposal.md`, whose
phase table now starts with this phase 0.
**Date:** 2026-09-21
**Reference implementation for conventions:** `D:\sources\work\inkspoke\inkspoke-main` (`InkSpoke.Api`, net10.0;
`web/site`). Where this document says "as inkspoke does", the pattern was read from that repo, not invented.

## 1. Goal and the one thing not to lose

Turn the single-user Node prototype into a multi-user platform that can scale: React + TypeScript frontend,
.NET 10 backend, OAuth2 login (Google first), PostgreSQL with `pgvector`, Redis, all runnable locally with Docker
for dev/test — so that phases 1+ (upload → interview → script → Q&A → present, plus a document knowledge base)
are built once, on the stack they will ship on.

The prototype's value is not its code, it is the **live-service behaviour it encodes** (work log 001, findings
1–8): output paced by the input-audio timeline (silence pump), continuous silence-included output audio (RMS
advance detection), ~2.3 s paragraph pauses, one narration part at a time, 500-token appends, client delegation
without task text. Every one of those was found by running against the real service, and every one can be lost
silently in a port. Phase 0 therefore treats the existing test suite (52 tests, fake GPT-Live server) as a
**parity oracle**: the C# port must pass the same scenarios before the Node version is retired.

## 2. Target architecture

### 2.1 Backend — .NET 10, Clean Architecture with vertical-slice features

```
PresenterAi.slnx                       Directory.Build.props: net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors (as inkspoke)
src/
  PresenterAi.Domain/                  entities, value objects, domain events; no package references
  PresenterAi.Application/             use cases as vertical slices + ports (interfaces); no infrastructure
    Decks/  Prep/  Generation/  Qa/  Knowledge/  Presenting/  Sessions/  Users/
    Presenting/Presenter.cs            the state machine (port of presenter.js), driven by TimeProvider
    Presenting/PromptBuilder.cs        port of prompt.js      Scripts/ScriptParser.cs, ScriptWriter.cs (parser + tested inverse)
  PresenterAi.Infrastructure/          adapters for the ports
    Persistence/ (EF Core 10, Npgsql, pgvector, migrations)   Caching/ (Redis, HybridCache)   Files/ (IFileStore: disk | S3/MinIO)
    Live/LiveSession.cs                ClientWebSocket to GPT-Live + silence pump (port of live-client.js)
    Llm/ (OpenAI Responses, Anthropic, Azure OpenAI; embeddings)   Extraction/ (HTML, PDF, PPTX, DOCX)   Office/ (soffice runner)
  PresenterAi.Api/                     ASP.NET Core host: minimal APIs (Map…Endpoints groups), /ws audio bridge, SSE, auth, DI root
    Endpoints/Admin/                   users, providers, models, bindings, usage, system — copied from InkSpoke.Api and pruned (§2.8)
  PresenterAi.Contracts/               request/response DTOs; OpenAPI → generated TypeScript client for the web app
tests/
  PresenterAi.Application.Tests/       parser, prompt, chunking, RMS, Presenter with FakeTimeProvider (= the Node unit tests)
  PresenterAi.Infrastructure.Tests/    LiveSession against an in-process fake GPT-Live WebSocket server (= test/fake-live-server.js)
  PresenterAi.Api.Tests/               WebApplicationFactory + Testcontainers (Postgres+pgvector, Redis); bridge + presenter end to end
web/                                   bun workspaces: shared | app | admin (inkspoke's web/ layout)
  shared/                              generated API client + types, auth store, UI primitives used by both apps
  app/                                 React 19 + TypeScript + Vite + Tailwind; TanStack Query; Zustand; react-router
    src/audio/worklets/*.ts            capture/playback AudioWorklets (ported 1:1; the DSP does not change)
    src/deck/deckDriver.ts             iframe adapters showFn | reveal | sections (ported 1:1)
  admin/                               operator dashboard copied from inkspoke web/admin and pruned (§2.8)
docker-compose.yml                     postgres (pgvector/pgvector:pg17), redis:7, minio (optional), api (optional)
```

Dependency direction: Domain ← Application ← Infrastructure ← Api. Application depends only on the BCL and
`Microsoft.Extensions.*` abstractions (`TimeProvider`, logging, options). The Presenter state machine lives in
Application because it is pure logic with timers — exactly what the Node tests exercise with `mock.timers`; in C#
that is `TimeProvider` + `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.

Why not MediatR: it is now commercially licensed; plain `IRequestHandler`-style interfaces registered in DI, or
Wolverine, give the same slice structure without the dependency. Why minimal APIs and endpoint groups: it is what
`InkSpoke.Api` does (`MapSsoEndpoints`, `Endpoints/Public`, `Endpoints/Admin`), so the two backends stay
recognisable to the same team.

### 2.2 The real-time path (the part that must not regress)

| Node today | .NET 10 |
|---|---|
| `ws` server, one browser client, text JSON commands + binary PCM16 frames | `app.UseWebSockets()` + `MapGet("/ws")`, same wire protocol byte for byte; one session per user; `System.Threading.Channels` between socket reader, presenter and upstream |
| `LiveSession` over `ws` to GPT-Live, silence pump on `setInterval(20)` | `ClientWebSocket`, `PeriodicTimer(20 ms)` pump with the same `#sentMs` accounting; `TimeProvider` for tests |
| `pcmRms()` on each output delta | same, `Span<short>` arithmetic; no allocation per frame |
| `Presenter` timers: advance silence, part gap, nudge, wrap-up | `ITimer` via `TimeProvider.CreateTimer`; identical constants |
| Browser AudioWorklets | unchanged (TypeScript files loaded with Vite `?url`); `AudioContext` at device rate (finding 7) |

Keeping the `/ws` protocol identical has a practical payoff: in step 0.3 the **existing Node web UI can be pointed
at the .NET API** as an interim parity check before the React app exists.

Scale-out caveat: a live session is stateful (upstream socket, audio timeline) and lives on one API instance. Behind
a load balancer this needs session affinity for `/ws`, or a dedicated realtime service later. Everything else in the
API is stateless.

### 2.3 Frontend — React 19 + TypeScript

Vite + Tailwind, TanStack Query for server state, Zustand for the presenter/session store, react-router; a typed
client generated from the API's OpenAPI document (replaces inkspoke's hand-written axios client) in the `shared`
workspace, consumed by both `app` and `admin` (§2.8). Screens: Sign-in, Library (decks, presentations,
documents), the six-step preparation wizard from 001 §6, Presenter (port of today's page), Sessions (transcripts,
questions asked). The audio worklets, deck driver and WebSocket client are ports, not rewrites.

Dev: Vite on a second rare port (47914) proxying `/api`, `/ws`, `/decks` to the API on 47913. Prod: the API serves
the built SPA from `wwwroot` (single container), or nginx in front as inkspoke's `cicd/` does.

### 2.4 Authentication — OAuth2 authorization code + PKCE, Google first (inkspoke's `SsoService` pattern)

Reuse the shape that already runs in production for inkspoke rather than a second design:

```
web ──GET /v1/auth/sso/google/authorize?redirect_uri&code_challenge&state──▶ api ──302──▶ accounts.google.com
google ──GET /v1/auth/sso/google/callback?code&state──▶ api: validate AES-encrypted state, exchange code with Google,
        get/create user by (provider, subject), issue one-time server code ──302──▶ web redirect_uri?code
web ──POST /v1/auth/sso/token {code, code_verifier}──▶ api: claim code once (Redis TTL), return JWT access (60 min) + refresh (30 d)
```

- Copy `OAuthSettings` (+ validator), `JwtSettings`, `SsoService` (Google + Microsoft; the Apple stub was removed
  there for a reason), `AuthRateLimiting` and `ApiKeyAuthHandler` as source into `PresenterAi.Api/Auth`, adapted
  to this domain — not a shared package; the two products should be free to diverge.
- JWT bearer for the API; API keys for CLI use (`headless-run` becomes `presenter-cli run <id> --api-key …`).
- WebSocket auth: no token in the query string. `POST /v1/sessions/{presentationId}/ticket` returns a 30-second
  one-time ticket (Redis) that the client sends as the first WebSocket message; the bridge upgrades the connection
  to a user session only after validating it.
- Local dev without Google: a `Dev` scheme enabled only in `ASPNETCORE_ENVIRONMENT=Development` that signs in a
  fixed user, so `docker compose up` + `dotnet run` needs no OAuth client.
- Alternative considered: ASP.NET Core `AddGoogle()` + cookie auth (BFF). Less code for a single same-origin SPA,
  but no CLI story and a second auth model next to inkspoke's; rejected for consistency.

### 2.5 Data — PostgreSQL 17 + pgvector, EF Core 10

Files stop being the database; the Markdown script stays the **interchange format** (export/import, external
editing, the parity tests) and is stored as text in the row.

| Table | Purpose |
|---|---|
| `users(…, role user/admin, is_disabled, quota jsonb)`, `external_logins(provider, subject)`, `refresh_tokens`, `api_keys`, `invitations`, `allowed_email_domains` | identity (inkspoke shape) + who may sign in (§2.8) |
| `providers`, `provider_models`, `model_bindings`, `audit_log` | upstream catalogue managed from the admin site (§2.8; inkspoke's `Provider`, `ProviderModel`, `ModelProviderBinding`, audit log) |
| `provider_keys(user_id, provider, encrypted_key)` | BYO LLM keys, encrypted at rest as inkspoke's API does |
| `decks(id, owner_id, name, kind html/pdf/pptx, storage_key, content_hash, extract jsonb, slide_count)` | uploaded decks; bytes in the file store |
| `presentations(id, deck_id, owner_id, title, script text, frontmatter jsonb, version)` + `presentation_versions` | canonical script + drafts (001 §4.4) |
| `prep(presentation_id, brief jsonb, questions jsonb, answers jsonb, step)` | the resumable wizard (001 §6) |
| `qa_items(id, presentation_id, slide_no, question, type, persona, answerable, policy, answer, priority, sources jsonb)` | the Q&A bank |
| `generation_jobs(id, presentation_id, kind, provider, model, status, progress, tokens_in/out, cost, error)` | durable job record |
| `sessions(id, presentation_id, started_at, ended_at, usage_seconds, upstream)` + `session_turns(role, text, slide_no, at)` | rehearsal log → "asked last time" |
| `documents(id, owner_id, name, mime, storage_key, status, pages)` + `document_chunks(id, document_id, ordinal, text, tsv tsvector, embedding vector(1536), meta jsonb)` | knowledge base (001 §7) |
| `presentation_documents(presentation_id, document_id)` | which documents a presentation may draw on |

- `CREATE EXTENSION IF NOT EXISTS vector` in the first migration; `Pgvector.EntityFrameworkCore` for the `vector`
  type; HNSW index on `embedding` (`vector_cosine_ops`) and a GIN index on `tsv` for hybrid search.
- Embedding dimension is fixed per deployment (1536 for `text-embedding-3-small`); switching models means
  re-embedding, so the model id is recorded per chunk.
- `IFileStore`: local disk under `data/` in dev, S3/MinIO in prod (inkspoke already runs MinIO in compose).
- Integration tests use Testcontainers (`pgvector/pgvector:pg17`, `redis:7`) — not EF InMemory, which cannot run
  vector or tsvector queries.

### 2.6 Redis — what it is for, and what it is not

| Use | Mechanism |
|---|---|
| Hot reads: presentation + parsed script, deck extract, provider/model catalog | `HybridCache` (in-process L1 + Redis L2), invalidated on write |
| Generation job progress → browser | Redis pub/sub → SSE endpoint; survives an API restart mid-job and works across instances |
| The shared upstream GPT-Live key (Azure: 10 RPM / 10k TPM) | a Redis lease: N concurrent live-session slots + a token-bucket per upstream; the API refuses Start with a clear "all presenter slots busy" instead of a 429 mid-talk |
| One-time SSO codes, WebSocket tickets, refresh-token revocation | keys with TTL (inkspoke pattern) |
| Embedding cache | `emb:{model}:{sha256(chunk)}` so re-uploading a revised document only embeds changed chunks |
| Per-user rate limits and cost budgets for generation | sliding window counters |
| SignalR/`/ws` scale-out | not needed while sessions are instance-affine; add a backplane only if a realtime service is split out |

Never cache audio, and never put the live session state in Redis — it is hot-path, per-instance memory.

### 2.7 Local dev/test with Docker

```yaml
services:
  postgres: { image: pgvector/pgvector:pg17, ports: ["5432:5432"], environment: {POSTGRES_DB: presenter, POSTGRES_USER: presenter, POSTGRES_PASSWORD: dev_password_change_me}, healthcheck: pg_isready }
  redis:    { image: redis:7-alpine, ports: ["6379:6379"], healthcheck: redis-cli ping }
  minio:    { image: minio/minio, profiles: ["storage"] }        # optional; disk store by default
  api:      { build: src/PresenterAi.Api/Dockerfile, profiles: ["full"], ports: ["47913:47913"], depends_on: [postgres, redis] }
```

`docker compose up -d` (Postgres + Redis) → `dotnet ef database update` → `dotnet run --project src/PresenterAi.Api`
→ `bun run dev` in `web/app`. Secrets via `dotnet user-secrets` in dev and environment variables in containers;
`appsettings.Example.json` documents every key (inkspoke convention). The Node `.env` names map to
`Upstream:Endpoint`, `Upstream:Key`, `Upstream:Fallback:*`, `Generation:OpenAI:Key`, `Generation:Anthropic:Key`.

### 2.8 Admin site — users, upstream providers and models (borrowed from inkspoke)

A second React app, `web/admin`, copied from inkspoke's `web/admin` and pruned, against `/v1/admin/*` endpoint
groups copied from `InkSpoke.Api/Endpoints/Admin` and pruned. Both are protected by the `AdminOnly` policy
(`RequireRole("admin")`, `users.role`); the first admin is bootstrapped from configuration
(`Admin:BootstrapEmails`) on first Google sign-in, later admins are promoted from the Users page. Same Google SSO,
same JWT; the admin app only adds the role check and its own layout.

**What is borrowed, and what it becomes here**

| inkspoke | presenter-ai | Change |
|---|---|---|
| `Provider` (name, display name, base URL, `EncryptedApiKey` AES-256-GCM, auth scheme bearer/x-api-key, `ApiFormat`, timeout, retries, metadata JSONB) | `providers` | `ProviderType llm/asr/both` → **capabilities** `live-voice`, `text-generation`, `vision`, `embedding`; `ApiFormat` gains `gpt-live` (WebSocket) and `anthropic`; Azure needs `api-key` header + deployment name → `AuthScheme = api-key`, deployment in metadata |
| `ProviderModel` (vendor model id, opaque `PublicId`, display name, type, status Active/Legacy/Retired + `ReplacementModelId`, `IsDefault`, context window, ratings, per-model overrides) | `provider_models` | `ModelType text/audio` → `live-voice | text | embedding`; `MinTier` dropped (no billing tiers) or kept as `allowed_roles`; keep the opaque public id so the web app never sees internal ids (inkspoke's `OpaqueModelIdLeakTests` come along) |
| `ModelProviderBinding` (model ↔ provider, priority, enabled, vendor model id, promote/test) | `model_bindings` | **this is the Azure → OpenAI failover** that `config.upstreams[]` hard-codes today: the live-voice model `gpt-live-1` bound to the Azure Foundry provider (priority 1) and OpenAI (priority 2); `LiveSession` tries bindings in priority order, exactly like `createSession(attempt)` now |
| `AdminProviderEndpoints` (list/get/create/update/disable/rotate-key), `AdminProviderTestEndpoints` (test-connection, test-preview, list vendor models, test model) | `/v1/admin/providers` | test-connection for a `gpt-live` provider = the existing live smoke (start a session, one sentence, close, report `usage.seconds`) — the smoke script becomes an admin button |
| `AdminModelEndpoints` (CRUD, `PUT /defaults`), `AdminModelBindingEndpoints` | `/v1/admin/models`, `/v1/admin/models/{id}/bindings` | defaults become **per capability**: default live-voice model, default generation model, default embedding model; the wizard's provider picker lists only `Active` text models the user's role may use |
| `AdminUserEndpoints` (list/search, detail, `PATCH` tier/admin/disable) + `AdminUserService` | `/v1/admin/users` | credits/Stripe/tier logic stripped; `PATCH` sets role, disabled, and **quotas** (session minutes per month, generation tokens per month, max live slots); detail shows logins, decks, sessions, usage |
| `AdminUsageEndpoints` (+ `AdminUsageAnalyticsService`) | `/v1/admin/usage` | metrics are session minutes ($0.05/min), generation tokens and embedding tokens per user/provider/model |
| `AdminSystemEndpoints` (health, audit log), `AdminRateLimitEndpoints`, `AdminApiKeyEndpoints` | `/v1/admin/system`, `/rate-limits`, `/api-keys` | audit every admin mutation (who, what, before/after); rate-limit page edits the Redis lease numbers (live slots per provider, per-user caps) |
| `web/admin`: layout, `authStore`, `api/client.ts`, `pages/users/*`, `pages/providers/*` (overview / models / diagnostics tabs, copy-as-cURL), `pages/models/*` (catalog, detail, bindings panel), `pages/system`, `pages/usage`, `pages/audit` | `web/admin` | delete `billing`, `stripe`, `subscriptions`, `devices`, `licenses`, `sync`, `diagnostics`, `pricing`; add **Sign-in policy** (allowed e-mail domains, invitations) and **Live sessions** (who is presenting now, on which provider, for how long; force-close) |

**Not borrowed:** anything tied to inkspoke's business model (credits, tiers, Stripe, licences, device sync,
wake-word builds). Copy as source with the origin commit in each file header (§4), not as a package.

**Admin screens, in order of value**

1. **Providers** — add Azure Foundry (`api-key`, deployment) and OpenAI, paste keys once (encrypted at rest;
   `.env` keys go away), test connection, rotate key, disable.
2. **Models** — catalogue per capability, bindings with priority (= failover), defaults, lifecycle
   (legacy → retired with a replacement, so a deprecated model id never breaks a user's saved presentation).
3. **Users** — search, role, disable, quotas, usage; sign-in policy (domain allow-list / invitations) so a
   company deployment is not open to any Google account.
4. **Usage & live sessions** — cost per user/provider, who is on the shared GPT-Live key right now.
5. **System** — health (Postgres, Redis, each enabled provider), audit log, rate limits.

**Effect on the rest of the design:** `Upstream:*` and `Generation:*` configuration keys (§2.7) become the
*seed* for the `providers` table on first run and are otherwise replaced by admin-managed rows; the web app's
`GET /v1/providers` returns only the public ids and display names the caller's role may use; the Redis lease
(§2.6) reads its limits from the rate-limit page instead of constants.

### 2.9 Cross-cutting

Serilog + OpenTelemetry traces (one span per generation job and per live session), `/health` with Postgres/Redis
checks, ASP.NET rate limiting on auth endpoints, per-user usage metering (session seconds, generation tokens,
embedding tokens) in the style of inkspoke's `Services/Metering`, structured problem-details errors, CI: `dotnet
test` + `bun run lint && bun run build`.

## 3. Migration plan (sub-phases, each leaves the repo runnable)

| Step | Scope | Exit criterion | Effort |
|---|---|---|---|
| 0.1 Scaffold | solution, `Directory.Build.props`, docker compose, health endpoint, CI, `web/app` skeleton, OpenAPI → TS client | `docker compose up`, `dotnet test`, `bun run build` green | 1–2 d |
| 0.2 Port the core | `ScriptParser`/`ScriptWriter`, `PromptBuilder`, chunking, RMS, `LiveSession` + silence pump, `Presenter` on `TimeProvider`; in-process fake GPT-Live server; **port every Node test scenario** | parity suite green; live smoke on Azure primary + OpenAI fallback speaks one sentence and closes with usage | 5–8 d |
| 0.3 API + bridge | `/ws` (protocol-identical), `/api/presentations`, `/decks` static, SSE plumbing; file-backed repository as a temporary adapter | the **existing Node web page** presents the Ricoh deck against the .NET API (slides 1–5, → key, Space, Esc) | 2–3 d |
| 0.4 React app | Sign-in placeholder, Library, Presenter page (worklets, deck driver, transcript, keys), Sessions | same Ricoh run from the React app; AC1–AC10 of plan 001 re-checked | 4–6 d |
| 0.5 Auth + persistence | Google SSO (inkspoke pattern), Dev sign-in, JWT + API keys, WebSocket tickets, EF migrations, decks/presentations/sessions in Postgres + `IFileStore`, `presenter-cli import presentations/*.md` | two Google users each see only their own decks; imported Ricoh presentation presents | 4–6 d |
| 0.6 Redis | `HybridCache`, job progress pub/sub → SSE, upstream session lease + token bucket, tickets/codes with TTL | second user pressing Start while a slot is busy gets the "slots busy" message, not a 429 | 2–3 d |
| 0.7 Admin | `providers` / `provider_models` / `model_bindings` / `audit_log` tables seeded from configuration; `/v1/admin/*` groups copied from `InkSpoke.Api/Endpoints/Admin` and pruned; `LiveSession` and the generation adapters resolve their upstream through bindings by priority; `web/admin` copied from inkspoke and pruned (Providers, Models, Users + sign-in policy, Usage & live sessions, System); admin bootstrap from `Admin:BootstrapEmails` | an admin adds the Azure Foundry and OpenAI providers in the UI, binds `gpt-live-1` to both with priorities, presses *Test connection* (live smoke) on each, disables Azure → the next Start fails over to OpenAI; a user from a non-allowed domain cannot sign in; every change is in the audit log | 5–7 d |
| 0.8 Retire Node | delete `src/`, keep `presentations/*.md` and `decks/` as fixtures; move `scripts/*.mjs` to `presenter-cli` | README/guides updated; parity suite is the regression suite | 1 d |

**Phase 0 total: 5–7 weeks for one developer** (the admin site is about one of them; most of it is copy-and-prune). Phases 1+ from 001 then run on this stack; their estimates grow
by roughly a third (typed contracts, migrations, two build systems) — 001's table says so.

## 4. Risks and decisions to confirm

- **Port regressions in the live path** are the main risk; the parity suite mitigates the logic, only live smoke
  and a real Chrome run catch timing. Budget the live checks in 0.2–0.4, not at the end.
- **Two backends, one owner.** Copying inkspoke's auth and admin as source means fixes land twice; a shared NuGet package
  would couple release cycles instead. Copy, and note the origin commit in the file header.
- **Costs become per-user.** With many users on one Azure GPT-Live key the 10 RPM limit is the platform's limit;
  the lease makes it visible, but the real fix is per-tenant upstream keys or a higher quota.
- **Embedding provider lock-in** per knowledge base (dimension + model recorded per chunk; re-embed on change).
- **Do we need multi-user now?** If the near-term audience is one presenter, 0.5 could ship the Dev sign-in only and
  defer Google; the schema and endpoints do not change. Recommend building Google in 0.5 anyway — it is the
  best-understood part thanks to inkspoke, and "user" is a foreign key everywhere else.
- **Frontend framework surface** (Tailwind version, component library) — follow `web/site` in inkspoke unless
  there is a reason not to; the decision is cosmetic and should not block 0.1.
