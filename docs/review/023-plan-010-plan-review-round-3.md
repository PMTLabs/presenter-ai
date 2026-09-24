# r010-plan-r3 — final confirmation review

Read-only review of `docs/plan/010-live-presenter-training.md` against round 2 (`docs/review/022-plan-010-plan-review-round-2.md`) and existing code. No tests, builds, secret reads or repository edits. Owner policies D2, D7, en/vi, R14 and R15 are accepted.

## Round-2 verdicts

| Finding | Verdict | Evidence |
|---|---|---|
| **D9**, held reconnect | **RESOLVED** | Plan `:418-423`, test `:941-943`: always call hold-aware `ResumeCore`, then skip only `PresentSlide` while held; test requires Presenting and replay without another Resume. Existing order is `ReconnectAsync` → `ResumeCore` → `PresentSlide` (`src/PresenterAi.Application/Presenting/Presenter.cs:2055-2062`); `ResumeCore` owns Presenting/idle-guard transition (`:2070-2111`). |
| **D10**, lost intermediate edits | **PARTIAL** | Plan `:267-275`, `:434-454`, `:949-953`: snapshot-wide narration diff and per-id terminal outcomes replace version deltas; reversed/duplicate-signal test identifies both edits. Existing producer queue may asynchronously reorder (`src/PresenterAi.Application/Presenting/Presenter.cs:370-401`). Separate `GetHead`/`GetOutcome` reads are not a coherent snapshot; see **D14**. |
| **D11**, intent marker/transport | **RESOLVED** | Plan `:377-402`, tests `:930-937`: `PendingToolConfirmation.Intent` is presenter-owned; approval enqueues on-loop before the off-loop tool acknowledgement, with no model-supplied id. Existing approval otherwise uses a snapshotted `ITool` and original arguments (`src/PresenterAi.Application/Presenting/Presenter.cs:1561-1567`, `:1733-1749`; `src/PresenterAi.Application/Tools/ToolSessionCatalogue.cs:95-111`). |
| **D12**, End/DB commit | **PARTIAL** | Plan `:276-296`, `:455-464`, T6 `:890-901` adds a ticket-linked registration, commit lock, cancellation-aware transaction and gated Postgres tests. Existing End/max cancel the StartTicket off-loop (`src/PresenterAi.Application/Presenting/Presenter.cs:253-277`, `:683-691`) but `_runCts` was loop-only for these paths (`:2148-2153`). The newly specified 5 s timeout has no safe lock/flag rule; see **D15**. R14's durable late commit itself is not challenged. |
| **D13**, long answer split by pauses | **RESOLVED** | Plan `:488-495`, T9 `:1000-1012`: exchange grouping spans multiple presenter turns despite the existing 1 s per-turn merge (`web/app/src/store/presenterStore.ts:81-103`); test selects either answer fragment and distinguishes the next question. Different-slide boundary follows R15. |
| **C2**, off-loop cancellation claim | **RESOLVED** | Plan `:95-101`, `:455-464`, test `:959-963` now says the ticket is cancelled off-loop and a *new ticket callback* closes the edit registration; `_runCts` retains its tool role. This accurately distinguishes current `CancelConnect`/guard (`src/PresenterAi.Application/Presenting/Presenter.cs:253-277`, `:683-691`) from loop-side `CancelRun` (`:2152`, `:2472-2476`). |
| **A2 partial**, narration gate | **RESOLVED** | Plan `:412-432`, T7 `:938-945` covers audio, timers, nudge, resume, reconnect, navigation and wrap-up, including the D9 correction. Existing independent paths are `OnAudio` (`src/PresenterAi.Application/Presenting/Presenter.cs:1068-1071`), `OnNudge` (`:1851-1881`), `ResumeAfterReconnectAsync` (`:2055-2062`) and `ArmAfterVoice` (`:2322-2332`). |
| **D4 partial**, commit/apply ordering | **PARTIAL** | Plan `:268-275`, `:434-454`, `:949-953`: single-loop reconciliation, full diff and settle-once ids avoid reliance on `Changed` delivery order. But a concurrent worker can mutate the service between two separately locked reads; **D14** permits `applied` before the corresponding head swap. Loop ownership alone (`src/PresenterAi.Application/Presenting/Presenter.cs:408-541`) cannot make those reads atomic. |

## §4.4 cross-thread handoff/mechanism check

| Handoff in plan `:654-668` | Sufficient? |
|---|---|
| Session/timers → loop (`:657`) | **Yes** for the existing event types: `QueueFromProducer` is the entry (`src/PresenterAi.Application/Presenting/Presenter.cs:370-401`), and loop dispatch/session or generation checks are at `:408-508`, `:1075-1081`, `:1522-1530`. New edit state must remain loop-only. |
| Confirmation → enqueue; off-loop acknowledgement (`:659-660`) | **Yes**: plan moves enqueue into loop-side approval, before the existing `Task.Run` invocation (`src/PresenterAi.Application/Presenting/Presenter.cs:1256-1258`, `:1733-1749`). `_gate` covers service enqueue, not the presenter intent. |
| `train_turn`; idle toggle → loop (`:661`, `:667`) | **Yes**, provided the added commands use the existing command queue and validate owner **on the loop**, rather than relying on the bridge's mutable connection (`src/PresenterAi.Application/Presenting/Presenter.cs:357-368`, `:615-649`; `src/PresenterAi.Api/Realtime/PresenterBridge.cs:350-425`). |
| Worker progress/commit/failure → presenter (`:662`) | **No**: idempotent signals are good, but independent `GetHead` and `GetOutcome` lock acquisitions are not an atomic reconcile (**D14**). |
| HTTP revert → presenter (`:663`) | **Conditional on D14**: DB CAS and monotonic `Observe` handle concurrent writers, but the resulting signal is subject to the same torn read (`src/PresenterAi.Application/Presenting/Presenter.cs:408-541` consumes it independently of HTTP). |
| Start/worker/rebuild → head snapshot (`:664`) | **Yes** for monotonicity: `_gate` protects `Observe` and newer version wins; Start's actual loader uses a scoped repository (`src/PresenterAi.Infrastructure/DependencyInjection.cs:175-191`). Atomic *reader* access still needs D14. |
| End/max ↔ DB commit (`:665`) | **No as specified at timeout**: commit lock serializes normal operations, but an unacquired lock cannot be released and `Closed` needs safe publication at the 5 s bound (**D15**); existing End/ticket paths are `src/PresenterAi.Application/Presenting/Presenter.cs:253-277`, `:2140-2182`. |
| Ticket → hung reviser (`:666`) | **Yes** if `OpenTalk` installs the linked registration before edit enqueue: existing End and max callbacks cancel that ticket off-loop (`src/PresenterAi.Application/Presenting/Presenter.cs:253-277`, `:683-691`); revised T6 explicitly tests this (`docs/plan/010-live-presenter-training.md:893-896`). |
| Web transcript grouping (`:668`) | **Yes**: pure grouping over stored turns on the JS event/reducer path (`web/app/src/store/presenterStore.ts:79-103`), with stable first-delta slide and a stored-reference/memoized selector in plan `:997-1002`. |

## New blocker-level findings

### D14 — Reconciliation reads an impossible head/outcome combination
**Severity:** blocker. **Plan:** service `:267-275`, presenter `:433-454`, handoff `:662-664`; T7 `:949-953`.

**Evidence:** `GetHead(presentationId)` and `GetOutcome(editId)` are **separate** synchronous reads under `_gate`. The presenter reads head v5, then the worker commits v6, calls `Observe(v6)` and marks edit A `applied(v6)`, then the presenter reads A's terminal outcome. It settles the hold and emits `script_edit:applied(v6)` after swapping **only v5**. No ordering of `Changed` signals repairs the already-sent false acknowledgement; the later reconcile can swap v6 without re-emitting the edit. `PresentSlide` builds/sends parts from `_presentation` (`src/PresenterAi.Application/Presenting/Presenter.cs:910-952`), while the producer path admits a concurrent worker between loop operations (`:370-401`, `:408-541`). Conversely a newer head and old processing outcome can delay release. The planned reversed-signal test cannot exercise this interleaving unless the service read itself is gated.

**Fix:** Expose one `GetReconciliationSnapshot(presentationId, localEditIds)` returning immutable head **and all requested outcomes under one `_gate` acquisition**, then perform one loop-only reconcile from that bundle. Add a gated test that commits between the old two read points; assert no `applied(v6)` until v6 narration has been swapped/replayed, and one terminal status per id.

### D15 — Timed-out close cannot both own/release the commit lock and mark `Closed` safely
**Severity:** blocker. **Plan:** `CloseTalkAsync` `:276-281`, commit phase `:285-296`, R14 `:747-748`, handoff `:665`, T6 `:891-901`.

**Evidence:** The plan says `CloseTalkAsync` waits for `CommitLock` **at most 5 s**, then “sets `Closed`, cancels `Cts`, releases the lock.” If acquisition times out while the worker holds the semaphore, releasing it without ownership can admit another waiter into the active commit phase or make the worker's eventual release throw. If the intended implementation instead skips the release but only publishes `Closed` under that same lock, Close cannot return boundedly as R14 requires. Existing End calls are queued then await `EndAsyncCore` (`src/PresenterAi.Application/Presenting/Presenter.cs:253-257`, `:639-644`, `:2140-2182`); the ticket can also signal off-loop (`:683-691`), so two concurrent `CloseTalkAsync` calls must agree on a single closure. The proposed timeout test asserts logging/row count, not lock permit count, late worker outcome, or a *second* waiter after timeout.

**Fix:** Make closure a separately atomic/idempotent transition (e.g. interlocked/`_gate`-protected `Closed` + one-shot `Cts.Cancel`), set it when the bound expires **without releasing a permit never acquired**; release only in `finally` for a successful wait. A worker checks `Closed` after acquiring and after any timed-out close; the already-running transaction follows accepted R14. Test two concurrent close callers, a third queued commit after timeout, late transaction completion, permit count and a subsequent talk; verify no extra commit or semaphore failure.

## Improvements (non-blocking)

- Ensure every failed Start *after* `OpenTalk` closes/disposes its registration: current connection failure returns to Idle without `EndAsyncCore` (`src/PresenterAi.Application/Presenting/Presenter.cs:785-794`), whereas plan registers at Start (`docs/plan/010-live-presenter-training.md:373-376`). This is cleanup, not a reason to revisit the accepted deployment policy.

## IS THIS PLAN READY TO IMPLEMENT?

**No. Blockers: D14, D15.** No owner decisions need reopening; fix the atomic read and bounded-close ownership contracts and their tests.
<!-- REVIEW-COMPLETE r010-plan-r3 -->