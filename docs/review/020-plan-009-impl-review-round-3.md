# 020 — Plan 009 implementation review, round 3 (final confirmation)

**Date:** 2026-09-23. **Reviewer:** pi `openai-codex/gpt-6-sol:high`, read-only, request `r-i009c`. **Subject:** `110886c..4966189` (round-2 fixes `6094b24`, `1996f81`, `c1a8fac`, plus the bounded dispose and CLI cancel waits in `4966189`). The user asked to stop after this round.

**Disposition (orchestrator):**
- The four round-2 findings are confirmed fixed. The reviewer says the cleanup item is only partial, but the only gap it
  names is the candidate race below.
- The "late candidate" finding is accepted as real in the abstract but **downgraded from blocker to should-fix**:
  - The window runs from the unlocked cancellation check (`Presenter.cs:841`) to `_createSession`, and the only
    thing created in it is an unconnected object.
  - The real `LiveSession.ConnectAsync` links the caller's token into `ClientWebSocket.ConnectAsync` and its
    `WaitAsync` (`LiveSession.cs:133-140`), so an already-cancelled token throws before any socket opens.
  - The presenter then disposes the candidate (`Presenter.cs:869-872`).
  - Even an `ILiveSession` that ignored the token is disposed by the post-connect check (`Presenter.cs:861-865`).
  - A billed upstream would therefore need a non-conforming `ILiveSession` implementation, and even then it would
    live only for one connect.
- **Escalation (third round):** the defect class is the same across all three rounds: off-loop cancellation racing
  loop-side work. Rounds 1 and 2 closed the published-state gaps; this round the reviewer found no new gap with the
  real session. The decision on the residual hardening goes to the user.

---

# r-i009c — final read-only confirmation

Reviewed `110886c..HEAD`; no tests or app run.

| Round-2 finding | Verdict; would the named test fail without the fix? |
|---|---|
| Start/End race | **Fixed** for queued/accepting Starts and reconnects (`Presenter.cs:200-218,264-274,597-608,696-700,836-842`). Yes: the new `PresenterLifetimeTests` queued/acceptance tests and `PresenterPauseCloseTests.End_before_a_reconnect_publishes_its_connect_creates_no_upstream_and_keeps_user_reason` exercise those gaps. The distinct candidate-creation gap below remains. |
| Unbounded cleanup + epoch→startCts gap | **Partially fixed**: `PresenterBridge.cs:494-515` bounds the entire End-to-idle operation; `Presenter.cs:696-700,770-773` links the start to its enqueue-time ticket. Yes: `BridgeBillingGuardTests.Disconnect_during_a_start_stuck_in_its_loader_frees_the_slot_without_an_upstream` and `PresenterLifetimeTests.End_while_a_start_is_being_accepted_cancels_the_load_and_a_later_cap_does_not_relabel_it` fail without their respective fixes. The new bound still permits the late creation below. |
| Writer cancellation | **Fixed** (`PresenterBridge.cs:690-699`); yes, `Writer_cancellation_not_caused_by_the_connection_ends_with_writer_failed` fails with the bare cancellation catch. |
| D5 oracle | **Fixed** (`BridgeBillingGuardTests.cs:84-103`, `ApiFactory.cs` counting timer); yes, restoring per-frame timer `Change` fails the new assertion. |

## Blocker

- **D · blocker — cancellation can still be passed before a candidate is created.** `Presenter.cs:841-855` checks `connectCts.IsCancellationRequested` **before** `_hasDelegationModel`, prompt construction and `_createSession`, without a second check or synchronization with `CancelConnect` (`:264-274`). Hold the injected `hasDelegationModel` callback immediately after the check; call `AbortPendingStart()` (which returns synchronously), or let bridge cleanup (`PresenterBridge.cs:494-504`) release the slot, or advance disposal past its 10-second bound (`Presenter.cs:313-326`). Release the callback: `_createSession` is invoked **after** abort/slot release/disposal. This also applies to fallback candidates and suspended Resume/navigation reconnects, which use the same loop. A session implementation that does not honor the already-cancelled `ConnectAsync` token can then open an unowned/unrecorded upstream. None of the new loader/acceptance/reconnect tests pauses *between the candidate check and factory*. Smallest safe fix: serialize the cancellation decision and candidate creation with the ticket's cancellation gate (including a final check after prompt preparation), and test this precise interleaving through both slot-release and dispose bounds. Merely adding an unlocked check still leaves a check→factory race.

## Improvements

None additional within this diff. No other unbounded **await** found on the newly bounded bridge End, presenter disposal or CLI Ctrl+C End paths (`PresenterBridge.cs:497`, `Presenter.cs:316`, `RunCommand.cs:261-263`); their guarantees depend on closing the candidate gap above. Fresh Starts after idle End/abort, per-talk first-wins reasons, and ticket/CTS lifetime showed no additional new defect in the reviewed paths.

## IS THIS BRANCH READY TO MERGE?

**No:** close the late-candidate gap; this is the last requested review round.

REVIEW COMPLETE r-i009c