# 013 — Plan 007 implementation review, round 3

**Date:** 2026-09-23. **Reviewer:** `pi` `openai-codex/gpt-6-sol:high`, read-only, request `r007-impl-03` (same session as rounds 1–2). **Scope:** confirmation of `088ecf1` (head `d1ec0d8`) against the disposition in `docs/review/012`, not a new whole-branch sweep. The reviewer's report follows unchanged; the orchestrator's disposition is at the end.

---


Read-only confirmation of `088ecf1` on branch head `d1ec0d8`, against the disposition in `docs/review/012-plan-007-impl-review-round-2.md`. No tests/builds run. Paths below are repository-relative. This is **not** another whole-branch sweep.

## Disposition of rows marked Fix

| Round-2 ID | Verdict | Code/test evidence |
|---|---|---|
| D-06 | **Partly fixed** | `src/PresenterAi.Application/Presenting/Presenter.cs:871-877,932-942,699-724,1640-1648`: No now enters `WaitingOnSlide`; voiced output and the unanswered timer no longer advance. The new 20 s test (`tests/PresenterAi.Application.Tests/Presenting/PresenterVoiceCommandTests.cs:401-426`) proves that path and a later new question. But Continue/Resume while waiting cannot restart narration (finding D-01 below). |
| A-01 | **Fixed** | `Presenter.cs:642-653,1533-1551`: only Presenting/permit-bearing Paused audio is forwarded; entering Ending flushes. Delayed-close and voice-confirmation oracles at `PresenterVoiceCommandTests.cs:428-483` check zero Ending audio and one extra flush. |
| D-02 | **Fixed for the specified failure** | `Presenter.cs:1205-1234` passes `doc.RootElement.Clone()` and attaches an exception-observing continuation. `PresenterToolTests.cs:404-433,859-873` forces a late read then a late exception and checks no sensitive exception message is logged. A small new logging mislabel is noted below. |
| D-03 | **Fixed** | `Presenter.cs:1244-1247,1439-1443` stores the generation produced by each navigating call and tests it against the current generation; `PresenterToolTests.cs:435-453` verifies button navigation before tool completion yields `ok:false, stale`. |
| D-05 | **Partly fixed** | `Presenter.cs:819-831,931-940`: the invalid-slide reply retains a hold and enters WaitingOnSlide once voiced. `PresenterVoiceCommandTests.cs:54-103` checks speech forwarding and no silent advance in both modes. `_rangeReply` is not reset for the *next* question (finding D-02). |
| B-01 | **Fixed within a fake-session oracle** | `PresenterVoiceCommandTests.cs:54-103` now checks forwarded `Presenter.Audio` frames, paused old-frame rejection, the range instruction, and no advance. It cannot establish that upstream actually speaks intelligible range words; that remains a live check, not an error in this test. |
| D-07 | **Partly fixed** | `src/PresenterAi.Application/Presenting/Tools/GoToSlideTool.cs:50-63` always calls `IPresenter.GotoAsync`; `ResumePresentationTool.cs:32-46` always calls `ResumeAsync`. `Presenter.cs:1461-1470` clears a check-in on already-Presenting resume. The new same-slide/end-pending and check-in tests (`PresenterToolTests.cs:133-179`) establish those effects. Resume while **WaitingOnSlide** still clears its hold without actually resuming (D-01). |
| D-08 | **Fixed** | `src/PresenterAi.Application/Tools/ToolResult.cs:53-81` binary-searches Unicode scalar boundaries against the *serialized JSON* byte count; `tests/PresenterAi.Application.Tests/Tools/ToolResultBudgetTests.cs:9-25` includes escapes and multibyte characters. Serialized size is bounded by construction. |
| D-09 | **Fixed for registry dictionary consistency** | `src/PresenterAi.Application/Tools/ToolRegistry.cs:13-24,61-90` locks the duplicate check, pinned count, insert, and value-list copy. Tool getters are deliberately evaluated outside the lock. `ToolRegistryTests.cs:174-198` exercises concurrent registration/catalogue creation. No requirement to freeze a tool object's mutable getters during registration is inferred. |
| C-01 | **Partly fixed** | `docs/guides/002-audience-questions.md:105-109,147-153,174-178` now describes the threshold branches and limits paused client-mode instructions correctly. A different claim about the final backend answer's timer was introduced in this edit and remains wrong after the correction commit (C-01 below). |
| Row 3/15 oracles | **Fixed as corrected** | `PresenterToolTests.cs:181-205` checks the final backend completion re-arms the 15 s answer window; `:115-132` checks an eligible confirmed End closes. The positive End test correctly uses close/idle rather than expecting an output on a closed session. |

## New defects introduced by `088ecf1`

### Blockers

- **D-01 · blocker — Continue after check-in No silently fails to resume.** `src/PresenterAi.Application/Presenting/Presenter.cs:742-752,837-860,871-877,1461-1470`; `src/PresenterAi.Application/Presenting/Tools/ResumePresentationTool.cs:32-46`. Sequence: answer a question → check-in → say "no" (WaitingOnSlide, still **Presenting**) → say "continue". The transcript handler clears WaitingOnSlide and opens a hold; `ExecuteVoiceCommand(Resume)` calls `ResumeCore`, which now merely clears that hold and returns **false** without sending `ResumeAfterQuestionInstruction` or a slide-resume instruction. The voice command is not logged as executed; no presenter timer is armed. The tool equivalent returns a misleading `ok:true,"already presenting"` despite not continuing. This breaks §1 AC2 / §3.7's exit-on-command behavior for the new phase. Fix: distinguish resuming from WaitingOnSlide versus idempotent already-Presenting, send the natural slide-resume instruction (not the question bridge for a navigation command), clear the hold and arm the appropriate timer. Add voice **and** tool tests after No (the existing tests only check immediate quiet and a *new question*).

### Improvements

- **D-02 · should-fix — Range-response marker contaminates the next question.** `src/PresenterAi.Application/Presenting/Presenter.cs:819-830,935-941,742-748,1788-1800`. Say "slide 40" on a two-slide deck → hear the valid-range reply → `_rangeReply=true`, phase WaitingOnSlide. Ask a new, ordinary question: `OnTranscript` leaves WaitingOnSlide and opens a fresh question hold but never clears `_rangeReply`. After its voiced answer and 700 ms quiet, `OnInteractionElapsed` interprets the *new answer* as another range reply and goes back to WaitingOnSlide, suppressing "Shall I carry on?" and automatic follow-up resume. Reset `_rangeReply` when a new utterance/question begins after WaitingOnSlide, while retaining it for the actual range response. Test range reply → new question → answer → check-in/yes or follow-up timer. The new range oracle stops before any new question; the new No oracle starts with `_rangeReply=false`.

- **C-01 · should-fix — Guide contradicts the corrected final-answer timer.** `docs/guides/002-audience-questions.md:124-126,156-159` now says a final completion leaves the "unanswered deadline unchanged" and omits backend finish from the timer's Starts column. But `Presenter.cs:1071-1098` calls `FinishBackendDelegation` → `ArmQuestionHold()` on final completion; `PresenterToolTests.cs:181-205` and the correction in `docs/review/012-plan-007-impl-review-round-2.md:88-93` affirm that this intentionally starts a *fresh* 15 s window. Operator sees completion at t=14 s and expects escape at t=15 s per guide, while code escapes around t=29 s. Update both guide lines to include backend final/failure re-arm; do not change the code to match the mistaken guide.

- **D-03 · nit — All ordinary asynchronous tool exceptions are mislabeled as *late* faults.** `src/PresenterAi.Application/Presenting/Presenter.cs:1208-1236`. The `OnlyOnFaulted` continuation is attached *before* awaiting. For a tool that faults immediately or before the deadline, the regular catch converts its failure to `{ok:false,"tool failed"}` **and** posts `BackgroundFailure("late tool fault", ...)`. Operators receive an apparent delayed fault even when none was abandoned. Observe all faults without logging when the await itself handles the exception; log a type only for faults actually arriving after the timeout. Include an immediate-throwing asynchronous tool test asserting no "late" line.

## Explicit disposition of deferrals/rejection/correction

- **D-01 deferral:** Accept **for the currently shipped production tool set**, not as a general guarantee of the `ITool` protocol. The six built-ins call `IPresenter` with the cancellation token (`Presenting/Tools/*.cs`); plan 008 external tools do not receive `IPresenter`. A concrete counterexample if arbitrary registered tools ever capture it is already expressible by `PresenterToolTests.cs:859-887`'s `NavigateThenGateTool`: make it wait without honoring cancellation *before* calling `NextAsync(CancellationToken.None)`; the five-second timeout completes but the later call still advances this or a restarted talk. `Presenter.ToolRegistry` is publicly exposed (`Presenter.cs:132-134`). Document this boundary/revisit before enabling any extension that can access `IPresenter`. No new finding against the deployed six built-ins.
- **D-04 rejection:** Accept for `LiveSession`: `LiveSession.cs:184-215,713-737` refuses only when no longer Open/finished (its unbounded writer is not explicitly completed); session closure clears the presenter tracker. FakeSession's arbitrary `RefuseContinue=true` models a condition the actual open transport does not promise to recover from. Not re-raised.
- **Correction paragraph:** Accept. Plan 005's fresh answer window is implemented by `Presenter.cs:1082-1098`; the oracle at `PresenterToolTests.cs:181-205` checks it. The initial fix brief's "final answer does not re-arm" was wrong. Narrowing the registry lock to dictionary/pinned-count operations is sufficient for that race, as above. Only the guide failed to absorb the correction (C-01).

topicsWithNoFindings: Ending audio gating/flush and delayed-close tests; paused speech permit preserved; navigation generation map across successive navigations; late argument cloning and fault observation without exception message; serialized 4 KiB result budget; concurrent registry insert/enumerate protection; same-slide Goto cancels pending End; final-answer timer re-arm; confirmed End tool closes only when eligible; conditional inline/meta-tool guide statement and paused client-delegation qualification.

## IS THIS BRANCH READY TO MERGE?

**No. Blocker:** D-01 (Continue/Resume fails after the new WaitingOnSlide phase). **Improvements:** D-02 (range flag leaks into a later question), C-01 (timer guide drift), D-03 (misleading late-fault logging). Deferred arbitrary-tool side effects are explicitly scoped above; no test/build was run in this read-only confirmation.


## Orchestrator disposition (checked against the code)

Round 3 is the escalation trigger in the review-convergence doctrine: only genuine blockers are fixed, the small should-fix items are folded into the same fix because each is one or two lines with a test, and there is **no round 4**. Any later finding goes to follow-ups.

| ID | Class | Decision | Reason |
|---|---|---|---|
| D-01 | D | **Fix (blocker)** | Confirmed: `ResumeCore` while Presenting only clears the hold, so "continue" after a check-in "no" never restarts narration and the tool says "already presenting". Resuming from `WaitingOnSlide` must send the slide-resume instruction and arm the presenting timer; voice and tool tests after "no". |
| D-02 | D | **Fix** | Confirmed: `_rangeReply` is not reset when a new question starts after `WaitingOnSlide`, so the next answer is treated as a range reply and the check-in never comes. Reset on a new question; test range reply → new question → check-in. |
| C-01 | C | **Fix** | The guide was edited under the wrong brief and not corrected with `d1ec0d8`: the final backend answer re-arms a fresh 15 s window. Correct the guide in place; code unchanged. |
| D-03 | D | **Fix** | Confirmed: the fault continuation is attached before the await, so a tool that fails normally is also logged as a "late tool fault". Log only when the run already gave up; test an immediately failing tool. |
| D-01 deferral (round 2) | — | **Accepted as scoped** | Holds for the six built-ins; recorded as a boundary to revisit before any extension can reach `IPresenter`. |
| D-04 rejection, correction paragraph | — | **Accepted** | No change. |

Root cause across rounds 2–3: the new `WaitingOnSlide` phase (added in round 2) was wired into the entry paths but not into every exit path (resume, new question, range flag). The fix brief asks for tests on each exit, not only the entry.
