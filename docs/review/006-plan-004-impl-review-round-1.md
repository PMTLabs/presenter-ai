# 006 — Plan 004 implementation review, round 1

**Date:** 2026-09-22
**Branch:** `feature/004-identity-persistence` at `8199b44` (PR #3 into `develop`), range `origin/develop...HEAD`.
**Reviewers:** two independent read-only `pi` agents running `openai-codex/gpt-5.6-sol:medium`, with briefs in
`.claude/agent-reports/plan-004/review-impl-{identity,persistence}-brief.md`. The shared rules covered the A/B/C/D
classification, blocker or improvement, and CONFIRMED or PLAUSIBLE.
**Verdict from both reviewers:** not ready to merge.

## Summary

Twenty findings in total: A 3, B 6, C 2, D 9. The orchestrator re-traced every blocker in the code before acting
(see "Triage").

- **Real defects:**
  - a start followed at once by a disconnect runs unrecorded, and on a busy loop with no owner;
  - pre-auth WebSocket frames have no size limit;
  - server-initiated closes can wait forever on a peer that never answers;
  - the SPA's 401 interceptor bypasses the single-flight refresh, which signs a user out on reload;
  - binding failures bypass the error contract;
  - the CLI crashes on an unreachable database.
- **A false completion claim:** T12's `/v1` headers half was marked done but never landed, and CORS covers only
  refresh and logout.
- **A first-run runbook that cannot work:** no JWT settings are provided anywhere.

## Identity reviewer (`pr004-rev-impl-identity`)

| Id | Class | Severity | Verdict | Where | Finding |
|---|---|---|---|---|---|
| I-01 | C | blocker | CONFIRMED | `Program.cs:114-156`, plan T12 | No `Cache-Control: no-store` and no `X-Request-Id` on `/v1`, although T12 and "T1–T13 complete" say they are there. `SecurityHeaderTests` does not exist. |
| I-02 | D | blocker | CONFIRMED | `Program.cs:93-97,143` | CORS is only the named `Refresh` policy, and there is no default policy for the other `/v1` routes. |
| I-03 | A | blocker | CONFIRMED | `client.ts:36-58`, `authStore.ts:41-55`, `App.tsx:46-49` | The 401 interceptor starts its own refresh instead of using the single-flight `refreshAuth`. On reload, the startup refresh races the interceptor with the same cookie; the loser calls `clearAuthSession()`. |
| I-04 | A | blocker | CONFIRMED | `AuthEndpoints.cs:37-90`, `PresentationEndpoints.cs:30-41` | Minimal API binding failures never reach the catalogue Problem Details. The affected inputs are missing `redirect_uri`, `code_challenge`, `state`, `code`, a malformed token body, and `page=abc`. |
| I-05 | D | blocker | PLAUSIBLE | `PresenterBridge.cs:116-122,185-197` | The pending-auth permit is released only after `CloseAsync(..., None)`, which waits for the peer's close frame, so a silent peer holds it indefinitely. |
| I-06 | D | blocker | CONFIRMED | `PresenterBridge.cs:128-144,199-224` | Both receive paths append fragments to an unbounded `MemoryStream`. Anonymous clients can stream fragments for the whole auth timeout. |
| I-07 | B | improvement | CONFIRMED | `AuthRateLimiting.cs:46-55`, `AuthEndpointSecurityTests.cs:51-75` | Every success reports `RateLimit-Remaining: 1` and `RateLimit-Reset: 60` as constants, and the headers are still emitted when rate limiting is disabled. The test checks only that the headers exist on the 429. |
| I-08 | B | improvement | CONFIRMED | `AuthTests.cs` | No negative JWT cases: wrong issuer, wrong audience, expired, not yet valid, wrong signature, and the skew boundary. |
| I-09 | B | improvement | CONFIRMED | `OAuthSettings.cs:17-56`, `OAuthSettingsValidatorTests.cs` | The validator ignores `ClientSecret`, the three endpoints and `AllowedRedirectUris` for an enabled provider, and the test's valid fixture has only a `ClientId`. |
| I-10 | D | improvement | PLAUSIBLE | `AuthRateLimiting.cs:59-72` | Behind a proxy, every client shares the proxy IP as its rate-limit partition, and no forwarded-headers handling is configured. |

## Persistence reviewer (`pr004-rev-impl-persist`)

| Id | Class | Severity | Verdict | Where | Finding |
|---|---|---|---|---|---|
| P-01 | D | blocker | CONFIRMED | `PresenterBridge.cs:94-105,257-270,311-341` | Disconnect cleanup neither tracks nor awaits the fire-and-forget start observation. A start that is queued but not yet dequeued leaves the snapshot `idle`, so the end step is skipped, the recorder is ended before `BeginAsync`, and the slot is released while the start still runs. |
| P-02 | A | blocker | CONFIRMED | `ImportCommand.cs:47-52,134-140`, `FilePresentationRepository.cs:55-68` | Import confinement is only lexical. A symlinked script or context file escapes it, and `OrdinalIgnoreCase` accepts a sibling directory that differs only in case on a case-sensitive filesystem. |
| P-03 | D | blocker | CONFIRMED | `SessionRecorder.cs:257-280,344-385,421-434` | A failed begin insert or final update is logged, and the barrier completes anyway: the run continues unrecorded, or `ended_at` stays null. |
| P-04 | D | blocker | CONFIRMED | `Cli/Program.cs:30-60`, `ImportCommand.cs:30-31`, `RunCommand.cs:34-45` | An unreachable database throws out of `Main` during the owner lookup: a stack trace and an undefined exit code instead of 0/1/2. |
| P-05 | C | blocker | CONFIRMED | `AGENTS.md:39-47,58-59`, `appsettings*.json`, `docker-compose.yml:31-51` | The first-run steps and the compose `full` profile provide no `Jwt:*` settings. `ValidateOnStart` stops the API before it serves anything. |
| P-06 | D | improvement | CONFIRMED | `PresenterAiDbContext.cs:25,119-120`, the migration and the snapshot | The model and snapshot describe a plain unique index on `Email`, while the migration creates `lower(email)`. `(user_id, started_at)` is ascending, where §4.3 says `desc`. |
| P-07 | B | improvement | CONFIRMED | `MigrationsApplyToPostgresTests.cs:20-32` | The oracle checks only six table names and `vector`: no columns, indexes, uniqueness, FKs or delete behaviour. |
| P-08 | B | improvement | CONFIRMED | `SessionRecorderTests.cs:135-143,347-355` | `Failed_start_leaves_no_row` polls for a count of 0, which is already true, so the assertion is vacuous. |
| P-09 | D | improvement | CONFIRMED | `Cli/Program.cs:105-123` | The file-backed `run` graph is the only graph built without `ValidateScopes` and `ValidateOnBuild`. |
| P-10 | B | improvement | CONFIRMED | `ImportCommandTests.cs:45-60`, `RunCommandTests.cs:40-55` | The unknown-owner tests make no database assertion (import), or query `sessions.user_id` with an email (run), which can never match an opaque `usr_…` id. |

## Triage (orchestrator, re-traced in the code)

| Id | Decision | Notes |
|---|---|---|
| I-01 | **Fix** | Confirmed. No `no-store` or `X-Request-Id` anywhere in `src/` or `tests/`. T12 in the plan and the work log overstated what landed. |
| I-02 | **Fix** (improvement) | Confirmed. Both documented deployments are same-origin: the API serves the SPA, and dev uses the Vite proxy. So nothing breaks today, but T12 and conventions §9 require CORS from `Cors:AllowedOrigins` without credentials on `/v1`, and with credentials only on the cookie routes. |
| I-03 | **Fix**, class | Confirmed. The same class covers the auth fetches in `authStore.ts` (refresh, dev sign-in, logout), which use relative URLs while `createApiClient` honours `VITE_API_URL`. One API base for every auth call. |
| I-04 | **Fix**, class | Confirmed. The fix funnels every binding failure (`BadHttpRequestException`) into `validation.failed` in one place, not per endpoint. |
| I-05 | **Fix**, class | This is how `ManagedWebSocket.CloseAsync` works: it waits for the peer's close frame. Same class: `SendBusyAsync` (busy, 1013) and the writer's backpressure close (1011). The writer's close runs inside `ClientConnection.DisposeAsync` on the owned path, so a silent peer there also **holds the presenter slot**. Every server-initiated close is bounded, and the permit is released before the close. |
| I-06 | **Fix** | Confirmed at both receive sites. |
| I-07 | **Fix** | The constant `Remaining: 1` is false data. Headers must carry true values or be omitted, and none when disabled. |
| I-08, I-09 | **Fix** | Tests, plus validator completeness. |
| I-10 | **Deferred** | The deployment topology (proxy or ingress) is not defined yet. Trusted forwarded headers belong to the deployment plan. The partition is not spoofable as written. The user confirmed deferring it on 2026-09-22. |
| P-01 | **Fix**, class | Confirmed, with a second path the reviewer missed. The start is passed `context.RequestAborted`, so a network drop cancels the *observer* while the queued command still runs and succeeds: the observer retires the recorder, and the run is unrecorded. Cleanup must await the start's real outcome, then end the run if it started. |
| P-02 | **Fix**, lowered to improvement | The CLI runs with the operator's own rights on the operator's own content root; no remote input reaches these paths, and the API no longer reads presentation files. Not a security boundary, but the case-insensitive comparison on Linux is a correctness bug. Fix the comparison per platform and refuse a script or context file that is itself a link. Directory junctions inside an operator-owned root are out of the threat model (recorded as deviation D9, which the user confirmed on 2026-09-22). |
| P-03 | **Disputed**; the comments are corrected | Best-effort recording is the design: a recording outage must not stop a presentation that is live in front of an audience, and plan §2/§9 explicitly excludes "sweeping stale session rows", which anticipates rows left with a null `ended_at`. The valid part is C: the comments call it a completion barrier when it is an attempt barrier. Recorded as deviation **D8**; the user confirmed best-effort on 2026-09-22. |
| P-04 | **Fix** | Confirmed. The owner and slug lookups are outside every catch. |
| P-05 | **Fix**, class | Confirmed. `appsettings.json` and `appsettings.Development.json` have no `Jwt` section, and compose maps none. Same class: dev refresh through the Vite proxy sends `Origin: http://localhost:47914`, which `OriginGuard` refuses unless `Cors:AllowedOrigins` lists it, and no checked-in dev config does. |
| P-06 | **Fix** (improvement) | A new migration for `started_at desc`; the model and snapshot are made honest about the `lower(email)` expression index. |
| P-07 to P-10 | **Fix** | Oracles, and the missing scope validation. |

## Files examined by the reviewers

- **Identity:**
  - `src/PresenterAi.Api/{Auth,Endpoints,Middleware}/*`, `Program.cs`, and the auth half of `Realtime/PresenterBridge.cs`
  - `src/PresenterAi.Infrastructure/{Identity,Redis}/*`, `src/PresenterAi.Application/Auth/*`, `src/PresenterAi.Contracts/Auth/*`
  - `web/shared/src/{auth,api}/*`, `web/app/src/{App.tsx,routes/*,ws/bridgeClient.ts}`, `web/admin/.../AdminRoute.tsx`
  - the auth, SSO, bridge-contract, slot and Redis tests
- **Persistence:**
  - `src/PresenterAi.Infrastructure/{Persistence,Content,Sessions}/**`, `DependencyInjection.cs`
  - `src/PresenterAi.Application/{Content,Sessions,Presenting}/*`
  - `PresenterBridge.cs`, `PresentationEndpoints.cs`, `src/PresenterAi.Cli/*`
  - `docker-compose.yml`, `appsettings*.json`, `ci.yml`, `AGENTS.md`
  - the bridge, recorder, content, CLI and migration tests

The raw reports are in `.claude/agent-reports/plan-004/review-impl-{identity,persistence}-report.md` (gitignored).
