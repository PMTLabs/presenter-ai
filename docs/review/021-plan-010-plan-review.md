# r010-plan — independent plan review

**Scope:** `docs/plan/010-live-presenter-training.md`, read-only review against the confirmed §2 brief and §9 decisions. Code inspected on `feature/010-live-presenter-training`; no builds or tests run. The en/vi confirmation lexicon, always-declared/gated tool, v1 backfill and 16 KiB command decision are treated as settled; findings concern delivery, not those choices. Locations below use plan line numbers and existing-code line numbers.

## Blockers

### A1 — New confirmation question is lost by the catalogue snapshot
**Severity:** blocker. **Plan:** §4.1 Tool (`:278-282`), T1/T7 (`:544-553`, `:632-660`).

**Evidence:** The plan adds `ITool.ConfirmationQuestion` and reads it from the *resolved* tool in `OnToolCall`. But `ToolSessionCatalogue` wraps every registered presenter tool in `SnapshotTool` (`src/PresenterAi.Application/Tools/ToolSessionCatalogue.cs:32-43`), and resolution returns that snapshot (`:97-150`). Its member-forwarding list at `:95-108` includes `RequiresConfirmation`, `Title`, etc., but not the proposed question. The new default interface property will therefore read as null, producing the old generic question (`src/PresenterAi.Application/Presenting/Presenter.cs:1553-1572`). This is the same forwarding fix applied to the original tool but missed at its snapshot site.

**Fix:** Add and snapshot/forward `ConfirmationQuestion` in `SnapshotTool`; test the exact spoken question through the real registry/catalogue, both direct and discovery (`call_tool`) resolution.

### A2 — Slide hold is specified for timer progress but not all narration-producing paths
**Severity:** blocker. **Plan:** §4.1 Hold/Apply/Idle (`:294-318`), §5 edge cases (`:501`), T7 (`:632-660`).

**Evidence:** `HoldBlocksProgress()` guards `OnPartGap` and `OnSilence` (`src/PresenterAi.Application/Presenting/Presenter.cs:1806-1845`), but `OnAudio` independently calls `ArmAfterVoice()` (`:977-1072`), `OnNudge` can issue fresh narration requests without that gate (`:1851-1882`), and `ResumeCore` issues `ResumeInstruction` and arms a nudge (`:2070-2111`). The plan explicitly changes `HoldBlocksProgress`, `PresentSlide` and `ResumeAfterQuestion`, yet allows navigation away/return and pause/reconnect while pending. `ResumeAfterReconnectAsync` calls `ResumeCore` *before* `PresentSlide` (`:2055-2062`); if a slide is held, that first resume instruction can restart stale narration. Stopping timers does not stop audio already queued at the upstream; `PromptBuilder.SlideInstruction` only interrupts when `interrupt:true` (`PromptBuilder.cs:104-119`). These are the same progress-suppression fix applied to some sites but missed at others.

**Fix:** Define a single hold-aware narration gate for audio/timer/nudge/resume/reconnect/wrap-up paths; explicitly interrupt/flush in-flight current-slide playback on entering the hold and on replay, and suppress `ResumeInstruction` and nudge until release. Preserve the guard's max-length deadline. Add gated fake-session tests that send late audio, advance clocks, pause/resume, suspend/reconnect, navigate away/back and reach wrap-up while held, asserting **no stale slide-part or automatic advance** before release.

### D1 — Per-presentation service cannot directly depend on the scoped Postgres store
**Severity:** blocker. **Plan:** §4.1 ports/service (`:194-225`), T2/T6 (`:562-568`, `:617-619`), wiring audit (`:714-718`).

**Evidence:** `ScriptRevisionService` is a singleton but `PostgresPresentationRevisionStore` is scoped; `AddPersistence` registers EF `PresenterAiDbContext` as scoped (`src/PresenterAi.Infrastructure/DependencyInjection.cs:26-34`). The existing singleton presenter deliberately creates a fresh scope for each load instead of capturing its scoped repository (`:162-191`). The plan specifies no equivalent scope boundary for service worker calls, revert, or commit-event publication. Capturing the store fails scope validation (or holds a disposed/shared DbContext); resolving it from the root makes concurrent FIFO workers share a non-thread-safe context.

**Fix:** Specify and implement an async scope per store operation/transaction via `IServiceScopeFactory` (or a scoped-operation factory), never retain DbContext across awaits to the reviser or across presentation workers. Define worker cleanup/disposal and independent cancellation for revert. Add a production-DI startup test and two-concurrent-presentation Postgres test, not just the in-memory service tests.

### D2 — Local FIFO and local `Committed` events do not keep multiple API instances/talks current
**Severity:** blocker. **Plan:** §4.1 service/apply (`:211-224`, `:302-309`), §4.4 parallel paths (`:467-473`), §5 data consistency (`:496`), T11/T12 (`:701-718`).

**Evidence:** One singleton worker/`Committed` publisher is process-local. The API creates a process-local singleton presenter (`src/PresenterAi.Infrastructure/DependencyInjection.cs:162-166`) and the bridge routes events only to that process's current connection (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:64-76`, `:139-143`). If HTTP revert or another talk's edit commits on instance B while a talk is active on A, A never swaps its `_presentation`. `PresentSlide` and reconnect use that cached slide list (`src/PresenterAi.Application/Presenting/Presenter.cs:910-952`, `:2055-2062`). CAS protects storage from lost writes, but neither refreshes the talking presenter nor gives a global FIFO ordering for concurrent edits on different instances. This is not asking for multi-device merge; it is the brief's subsequent-narration/replay and FIFO semantics on a shared presentation.

**Fix:** Either explicitly constrain deployment to one API instance and one active talk for a presentation and enforce that constraint, or use a cross-instance per-presentation queue/lock plus commit notification (with durable head reload, version deduplication and ordering) for all active talks. Test two hosts against the same Postgres: edit/revert on B while A is presenting, then verify A's next narration/replay and version, as well as arrival-order semantics or the documented rejected-concurrency response.

### D3 — Approved voice tool can edit after a navigation invalidates its generation, or target the wrong slide
**Severity:** blocker. **Plan:** §4.1 Tool/EnqueueEdit (`:278-297`), §5 duplicate/edge claims (`:501`), T7 (`:642-660`).

**Evidence:** Approval launches the tool off-loop (`src/PresenterAi.Application/Presenting/Presenter.cs:1733-1749`); `OnApprovedToolCompleted` checks session and `_runGeneration` only *before announcing its result* (`:1751-1765`). Navigation increments `_runGeneration` (`:1973-1976`). The plan makes `ReviseScriptTool.InvokeAsync` enqueue a new internal command, and `EnqueueEdit` defaults targets to the **then-current** slide. A confirmation requested on slide 2 can thus be approved, followed by navigation to slide 3 before that command runs: an obsolete tool side effect can edit slide 3 despite its completion being considered stale. Existing generation checks for announcement are insufficient for the new persistent side effect.

**Fix:** Capture presentation id, talk ticket, slide and targets at tool-call/confirmation time; validate generation/ticket/owner before enqueueing **and** before executing the queued edit command. Define whether navigation cancels a confirmed request or retains its original target, and test navigation before yes, between yes and tool completion, and after enqueue. Never silently substitute the new current slide.

### D4 — Commit, applied status and presenter swap lack an ordering/linearization rule
**Severity:** blocker. **Plan:** §4.1 service/Apply (`:211-224`, `:302-312`), sequence (`:429-436`), T6–T8 (`:621-672`).

**Evidence:** The plan separately emits `ScriptEditUpdated(applied)` through `QueueFromProducer` and raises `Committed` after an append, then says both are handled on the loop. It does not specify whether the local presenter first swaps slides and releases the hold or first emits `script_edit:applied`. `QueueFromProducer` is asynchronous (`src/PresenterAi.Application/Presenting/Presenter.cs:370-401`) and the event loop processes each event independently (`:408-541`). A status can arrive first, release a held slide and/or tell the UI success while `_presentation` and `_parts` are still old; another commit/revert may already have advanced the head when an earlier callback arrives. The tests check the last append and frames individually, not the ordering relative to the durable head and in-memory script.

**Fix:** Make an applied status carry its exact committed revision and apply it as one loop transition: monotonic version check, swap and rebuild/replay/hold disposition, then emit `script_version` and `script_edit:applied`. For distinct external commits reload newer head instead of accepting stale events. Test out-of-order completion/commit delivery with two queued edits and a racing revert; assert each announced version corresponds to the narration used and no held timer runs in the gap.

## Improvements

### C1 — Current-state claim says all background work returns by queue; off-loop cancellation is a deliberate exception
**Severity:** minor. **Plan:** §3 Presenter loop (`:92-94`).

**Unsupported claim:** “background work re-enters only through `QueueFromProducer`.” The max-length timer also directly calls `ticket.Cancel(EndReasons.MaxLength)` off-loop (`src/PresenterAi.Application/Presenting/Presenter.cs:683-691`), and the ticket/cancellation design expressly uses off-loop signals during blocked Start/reconnect (`:132-143`, `:833-842`). `QueueFromProducer` is the path for state-processing events, not the exclusive path for run cancellation.

**Fix:** Qualify the current-state sentence and explicitly say revision completion must be queued but End/max-length cancellation must remain available off-loop; include a hung Responses request versus End/max-length test with no post-End commit.

### A3 — Trainer availability and tool gating omit the actual connected delegation mode
**Severity:** major. **Plan:** §3 tools (`:111-114`), §4.1 Trainer mode (`:320-322`), T7/T8 (`:642-672`).

**Evidence:** Trainer availability is defined as “the reviser has a route with a `DelegationModel`,” but the live attempt can connect on a route **without** a delegation model even when another configured route has one. The existing presenter tests `_hasDelegationModel(attempt)` per attempt and enters `client` delegation mode for that connection (`src/PresenterAi.Application/Presenting/Presenter.cs:845-854`, `:800-810`); `LiveSession.CreateDelegation` omits backend tools in client mode (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:699-726`). The fallback is to a different reviser route, but the voice tool is never offered on the active client-mode socket. Availability is gated by configured routes at one site and missed at the actual connected session.

**Fix:** Define availability separately for voice versus transcript training and derive voice capability from active `LiveSessionInfo.DelegationMode`; either disable the toggle with an accurate status on a client-mode talk, or explicitly support transcript-only mode. Test primary-failure/fallback-to-client-mode and reconnect changes of delegation mode.

### D5 — `ScriptRevisionValidator` allows an empty or unchanged “successful” edit
**Severity:** major. **Plan:** §4.1 validator (`:203-210`), schema (`:246-256`), T1/T6 (`:551-557`, `:621-628`).

**Evidence:** Uniqueness/subset/range/non-blank checks are vacuously true for `slides:[]`; a nonempty summary satisfies the remaining condition. An unchanged narration string is likewise valid. The composer can append an identical full Markdown revision, returning `applied` and announcing a replay even though no slide was rewritten. The confirmed brief requires a changed slide and failure not to claim success; the planned invalid-output tests name duplicates/blank/out-of-range only.

**Fix:** Require at least one returned *requested* target whose narration differs from head, specify whether a zero-change model result is `invalid_output` or an explicit no-op with no version, and test empty list, identical narration and mixed unchanged/changed targets.

### D6 — Rollback discards the recoverable history it promises to retain
**Severity:** major. **Plan:** §4.3 migration (`:367-369`), §5 rollback (`:530-532`), T2 (`:562-576`).

**Evidence:** `Down` simply drops `presentation_revisions`. After any live edits/imports/reverts, that destroys every prior version even though current `presentations.script` remains. The plan calls history append-only and recoverable (§2 `:39-44`); the stated rollback describes only the current script. The migration also resets pre-existing versions to 1, so this cannot be reversed by restoring the old counter from the new table.

**Fix:** Document that downgrade is destructive and requires a verified history backup/export before schema rollback, or make schema rollback explicitly unsupported once revisions exist (guarded Down). Test the chosen nonempty-history rollback procedure as well as empty-data migration Down and v1 backfill.

### D7 — HTTP revert has no bounded-resolution or post-commit-notification policy
**Severity:** major. **Plan:** §4.1 RevertAsync (`:220-224`), §4.3 HTTP (`:389-397`), T4/T6 (`:589-600`, `:621-628`).

**Evidence:** A revert is “instant,” outside the queue, with only one CAS retry; a busy importer or other revert can exhaust both attempts and return 409, which is reasonable, but the presenter keeps a held earlier edit that will rebase over the chosen revert. The sequence asserts it “never replac[es]” the revert (`:450-456`), yet if the in-flight edit targets the same slide it deliberately revises that slide again on the post-revert version (`:215-219`). The returned version remains recoverable, but the selected revert is not necessarily the final *current* narration. Neither the HTTP response nor the UI tells the owner that another edit is still pending or will supersede it.

**Fix:** State the linearization policy explicitly (revert remains a historical row while later accepted edits may change head), surface pending edits and the subsequent version in the UI, or cancel conflicting pending edits when reverting. Test a revert to an older narration while a gated edit targets exactly that slide and assert the chosen policy, including before/after UI and versions.

### B1 — The prior-exchange web oracle does not identify its target slide or exchange uniquely
**Severity:** major. **Plan:** T9 transcript tests (`:677-690`), §4.3 transcript identity (`:384-386`).

**Evidence:** `Transcript.spec.tsx` is proposed to assert only that “train on this sends the preceding question and this answer.” Existing transcript deltas merge by role within one second (`web/app/src/store/presenterStore.ts:81-103`), and turns currently have no slide (`:3`). The plan adds `Turn.slide` but does not say to stamp it on the *first* delta and retain it while merging. **Wrong implementation that passes:** clicking an old slide-2 answer sends its correct question and answer but defaults `slideIndex` to the currently displayed slide 5; the named test passes if run without an intervening navigation, while AC6 edits the wrong slide. Another wrong implementation can associate the last user turn from a different exchange.

**Fix:** Assert exact `slideIndex`, question and answer for at least two exchanges spanning navigation, time gap and multiple deltas; retain slide attribution at turn creation, and reject ambiguous/no-question selection rather than inventing a different exchange. Verify the corresponding reviser payload and committed changed slide, not just the browser send.

### B2 — Reviser HTTP oracle under-identifies route handling and output parsing
**Severity:** minor. **Plan:** T5 (`:603-614`), §4.1 Responses client (`:229-245`).

**Evidence:** The planned Azure request test names URL, headers, model, format name/strict and targets, but not full schema (`required`, `additionalProperties:false` at both levels), `store:false`, reasoning effort and token budget; the fallback test says only “Falls_back_to_openai_route_on_503.” **Wrong implementation that passes:** post a non-strict array schema missing `additionalProperties:false`, retry to the fallback with the *primary* model or Azure `api-key`, and parse text from a failed/non-message output; simple 503→200 and primary-request assertions pass. `UpstreamRoute` contains per-route URL, headers and model (`src/PresenterAi.Infrastructure/Live/UpstreamRoute.cs:3-8`, `:26-49`), so all three must switch together.

**Fix:** Assert the complete request and all headers/model on *both* handler invocations, parse multiple output elements with refusal/incomplete/no-text, assert exactly one no-retry 4xx/invalid-output call, and check usage logging without payload/secrets. Add custom endpoint-path/query URL cases and cancellation after primary failure before fallback.

### D8 — Prompt isolation is instruction-only, not an output safety invariant
**Severity:** minor. **Plan:** §4.1 reviser prompt/validator (`:203-210`, `:259-268`), risk R7 (`:520-523`).

**Evidence:** Feedback, recent turns and prior assistant answers are untrusted transcript text. Encoding them as JSON `input` and telling the reviser to treat them as data helps, but the reviser *must* follow the legitimate feedback, so an injected transcript can ask it to insert arbitrary stage directions or instructions into narration. The validator rejects only lines beginning `## Slide` and `>`; `PromptBuilder.SlideInstruction` sends whatever narration is saved straight back to the live model (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:104-141`). The schema and target-scoping prevent structural/script-wide edits, not instruction-like content inside the target slide.

**Fix:** Specify the accepted trust boundary (owner-confirmed corrections, but not automatic obedience to quoted exchange/recent turns); delimit transcript data and use only the explicitly confirmed feedback as editing intent, validate additional prohibited narration markup/stage-direction shapes as appropriate, and add adversarial transcript/quoted-instruction cases checking that no command becomes narration. Avoid promising absolute prompt-injection immunity from JSON schema alone.

## Coverage and sequencing notes

- **AC trace:** AC1/3/4/6/8 have T7→T11 hops; AC2 and AC5 rely mostly on Presenter unit tests; AC7 on reviser/service/presenter/bridge/web tests. Address A1/A2/D3/D4 before claiming those hops connect. Keep mutation evidence for each new oracle, as the plan proposes.
- **Order:** T4 says its endpoint tests use the *real* `ScriptRevisionService` (`docs/plan/010-live-presenter-training.md:590-591`), but T6 is in a parallel lane and is not available until later; T7 similarly depends on service shape in T1. Move T4 endpoint verification after T6 or introduce a defined T1 revert port and fake; integration T11 must follow generated client, bridge, and service wiring. The `git diff --exit-code` check in T4 only works after generated files are staged/committed or as a second regeneration check; first regenerate the checked-in OpenAPI snapshot from the live API, then generate TS and check drift.
- **Additional unhappy paths:** test End immediately around transaction commit (durable head versus suppressed old-talk status), queue overflow and cancellation in the same talk, invalid `train_turn` types/negative/out-of-range slide/UTF-8 byte-boundary, unauthorised or other-owner idle toggle and HTTP revert, 409 after second revert conflict, malformed provider bodies/401 fallback/429 deadline, and a remote import or revert while a current slide is held. Keep 4 KiB auth separate from 16 KiB authenticated command cap (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:30-32`, `:202-240`, `:329-347`).
- **Current-state audit:** Apart from C1, the sampled descriptions of script readers/writer/import, confirmation timing, client-delegation tool exposure, owner-scoped repository, bridge limit, transcript merge and reconnect source match the cited code (`src/PresenterAi.Infrastructure/Content/PostgresPresentationRepository.cs:60-81`; `src/PresenterAi.Cli/ImportCommand.cs:78-105`; `src/PresenterAi.Application/Presenting/Presenter.cs:1338-1347`, `:1553-1575`, `:2055-2062`; `src/PresenterAi.Api/Realtime/PresenterBridge.cs:30-32`, `:372-425`; `web/app/src/store/presenterStore.ts:81-103`). The plan should not imply that its in-memory file-mode store is user-accessible: §9 Q3 already explicitly rules out a CLI trainer entry.

## IS THIS PLAN READY TO IMPLEMENT?

**No.** Resolve **blockers A1, A2, D1–D4** in the design and task/test text first. The remaining findings are improvements, especially actual-session capability, rollback safety and tests that identify the selected historical exchange. No repository files were changed and no tests/builds were run.
<!-- REVIEW-COMPLETE r010-plan -->