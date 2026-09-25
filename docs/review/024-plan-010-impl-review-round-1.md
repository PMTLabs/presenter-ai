# r010-impl-r1 — implementation review

Scope: `git diff 9b8e61a..c68bbea` against plan 010 (§2 and §4.4), reviews 021–023, and `AGENTS.md`. Read-only review; I did not run builds/tests or open secrets. The implementers report the specified suites green. The audit in `docs/research/009-plan-010-wiring-audit.md` is not a substitute for the missing end-user cases below.

## Blockers

### D1 — Version history becomes inaccessible after page 1
**Severity:** blocker. **Location:** `web/app/src/components/ScriptVersions.tsx:50-63,158-188`.

`loadVersions()` issues `GET /v1/presentations/{id}/revisions` without `page`/`pageSize`, stores only `data.items`, and never uses `data.total` or requests another page. The endpoint defaults to 25 (`src/PresenterAi.Api/Endpoints/RevisionEndpoints.cs:41-42`). With 26 versions, v1 disappears from the panel and cannot be inspected or reverted there, contrary to AC4/AC9 and the claim that *every* version is recoverable in the panel. The UI test called “lists every version” (`ScriptVersions.spec.tsx:65-78`) supplies only two items, so it cannot detect this.

**Fix:** Add paging/load-more or fetch all pages until `items.length === total`, with cancellation/race protection on presentation change and refresh. Add a >25-version UI oracle selecting and reverting an item from page 2.

### D2 — Required live Trainer-mode verification has not been recorded
**Severity:** blocker (merge gate, not a code failure). **Location:** `docs/plan/010-live-presenter-training.md:78-79,1104-1120,1179`; `docs/research/009-plan-010-wiring-audit.md:111-116`.

AC10 requires “a live run with Trainer mode … recorded in the work log with usage seconds.” The audit explicitly places the live run in *T13 (not done here)*, and the current `docs/progress/` work log contains no plan-010 Trainer-mode run/usage entry. Green fake-server and Testcontainers flows are not an observed upstream run. This is an unmet acceptance gate, not an assertion that the implementation would fail live.

**Fix:** Complete the plan's T13 live run and record the observed Trainer-mode flow and `usage.seconds` in the existing work log before merging; do not put credentials in the log.

## Improvements

### D3 — An ordinary long question makes “Train on this” silently fail
**Severity:** should-fix. **Location:** `web/app/src/store/exchanges.ts:32-48`; `src/PresenterAi.Api/Realtime/PresenterBridge.cs:475-489`.

The answer is truncated to 2,000 characters but the joined question is not. A transcript with multiple user turns totalling 2,001 characters offers an enabled button; clicking it sends a `train_turn` that the bridge rejects with `protocol`, so the selected exchange is never enqueued. There is no feedback-specific recovery in the transcript UI. This is the same size constraint already handled for the answer, missed for the other field.

**Fix:** Apply an explicit 2,000-character policy to **both** fields when building the exchange (or disable with a visible explanation), and test a multi-turn overlong question through the click/bridge path.

### D4 — The detail panel retains obsolete `isCurrent` after a head change
**Severity:** should-fix. **Location:** `web/app/src/components/ScriptVersions.tsx:71-106,188-205`.

A `script_version` refreshes only the list. The selected `detail` is fetched only when `selected` or `presentationId` changes. If a selected current revision is superseded by a live edit, its old `detail.isCurrent === true` still hides the Revert button; conversely, after a revert the selected revision may still show a Revert button even though it is now current. The refreshed list and detail disagree during a talk.

**Fix:** Refetch the selected detail on version/list change or derive its current flag from the refreshed list/head version. Test a selected revision changing current status without changing selection.

### D5 — Response-side parser does not enforce the advertised strict schema
**Severity:** should-fix. **Location:** `src/PresenterAi.Infrastructure/Training/ResponsesScriptReviser.cs:268-301`.

`Schema()` declares `additionalProperties:false` for both the root and slide items (`:361-395`), but `ParseRevision()` checks only that `slides`, `summary`, `number` and `narration` exist. A successful response containing `{"slides":[{"number":2,"narration":"changed","extra":"x"}],"summary":"x","extra":"x"}` returns `Ok` rather than `invalid_output`. Strict request formatting asks the provider to comply; it does not validate an actual provider response. The subsequent validator checks slide scope and narration but never sees these extra keys.

**Fix:** Check the exact allowed property sets in `ParseRevision()` (and reject missing/wrong types as today), or validate the parsed JSON against the declared response schema. Add an adversarial response test asserting `invalid_output` and no fallback/append.

### B1 — “Across navigation” exchange oracle cannot tell question-slide from answer-slide targeting
**Severity:** should-fix. **Location:** `web/app/src/store/exchanges.spec.ts:54-61`.

The test named “uses the slide of the question's first delta across navigation” supplies `user(..., 2)` and `presenter(..., 2)` and asserts target 2. A wrong `groupExchanges()` that takes `turns[answerStart].slide` instead of `turns[questionStart].slide` passes unchanged; no navigation actually appears in the input. This is precisely the target-selection identity that AC6 and the plan-review B1 require the oracle to distinguish. The other exchange test verifies a different-slide answer is excluded, not this claimed cross-navigation case.

**Fix:** Construct a distinguishable navigation case in the reducer/first-delta tests and assert both the selected question, full answer and target slide, including an answer whose slide stamp differs from the question when the intended policy permits it; otherwise rename this test to state its actual subject and add an end-to-end target-identity oracle for navigation.

## Checked without findings

The service does take one `_gate` snapshot for head plus outcomes and writes an applied outcome with its head under that lock (`ScriptRevisionService.cs:252-270,649-655`); the presenter settles by id rather than signal order. Commit and close each release only an acquired permit, including the timed-out close (`:619-670,686-760`); stores are scoped per operation. Postgres append keeps CAS, revision insert and commit in one transaction; migration backfills v1 and guards downgrade with history. The bridge retains a separate 4 KiB authentication cap and a 16 KiB authenticated-text cap. Existing test edits in this diff are limited to the allowed tool-count and oversize-bound changes. These checks do **not** establish that every unhappy path or live upstream behaviour is covered.

## IS THIS BRANCH READY TO MERGE?

**No. Blockers:** D1 (old revisions inaccessible from the required UI) and D2 (AC10 live run/usage evidence outstanding). **Improvements:** D3–D5 and B1; address and add distinguishing oracles without weakening existing tests. No tests or builds were run as requested.
<!-- REVIEW-COMPLETE r010-impl-r1 -->