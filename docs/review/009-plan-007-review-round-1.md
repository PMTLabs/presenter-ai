# 009 — Plan 007 external plan review, round 1

**Date:** 2026-09-23. **Reviewer:** `pi` `openai-codex/gpt-5.6-sol:medium`, read-only, request `p007-plan-rv-01`.
**Scope:** `docs/plan/007-voice-control-and-tool-protocol.md` (draft) against `develop` @ `0a30aa3`, research 005/006,
plans 005/006. **Verdict given:** not ready (8 blockers). Every claim was checked against the code before it was
accepted; all 13 findings were accepted and folded into plan revision 1. The raw report stays outside the repository.

§2 "Current state": all eight claims verified true. The paused-audio claim was incomplete (finding A-001).

## Findings and outcome

| # | Class | Severity | Finding | Outcome |
|---|---|---|---|---|
| A-001 | A | blocker | "Pause keeps listening" removed only the upstream mute; `SendAudioCore` (`Presenter.cs:944-945`) and `BridgeClient.sendAudio` (`bridgeClient.ts:186-190`) also drop paused audio, and `MuteCore`/`UnmuteCore` act only while presenting | **Folded:** §3.6 defines the input contract at all three gates plus mute in both states; tests for paused frames, paused mute and unmute |
| D-001 | D | blocker | A tool calling `IPresenter` while the loop awaits the tool deadlocks the single reader | **Folded:** §3.2 execution contract — start without awaiting, completion comes back as an event; deadlock oracle and mutation |
| D-002 | D | blocker | Nothing makes the live model delegate commands; tools are unreachable if it just says "Sure" | **Folded:** a delegation policy in the system instructions; numbered navigation also local; the Task 0 probe measures delegation for the specific phrases and gates on title navigation (≥ 4/5) |
| D-003 | D | blocker | "The next completion after a call" is not a sound rule for sequential calls or interleaved delegations | **Folded:** §3.3 tool-round tracker keyed by delegation, replacing `_pendingBackendDelegation`; global `response.create` barrier (probe-checked); exact-sequence and interleaving tests |
| D-004 | D | blocker | No stale-session, failure or exactly-once-output contract for tool work | **Folded:** captured session + run generation, exactly one output per accepted call (exceptions, timeout, malformed), stale completions dropped; tests |
| D-005 | D | blocker | One flush does not stop narration that arrives after it | **Folded:** paused output gate with speech permits keyed to the utterance's `end_ms`; delayed-old-audio test |
| D-006 | D | blocker | Echo of the model's voice could execute commands or answer the end confirmation | **Folded:** eligibility rule — during model speech only Pause is eligible; every other intent needs silence; loudspeaker runbook step |
| D-007 | D | blocker | Check-in and end-confirmation phases were undefined; the end deadline started at the ask, not after it was voiced | **Folded:** §3.7 phase table with observable transitions; deadline starts after the question is voiced (8 s fallback); tests |
| D-008 | D | should-fix | No precedence between a pending confirmation and buttons, navigation, stall, take-over, recording | **Folded:** §3.7 precedence table, every row tested including recorder and plan 006 resume |
| D-009 | D | should-fix | The session tool list was not an immutable snapshot | **Folded:** `ToolSessionCatalogue` snapshot; meta-tools close over it; test for tools registered after the snapshot |
| B-001 | B | should-fix | The 100-small-tools size oracle passes an implementation with unbounded pinned tools or huge schemas | **Folded:** per-definition and pinned-count limits at registration, a 32 KiB catalogue budget, parameterised fixtures, assertion on the real start payload |
| D-010 | D | should-fix | Plain delta concatenation breaks on fragmentation and gaps | **Folded:** spaced and space-free matching, length/duration caps, resets; fragmentation and 699/700/701 ms tests |
| C-001 | C | nit | "Drop the length limit" mutation cannot kill under exact matching | **Folded:** replaced by substring matching, removing eligibility, arbitrary word stripping, merging across the gap |
| C-002 | C | nit | The docs task had no verify step | **Folded:** docs checklist added |

## topicsWithNoFindings (reviewer)

§2 current-state truth; the documented function-call frame shapes; MCP deferred to plan 008; backward compatibility of
an unknown `flush` frame; confirmed-end recording through the existing path; nothing beyond the confirmed brief.
