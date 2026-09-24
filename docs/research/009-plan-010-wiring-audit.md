# 009 — Plan 010 T12 wiring audit (emission → consumption)

**Scope:** branch `feature/010-lane-f` (all lanes T1–T10 merged, T11 at `d00d358`), 2026-09-24. Rule applied: plan 010
§6 T12 and `~/.claude/docs/07-integration-boundary-audit.md` §5 — every new entity traced from its writer to its reader;
**NO CALLER** rows are findings. `.env` was not opened.

## Disposition

| Finding | Verdict | Action |
|---|---|---|
| The §4.3 presenter page-log lines (`edit: queued/processing/applied/failed/hold/replay/head reloaded`, `trainer: on (voice: …)`) had no test and no runbook log (T12 verify: "each §4.3 log line appears in a test or runbook log") | real (test gap) | `PresenterTrainingServiceTests.Page_log_lines_follow_an_edit_from_queue_to_replay_and_failure` asserts every line and their order against the real service |
| `RevisionEndpointIntegrationTests`, `ScriptRevisionServicePostgresTests`, the production-DI test and the CLI DI assertions for `IScriptRevisionService`/`IScriptReviser` were deferred by lanes A/B | real (deferred) | added in T11 (`d00d358`) |
| Presenter training tests ran only against `FakeScriptRevisionService` (lane C) | real (deferred) | `PresenterTrainingServiceTests` covers the key flows against the real service |
| The Postgres store has no hook between CAS and `COMMIT` | by design | test-only seams in `Integration.Tests/Support/TrainingSupport.cs` (`CommitGateInterceptor`, a `DbTransactionInterceptor`) and a racing `DbCommandInterceptor` in `RevisionEndpointIntegrationTests`; no production hook |
| `Changed` is subscribed in the `Presenter` constructor (`Presenter.cs:168`, unsubscribed at `:316`), not in `AddPresenter` as §4.1 words it | equivalent | none — `AddPresenter` passes the service (`DependencyInjection.cs:265`); the subscription lives with the presenter's lifetime |
| Lane C named some §6 T7 tests differently (`End_closes_the_talk_registration_before_the_upstream_and_drops_later_signals` for `End_while_the_commit_is_gated_after_cas_in_both_orders`; no `Next_start_loads_a_commit_that_finished_before_close`) | covered elsewhere | the gated-commit End in both orders runs on Postgres (`ScriptRevisionServicePostgresTests`, `LiveTrainingFlowTests.End_over_ws_while_the_commit_is_gated_after_cas`), next-Start loading in `LiveTrainingFlowTests.Commit_from_another_process_is_used_at_next_start` and `PresenterTrainingServiceTests.Next_start_loads_a_commit_made_while_no_talk_was_running` |

No production wiring gap was found: every new port, option key, frame, endpoint and error code has a writer, a reader
and a test (tables below). No production code changed in T12.

## Ports → production registrations → resolvers

| Port | API | CLI `run --owner` | CLI file mode | Resolved by | Test that dies |
|---|---|---|---|---|---|
| `IPresentationRevisionStore` | `PostgresPresentationRevisionStore`, scoped, `AddPersistence` (`Infrastructure/DependencyInjection.cs:35`) | same (`Cli/Program.cs` `BuildRunServices` → `AddPersistence`) | `InMemoryPresentationRevisionStore` singleton seeded from the file, `AddPresenter(fileBacked)` (`DependencyInjection.cs:181-199`) | `ScriptRevisionService` (a fresh async scope per operation, `ScriptRevisionService.cs:193,646,796,816`); `RevisionEndpoints` list/detail (`RevisionEndpoints.cs:44,78`) | `LiveTrainingFlowTests.Production_container_resolves_training_services`, `CliTests.Run_services_build_with_training_services_in_owner_and_file_mode`, `ScriptRevisionServicePostgresTests.Two_presentations_edit_concurrently_on_postgres` |
| `IScriptReviser` → `ResponsesScriptReviser` | `AddScriptTraining` from `AddPresenter` (`DependencyInjection.cs:179,279`) | same | same | `ScriptRevisionService` ctor (`DependencyInjection.cs:289`) | same two DI tests; `ResponsesScriptReviserTests.*`; the stub `"responses"` handler in every `LiveTrainingFlowTests` flow (the real reviser builds and parses each request) |
| Named `HttpClient` `"responses"` | `AddScriptTraining` (`DependencyInjection.cs:275-277`) | same | same | `ResponsesScriptReviser.ReviseAsync` (`ResponsesScriptReviser.cs:80`) | `LiveTrainingFlowTests.Voice_edit_confirmed_over_ws_is_persisted_and_used_by_the_next_talk` (asserts the posted `input`) |
| `IScriptRevisionService` → `ScriptRevisionService` (singleton) | `AddScriptTraining` (`DependencyInjection.cs:287`) | same | same | `Presenter` (`DependencyInjection.cs:265`); `RevisionEndpoints.RevertAsync` (`RevisionEndpoints.cs:126`) | the two DI tests (with `ValidateScopes` + `ValidateOnBuild`); `RevisionEndpointIntegrationTests.*` |
| `TrainingOptions` | `AddUpstreamOptions`, `Validate` + `ValidateOnStart` (`DependencyInjection.cs:124-131`) | `AddUpstreamOptions` | `AddUpstreamOptions` | `ScriptRevisionService` ctor (`ScriptRevisionService.cs:63`), `ResponsesScriptReviser` budget (`DependencyInjection.cs:282`) | `StartupTests.Out_of_range_reviser_timeout_fails_startup`, `ScriptRevisionServiceTests.Timeout_after_60_seconds_fails_with_timeout` |
| `ITool` `revise_script` (`ReviseScriptTool`) | `PresenterToolsRegistration.cs:42-44` | same | same | catalogue snapshot forwards `ConfirmationQuestion` (`ToolSessionCatalogue.cs:106`) → `OnToolCall` question (`Presenter.cs:1598`) → gate by resolved `Name` (`Presenter.Training.cs:289`) | `BridgeTrainingTests.Tool_call_yes_then_applied_emits_script_edit_frames_in_order`, `LiveTrainingFlowTests.Voice_edit_confirmed_over_ws_…` (FakeLiveServer sees `revise_script` in `session.start`), `ToolSessionCatalogueTests.Snapshot_forwards_confirmation_question` |

## Option keys → readers

| Key | Files | Reader |
|---|---|---|
| `Training:ReviserTimeoutSeconds` (60, 10…180) | `Api/appsettings.json:31`, `appsettings.Example.json:28` | `ScriptRevisionService` (per-call bound) and `ResponsesScriptReviser` (budget across routes) |
| `Upstream:DelegationModel`, `Upstream:Fallback:DelegationModel` (existing, new reader) | appsettings, compose `docker-compose.yml:48-49`, `.env.example` placeholders | `ResponsesScriptReviser` routes (`ResponsesScriptReviser.cs:54-55`); presenter trainer availability (`DependencyInjection.cs` `IsService`/route checks) |

No new key without a reader; no reader without a bound key.

## `/ws` frames (plan §4.3, additive)

| Frame | Writer | Reader | Tests |
|---|---|---|---|
| C→S `trainer_mode` | `bridgeClient.ts:195-196` ← `TrainerControls` toggle (`Present.tsx:410`) | `PresenterBridge.cs:446` → `IPresenter.SetTrainerModeAsync` | `bridgeClient.spec.ts` "sends trainer_mode and train_turn", `BridgeTrainingTests.Trainer_mode_frame_is_acknowledged_by_script_version`, `LiveTrainingFlowTests` (every flow) |
| C→S `train_turn` | `bridgeClient.ts:198-199` ← Transcript "Train on this" (`Present.tsx:558`) | `PresenterBridge.cs:455` (types, 1…2,000 chars, range, ≤ 16 KiB `:33`) → `TrainOnTurnAsync` | `BridgeTrainingTests.Train_turn_frame_creates_an_edit`, `Invalid_train_turn_fields_are_protocol_errors`, `Vietnamese_train_turn_at_16_KiB_…`, `Transcript.spec.tsx`, `LiveTrainingFlowTests.Train_on_this_over_ws_persists_a_live_edit_for_the_selected_slide` |
| S→C `script_edit` | `PresenterBridge.cs:81,85` ← `Presenter.ScriptEdit` (reconcile) | `bridgeClient.ts:273` → `presenterStore.ts:182` → status chip | `BridgeTrainingTests.Tool_call_yes_then_applied_…`, `Failed_edit_emits_failed_frame_with_error`, `presenterStore.spec.ts` "does not regress a terminal edit status", `Present.spec.tsx` "shows updating then updated …", `LiveTrainingFlowTests.Two_queued_edits_and_a_revert_converge_with_all_rows_kept` (one terminal frame per id) |
| S→C `script_version` | `PresenterBridge.cs:82,96,168` (connect, Start, toggle, reconnect, commits) | `bridgeClient.ts:276` → `presenterStore.ts:174` → `ScriptVersions` refresh | `BridgeTrainingTests.Reconnecting_client_gets_script_version`, `ScriptVersions.spec.tsx` "revert posts and refreshes", `LiveTrainingFlowTests` (monotonic versions) |
| Auth frame stays ≤ 4 KiB | `PresenterBridge.cs:30` | — | `BridgeTrainingTests.Auth_frame_is_still_limited_to_4_KiB` |

## HTTP endpoints → OpenAPI → generated client → UI

| Endpoint | OpenAPI (`web/shared/openapi/v1.json`) | Generated client (`generated.d.ts`) | UI caller | Tests |
|---|---|---|---|---|
| `GET /v1/presentations/{id}/revisions` | `:229` | `:178` | `ScriptVersions.tsx:52` | `RevisionEndpointTests.List_is_newest_first_…`, `RevisionEndpointIntegrationTests.List_detail_and_revert_round_trip_on_postgres` |
| `GET …/revisions/{number}` | `:315` | `:194` | `ScriptVersions.tsx:88` | `RevisionEndpointTests.Detail_has_before_and_after_per_changed_slide`, integration round trip |
| `POST …/revisions/{number}/revert` | `:378` | `:210` | `ScriptVersions.tsx:114` | `RevisionEndpointTests.Revert_returns_201_…`, `RevisionEndpointIntegrationTests.Revert_that_loses_the_cas_twice_is_409_and_writes_nothing`, `LiveTrainingFlowTests.Revert_over_http_during_a_talk_replays_and_persists` |

Drift: `OpenApiTests.Checked_in_document_matches_live_document` passes on this branch, so the checked-in document is
the live one; the TS client was not regenerated (no document change).

**Error code `revision.not_found`:** `Contracts/ErrorCodes.cs:23,86` → `errorCodes.ts:34,95` → `errorMessages.ts:34` →
conventions doc `docs/reference/001-api-and-code-conventions.md:124`; `OpenApiTests.Every_error_code_has_title_and_status`,
`RevisionEndpointIntegrationTests.Unknown_revision_and_other_owner_are_not_found`, `ScriptVersions.spec.tsx` "shows
revision.not_found message".

## Voice-edit chain (plan §6 chain map) — end-to-end evidence added in T11

registry → catalogue snapshot → `session.start` tools → tool call → `PendingToolConfirmation.Intent` (`Presenter.cs:1574,
1837-1838`) → yes on the loop → `ApproveScriptEdit(intent)` (`Presenter.cs:1771`) → `EnqueueEdit` →
`IScriptRevisionService.Enqueue` (`Presenter.Training.cs:362`) → worker: scope → head → `ResponsesScriptReviser` (stub
upstream) → validate → compose → commit lock → Postgres CAS → `Observe` + `applied` in one `_gate` section → `Changed`
→ `OnScriptRevisionsChanged` → `QueueFromProducer(ReconcileRequested)` (`Presenter.Training.cs:87-88`) →
`ReconcileWithHead` (`:412`) → `ScriptVersion`/`ScriptEdit` → bridge frames → next Start loads `presentations.script`
(`PostgresPresentationRepository.cs:80` sets `Version`). One test walks all of it:
`LiveTrainingFlowTests.Voice_edit_confirmed_over_ws_is_persisted_and_used_by_the_next_talk`.

Start → `Observe` + `OpenTalk` only after the upstream is connected (`Presenter.cs:813` → `Presenter.Training.cs:140-142`);
End → `CloseTrainingTalkAsync` awaited in `EndAsyncCore` (`Presenter.cs:2214`); `OnClosed` → `ResetTrainingOnClosed`
(`Presenter.cs:2276`, observed close); ticket cancellation → `ticketToken.Register(CloseEntryAsync)`
(`ScriptRevisionService.cs:296`). Import → revision row (`Cli/ImportCommand.cs:111-116`); guarded `Down`
(`Migrations/20260924143442_PresentationRevisions.cs:13,65`).

## §4.4 handoff table re-checked against the code

| Handoff | Mechanism in code |
|---|---|
| Tool call / transcript / audio / timers → loop | existing `QueueFromProducer`; training handlers touch loop-only fields (`Presenter.Training.cs` state block) |
| Confirmation → enqueue | intent in `PendingToolConfirmation` (loop-only), `Enqueue` under the service `_gate` — no off-loop hop (`Presenter.cs:1771`) |
| Off-loop tool acknowledgement | `ReviseScriptTool.InvokeAsync` returns a constant; no shared state |
| Train-on-this → loop | `EnqueueCommandAsync(new TrainOnTurnCommand(...))` (`Presenter.Training.cs:62`), validated on the loop (`:244-270`) |
| Worker → presenter | `Changed` is payload-free (`Presenter.Training.cs:87-88`); `GetReconciliationSnapshot` one `_gate` read (`ScriptRevisionService.cs` `GetReconciliationSnapshot`); head + `applied` in one section (`CommitAsync`) |
| HTTP revert → presenter | store CAS + `Observe` under `_gate` + reconcile (`RevertAsync`) |
| Head snapshot updates | `ObserveLocked` monotonic max under `_gate` |
| End/max length vs commit | per-talk `CommitLock` + `MarkClosed` one-shot; close bounded by `CloseBound` (`ScriptRevisionService.cs:694`) |
| Start failure after `OpenTalk` | `FailSafeCloseAsync` → `OnClosed` → `ResetTrainingOnClosed` |
| End → hung reviser | `registration.Cts` cancellation via the ticket callback |
| Idle toggle | stored with the owner id on the loop (`Presenter.Training.cs:220`) |
| Web transcript grouping | single-threaded reducer (`store/exchanges.ts`) |

Every handoff names one of the three mechanisms; none depends on event order.

## AC → passing tests

| AC | Tests (all passing on this branch) |
|---|---|
| 1 question, request on yes | `PresenterTrainingTests.Voice_feedback_asks_to_add_it_and_yes_sends_the_reviser_request`, `BridgeTrainingTests.Tool_call_yes_then_applied_…`, `LiveTrainingFlowTests.Voice_edit_confirmed_over_ws_…` |
| 2 hold current / continue to later | `PresenterTrainingTests` gate family (`Late_audio_while_held_…`, `Pending_edit_on_later_slide_lets_narration_continue_then_holds_on_arrival`, `Held_reconnect_…`) |
| 3 N+1, replay, N revertible | `PresenterTrainingTests.Applied_edit_replays_current_slide_with_new_narration`, `PresenterTrainingServiceTests.Voice_edit_through_the_real_service_is_applied_replayed_and_versioned`, `LiveTrainingFlowTests.Voice_edit_confirmed_over_ws_…` |
| 4 revert used now and later | `LiveTrainingFlowTests.Revert_over_http_during_a_talk_replays_and_persists`, `Revert_is_used_by_the_next_load`, `RevisionEndpointIntegrationTests.List_detail_and_revert_round_trip_on_postgres`, `ScriptVersions.spec.tsx` "revert posts and refreshes" |
| 5 off / no / silence → Q&A | `PresenterTrainingTests.Trainer_off_tool_call_returns_trainer_mode_off_…`, `No_or_ten_seconds_silence_answers_as_question_…`, `Vietnamese_co_confirms_and_khong_declines_the_edit` |
| 6 Train on this | `exchanges.spec.ts` (3), `Transcript.spec.tsx` (3), `PresenterTrainingTests.Train_on_turn_sends_the_exchange_for_the_given_slide`, `LiveTrainingFlowTests.Train_on_this_over_ws_…` |
| 7 failure unchanged, spoken + UI | `PresenterTrainingTests.Failed_edit_speaks_failure_once_releases_hold_and_keeps_text`, `ScriptRevisionServiceTests.Invalid_or_zero_change_output_leaves_version_unchanged`, `Timeout_after_60_seconds_fails_with_timeout`, `BridgeTrainingTests.Failed_edit_emits_failed_frame_with_error`, `Present.spec.tsx` "shows failure reason", `ResponsesScriptReviserTests.*` |
| 8 FIFO on newest, revert kept | `ScriptRevisionServiceTests.Queued_edit_is_applied_on_the_newest_version`, `Revert_to_older_narration_while_edit_targets_same_slide`, `LiveTrainingFlowTests.Two_queued_edits_and_a_revert_converge_with_all_rows_kept`, `Remote_import_while_current_slide_is_held_rebuilds_and_reconciles`, `ScriptVersions.spec.tsx` "revert shows the pending edits that will follow" |
| 9 UI states, version list | `Present.spec.tsx` "shows updating then updated with version and summary", `ScriptVersions.spec.tsx` "lists every version with source and summary", `ImportCommandTests.Import_then_reimport_creates_revisions_1_and_2`, `RevisionEndpointTests.*` |
| 10 build, suites, live run | this T12 verification (build `-warnaserror`, every .NET suite, web lint/test/build); the live run is T13 (not done here) |
