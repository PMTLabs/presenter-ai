# 007 — Plan 004 implementation review, round 2

**Date:** 2026-09-22
**Branch:** `feature/004-identity-persistence` at `06537c3` (PR #3 into `develop`). Range: the round-1 fix diff,
`032f2eb..06537c3`, code only.
**Reviewers:** two fresh, independent, read-only `pi` agents running `openai-codex/gpt-5.6-sol:medium`, with briefs
in `.claude/agent-reports/plan-004/review-r2-{common,identity-brief,persistence-brief}.md`.
- Settled for them: D1–D9 and the I-10 deferral.
- Their task: judge each round-1 fix as complete (A), proven (B), honest (C) and safe (D).

**Verdict from both reviewers:** not ready to merge, with 3 blockers claimed each.
**Orchestrator's verdict after re-tracing:** no finding blocks merging as it stands. Three are real defects that
are cheap to fix, so they are fixed on the branch before merge (see "Triage").

## Summary

Sixteen findings: A 1, B 10, C 0, D 5. Round 1's fixes held up: no reviewer found a round-1 finding left open,
and several topics came back clean:
- the I-01 headers in production;
- the I-06 frame caps;
- P-02 confinement;
- P-05 configuration;
- P-06 schema;
- P-09.

The orchestrator also checked P-06 separately: `dotnet ef migrations has-pending-model-changes` reports no model
drift, so a future migration will not drop the `lower(email)` index.

The real new defects:
- a sign-out made while a refresh is in flight does not stick;
- the 401 interceptor would replay the one-time SSO code exchange;
- rate-limit headers are chosen by a path suffix, not by the endpoint's policy.

## Identity reviewer (`pr004-rev2-identity`)

| Id | Class | Severity (reviewer) | Where | Finding |
|---|---|---|---|---|
| R2-I-01 | D | blocker | `web/shared/src/auth/authStore.ts:28-57,79-89` | A refresh that succeeds after the user has signed out, or after a newer sign-in, saves its session anyway. |
| R2-I-02 | D | blocker | `web/shared/src/api/client.ts:40-63`, `client.spec.ts:168-200` | The 401 interceptor refreshes and retries `POST /v1/auth/sso/token`, which redeems a one-time code. The new body-clone test blesses that replay. |
| R2-I-03 | D | blocker | `Middleware/AuthRateLimiting.cs:29-89` | The header policy is guessed from a case-sensitive `EndsWith` on the path. A 404 on `/v1/x/refresh` gets `RateLimit-Limit: 30`, and `/v1/auth/sso/google/AUTHORIZE` is limited but gets no header, and `Limit: 0` on its 429. |
| R2-I-04 | B | improvement | `SecurityHeaderTests.cs` | No 500 and no streaming case, so assigning the headers after `next()` would pass. |
| R2-I-05 | B | improvement | `CorsTests.cs` | Only `/v1/presentations` and refresh are exercised; logout's credentialed policy is untested. |
| R2-I-06 | B | improvement | `BindingFailureTests.cs`, `DomainExceptionHandler.cs:57-65` | Nothing tests the 413 and 415 mappings. |
| R2-I-07 | B | improvement | `AuthRateLimiting.cs:44-47,82-88` | The limits are written twice, and only authorize is tested. |
| R2-I-08 | B | improvement | `AuthTests.cs:69-99` | The skew tests allow any skew from 21 to 29 s. |
| R2-I-09 | B | improvement | `OAuthSettingsValidatorTests.cs:77-101` | No test that a loopback-looking host is rejected (`localhost.evil.test`, `127.0.0.1.nip.io`). |
| R2-I-10 | B | improvement | `client.spec.ts`, `baseUrl.ts` | No trailing-slash or empty-base cases. |

## Persistence reviewer (`pr004-rev2-persistence`)

| Id | Class | Severity (reviewer) | Where | Finding |
|---|---|---|---|---|
| R2-P-01 | D | blocker | `Realtime/PresenterBridge.cs:21-25,111-120` | When the 90 s start-observation bound expires, cleanup continues with the start still running. The bound's justification ("four routes", "ten seconds for the load") does not match the configuration. |
| R2-P-02 | D | blocker | `Realtime/PresenterBridge.cs:246-258` | The authenticated 1009 close runs from the receive loop, concurrent with the writer's sends. |
| R2-P-03 | A | blocker | `Cli/ImportCommand.cs:74-115`, `Presenter.cs:354-365` | A database failure inside import's per-file catch, or inside the presenter's load, does not reach the new CLI boundary mapping. |
| R2-P-04 | B | improvement | `MigrationsApplyToPostgresTests.cs:35-87` | The catalog oracle samples the schema: the identity indexes are not pinned, and FKs are matched by name only. |
| R2-P-05 | B | improvement | `SessionRecorderTests.cs:135-145` | The error frame used as the failed-start barrier is raised before the recorder is retired. |
| R2-P-06 | B | improvement | `CliTests.cs`, `ImportCommandTests.cs`, `RunCommandTests.cs` | The connectivity path (`Database unavailable.`) is tested only through the classifier, not through a real command. |

## Triage (orchestrator, re-traced in the code)

| Id | Decision | Notes |
|---|---|---|
| R2-I-01 | **Fix**, lowered to improvement | Real, and worse than stated. The refresh rotates the cookie on the server before logout arrives, and logout presents the old, already revoked cookie. `TokenService.LogoutAsync` matches `!IsRevoked`, so it revokes nothing, and the rotated successor survives: the user is signed in again, even after a reload. This predates the fix, and the window is one round trip. The fix is client-only: `signOut` first awaits any in-flight refresh, so logout presents the newest cookie, and a refresh applies its result only if no sign-in or sign-out happened while it was in flight. |
| R2-I-02 | **Fix**, lowered to improvement | Real, but the trigger is contrived: `sso/token` is anonymous and does not answer 401 by itself. The class rule: the `/v1/auth/*` routes other than `/me` do not use the bearer token, so a 401 from them is never an expired access token; they are not refreshed or retried. The clone is kept, and tested on an authenticated route. |
| R2-I-03 | **Fix**, lowered to improvement | Real, but only header accuracy on odd paths. One table maps policy to limit; `AddPolicy`, the success header and `OnRejected` all read the endpoint's rate-limiting metadata (`UseRouting` runs first, at `Program.cs:156`). Fixing this also removes R2-I-07's duplication. |
| R2-I-04 | Deferred | The reviewer confirmed the production path is correct (`OnStarting`, placed inside the exception handler). Pinning it needs test-only endpoints. |
| R2-I-05 | **Fix** | Cheap: one representative route per `/v1` group, plus logout. |
| R2-I-06 | **Fix** | Cheap: one 413 case and one 415 case. |
| R2-I-07 | **Fix** (with R2-I-03) | Assert the advertised limit for all four policies. |
| R2-I-08 | Disputed | The realistic regressions, a dropped `ClockSkew` (the 5-minute default) and a zero skew, both fail the tests (mutation-verified). Pinning an exact 30 s would need an injectable validation clock, or margins that flake on a slow host. |
| R2-I-09 | **Fix** | Cheap: add the deceptive hosts as negative cases. `Uri.IsLoopback` already rejects them. |
| R2-I-10 | **Fix** | Cheap: trailing slashes and an empty base. |
| R2-P-01 | **Fix the claim**; the rest is disputed | The C part is real: there are at most 2 routes (primary and an optional fallback, `UpstreamRoute.cs:32-45`), and nothing bounds the repository load (Npgsql `EnableRetryOnFailure` defaults). The comment is corrected to say what the bound really covers. The behaviour past the bound is not a regression: before round 1, cleanup did not wait at all. The slot is released once the retry-limited load ends. A run lost in that window is best-effort recording (D8). "Indefinitely" requires an unbounded load, and none exists. |
| R2-P-02 | Disputed | .NET's `ManagedWebSocket` serialises every frame, a close included, through its `_sendMutex`; the .NET 10.0.12 runtime ships `_sendMutex`, `SendFrameFallbackAsync` and `SendFrameLockAcquiredNonCancelableAsync`. So a concurrent 1009 close waits for the writer's send, and does not throw. If it waits more than 1 s it aborts, which is the intended bound for a slow peer. |
| R2-P-03 | **Fix** (import only), lowered to improvement | Both paths already end with a defined exit code and no stack trace, which round 1 required. Import is improved to stop at the first database failure with the boundary's line, instead of retrying and failing every remaining file. The presenter's load catch reports "cannot load presentation", and is left as it is. |
| R2-P-04 | Deferred | A full catalog oracle is worth doing, but it is test hardening of a schema the integration tests already exercise. |
| R2-P-05 | Deferred | A causal barrier needs a new test seam. The wrong implementation it describes (a session row inserted after the start has failed) has no path in the recorder. |
| R2-P-06 | Deferred | A real unreachable-database run takes more than 80 s under the production retry strategy (round-1 report). It needs a test-only retry configuration. |

Deferred items are listed for a follow-up after PR #3. None of them changes production behaviour.

## Fixes (landed on `feature/004-identity-persistence`)

One `pi` implementer (gpt-5.6-terra high) worked in the worktree `fix-r2`. The orchestrator corrected its pass,
and the commits went onto the PR branch.

| Commit | Findings | What changed |
|---|---|---|
| `b7a0753` fix(web) | R2-I-01, R2-I-02, R2-I-10 | Session changes bump a generation. A refresh applies its session only when no sign-in or sign-out happened while it was in flight. `signOut` awaits any in-flight refresh, so logout presents the newest cookie. A 401 from `/v1/auth/*` other than `/me` is returned as is. Base-URL tests cover trailing slashes and an empty base. |
| `f6886b2` fix(api) | R2-I-03, R2-I-07 | One `Limits` table feeds the four policies. The success and 429 headers read the endpoint's `EnableRateLimitingAttribute` policy, not the path. Every policy is tested at its exact threshold, plus an uppercase route and a 404 path ending in `/refresh`. |
| `1e2f537` test(api) | R2-I-05, R2-I-06, R2-I-09 | CORS on one route per `/v1` group, and logout's credentialed policy. The 413 and 415 catalogue mappings. The look-alike hosts `localhost.evil.test` and `127.0.0.1.nip.io` are rejected for both providers. |
| `b3f4811` fix(cli) | R2-P-03, R2-P-01 (comment) | A database failure inside import's per-file catch is rethrown to the CLI boundary: one `Database error:` line, exit 1, no `failed:` line. The bridge comment names two routes, two 10 s handshakes and a load bounded only by Npgsql's retries. |

**What the orchestrator changed in the implementer's pass:**

- **R2-I-06:** the implementer added production code to make the test possible: token-route checks that turned a
  non-JSON or oversized body into a `BadHttpRequestException`, and a 30 MiB request limit on the token endpoint.
  Both were removed. TestServer does not enforce Kestrel's body-size limit, so the theory now calls
  `DomainExceptionHandler.TryHandleAsync` directly with a 413 and a 415 `BadHttpRequestException`. It asserts the
  catalogue code and that the raw input is not echoed.
- **Found while doing that:** a non-JSON `POST /v1/auth/sso/token` returns **404, not 415**. When the content-type
  matcher policy rejects the endpoint, the global `MapFallback` is still a candidate and wins. This predates PR #3,
  and the SPA always sends JSON. It is a follow-up.
- **`refreshAuth`:** the generation check now runs after `response.json()`, so a sign-out during the body read is
  honoured.
- **The Api suite had grown from 4 s to 87 s:**
  - the authenticated CORS ticket row waited on the test host's unreachable database; it is now anonymous, because
    CORS runs before authentication;
  - the token rate-limit row sent a well-formed body that reached the code store; it now sends malformed JSON,
    which the limiter counts and binding rejects.

  The suite is back to 4 s.

**Verification on the integrated branch:**

- **Build:** 0 warnings, 0 errors.
- **.NET suite:** Application 52, Infrastructure 31 (+3 Linux-only skipped), Api 137, Cli 11, Integration 40 (+1
  skipped).
- **Api and Cli without a container:** pass.
- **Web:** lint passes; shared 18 and app 22 tests pass; both builds pass.
- **Also:** the secrets guard and `git diff --check`.
- **Orchestrator's mutations, each restored and md5-checked:**
  - both 413 and 415 handler arms deleted;
  - the `signOut` wait removed;
  - the old refresh-only retry rule put back;
  - the token limit set to 3.

  Each mutation failed at least one test. The implementer's own mutations are in
  `.claude/agent-reports/plan-004/impl-review-r2-fixes-report.md` (gitignored): the generation check, the
  suffix lookup, logout's CORS policy, a prefix loopback check, an untrimmed base URL and the import rethrow.

**No round 3.** After re-tracing, no blocker remains.

**Follow-ups after PR #3, none blocking:**

- R2-I-04: a 500 and a streaming case for the `/v1` header oracle.
- R2-P-04: a full catalogue oracle for the schema.
- R2-P-05: a causal failed-start barrier in the recorder tests.
- R2-P-06: a real-command test of the `Database unavailable.` path, with a test-only retry configuration.
- R2-P-02, optional: a single writer that owns both sends and closes.
- The 415→404 routing fallback above.
- I-10: trusted forwarded headers, deferred to the deployment plan.

## Files examined by the reviewers

- **Identity:**
  - `src/PresenterAi.Api/`: `Program.cs`, `Middleware/{V1SecurityHeaders,AuthRateLimiting}.cs`,
    `Errors/DomainExceptionHandler.cs`, `Auth/OAuthSettings.cs`, and `Endpoints/*`;
  - `web/shared/src/{api/client.ts,api/baseUrl.ts,auth/authStore.ts}` and their specs;
  - the Api tests for headers, CORS, binding, rate limits, JWT and OAuth;
  - `appsettings*.json`, `docker-compose.yml`, `.env.example` and `AGENTS.md`;
  - conventions §9.
- **Persistence:**
  - `src/PresenterAi.Api/Realtime/PresenterBridge.cs`;
  - `src/PresenterAi.Application/Presenting/Presenter.cs`;
  - `src/PresenterAi.Cli/{Program,ImportCommand,RunCommand}.cs`;
  - `FilePresentationRepository.cs` and `PresenterAiDbContext.cs`;
  - the two new migrations and the snapshot;
  - `web/app/src/ws/bridgeClient.ts` and the capture worklet;
  - the bridge, recorder, CLI and migration tests.
