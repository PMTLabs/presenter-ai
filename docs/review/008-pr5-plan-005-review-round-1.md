# 008 — PR #5 (plan 005 + A1) external review, round 1

**Date:** 2026-09-22. **Reviewer:** `pi` `openai-codex/gpt-5.6-sol:medium`, read-only, request `p005-rv-01`.
**Scope:** `origin/develop...6e3a7ba` (branch `feature/005-echo-and-stall`). **Verdict given:** not ready (3 blockers).
Each finding was traced against the code before it was accepted; the raw report stays outside the repository.

## Findings and outcome

| # | Class | Reviewer severity | Finding | Outcome |
|---|---|---|---|---|
| 1 | D | blocker | Echo gate learns `near / far` from frames before the delayed echo arrives and from pauses in the far end (the 250 ms far tail keeps it active), so the coupling drops to its 0.01 floor and the echo that follows opens the gate and can trigger barge-in (`echoGate.ts`) | **Fixed.** Coupling is learned only after the far end has been above the floor for the whole tail window. Tests: acoustic delay, far-end pause; the existing tests now learn over a tail window first |
| 2 | A | blocker | `OnDelegation` has no staleness guard: a late delegation after navigation opens a hold on the new slide; a late client delegation while paused appends "answer now" and makes the model talk through the pause | **Partly fixed.** Pause/ending: fixed, the client answer-now is appended only while presenting (test `Client_delegation_while_paused_does_not_tell_the_model_to_answer`). Navigation: **kept by design**: GPT-Live speaks the backend answer regardless (there is no cancel), so holding the new slide stops the next part from talking over it; the hold is still capped by the 15 s timer. Documented in plan §4.5 |
| 3 | D | blocker | A second question while a backend answer is pending: the direct answer to Q2 is not counted until Q1's backend completes | **Rejected as a defect; documented.** The hold spans every open question rather than tracking them one by one. Q1's backend answer will be spoken anyway, so waiting for it is the intended behaviour. The talk cannot stall (15 s cap) and cannot advance during an answer. Superseding the pending delegation on a later user delta would need `offset_ms` semantics that are not documented: its example equals the transcript `start_ms`. It would also bring back the bug where the "One moment." filler counted as the answer. Guide step 4 and plan §4.5 now state this |
| 4 | D | should-fix | A delegation rejection that the client-mode retry recovers is still raised as `UpstreamError`, so the page shows an error as well as the warning | **Fixed** (`LiveSession.HandleEvent`: the retryable case returns before raising). Tests assert no error when recovered and exactly one error when the second rejection is not recovered |
| 5 | D | should-fix | The loopback fallback can be overwritten by the stale loopback destination and leave playback silent | **Fixed**, with a narrower window than reported: the race is while `context.resume()` is pending (a fallback during `addModule` was already correct). `AudioPlayback` remembers the fallback and `start` honours it. New `startAudio.fallback.spec.ts` covers both the loopback and the fallback-during-resume cases |
| 6 | C | should-fix | Guide says a backend failure is handled; a top-level `error` with code `backend_error` (the second documented failure envelope) left the backend pending until the 15 s cap | **Fixed in code** so the claim is true: a `backend_error` ends the pending delegation (`question: backend answer failed (backend_error)`); other errors do not. Test `Top_level_backend_error_ends_the_pending_backend_answer` |
| 7 | B | should-fix | `Audio_before_the_latest_user_delta_is_not_the_answer` queued the audio before the hold opened, so it never reached `IsAfterLatestQuestion` | **Fixed**: the older audio now arrives after the hold opened, then newer audio is accepted (presence and absence) |
| 8 | C | nit | README still says the app nudges once | **Fixed**: two nudges, then a pause with a warning. Other "once" hits are historical notes (plans 001/005, handoffs), not live claims |

## Verification

- Mutations, each restored by checksum. Every one was killed by the named test, with the expected assertion:
  - the delegation state gate;
  - `backend_error` handling disabled;
  - any error ends the backend;
  - the timestamp guard removed;
  - the recovered error raised;
  - the unrecovered error suppressed;
  - coupling always learned;
  - the continuous-far condition dropped;
  - the playback fallback ignored.
- `dotnet build PresenterAi.slnx -warnaserror`: 0 warnings. Tests: Application 76, Infrastructure 40 (+3 skipped), Api 140.
- Web: lint clean; tests shared 20, app 60; app production build with check-dist ok.

## topicsWithNoFindings (reviewer)

The reviewer found nothing in these areas:

- settings and their mappings;
- stall escalation and diagnostics;
- `LiveSession` socket lifetime and the one-retry limit;
- web auth gating, the bridge and stick-to-bottom.
