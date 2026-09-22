# 005 — External plan review: plan 004 (identity, persistence, auth-state Redis)

**Reviewer:** `codex --yolo -m gpt-5.6-sol -c model_reasoning_effort="medium"`, review-only (no edits, no tests, no
builds, no containers), request_id `pr004-rev-1`, 2026-09-22.
**Subject:** `docs/plan/004-identity-persistence-auth-state.md` (revision 1, draft before fold-in) against
`docs/reference/001-api-and-code-conventions.md`, `docs/research/002-phase-0-platform-architecture.md`, `AGENTS.md`
and the as-built code the plan cites.
**Outcome:** 9 blockers + 2 improvements; verdict NO. The three factual blockers (F4 call-site inventory, F5 context
file, F6 upstream metadata) were re-verified against the code by the orchestrator before fold-in — all three held.
All 11 findings are folded into plan revision 2; the disposition table is in the plan's approval log (§10).

---


### F1 — PR-A breaks the only web client at both HTTP and WebSocket boundaries   [BLOCKER]  [sequencing]
Where: plan §8 lines 543-549; `src/PresenterAi.Api/Program.cs:36-40`; `web/shared/src/api/client.ts:1-12`; `web/app/src/ws/bridgeClient.ts:68-75`
Why it matters: T4 replaces Dev auth with JWT and T7 requires a real ticket, but the frontend does not acquire either until T11 in PR-B. Protected `/api` calls therefore become 401 and `DEV_TICKET` fails. T6 also changes OpenAPI while snapshot regeneration waits for T11, making PR-A CI red (`docs/plan/004-identity-persistence-auth-state.md:165-168`).
Suggested change: Move the frontend auth/ticket and OpenAPI slices into PR-A, retain a tested transition path, or combine the PRs. Exercise the checked-in client at the boundary.

### F2 — An unauthenticated socket can seize the singleton slot before ticket validation   [BLOCKER]  [security]
Where: plan §4.4 lines 287-291 and T7 lines 431-435; `src/PresenterAi.Api/Realtime/PresenterBridge.cs:48-60`
Why it matters: The design deliberately keeps the CAS before the first-frame wait. Any unauthenticated client can repeatedly occupy the sole process-wide slot for `Session:AuthFrameTimeoutSeconds`, causing valid ticket holders to receive `busy`/1013; this is a cheap denial of service and also leaks busy state before authentication.
Suggested change: Claim the first-frame ticket before acquiring `_client` (with a separate bounded pending-auth guard if needed), then run the unchanged CAS/busy behavior for authenticated connections. Add a test proving an unauthenticated stalled socket cannot block a valid one.

### F3 — Refresh rotation is not specified as an atomic single-winner operation   [BLOCKER]  [security]
Where: plan §4.1 lines 193-195, §5 line 334, T4 lines 398-403, §7 lines 512-520
Why it matters: "Revoke and insert inside one transaction" does not prevent two concurrent requests under ordinary isolation from both reading the token as active and issuing two successors. The promised rotation/replay guarantee therefore fails under the attacker-relevant race, while the named test covers only sequential replay.
Suggested change: Require a conditional revoke (`WHERE is_revoked = false` with exactly one affected row), row lock, or serializable transaction, and add a concurrent two-redeemer integration test asserting exactly one token pair is issued.

### F4 — T8's repository signature change omits required implementations and test callers   [BLOCKER]  [wrong-fact]
Where: plan §3 lines 137-150, §4.4 lines 300-304, T8 lines 440-453; `src/PresenterAi.Infrastructure/Content/FilePresentationRepository.cs:8`; `tests/PresenterAi.Application.Tests/Presenting/PresenterTests.cs:14-440`
Why it matters: The asserted inventories omit many test calls/fakes. T8 also changes both repository methods without adapting `FilePresentationRepository`, which must still implement the interface for T10; following the task literally will not compile.
Suggested change: Inventory production plus test implementations/callers explicitly. Decide whether the file reader retains a separate ownerless import interface or accepts/ignores an owner, and include all mocks, harnesses, and tests in T8.

### F5 — Persisted Markdown cannot reproduce the Ricoh presentation's external context   [BLOCKER]  [gap]
Where: plan §4.1 lines 198-203, §4.3 lines 237-242, T10 lines 467-474; `src/PresenterAi.Infrastructure/Content/FilePresentationRepository.cs:44-58`; `presentations/ricoh-delivery-overview.md:1-6`
Why it matters: `ScriptParser` preserves only the context path; the current file repository separately reads that path. The planned table has script/frontmatter but no context content, so a Postgres read cannot reconstruct `LoadedPresentation.Context`, undermining AC4's claim that the imported Ricoh presentation behaves as today.
Suggested change: Persist normalized context text during import (or model it separately) and define its reconstruction and ownership rules; test that imported Ricoh context reaches the presenter.

### F6 — The recorder lacks both required start metadata and a safe completion boundary   [BLOCKER]  [gap]
Where: plan §4.4 lines 292-298 and T9 lines 455-465; `src/PresenterAi.Application/Presenting/Presenter.cs:323-397,778-815`; `src/PresenterAi.Application/Presenting/PresenterSnapshot.cs:4-15`; `src/PresenterAi.Api/Realtime/PresenterBridge.cs:68-76`
Why it matters: `StartAsync` returns only `bool`, the chosen upstream is local to `Presenter`, and no event exposes it, although AC5 requires `sessions.upstream`. On disconnect, bridge cleanup can detach after awaiting upstream close but before the queued `Presenter.Closed` event is delivered, leaving `ended_at`/turns unflushed.
Suggested change: Add an explicit run-start event/result carrying presentation, upstream and upstream-session metadata, plus an awaitable, idempotent run-completion barrier. Test fallback selection, disconnect, failed start, and two sequential runs for handler leakage.

### F7 — Cross-provider email linking permits account takeover unless email authority is verified   [BLOCKER]  [security]
Where: plan §4.1 lines 188-196 and T5 lines 405-416
Why it matters: The design falls back from `(provider, subject)` to lower-cased email but never requires a provider-verified/authoritative email. A provider identity able to assert an existing user's unverified address could be linked to that account.
Suggested change: Require and validate provider-specific verified-email evidence before automatic linking, bind identities to the correct issuer/tenant, and test an unverified collision; otherwise require an already-authenticated explicit link flow.

### F8 — `is_disabled` is stored but never enforced in any token-issuing path   [BLOCKER]  [security]
Where: plan §4.3 lines 230-239, §4.4 lines 275-282, T4-T6 lines 394-426; `src/PresenterAi.Contracts/ErrorCodes.cs:18,62`
Why it matters: The catalogue contains `auth.account_disabled`, yet callback, dev sign-in, refresh, ticket, and `/me` behavior never specifies the check. A disabled existing user can continue obtaining fresh access tokens.
Suggested change: Define enforcement at SSO/dev issuance and refresh (and the intended behavior for already-issued JWTs), then add endpoint tests for each path.

### F9 — The planned auth/session responses do not fully satisfy the adopted HTTP contract   [IMPROVEMENT]  [contract]
Where: plan §4.3 lines 256-262, T6 lines 421-425, T12 lines 491-500; `docs/reference/001-api-and-code-conventions.md:52-65,172-180`
Why it matters: The ticket endpoint specifies 201 without the required `Location`; the provider list is manually expected as bare `[]` despite the mandatory list envelope; and rate limiting names only `Retry-After`, omitting required `RateLimit-Limit`, `-Remaining`, and `-Reset`. AC8/header tests would not catch these violations.
Suggested change: Use 200 for the synchronous ticket action (or add `Location`), specify the provider DTO/envelope, implement all required headers, and add contract tests.

### F10 — CLI database/recorder wiring and owner lookup are absent   [BLOCKER]  [gap]
Where: plan T9-T10 lines 455-474; `src/PresenterAi.Cli/Program.cs:63-80`
Why it matters: The CLI currently registers file content, live sessions, and presenter only. The tasks require EF-backed import and recording but never add persistence/recorder registration or define how `--owner <email>` resolves a missing/disabled user, so AC4 cannot be implemented from the task text.
Suggested change: Add explicit CLI persistence configuration and DI work, owner lookup/error semantics, and end-to-end CLI tests through the real service graph.

### F11 — `SameSite=Strict` and CORS alone do not close refresh/logout CSRF   [IMPROVEMENT]  [security]
Where: plan §2 line 65, §4.3 lines 256-259; `docs/reference/001-api-and-code-conventions.md:174-180`
Why it matters: Strict cookies are still sent from hostile same-site origins (such as a sibling subdomain), while CORS prevents reading a response rather than all simple POSTs. An attacker may rotate or revoke the victim's credential as a denial of service.
Suggested change: Require an allowed `Origin` (or CSRF token) on cookie-authenticated mutations and test rejected same-site cross-origin refresh/logout requests.

## topicsWithNoFindings

None; questions 1-7 each produced at least one finding. The referenced inkspoke commit was not present in the reviewed repository, so source-level weaker-than-inkspoke comparison could not be independently verified.

## IS THIS PLAN READY TO IMPLEMENT?

NO

- F1: PR-A is neither runnable nor CI-green.
- F2: pre-auth sockets can deny the sole presenter slot.
- F3: concurrent refresh redemption can mint multiple successors.
- F4: owner-signature work is incomplete and will not compile as written.
- F5: imported presentations lose external context.
- F6: session recording lacks metadata and reliable finalization.
- F7: unverified email linking permits account takeover.
- F8: disabled accounts can obtain new credentials.
- F10: CLI persistence and owner wiring are undefined.

<!-- REPORT COMPLETE -->
