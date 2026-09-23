# 017 — Plan 009 (upstream billing guards) plan review, round 1

**Date:** 2026-09-23. **Reviewer:** codex `gpt-6-sol` (high), read-only, request `r-p009`. **Subject:** `docs/plan/009-upstream-billing-guards.md` (draft) against the confirmed brief.

**Disposition (orchestrator):** all findings accepted and folded into the plan before approval: the cap is armed at Start acceptance (A1); every connect runs under an off-loop-cancellable `_connectCts` with a cap check before each candidate and after connect; MCP cancellation handled around refresh and reconnect; `writer_failed` and `backpressure` added with an explicit close-path mapping table; stronger test oracles (deadline timestamp, sessions created, no reopen, end reason on row and frame); `ApiFactory.UseFakeClock()` and `AdvanceAndSettleAsync` named. The reviewer wrote two passes into one report; both are kept below.

---

The plan closes the main existing orphan paths and centralizes talk deadlines in the presenter loop.
Two design choices still leave billing and acceptance gaps: the cap starts too late, and reconnect cannot be interrupted by End.
The MCP retry wording must handle cancellation during refresh/reconnect, not only before retry.
The proposed tests need explicit race oracles for reconnect versus End/max-length and for caller cancellation side effects.
The plan is not ready to implement until the two blocker findings below are resolved.

## Blockers

### D1 — Hard cap starts at upstream connect instead of talk start

- **Severity:** blocker
- **Plan section:** §4.1 Max length; §9 decision 2
- **Evidence:** The confirmed brief defines max length as wall-clock from talk start, including pauses (`r-p009-brief.md`, A1, lines 57–60). The plan instead says to arm it when the first upstream connects (`docs/plan/009-upstream-billing-guards.md:215-218`) and repeats that choice in §9 (`:650-655`). The existing presenter performs content loading and connection inside the queued start operation (`src/PresenterAi.Application/Presenting/Presenter.cs:432-437`, with the current connection await at `:576`).
- **Risk:** A slow load or connection can consume time after the user starts the talk without consuming the configured cap. This contradicts A1 and means the max-length acceptance test cannot prove the specified wall-clock boundary.
- **Smallest plan fix:** Record the talk start timestamp and arm the max deadline when the start is accepted, before load/connect. Keep the deadline through connection, pauses, suspension, and reconnect. Retain a separate bounded start/connect cancellation path so an upstream is never held while waiting for the cap event to reach the loop.

### D2 — End/max-length cannot cancel a reconnect that is awaited by the single reader

- **Severity:** blocker
- **Plan section:** §4.1 Resume / navigation; §4.4(b); §5 edge cases; T4
- **Evidence:** Resume and suspended navigation call the extracted connect routine and await it (`docs/plan/009-upstream-billing-guards.md:225-228`, `:358-363`). The plan explicitly says that End during reconnect is queued behind the bounded connect (`:409`). The presenter processes commands serially and awaits `StartAsyncCore` in the event-loop handler (`src/PresenterAi.Application/Presenting/Presenter.cs:432-437`); the current route loop awaits `ConnectAsync` (`:576`).
- **Risk:** While a reconnect is in progress, a queued max-length timer or End command cannot run. The talk can exceed its effective cap by the whole reconnect/retry window, and a browser disconnect or shutdown cannot promptly stop the attempt. A bounded provider handshake limits the overrun but does not satisfy “ends at the effective cap” or the requested reconnect-versus-End race coverage.
- **Smallest plan fix:** Give every reconnect a cancellation source owned by the run and cancel it from End, max-length expiry, shutdown, and bridge abort before waiting for the reconnect task. Make the reconnect path dispose its candidate on cancellation and return the requested end reason. Add a fake-clock test that hangs reconnect, queues End/max-length, then asserts no new session remains and the close reason is preserved.

## Improvements

### D3 — MCP cancellation during refresh/reconnect can still enter the retry catch

- **Severity:** should-fix
- **Plan section:** §4.1 Per-run tool token; T5
- **Evidence:** The plan says the MCP call checks the caller token before any retry and does not mark cancellation as `needs_reconnect` (`docs/plan/009-upstream-billing-guards.md:243-245`). The existing refresh and reconnect retry blocks use broad catches after awaiting the refresh/reconnect (`src/PresenterAi.Infrastructure/Tools/Mcp/McpTool.cs:113-133`, `:159-176`). A caller token can be cancelled during those awaits; the bare catch then follows the retry error path, and the 401 path may call `_setStatusAsync("needs_reconnect", cancellationToken)` (`:126-131`).
- **Risk:** End can still cause a retry attempt or a server-status mutation after cancellation, contrary to AC9.
- **Smallest plan fix:** Specify cancellation handling around both refresh and reconnect: if the caller token is cancelled before or during either operation, return the cancelled result immediately, skip the retry call, and never call `_setStatusAsync`. Keep separate tests for cancellation during refresh and during reconnect and assert both “retry call count = 0” and “status mutation count = 0”.

### B1 — Some proposed race tests identify only the final state

- **Severity:** should-fix
- **Plan section:** T3, T4, T7
- **Evidence:** The plan lists `Max_length_ends_from_presenting|paused|question_hold|stalled_slide` with `EndReason` and `DisposeCount` assertions (`docs/plan/009-upstream-billing-guards.md:481-488`), and a heartbeat test with `fake.ConnectionCount == 0` (`:545-552`). Those fields do not identify when the guard fired or whether a reconnect/start task later created another upstream.
- **Wrong implementation that passes:** A guard can wait until a later queued event to close, or a reconnect can open a second session after the test observes the first disposal; the final `DisposeCount == 1` / active `ConnectionCount == 0` can still pass.
- **Smallest plan fix:** Add assertions for the fake clock timestamp/reason at the deadline, the number of upstream sessions created, and a quiescence barrier after cancellation. For heartbeat and start-observation tests, assert no connection is created after abort and that slot release occurs only after the presenter is idle.

## Decisions review

Decision 2 conflicts with the confirmed brief as described in D1. Decisions 3–9 are compatible with the brief provided reconnect cancellation is added as in D2 and MCP cancellation is made explicit as in D3. The task ordering is otherwise coherent: T1 precedes shape consumers, T3→T4→T5 protects the shared presenter file, and T7/T8 integration work follows the presenter lifecycle changes.

## Readiness

The plan is not ready to implement. Resolve D1 and D2 before approval; take D3 and B1 into the corresponding task text and test oracles.

IS THIS PLAN READY TO IMPLEMENT?
NO — blockers D1 and D2 remain.

REVIEW COMPLETE r-p009
