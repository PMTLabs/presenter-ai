# 004 — Identity, persistence and auth-state Redis

**Date:** 2026-09-22
**Status:** Implemented (2026-09-22) and open as PR #3 into `develop` — revision 2, after external review `pr004-rev-1`. The external implementation review, round 1 (`docs/review/006`), found gaps that are being fixed, including T12's `/v1` headers and CORS half, which had not landed. The manual runbook (§7) and the T13 Serilog live cycle are still to be run by the user.
**Size:** L
**Area:** `src/PresenterAi.Api` (Auth, Endpoints, Realtime, Program), `src/PresenterAi.Application`, `src/PresenterAi.Infrastructure`, `src/PresenterAi.Contracts`, `src/PresenterAi.Cli`, `web/shared`, `web/app`, `web/admin`, `tests/*`, compose/CI/docs
**Requirement brief confirmed:** 2026-09-22 (G1)
**Implements:** research `docs/research/002-phase-0-platform-architecture.md` step 0.5 (+ the auth-state slice of 0.6)

---

## 1. Goal

A presenter signs in with their Google or Microsoft account and sees only their own presentations, stored in
Postgres instead of on disk. Pressing *Start* still presents the Ricoh deck exactly as it does today, but the
WebSocket now opens against a one-time ticket instead of an ignored auth frame, and when the run ends there is a
row in `sessions` with what was said, on which slide, for how long.

## 2. Requirement (as confirmed at G1)

- **Problem:** the API authenticates every request as one hard-coded dev user (`src/PresenterAi.Api/Auth/DevAuthHandler.cs:20-32`),
  the `/ws` auth frame is accepted and ignored (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:132-134`), and all
  content is files on disk. Postgres and Redis are composed but unused. There is no notion of *whose* presentation
  this is, and a finished rehearsal leaves no trace.

- **In scope:**
  - Identity tables `users`, `external_logins`, `refresh_tokens` (inkspoke shape, pruned of tier/billing/device/sync).
  - OAuth2 authorization-code + PKCE for Google and Microsoft, adapted from `InkSpoke.Api/Services/SsoService.cs`
    (AES-GCM sealed state, provider binding, nonce replay check, one-time code).
  - JWT access tokens + rotating refresh tokens.
  - `/v1` endpoints: `GET /v1/auth/sso/providers`, `GET /v1/auth/sso/{provider}/authorize`,
    `GET /v1/auth/sso/{provider}/callback`, `POST /v1/auth/sso/token`, `POST /v1/auth/refresh`,
    `POST /v1/auth/logout`, `GET /v1/auth/me`, `POST /v1/auth/dev/sign-in`, `POST /v1/sessions/ticket`.
  - Dev sign-in becomes a token-issuing endpoint backed by a real `users` row, gated on `Auth:Dev:Enabled`
    **and** `IHostEnvironment.IsDevelopment()`; `DevAuthHandler` is deleted.
  - Mandatory one-time WebSocket ticket as the first text frame; `4401 session.ticket_invalid` otherwise.
  - `presentations`, `sessions`, `session_turns` in Postgres via EF Core 10 + Npgsql; every read scoped to the caller.
  - `presenter-cli import presentations/*.md --owner <email>`; `run`/`smoke` stay in-process.
  - Redis for auth state only: tickets, one-time SSO codes, SSO state nonces.
  - Sign-in policy from configuration + `Admin:BootstrapEmails`; `users.role` and `users.is_disabled`.
  - Frontend: real access token in memory + refresh-on-401, sign-in page, `AdminRoute` role check, `/v1` calls.
  - The `/api` parity trio is removed in favour of `/v1/presentations`, `/v1/presentations/{id}`, `/v1/config`.
  - `Cache-Control: no-store` and `X-Request-Id` on `/v1`, CORS from configuration, pruned auth rate limiting.

- **Out of scope / non-goals:** API keys and `X-Api-Key` · upstream session lease, N slots, token bucket,
  `session.slots_busy` · `HybridCache`, job pub/sub, SSE · deck upload, `decks` table, `IFileStore` · admin UI and
  the provider/model catalogue · `invitations` / `allowed_email_domains` tables · quota **enforcement** ·
  `presentation_versions`, `prep`, `qa_items`, `generation_jobs`, `documents` · per-connection presenters.

- **Users and surfaces:** presenters via `web/app` (sign-in, Library, Present); an admin-to-be via `web/admin`
  (guard only); developers via `presenter-cli import` and Dev sign-in. Full surface list in §4.5.

- **Behaviour:**
  - *Happy path:* sign in with Google → Library lists only your presentations → *Start* → the app fetches a ticket,
    opens `/ws`, sends the auth frame, presents the Ricoh deck as today → on close a `sessions` row and its turns
    are written.
  - *Unhappy paths:* a callback failure returns one fixed message revealing neither the crypto reason nor the
    server's lifetime window; a replayed state nonce, an expired or reused SSO code and a reused ticket all fail;
    a reused refresh token is rejected after rotation; a second WebSocket client still gets `busy 1013`; a user
    outside the allowlist is refused at callback, before an account exists; an access token expiring mid-session
    does not kill the live socket.

- **Constraints and assumptions:**
  1. Refresh token in an httpOnly `SameSite=Strict` cookie, not in the JSON body as inkspoke does
     (`docs/reference/001-api-and-code-conventions.md:174-176`); CORS credentials only on the refresh route.
  2. Metering now (`sessions.usage_seconds`), enforcement later — no `users.quota` column until the admin plan
     can set one.
  3. Redis is required at startup, not optional-with-fallback as in inkspoke.
  4. No auto-migrate at startup; `dotnet ef database update` in the runbook, covered by a migrations-apply test.
  5. One-time SSO codes live in Redis (60 s, atomic claim) — no `sso_authorization_codes` table.
  6. The first migration includes `CREATE EXTENSION IF NOT EXISTS vector` though no vector column exists yet.
  7. Markdown stays the interchange format: the script is stored as text and reparsed on read; no cache layer.
  8. Copied inkspoke files carry an origin header naming commit `b83e691f`.
  9. `/decks/**` remains anonymous.

- **Acceptance criteria:**
  1. **AC1** — two different Google accounts sign in through real Chrome; each sees only its own presentations.
  2. **AC2** — with no OAuth client configured, `POST /v1/auth/dev/sign-in` returns a usable token pair in
     Development; the endpoint is absent outside Development (asserted by test).
  3. **AC3** — `/ws` with a missing, malformed, expired or already-used ticket closes `4401 session.ticket_invalid`;
     with a valid ticket the Ricoh deck presents end to end, frozen frames and `1013`/`1011` unchanged.
  4. **AC4** — `presenter-cli import presentations/*.md --owner <email>` imports the sample and Ricoh scripts
     **including their context files**; the imported Ricoh presentation presents from the React app.
  5. **AC5** — a completed run leaves one `sessions` row with `started_at`, `ended_at`, `usage_seconds`, `upstream`,
     and `session_turns` rows carrying `slide_no`.
  6. **AC6** — an expired access token is refreshed silently via the cookie; rotation revokes the previous token and
     replaying it returns 401.
  7. **AC7** — an email outside `Auth:SignIn:AllowedEmailDomains` cannot sign in; an email in `Admin:BootstrapEmails`
     receives `role = admin` on first sign-in.
  8. **AC8** — `/api/*` is gone, every `/v1` error is Problem Details with a catalogue code, the OpenAPI snapshot and
     generated TS client regenerate with no drift, and CI is green including the Testcontainers tests.

- **Decisions made:** scope = step 0.5 + auth-only Redis · presentations + sessions persisted, decks stay on disk ·
  Dev sign-in as a Development-only token endpoint, `DevAuthHandler` removed · config allowlist +
  `Admin:BootstrapEmails` · mandatory ticket frame, anonymous `/ws` upgrade · API keys deferred, CLI in-process ·
  singleton presenter kept, sessions record the owner · Testcontainers in CI.

## 3. Current state (as-built)

Verified against `develop` at `40d445c`. Line references were opened during discovery (two scout sweeps plus direct
reads; reports in the session scratchpad under `plan-004/`). Claims corrected by external review `pr004-rev-1` are
marked ⚑.

**Auth**
- `src/PresenterAi.Api/Auth/DevAuthHandler.cs:20-32` — the only authentication handler. Gated **only** on
  `Auth:Dev:Enabled`; no `IHostEnvironment` check, so any environment can enable it. Emits fixed `sub`, `email`
  and role `user`.
- `src/PresenterAi.Api/Program.cs:36-41` registers it as the default authenticate/challenge scheme; `:87-88` runs
  the authentication/authorization middleware. There is no `AddCors`, no JWT registration, no security-header,
  cache-control or request-id middleware anywhere in `Program.cs:28-96`.
- `src/PresenterAi.Api/Auth/DevAuthHandler.cs:35-42` — the challenge already returns Problem Details
  `auth.required` through the shared `Problems` producer; that behaviour must survive the switch to JWT.
- Protected today: `/api/presentations` and its detail route (`Endpoints/PresentationEndpoints.cs:10-20`),
  `/api/config` (`Endpoints/ConfigEndpoints.cs:9-17`), `/ws` (`Realtime/PresenterBridge.cs:326-332`).
  `/health`, `/decks/**`, static assets and OpenAPI are anonymous.
- `src/PresenterAi.Api/Realtime/PresenterBridge.cs:132-134` — `case "auth":` logs
  *"WebSocket auth frame accepted and ignored during Dev auth transition"* and returns. The socket is accepted at
  `:48` and the single-client slot taken by `Interlocked.CompareExchange(ref _client, connection, null)` at `:50`
  **before any frame arrives** — the ordering that §4.4 must change.
- `web/shared/src/auth/authStore.ts:5-24` — a persisted Zustand store holding `{id,email,displayName}`; sign-in is
  a synchronous local state change. `web/shared/src/api/client.ts:1-12` adds no auth header.
  `web/app/src/ws/bridgeClient.ts:68-75` sends `{type:"auth",ticket:"dev"}` from the public constant
  `web/shared/src/auth/devSignIn.ts:1-11`. `web/admin/src/components/layout/AdminRoute.tsx:1-10` redirects a null
  user to `/login` and checks no role.
- **Reusable:** `src/PresenterAi.Api/Errors/Problems.cs` (the Problem Details producer used by the challenge) and
  `src/PresenterAi.Contracts/ErrorCodes.cs` — the catalogue **already contains** `auth.required`,
  `auth.forbidden`, `auth.sso_provider_disabled`, `auth.sso_state_invalid`, `auth.sso_code_used`,
  `auth.signup_not_allowed`, `auth.account_disabled` and `session.ticket_invalid`, and
  `web/shared/src/api/errorMessages.ts:3-46` already maps them. No new codes are needed.

**Content**
- `src/PresenterAi.Application/Content/IPresentationRepository.cs:9-11` exposes `ListAsync` and `LoadAsync`,
  neither taking an owner. `FilePresentationRepository` is its **only** implementation, so any signature change
  breaks it — and T10 still needs a file reader. ⚑
- `src/PresenterAi.Infrastructure/Content/FilePresentationRepository.cs:37-58` — `LoadAsync` parses the Markdown
  **and separately reads the context file's content** from `script.Meta.Context`, rejecting paths that escape the
  root, returning `LoadedPresentation(id, meta, slides, context)`. `ScriptParser` keeps only the context *path*
  (`src/PresenterAi.Application/Scripts/PresentationScript.cs:11`), so storing script + frontmatter alone cannot
  reconstruct a loaded presentation. ⚑
- `src/PresenterAi.Infrastructure/DependencyInjection.cs:37-52` (`AddFileContent`) registers the singleton
  repository and `FileDeckStore`; `src/PresenterAi.Api/Program.cs:60-70` serves `/decks` from `IDeckStore.DeckRoot`.
- Consumers of `IPresentationRepository` / `LoadAsync` / `StartAsync`, grep-verified — **two in production**
  (`PresentationEndpoints.cs:24-50`; the load delegate in `DependencyInjection.cs:68-84`) plus
  `PresenterBridge.cs:144` and `Cli/RunCommand.cs:149` for `StartAsync`, **and nine test files**: ⚑
  `BridgeContractTests`, `BridgeSlotTests`, `BridgeStartTests`, `BridgeTests`, `PresenterTests`, `CliTests`,
  `FilePresentationRepositoryTests`, `FakeLiveServer`, `LiveSessionTests`. T8 must budget for all of them.
- `src/PresenterAi.Cli/Program.cs:48-57` injects `Content:RootDir = "."` when the setting is absent;
  `:63-80` registers **only** file content, live sessions and the presenter — there is no persistence, no Redis and
  no recorder in the CLI's service graph. ⚑ `docker-compose.yml:49-52` sets a third content root (`/app/content`).

**Sessions / presenter**
- `src/PresenterAi.Application/Presenting/IPresenter.cs` exposes `State`, `Slide`, `Audio`, `Transcript`, `Usage`,
  `Closed`, `Log`, `UpstreamError` events and `StartAsync(string id, int? fromIndex, CancellationToken)` returning
  **`bool`**.
- `PresenterEvents.cs:3-9` — `PresenterTranscript(Role, Delta, StartMs, EndMs)` carries **no slide number**;
  `PresenterUsage(Seconds, Ratio)`; `PresenterClosed(Reason, Seconds)`. No wall-clock start/end is emitted
  (`Presenter.cs:778-815`), and **neither `PresenterSnapshot` nor `LiveEvents` exposes which upstream was
  chosen** — the fallback decision stays inside `Presenter`. ⚑ `sessions.upstream` therefore needs a new signal.
- `src/PresenterAi.Infrastructure/DependencyInjection.cs:68-84` builds the singleton `Presenter` with
  `repository.LoadAsync` captured as a delegate — the seam that owner scoping must pass through.
- `Realtime/PresenterBridge.cs:15-18,43-57,68-76,211-216` — the process-wide single-client CAS slot, `busy 1013`
  for the loser, cleanup that releases the slot on disconnect, and `1011` on backpressure at `:285-315`. Frozen by
  `tests/PresenterAi.Api.Tests/BridgeContractTests.cs:83-102`.

**Infrastructure**
- No EF Core, Npgsql, Pgvector, StackExchange.Redis, `HybridCache` or Testcontainers package exists anywhere
  (`src/PresenterAi.Infrastructure/PresenterAi.Infrastructure.csproj:1-8`,
  `tests/PresenterAi.Api.Tests/PresenterAi.Api.Tests.csproj:7-22`).
- `docker-compose.yml:2-23` provisions `pgvector/pgvector:pg17` on host port **5433** and `redis:7-alpine` on host
  port **6382**, both healthchecked; the `api` service (`:25-57`) depends on them but receives no connection strings.
- `.github/workflows/ci.yml:1-83` — Ubuntu, secrets guard, `docker compose config -q`, .NET restore/build/test, and a
  separate Bun lint/build/OpenAPI-drift job. It never starts compose services; GitHub-hosted Ubuntu runners do have
  a Docker daemon, so Testcontainers can run in the existing job.
- `appsettings.Example.json:2-29` documents upstream, presenter, content, Dev auth and Serilog only.

**Contract chain**
- `Program.cs:92-94` maps `/openapi/v1.json`; `tests/PresenterAi.Api.Tests/OpenApiTests.cs:148-160` compares it to
  `web/shared/openapi/v1.json` — so **any PR that adds an endpoint must regenerate the snapshot in the same PR** or
  CI goes red. ⚑ `web/shared/scripts/generate-client.ts:7-34` regenerates `generated.d.ts` plus the error-code
  union; CI fails on any diff under `web/shared/src/api` (`.github/workflows/ci.yml:67-83`).
- Consumers of the parity trio after the Node retirement (plan 003), grep-verified — two:
  `web/app/src/routes/Library.tsx:15` and `web/app/src/routes/Present.tsx:130`; `/api/config` has **no** caller.
- `docs/reference/001-api-and-code-conventions.md:186-196` marks all three "Removed in plan 004"; `:128-137`
  specifies the ticket handshake and `4401`; `:52-65` gives the response/status table (a synchronous action is
  **200**, a list is the `{items,page,pageSize,total}` envelope); `:174-184` requires `RateLimit-Limit`,
  `RateLimit-Remaining` and `RateLimit-Reset` on rate-limited routes alongside `Retry-After`.

**Quirks that will bite**
- `Presenter` and `PresenterBridge` are singletons; the presenter's event handlers are process-wide, so a recorder
  must attach and detach per run rather than subscribe once — and two sequential runs must not leak handlers.
- `PresentationEndpoints.cs:24-62` returns raw exception messages — that parity behaviour must not be carried into
  `/v1` (`conventions:67-75`).
- React StrictMode double-invokes effects; the SSO callback exchange must be idempotent or guarded, since the
  one-time code is consumed on first use.

## 4. Design

### 4.1 Approach

Three layers land together, each reusing what already exists rather than introducing a parallel mechanism.

**Identity.** A new `PresenterAi.Infrastructure/Identity` folder holds the EF entities and services adapted from
inkspoke: `OAuthSettings` + its validator and `JwtSettings` are near-verbatim copies (with an origin header naming
commit `b83e691f`); `SsoService` is rewritten against our own `User` entity, keeping the parts that are security
decisions rather than domain code — AES-GCM sealed state, the 10-minute state lifetime, provider binding, the
one-time nonce and the S256 PKCE requirement. Account linking is **stricter than inkspoke's**: a match on
`(provider, subject)` always links, but the fall-back match on lower-cased email links *only when the provider
asserts the address is verified* (`email_verified` from Google, the equivalent Graph claim for Microsoft) — an
unverified address that collides with an existing user is refused with `auth.signup_not_allowed` rather than
silently taking over the account. Tokens are issued by an `ITokenService`: HMAC-SHA256 JWT with `sub`, `email`,
`role`; refresh tokens are opaque, persisted only as a SHA-256 hash, and rotated by a **conditional** revoke
(`UPDATE … WHERE id = @id AND is_revoked = false` must affect exactly one row) so two concurrent redemptions cannot
both mint a successor. `is_disabled` is checked at every issuance point — SSO callback, dev sign-in, refresh and
ticket issuance — returning `auth.account_disabled`; an access token already in flight simply expires within
`Jwt:AccessTokenMinutes`, and because ticket issuance re-checks, a disabled user cannot start a new session.
`DevAuthHandler` is deleted and its job taken by `POST /v1/auth/dev/sign-in`, which upserts a real `users` row and
returns the same token pair as SSO — so the SPA, the bridge and the tests exercise one code path.

**Persistence.** `PresenterAiDbContext` maps six entities with explicit snake_case columns (inkspoke's convention —
no global naming convention call) and opaque prefixed ids generated server-side (`usr_`, `prs_`, `ses_`, per
conventions §3). The interface is **split** rather than widened: `IPresentationRepository` keeps its name but both
methods take an owner and are implemented only by `PostgresPresentationRepository`, while the file reader moves
behind a new ownerless `IPresentationImportSource` (`ListFilesAsync`, `ReadAsync`) used solely by the CLI importer.
That keeps `FilePresentationRepository`'s own tests meaningful and stops an ownerless implementation from
satisfying an owner-scoped contract. The Markdown script is stored as text and reparsed by the existing
`ScriptParser` on read; because a loaded presentation also needs the **content** of its context file, import
resolves that path once and stores the text in `presentations.context`, keeping the original path in `frontmatter`
so an export round-trips.

**Sessions.** `StartAsync` returns a `PresenterStartResult` (`Started`, `PresentationId`, `Upstream`,
`UpstreamSessionId`, `Model`) instead of a bare `bool`, because nothing currently exposes which upstream the
fallback chose and `sessions.upstream` needs it. An `ISessionRecorder` (Application) with an EF implementation
(Infrastructure) is attached to the presenter's events for the duration of one run by whoever started it: the
bridge for `/ws`, `RunCommand` for the CLI. It stamps `started_at`/`ended_at` from `TimeProvider`, tracks the
current slide from `Slide` events, and aggregates `Transcript` deltas into a turn row, flushing when the role
changes or the session closes. Completion is an **awaitable, idempotent barrier** (`EndAsync` completes once,
whether reached by `Closed`, by disconnect cleanup or by a failed start) which the bridge awaits in its `finally`
before detaching handlers — otherwise a disconnect can release the slot while the queued `Closed` event is still
in flight and the row would keep a null `ended_at`.

**Redis** holds only TTL state: `ws:ticket:{id}` (30 s) and `sso:code:{hash}` (60 s), both claimed with an atomic
`GETDEL` so a reuse loses the race, and `sso:nonce:{n}` (10 min) written with `SET NX` so a replay is detected.
Registration is required, not optional — sign-in and `/ws` both depend on it, and a silent in-memory fallback
would make a multi-instance deployment fail in a way tests never see.

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — SSO copied-and-pruned from inkspoke, JWT + refresh cookie, Redis TTL state | proven in production; the security decisions are already made and tested; ports its own test suite | ~2 500 lines to read and prune; fixes now land in two places | **Chosen** — research §2.4 and the brief; the error-leak and PKCE tests come with it |
| B — ASP.NET `AddGoogle()` + cookie auth (BFF) | far less code for one same-origin SPA | no CLI/API-key story later, and a second auth model next to inkspoke's | Rejected — research §2.4 rejected it for the same reason |
| C — shared NuGet package with inkspoke | one fix, one place | couples two release cycles for two products that should be free to diverge | Rejected — research §4 |
| Owner scoping: split into an owner-scoped `IPresentationRepository` + an ownerless `IPresentationImportSource` | the scope is explicit in the signature; the file reader keeps a contract it can honestly satisfy; a new caller cannot bypass scoping | two interfaces where there was one | **Chosen** (revised after review F4) |
| Owner scoping: widen `IPresentationRepository` and let the file reader ignore the owner | one interface | an ownerless implementation silently satisfies an owner-scoped contract — exactly the bug the change exists to prevent | Rejected |
| Owner scoping: check ownership at the endpoint/bridge boundary only | no contract change | the load path stays global-by-id, so any future caller silently bypasses the check | Rejected |
| Session recording via a hosted service subscribed to the singleton presenter | one attachment point | process-wide handlers on a singleton leak across runs; the CLI has its own container | Rejected |
| Keep `/api` and add `/v1` beside it | no frontend change | two shapes for one resource, and the trio cannot express ownership | Rejected — conventions §10 removes it here |

### 4.3 Data / config / API model

**Tables** (snake_case; `id` is an opaque prefixed string; timestamps `timestamptz`):

| Table | Columns | Indexes |
|---|---|---|
| `users` | `id` (`usr_`), `email`, `display_name`, `role` (`user`\|`admin`), `is_disabled`, `auth_method`, `created_at`, `updated_at`, `last_sign_in_at` | unique `lower(email)` |
| `external_logins` | `id`, `user_id`, `provider`, `subject`, `provider_email`, `provider_email_verified`, `provider_display_name`, `created_at` | unique `(provider, subject)`; `user_id` |
| `refresh_tokens` | `id`, `user_id`, `token_hash`, `expires_at`, `is_revoked`, `created_at`, `revoked_at` | unique `token_hash`; `(user_id, is_revoked)` |
| `presentations` | `id` (`prs_`), `owner_id`, `slug`, `title`, `deck`, `driver`, `script`, `context`, `frontmatter` jsonb, `slide_count`, `version`, `created_at`, `updated_at` | unique `(owner_id, slug)`; `owner_id` |
| `sessions` | `id` (`ses_`), `presentation_id`, `user_id`, `started_at`, `ended_at`, `usage_seconds`, `upstream`, `upstream_session_id`, `close_reason` | `(user_id, started_at desc)`; `presentation_id` |
| `session_turns` | `id`, `session_id`, `ordinal`, `role`, `text`, `slide_no`, `at` | `(session_id, ordinal)` |

`slug` is the Markdown file stem (`ricoh-delivery-overview`), unique per owner so a re-import upserts instead of
duplicating; the public id stays opaque. `context` holds the **text** of the file named by the script's `context:`
front-matter key, resolved at import (the path itself stays in `frontmatter` for export). The first migration also
runs `CREATE EXTENSION IF NOT EXISTS vector`.

**Redis keys:** `ws:ticket:{id}` → `{userId}`, TTL 30 s, `GETDEL`; `sso:code:{sha256}` →
`{userId, codeChallenge, redirectUri}`, TTL 60 s, `GETDEL`; `sso:nonce:{nonce}` → `1`, TTL 10 min, `SET NX`.

**Config keys** (all documented in `appsettings.Example.json`; placeholders only, never values):
`ConnectionStrings:Postgres`, `ConnectionStrings:Redis` · `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SecretKey`,
`Jwt:AccessTokenMinutes` (60, research §2.4), `Jwt:RefreshTokenDays` (30) · `OAuth:ApiBaseUrl`,
`OAuth:StateEncryptionKey` (base64, 16/24/32 bytes), `OAuth:AllowedRedirectUris`,
`OAuth:{Google,Microsoft}:{Enabled,ClientId,ClientSecret,AuthorizationEndpoint,TokenEndpoint,UserInfoEndpoint}`,
~~`OAuth:Microsoft:TenantId`~~ (dropped in T13, see D7) · `Auth:Dev:{Enabled,UserId,Email,DisplayName}` ·
`Auth:SignIn:{AllowedEmailDomains,AllowedEmails}` · `Admin:BootstrapEmails` · `Cors:AllowedOrigins` ·
`RateLimiting:Enabled` · `Session:TicketTtlSeconds` (30), `Session:AuthFrameTimeoutSeconds` (5),
`Session:MaxPendingAuthConnections` (8).

**API shapes.** `POST /v1/auth/sso/token` and `POST /v1/auth/dev/sign-in` →
`200 {accessToken, expiresAt, user:{id,email,displayName,role}}` plus
`Set-Cookie: presenter_rt=…; HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`. `POST /v1/auth/refresh` reads that
cookie, rotates, returns the same body. Cookie-authenticated mutations (`refresh`, `logout`) additionally require an
`Origin` header present in `Cors:AllowedOrigins` and return `403 auth.forbidden` otherwise — `SameSite=Strict` does
not stop a hostile sibling subdomain, and CORS blocks reading a response rather than sending a simple POST.
`POST /v1/sessions/ticket` → **200** `{ticket, expiresInSeconds: 30}` (a synchronous action result, conventions
§4 — not a created resource, so no `Location`). `GET /v1/auth/sso/providers` and `GET /v1/presentations` both use
the list envelope `{items, page, pageSize, total}`. Rate-limited routes carry `RateLimit-Limit`,
`RateLimit-Remaining`, `RateLimit-Reset` and, on 429, `Retry-After`. Every error is Problem Details + `code` from
the existing catalogue.

### 4.4 Sequence diagram of the new hot path

```
Sign-in
  Browser → GET /v1/auth/sso/google/authorize?redirect_uri&code_challenge&state
    → SsoService.BuildAuthorizationUrl → seal state (AES-GCM) → 302 accounts.google.com
  Google → GET /v1/auth/sso/google/callback?code&state
    → SsoService.HandleCallback
      → open state, check lifetime + provider binding
      → ISsoStateStore.TryConsumeNonce (Redis SET NX)   ← replay dies here
      → exchange code with Google (HttpClient) → userinfo (incl. email_verified)
      → (BRANCH: identity resolution)
         ├─ (provider, subject) match      → link, always
         ├─ email match AND provider-verified → link
         ├─ email match, NOT verified      → refuse auth.signup_not_allowed  ← takeover dies here
         └─ no match + policy allows       → create user (role = admin if ∈ Admin:BootstrapEmails)
      → user.is_disabled? → refuse auth.account_disabled
      → ISsoCodeStore.Issue (Redis, 60 s) → 302 redirect_uri?code&state
  Browser → POST /v1/auth/sso/token {code, codeVerifier}
    → ISsoCodeStore.Claim (GETDEL)  ← reuse dies here
    → verify PKCE S256 → re-check is_disabled → ITokenService.Issue
    → refresh row (hash only) + Set-Cookie → {accessToken, user}

Refresh
  Browser → POST /v1/auth/refresh (cookie)
    → Origin ∈ Cors:AllowedOrigins?  ← CSRF dies here
    → UPDATE refresh_tokens SET is_revoked = true WHERE id = @id AND is_revoked = false
      → rows affected = 1 ? issue successor : 401   ← concurrent double-redemption dies here
    → re-check is_disabled → new access token + rotated cookie

Start a presentation
  Present.tsx → POST /v1/sessions/ticket (Bearer) → is_disabled re-checked
    → ITicketStore.Issue (Redis, 30 s, single use) → 200 {ticket}
  bridgeClient → WS /ws                      (anonymous upgrade — a browser cannot send Bearer here)
    → PresenterBridge.HandleAsync → accept socket
      → pending-auth guard (≤ Session:MaxPendingAuthConnections) → await first text frame
        ├─ {"type":"auth","ticket"} valid → ITicketStore.Claim (GETDEL) → userId
        └─ anything else / invalid / timeout → close 4401 session.ticket_invalid   ← NO slot taken
      → ONLY NOW: CAS single-client slot (unchanged) → loser gets busy 1013
    → "start" → IPresentationRepository.LoadAsync(id, ownerId)   ← 404 for another user's id
      → Presenter.StartAsync(id, fromIndex, ownerId) → PresenterStartResult{upstream, upstreamSessionId, model}
        → ISessionRecorder.BeginAsync(...) → sessions row, started_at, upstream
        → LiveSession … (unchanged hot path: audio, transcript, silence pump)
           ├─ Slide event  → recorder tracks current slide
           ├─ Transcript   → recorder aggregates deltas per role, stamps slide_no
           └─ Usage/Closed → recorder.EndAsync (idempotent barrier)
    → finally: await recorder.EndAsync before detaching handlers ← disconnect race dies here

Parallel paths checked:
  • StartAsync — 2 production call sites (PresenterBridge.cs:144, Cli/RunCommand.cs:149) plus 9 test files
    (BridgeContractTests, BridgeSlotTests, BridgeStartTests, BridgeTests, PresenterTests, CliTests,
    FilePresentationRepositoryTests, FakeLiveServer, LiveSessionTests). All are in T8's budget.
  • Presentation reads — PresentationEndpoints, the load delegate in DependencyInjection.cs:68-84, and the
    file reader, which moves to IPresentationImportSource rather than implementing the scoped contract.
  • Parity trio callers — Library.tsx:15 and Present.tsx:130; /api/config has none.
  • OpenAPI — every PR that adds an endpoint regenerates web/shared/openapi/v1.json in that same PR.
```

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| `POST /v1/auth/dev/sign-in` | new; Development only | T6 |
| `GET /v1/auth/sso/providers` (envelope), `.../{provider}/authorize`, `.../{provider}/callback`, `POST /v1/auth/sso/token` | new | T5, T6 |
| `POST /v1/auth/refresh`, `POST /v1/auth/logout` (Origin-checked), `GET /v1/auth/me` | new | T4, T6 |
| `POST /v1/sessions/ticket` | new, 200 | T7 |
| `/ws` | anonymous upgrade; ticket claimed **before** the slot; owner on the connection | T7 |
| `GET /v1/presentations`, `GET /v1/presentations/{id}`, `GET /v1/config` | replace the `/api` trio, owner-scoped, paged list | T8 |
| `GET /api/presentations`, `/api/presentations/{id}`, `/api/config` | **deleted** | T8 |
| `/decks/**`, `/health`, `/openapi/v1.json` | unchanged, anonymous | — |
| `presenter-cli import` | new command; CLI gains persistence DI | T10 |
| `presenter-cli run` | `--owner`; attaches the recorder | T9, T10 |
| `web/app` sign-in page + header | real SSO / dev sign-in | T11a |
| `web/app` Present | ticket before WS | T11a |
| `web/app` Library, Present | `/v1`, paged list | T11b |
| `web/admin` `AdminRoute` | `role === "admin"` | T11a |
| `web/shared` auth store, api client | memory token, refresh on 401, credentials + Origin on refresh | T11a |
| `web/shared/openapi/v1.json`, `generated.d.ts` | regenerated **in every PR that changes endpoints** | T6, T7, T8 |
| `appsettings.Example.json`, `docker-compose.yml`, `AGENTS.md`, CI | new keys, connection strings, runbook, Docker for Testcontainers | T2, T12 |

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management — what survives a crash mid-operation? | A crash mid-run leaves a `sessions` row with a null `ended_at`; the recorder writes `started_at` immediately rather than at the end, so the row is evidence, not a lie. A *disconnect* is not a crash: the bridge awaits the recorder's idempotent completion barrier before detaching, so an ordinary drop still finalises. A startup sweep of stale rows is **not** in scope. Tickets and SSO codes expire by TTL, so a crash strands nothing. |
| Data consistency — orphans, races, double-apply? | Ticket and SSO-code claims are atomic `GETDEL`. Refresh rotation revokes conditionally and checks the affected-row count, so exactly one concurrent redeemer wins. Import upserts on `(owner_id, slug)`. Turn rows carry an `ordinal` so ordering never depends on clock resolution. The recorder's `EndAsync` is idempotent, so `Closed` plus disconnect cleanup cannot double-write. |
| User experience — root problem or symptom; any surprise? | Root problem: there was no user. The visible surprise is that the library is **empty after first sign-in** until `presenter-cli import` runs for that account — called out in the runbook and in AC4. |
| Backward compatibility — existing data / sessions / configs? | There is no production data; the files under `presentations/` stay in the repo as the import source and as parser fixtures. `Auth:Dev:*` keys keep their names but change meaning (they now seed a row). Removing `/api/*` breaks any external caller — grep says there are none left after plan 003. |
| Error recovery — what happens on failure? | Postgres or Redis unreachable at startup → the API fails fast with a clear message rather than serving a half-broken surface. Mid-run Redis loss does not kill a live session (the ticket was already spent); it blocks new starts. A failed callback returns one fixed message and logs the detail. A failed `StartAsync` still finalises the session row through the same barrier. |
| Logging & debugging — enough to diagnose in the field? | User id and session id on every session log line; never the token, the ticket, the state blob or transcript text (conventions §9). SSO failures log the internal reason at warning with the trace id that the fixed client message also carries. Refused links (unverified email collision) and disabled-account refusals are logged at warning — they are the security-relevant events. |
| Edge cases — empty, huge, repeated, concurrent, interrupted? | Empty library; a user with no `external_logins`; two browser tabs racing for the single presenter slot (still `1013`); unauthenticated sockets parked on the pending-auth guard; a StrictMode double-mount redeeming the SSO code twice (the second gets `auth.sso_code_used` — the callback page treats it as success if a token is already held); a very long transcript (turns flush on role change); a ticket claimed 31 s later; two sequential runs in one process (handlers must not leak). |

**Risks**
- **R1 — the live path regresses while the handshake changes.** The frozen frames, `1013` and `1011` are covered by
  `BridgeContractTests`; T7 adds cases rather than editing existing ones, and the manual runbook re-runs the Ricoh
  deck in real Chrome before the plan is called done.
- **R2 — Testcontainers makes CI slower or flaky.** Containers are shared per collection fixture, not per test;
  if CI time grows unacceptably the integration collection can move to its own job. Local Windows runs need
  `DOCKER_HOST` for the WSL daemon — documented in `AGENTS.md`, and the fixture fails with that hint.
- **R3 — pruning ~2 500 lines of inkspoke auth drops a security check silently.** Mitigation: port
  `OAuthSettingsValidatorTests`, `SsoCallbackErrorLeakTests` and the PKCE/linking cases from `SsoServiceTests`
  *first*, so the copy is measured against its own oracles.
- **R4 — the refresh cookie deviates from inkspoke**, so the two codebases' clients are not interchangeable.
  Accepted at G1; conventions §9 require it and the SPA is same-origin.
- **R5 — owner scoping missed on a future read path.** The split interfaces make it a compile error rather than a
  review catch; T13 audits every new entity for a caller.
- **R6 — moving the ticket claim ahead of the CAS slot changes the order of two concurrency primitives.** A bug
  here trades a DoS for a lost slot. Mitigated by the pending-auth bound, by releasing the pending slot in a
  `finally`, and by a test proving a stalled unauthenticated socket cannot block a valid ticket holder.

**Rollback:** the work is additive until T8. If it must be reverted after merge, `git revert` the range and run
`dotnet ef database update <previous-migration>`; the `presentations/` Markdown files are still the source of truth
on disk, so nothing is lost by dropping the tables.

## 6. Tasks

### T1 — EF Core, entities and the first migration  (AC1, AC4, AC5)
- **Files:** `src/PresenterAi.Infrastructure/PresenterAi.Infrastructure.csproj`,
  `src/PresenterAi.Infrastructure/Persistence/{PresenterAiDbContext,PresenterAiDbContextFactory}.cs`,
  `Persistence/Entities/{User,ExternalLogin,RefreshToken,Presentation,Session,SessionTurn}.cs`,
  `Persistence/Migrations/*`, `src/PresenterAi.Infrastructure/DependencyInjection.cs`
- **Change:** add `Microsoft.EntityFrameworkCore.Design` and `Npgsql.EntityFrameworkCore.PostgreSQL`; map the six
  entities of §4.3 (including `presentations.context` and `external_logins.provider_email_verified`) with explicit
  snake_case columns and server-side opaque id generation; `AddPersistence()` reads `ConnectionStrings:Postgres`
  and registers the context (scoped) with retry; the first migration includes `CREATE EXTENSION IF NOT EXISTS
  vector`. No migration is applied at startup.
- **Verify:** `dotnet ef migrations list`; `dotnet build` clean under warnings-as-errors.
- **Test that dies if this breaks:** `MigrationsApplyToPostgresTests.Migrations_apply_to_a_blank_database` (T2).

### T2 — Testcontainers fixtures and CI  (AC8)
- **Files:** `tests/PresenterAi.Integration.Tests/` (new project, added to `PresenterAi.slnx`),
  `Support/{PostgresFixture,RedisFixture,IntegrationCollection}.cs`, `MigrationsApplyToPostgresTests.cs`,
  `.github/workflows/ci.yml`, `AGENTS.md`
- **Change:** collection fixtures starting `pgvector/pgvector:pg17` and `redis:7-alpine`, shared per collection;
  a fixture failure names `DOCKER_HOST` explicitly for Windows/WSL; CI runs the new project in the existing
  dotnet job.
- **Verify:** `dotnet test tests/PresenterAi.Integration.Tests` green locally and on CI.
- **Test that dies if this breaks:** the migrations test itself.

### T3 — Redis auth-state stores  (AC3, AC6, AC7)
- **Files:** `src/PresenterAi.Infrastructure/Redis/{RedisConnection,TicketStore,SsoCodeStore,SsoStateStore}.cs`,
  `src/PresenterAi.Application/Auth/{ITicketStore,ISsoCodeStore,ISsoStateStore}.cs`, `DependencyInjection.cs`
- **Change:** add `StackExchange.Redis`; register `IConnectionMultiplexer` as a singleton from
  `ConnectionStrings:Redis`, **required** (startup fails with a clear message when absent); implement the three
  stores of §4.3 with atomic `GETDEL` / `SET NX`.
- **Verify:** integration tests against the Redis container: issue → claim → second claim returns null; two
  parallel claims yield exactly one winner; nonce replay returns false; expiry honoured with a short TTL.
- **Test that dies if this breaks:** `TicketStoreTests.Two_parallel_claims_yield_exactly_one_winner`.

### T4 — JWT issuing, validation and atomic refresh rotation  (AC2, AC6)
- **Files:** `src/PresenterAi.Api/Auth/{JwtSettings,TokenService,RefreshCookie,OriginGuard}.cs`,
  `src/PresenterAi.Api/Program.cs`, delete `src/PresenterAi.Api/Auth/DevAuthHandler.cs`,
  `tests/PresenterAi.Api.Tests/AuthTests.cs`
- **Change:** add `Microsoft.AspNetCore.Authentication.JwtBearer`; HMAC-SHA256 tokens carrying `sub`/`email`/`role`
  with 30 s clock skew; refresh tokens opaque, stored as a SHA-256 hash; rotation is a **conditional** update
  (`WHERE id = @id AND is_revoked = false`) that issues a successor only when exactly one row was affected;
  `is_disabled` re-checked on every rotation; httpOnly `SameSite=Strict` cookie scoped to `/v1/auth`;
  `OriginGuard` rejects cookie-authenticated mutations whose `Origin` is not in `Cors:AllowedOrigins`
  (`403 auth.forbidden`); the JWT challenge keeps emitting Problem Details `auth.required` through `Problems`
  (the behaviour `AuthTests` already pins).
- **Verify:** `dotnet test tests/PresenterAi.Api.Tests --filter Auth`, plus an integration test firing two
  simultaneous refreshes with the same cookie.
- **Test that dies if this breaks:** `RefreshTokenTests.Two_concurrent_redemptions_issue_exactly_one_successor`
  and `RefreshTokenTests.Cross_origin_refresh_is_rejected`.

### T5 — SSO service for Google and Microsoft  (AC1, AC7)
- **Files:** `src/PresenterAi.Api/Auth/{OAuthSettings,OAuthSettingsValidator}.cs`,
  `src/PresenterAi.Infrastructure/Identity/{SsoService,ProviderClient,SignInPolicy}.cs`,
  `tests/PresenterAi.Integration.Tests/Sso/*`
- **Change:** copy `OAuthSettings` + validator with the origin header; rewrite `SsoService` against our `User`:
  sealed state, 10-minute lifetime, provider binding, nonce via `ISsoStateStore`, S256 PKCE required, token
  exchange through `IHttpClientFactory`; identity resolution per §4.4 — `(provider, subject)` always links, email
  links **only when the provider asserts the address is verified**, an unverified collision is refused; issuer /
  tenant bound per provider; `SignInPolicy` applies `Auth:SignIn:*` **before** a user row is created and grants
  `role = admin` for `Admin:BootstrapEmails`; `is_disabled` refuses with `auth.account_disabled`.
- **Verify:** ported inkspoke tests green — validator cases, PKCE missing/wrong/truncated, code expired/used,
  linking and case-insensitive email lookup, callback error-leak cases — plus new cases for the unverified-email
  collision, the allowlist, bootstrap admin and a disabled account.
- **Test that dies if this breaks:** `SsoServiceTests.Unverified_provider_email_does_not_link_to_an_existing_user`.

### T6 — Auth endpoints, dev sign-in and rate limiting  (AC1, AC2, AC7, AC8)
- **Files:** `src/PresenterAi.Api/Endpoints/AuthEndpoints.cs`,
  `src/PresenterAi.Api/Middleware/AuthRateLimiting.cs`, `src/PresenterAi.Contracts/Auth/*.cs`, `Program.cs`,
  `web/shared/openapi/v1.json`, `web/shared/src/api/generated.d.ts`
- **Change:** the nine `/v1/auth/*` routes of §4.5 as one endpoint group, Problem Details throughout, with
  `Produces` declarations for OpenAPI; the providers response uses the list envelope; `dev/sign-in` is mapped only
  when `Auth:Dev:Enabled && env.IsDevelopment()` and refuses a disabled user; `AuthRateLimiting` pruned to four
  policies (authorize, callback, token, refresh) emitting `RateLimit-Limit`, `RateLimit-Remaining`,
  `RateLimit-Reset` and `Retry-After`. **Regenerate the OpenAPI snapshot and TS client in this task** — the drift
  test fails otherwise.
- **Verify:** endpoint tests, including a factory run with `Production` asserting `dev/sign-in` is 404, a
  disabled-user case per issuing route, and a rate-limit header assertion; `bun run generate-client` shows no diff
  afterwards.
- **Test that dies if this breaks:** `DevSignInTests.Is_not_mapped_outside_development`,
  `AuthEndpointTests.A_disabled_user_cannot_obtain_a_token`, `RateLimitHeaderTests`.

### T7 — Session tickets and the `/ws` handshake  (AC3)
- **Files:** `src/PresenterAi.Api/Endpoints/SessionEndpoints.cs`,
  `src/PresenterAi.Api/Realtime/PresenterBridge.cs`, `tests/PresenterAi.Api.Tests/BridgeContractTests.cs`,
  `web/shared/openapi/v1.json`
- **Change:** `POST /v1/sessions/ticket` (Bearer, `is_disabled` re-checked) issues a 30 s single-use ticket and
  returns **200**; `/ws` drops `RequireAuthorization()` and instead requires `{"type":"auth","ticket":…}` as the
  **first** text frame within `Session:AuthFrameTimeoutSeconds`, closing `4401 session.ticket_invalid` on anything
  else. **The ticket is claimed before the single-client CAS slot is taken**, with a bounded pending-auth guard
  (`Session:MaxPendingAuthConnections`, released in a `finally`) so unauthenticated sockets cannot occupy or even
  observe the slot; the claimed user id is stored on the connection and used for `start`. The CAS slot itself,
  the frozen frames, `1013` and `1011` are otherwise untouched. Regenerate the OpenAPI snapshot.
- **Verify:** new contract cases for missing / malformed / expired / reused ticket, for the happy path, and for a
  stalled unauthenticated socket **not** blocking a valid ticket holder or provoking `busy`; existing
  `BridgeContractTests` cases stay green unedited.
- **Test that dies if this breaks:** `BridgeContractTests.First_frame_without_a_valid_ticket_closes_4401` and
  `BridgeSlotTests.A_stalled_unauthenticated_socket_does_not_occupy_the_slot`.

### T8 — Presentations in Postgres, owner scoping, `/v1` cutover  (AC1, AC4, AC8)
- **Files:** `src/PresenterAi.Application/Content/{IPresentationRepository,IPresentationImportSource}.cs`,
  `src/PresenterAi.Application/Presenting/{IPresenter,Presenter,PresenterEvents}.cs`,
  `src/PresenterAi.Infrastructure/Content/{PostgresPresentationRepository,FilePresentationRepository}.cs`,
  `src/PresenterAi.Infrastructure/DependencyInjection.cs`,
  `src/PresenterAi.Api/Endpoints/{PresentationEndpoints,ConfigEndpoints}.cs` (delete the `/api` mappings),
  `src/PresenterAi.Api/Realtime/PresenterBridge.cs`, `src/PresenterAi.Cli/RunCommand.cs`,
  `web/shared/openapi/v1.json`, and **all nine test files listed in §3**:
  `tests/PresenterAi.Api.Tests/{BridgeContractTests,BridgeSlotTests,BridgeStartTests,BridgeTests}.cs`,
  `tests/PresenterAi.Application.Tests/Presenting/PresenterTests.cs`, `tests/PresenterAi.Cli.Tests/CliTests.cs`,
  `tests/PresenterAi.Infrastructure.Tests/Content/FilePresentationRepositoryTests.cs`,
  `tests/PresenterAi.Infrastructure.Tests/Live/{FakeLiveServer,LiveSessionTests}.cs`
- **Change:** `IPresentationRepository.{ListAsync,LoadAsync}` take an owner and are implemented **only** by
  `PostgresPresentationRepository`; `FilePresentationRepository` moves behind the new ownerless
  `IPresentationImportSource` and keeps its own tests; `StartAsync` takes an owner and returns
  `PresenterStartResult` (§4.1), so every fake, mock and harness in the nine test files is updated in this task.
  `PostgresPresentationRepository` reads owner-scoped rows, reparses the stored script with `ScriptParser` and
  supplies `presentations.context` as the loaded context. `/v1/presentations` returns the paged envelope;
  `/v1/presentations/{id}` returns 404 `presentation.not_found` for someone else's id (never 403 — it must not
  confirm existence); `/v1/config` replaces `/api/config`; no exception message reaches a response body.
  Regenerate the OpenAPI snapshot.
- **Verify:** `dotnet test` (whole solution — this task is expected to touch every test project); an ownership test
  where user B requests user A's presentation id; a test that a loaded presentation's context equals the imported
  file's text.
- **Test that dies if this breaks:** `PresentationEndpointTests.Another_users_presentation_is_not_found` and
  `PostgresPresentationRepositoryTests.Loaded_presentation_carries_its_context_text`.

### T9 — Session recording  (AC5)
- **Files:** `src/PresenterAi.Application/Sessions/ISessionRecorder.cs`,
  `src/PresenterAi.Infrastructure/Sessions/SessionRecorder.cs`,
  `src/PresenterAi.Api/Realtime/PresenterBridge.cs`, `src/PresenterAi.Cli/RunCommand.cs`
- **Change:** `BeginAsync` takes the `PresenterStartResult` and writes the row with `started_at` from
  `TimeProvider`, `upstream` and `upstream_session_id`; the recorder tracks the current slide from `Slide`,
  aggregates `Transcript` deltas per role into turns with `ordinal` and `slide_no`; `EndAsync` is an **idempotent,
  awaitable barrier** stamping `ended_at`, `usage_seconds`, `close_reason` and flushing the open turn, reached by
  `Closed`, by disconnect cleanup or by a failed start. Attached per run by the bridge and by `RunCommand`; the
  bridge awaits `EndAsync` in its `finally` **before** detaching handlers and releasing the slot.
- **Verify:** integration tests driving the in-process fake GPT-Live server: a completed run; a client disconnect
  mid-run; a failed start; a fallback to the second upstream; two sequential runs in one process.
- **Test that dies if this breaks:** `SessionRecorderTests.A_disconnect_still_finalises_the_row` and
  `SessionRecorderTests.Two_sequential_runs_do_not_leak_handlers`.

### T10 — `presenter-cli import` and CLI persistence wiring  (AC4)
- **Files:** `src/PresenterAi.Cli/{Program,ImportCommand,RunCommand}.cs`, `tests/PresenterAi.Cli.Tests/*`
- **Change:** extend the CLI service graph (`Program.cs:63-80`) with `AddPersistence()` and the recorder — it
  currently registers only file content, live sessions and the presenter. `presenter-cli import <glob> --owner
  <email>` reads through `IPresentationImportSource`, resolves and stores each script's context text, upserts on
  `(owner_id, slug)`, and reports created/updated/failed per file. `--owner` must resolve to an existing,
  non-disabled user; an unknown or disabled owner exits non-zero with a clear message (it does **not** create a
  user — accounts come from sign-in). Any parse failure also exits non-zero. `run` gains the same `--owner`.
- **Verify:** import the repo's `presentations/*.md` into a Testcontainers database twice; assert the second run
  updates without duplicating, that Ricoh's context text landed, and that an unknown owner exits non-zero.
- **Test that dies if this breaks:** `ImportCommandTests.Reimporting_updates_instead_of_duplicating` and
  `ImportCommandTests.An_unknown_owner_exits_non_zero`.

### T11a — Frontend: real auth and tickets (still on `/api`)  (AC1, AC2, AC3, AC6)
- **Files:** `web/shared/src/auth/*`, `web/shared/src/api/client.ts`, `web/app/src/routes/SignIn.tsx`,
  `web/app/src/routes/Present.tsx`, `web/app/src/ws/bridgeClient.ts`, `web/app/src/App.tsx`,
  `web/admin/src/components/layout/AdminRoute.tsx`
- **Change:** replace the persisted fake store with an in-memory access token plus a refresh-on-401 interceptor
  (`credentials: "include"` on the refresh call only); a sign-in page offering the configured providers and, in
  development, dev sign-in; a callback route that redeems the code **once** despite StrictMode double-mounting and
  treats `auth.sso_code_used` as success when a token is already held; `Present` fetches a ticket and sends it as
  the first frame; `AdminRoute` requires `role === "admin"`; `devSignIn.ts`'s `DEV_TICKET` is deleted. Library and
  Present keep calling `/api` in this task, now with a Bearer token.
- **Verify:** `bun run lint && bun run build && bun test`; manually: sign in, present the Ricoh deck, confirm the
  app is fully usable **before** any `/v1` content move.
- **Test that dies if this breaks:** `client.spec.ts` auth-header and refresh-on-401 cases.

### T11b — Frontend: move content calls to `/v1`  (AC1, AC8)
- **Files:** `web/app/src/routes/{Library,Present}.tsx`, `web/shared/src/api/generated.d.ts`
- **Change:** Library and Present call `/v1/presentations` and `/v1/presentations/{id}`, reading the paged envelope
  instead of the bare array; regenerate the client against the T8 snapshot.
- **Verify:** `bun run lint && bun run build && bun test`; the CI drift job passes with no diff under
  `web/shared/src/api`.
- **Test that dies if this breaks:** `OpenApiTests` drift check and the Library rendering test.

### T12 — Configuration, compose, docs  (AC8)
- **Files:** `src/PresenterAi.Api/appsettings.{json,Development,Example}.json`, `Program.cs`,
  `docker-compose.yml`, `AGENTS.md`, `docs/reference/001-api-and-code-conventions.md`
- **Change:** document every key of §4.3 with placeholders only; add `Cache-Control: no-store` and `X-Request-Id`
  for `/v1` plus CORS from `Cors:AllowedOrigins`; give the compose `api` service its connection strings; update the
  runbook (`compose up` → `dotnet ef database update` → `import` → `dotnet run`) and strike the frozen-trio section
  of the conventions once it is gone.
- **Verify:** `docker compose config -q`; header tests on a `/v1` response; `rg -n "api/presentations" docs src web`
  returns nothing outside history.
- **Test that dies if this breaks:** `SecurityHeaderTests.V1_responses_are_no_store_and_carry_a_request_id`.

### T13 — Wiring audit
- **Change:** trace every new entity end to end — `AddPersistence`/`AddRedis`/`AddIdentity` registrations → who
  resolves them, **in both the API and the CLI service graphs**; `ISessionRecorder` → attached in both `StartAsync`
  callers; `ITicketStore` → issued in `SessionEndpoints`, claimed in the bridge; `IPresentationImportSource` → the
  importer only; every new config key → the class that reads it; every new endpoint → a caller in `web/` or a test.
  Flag anything with no caller.
- **Verify:** run the app with Serilog at debug and confirm one log line per new entry point during a full
  sign-in → import → present → close cycle; record the checklist in the work log.

## 7. Test strategy

- **Unit:** token issuing/validation, conditional refresh rotation, `OAuthSettings` validation, PKCE verification,
  sealed-state open/lifetime/provider binding, identity resolution incl. the unverified-email refusal, sign-in
  policy (allowlist, bootstrap admin, disabled), turn aggregation from transcript deltas. Deliberately not
  unit-tested: the provider HTTP exchange — covered with a fake `HttpMessageHandler` instead, as inkspoke does; no
  live Google call in CI.
- **Integration** (Testcontainers Postgres + Redis, real DI container, upstream faked):
  `MigrationsApplyToPostgresTests`; `SsoFlowTests` (authorize → callback → token → `/v1/auth/me`);
  `DevSignInTests` (Development and Production); `RefreshTokenTests` (rotation, sequential replay, **two
  concurrent redemptions**, cross-origin refusal); `TicketHandshakeTests` (valid, missing, expired, reused,
  **stalled unauthenticated socket**); `PresentationOwnershipTests` (two users, cross-read returns 404, context
  text round-trips); `SessionRecorderTests` (completed run, disconnect, failed start, upstream fallback, two
  sequential runs); `ImportCommandTests` (import, re-import, unknown owner) driven through the real CLI service
  graph.
- **Contract:** the existing `BridgeContractTests` and `OpenApiTests` must stay green; new cases are added, not
  edited; the OpenAPI snapshot is regenerated in the same task that changes an endpoint (T6, T7, T8).
- **Manual runbook:**

| # | Step | Expected |
|---|---|---|
| 1 | `docker compose up -d postgres redis` | both healthy |
| 2 | `dotnet ef database update` | 6 tables + the `vector` extension present |
| 3 | `dotnet run --project src/PresenterAi.Api` with no OAuth keys | starts; `GET /v1/auth/sso/providers` returns an empty envelope |
| 4 | `POST /v1/auth/dev/sign-in` from the sign-in page | signed in; `GET /v1/auth/me` shows the dev user |
| 5 | Library before import | empty, with the "import your presentations" hint |
| 6 | `presenter-cli import presentations/*.md --owner dev@presenter-ai.local` | 2 created, 0 failed |
| 7 | Library after import | sample + Ricoh listed |
| 8 | Present Ricoh in real Chrome: slides 1–5, `→`, `Space`, `Esc` | identical behaviour to today; AC1–AC10 of plan 001 hold; answers still use the Ricoh context |
| 9 | While presenting, open a second tab and connect | `busy` error frame and close `1013` |
| 10 | Open a socket and send nothing for 10 s, then connect properly in another tab | the idle socket closes `4401`; the proper one presents — it is never told `busy` |
| 11 | Tamper with the ticket in the first frame | socket closes `4401`, reason `session.ticket_invalid` |
| 12 | Close the browser tab mid-run, then query `sessions` | the row has `ended_at`, `usage_seconds` and `upstream` filled |
| 13 | After a normal run, query `sessions` and `session_turns` | one row; turns with slide numbers in `ordinal` order |
| 14 | Configure Google, sign in with two accounts, import for one | each account's Library shows only its own |
| 15 | Set `Auth:SignIn:AllowedEmailDomains` to exclude the second account, sign in again | refused, and no user row is created |
| 16 | Set `is_disabled` on the dev user, then refresh and request a ticket | both refused with `auth.account_disabled` |

## 8. Rollout / phasing

Three sequenced pull requests. The split was revised after review finding F1: the original two-PR split left the
React client unable to authenticate for the whole of PR-A and deferred the OpenAPI snapshot past the endpoints that
change it, so PR-A would have been neither runnable nor CI-green.

- **PR-A — T1, T2, T3, and T12's configuration half.** Packages, schema, Testcontainers and the Redis stores. No
  authentication or routing behaviour changes at all; the app keeps running on the Dev handler and `/api`.
- **PR-B — T4, T5, T6, T7, T11a.** The auth cutover. `DevAuthHandler` dies, JWT and the ticket handshake arrive,
  **and the client learns to sign in and send tickets in the same PR**, so the app is usable at the boundary. Each
  of T6 and T7 regenerates the OpenAPI snapshot, so the drift test stays green.
- **PR-C — T8, T9, T10, T11b, T12's docs half, T13.** The content cutover: owner-scoped Postgres reads, session
  recording, the importer, `/api` deleted and the frontend moved to `/v1`.

`presentations/` and `decks/` stay in the repo as import sources and parser fixtures throughout.

## 9. Open questions

None blocking. Deferred, with reason:
- Quota enforcement and a `users.quota` column — nothing can set a quota until the admin plan; `usage_seconds` is
  recorded now so the data exists when enforcement arrives.
- `invitations` / `allowed_email_domains` tables — configuration covers the policy until there is a UI.
- API keys and the `X-Api-Key` surface — no API client exists while the CLI runs in-process.
- Sweeping stale `sessions` rows with a null `ended_at` after a *process crash* — ordinary disconnects now finalise
  through the recorder barrier (T9), so this is limited to hard kills and stays cosmetic until the Sessions screen.
- An explicit "link another provider to my account" flow for users whose second provider reports an unverified
  email — they can still sign in with the first provider; deferred to the admin/profile plan.

## 10. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-22 | Requirement brief confirmed (G1) | 2 rounds, 8 questions; discovery by two scout agents (inkspoke inventory, presenter as-built) |
| 2026-09-22 | External plan review `pr004-rev-1` (codex, gpt-5.6-sol medium) | 9 blockers + 2 improvements; all folded into revision 2 — see below |
| 2026-09-22 | Plan approved (G2) | revision 2; implementation not started — awaits an explicit instruction |
| 2026-09-22 | Implementation complete (T1–T13), with one exception | Branch `feature/004-identity-persistence`, PR #3. Build 0/0; 207 tests pass. **The exception, corrected after review round 1:** T12's `/v1` `Cache-Control: no-store`, `X-Request-Id` and CORS half had not landed (`docs/review/006`, I-01 and I-02). Manual runbook §7 and the T13 Serilog live cycle are left to the user. Deviations D1–D9 are below. |
| 2026-09-22 | External implementation review, round 1 (two `pi` reviewers, gpt-5.6-sol medium) | 20 findings (A 3 · B 6 · C 2 · D 9). 11 were claimed as blockers; the orchestrator re-traced every one: 9 stay blockers, P-02 is lowered to an improvement, and P-03 is disputed as design (D8). I-10 is deferred to the deployment plan. Fixes are in progress; see `docs/review/006`. |

**Review findings and disposition (revision 2):**

| # | Finding | Disposition |
|---|---|---|
| F1 | PR-A breaks the web client and the OpenAPI drift test | §8 split into three PRs; T11 split into T11a/T11b; snapshot regenerated in T6/T7/T8 |
| F2 | Unauthenticated socket can seize the singleton slot | §4.4 and T7: ticket claimed **before** the CAS slot, bounded pending-auth guard, new slot test |
| F3 | Refresh rotation not atomic under concurrency | §4.1 and T4: conditional revoke with an affected-row check; concurrent-redemption test |
| F4 | Repository signature change omits implementations and test callers | §3 corrected (9 test files); interface split into `IPresentationRepository` + `IPresentationImportSource`; T8 lists every file |
| F5 | Persisted Markdown cannot reproduce the context file | `presentations.context` column; import resolves and stores the text; context round-trip test; AC4 wording |
| F6 | Recorder lacks start metadata and a safe completion boundary | `PresenterStartResult` carries the chosen upstream; `EndAsync` is an idempotent awaited barrier; disconnect/fallback/leak tests |
| F7 | Cross-provider email linking permits takeover | Email linking requires a provider-verified address; unverified collision refused; new test |
| F8 | `is_disabled` stored but never enforced | Enforced at SSO issuance, dev sign-in, refresh and ticket issuance; per-route tests; runbook step 16 |
| F9 | Ticket 201 without `Location`, bare provider list, missing `RateLimit-*` | Ticket is 200; providers use the list envelope; all three `RateLimit-*` headers required in T6 |
| F10 | CLI persistence/recorder wiring and owner lookup undefined | T10 extends the CLI service graph and defines `--owner` resolution and exit codes |
| F11 | `SameSite=Strict` + CORS do not close refresh/logout CSRF | `OriginGuard` on cookie-authenticated mutations; cross-origin refusal test |

**Implementation deviations (recorded during PR-A/PR-B, per "never diverge silently"):**

| # | Deviation from the plan as written | Why |
|---|---|---|
| D1 | §4.1 says the Redis/Postgres registration fails fast at startup. Implemented as **eager configuration validation with a lazy connection**, and reachability moved to `/health`. | `AddPersistence`/`AddRedis` connected at *registration* time, so calling them from `Program.cs` would have forced all 48 (now 72) `Api.Tests` to need live Postgres and Redis. A misconfigured deployment still fails to boot on a missing setting, and `/health` is what an orchestrator reads. |
| D2 | `POST /v1/auth/dev/sign-in` is **absent from the checked-in OpenAPI snapshot**. | It is mapped only when `Auth:Dev:Enabled && IsDevelopment()`, while the drift fixture runs under `Testing`. The frontend therefore calls it with a plain typed `fetch` rather than the generated client. |
| D3 | `AuthTests` now expects `WWW-Authenticate: Bearer` where it expected `Dev`. | The Dev scheme was deleted by T4, so `Dev` became the wrong value. The test was adapted to the new reality rather than production code bent to the old test. |
| D4 | Eight `NuGetAuditSuppress` entries in `PresenterAi.Infrastructure.csproj`. | EF design-time tooling pulls `System.Security.Cryptography.Xml`, whose every stable version carries those advisories (only 11.0.0 previews exist). Suppressed by advisory id so any *other* transitive advisory still fails the build. Revisit when 11.0.0 ships stable. |
| D5 | `presenter-cli run` **without** `--owner` stays file-backed and records no session. With `--owner` it loads the imported presentation from Postgres and records exactly as `/ws` does (T9/T10 as written). | `sessions.user_id` and `sessions.presentation_id` are required foreign keys, and a local-file run has neither row. Keeping the no-owner path file-backed also keeps `Cli.Tests` container-free (PR-C1 seam 2). User decision, 2026-09-22. |
| D6 | A failed start writes **no** `sessions` row; the recorder's `EndAsync` barrier still completes. | `sessions.upstream` is NOT NULL and a failed start has no upstream. §7's "failed start" recorder test asserts exactly this. |
| D7 | `OAuth:Microsoft:TenantId` is **dropped** from §4.3, the option classes and `appsettings.Example.json`. | The T13 audit found that nothing read it. The tenant is part of the configured Microsoft endpoint URLs (`/common/`, `/organizations/` or a tenant id), so a separate key would only have looked meaningful. |
| D8 | Session recording is **best-effort**. If Postgres fails on the begin insert or the final update, the error is logged at Error level and the presentation continues. The recorder's begin/end barrier is an *attempt* barrier: it completes once the write has been attempted. It does not guarantee the write is durable, so a row can be left without `ended_at`. | A recording outage must not stop a presentation that is live in front of an audience. §2 and §9 already exclude "sweeping stale session rows", which anticipates such rows. Review round 1 P-03 asked for failure to stop the run; disputed. The user can overturn this. |
| D9 | The CLI import refuses a script or context file that is itself a symbolic link, and compares paths case-sensitively on Linux. A directory junction or link *inside* the content root is **not** resolved. | The CLI runs with the operator's own rights on the operator's own content, and no remote input reaches these paths: the API no longer reads presentation files. Confinement guards against mistakes, not an attacker (review round 1 P-02). |
