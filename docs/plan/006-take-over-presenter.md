# 006 — Take over the presenter from another tab

**Status:** Approved (2026-09-22)
**Size:** M (server bridge protocol + Present page). **Branch:** `feature/006-take-over-presenter`, stacked on
`feature/005-echo-and-stall`; PR to `develop` after PR #5 merges.

## 1. Requirement (confirmed 2026-09-22)

Only one presenter page may be connected. Every other tab shows "The presenter is in use in another tab." and retries
quietly. With 100+ tabs open, the owner cannot find the tab that holds it.

**Goal:** a **Take over** button on the busy notice that moves the presenter to this tab.

**Decisions (user):**

| Question | Answer |
|---|---|
| A talk is running in the other tab | End it, then take over. Record "move the live talk" as backlog |
| The tab that loses control | Stops silently: a short notice, no button, no auto-reconnect; reload to use it again |
| Who may take over | The same signed-in user only |

**Out of scope:**
- moving a running talk without ending it (backlog, `docs/requirement/002-backlog-move-live-talk-between-tabs.md`);
- more than one presenter at a time;
- an admin "kick any user" control.

**Acceptance criteria:**
1. With tab A connected, tab B shows the busy notice with a **Take over** button.
2. Pressing it in B makes B the presenter within a few seconds, and Start works there.
3. A shows the taken-over notice, stops its audio and microphone, and does not reconnect until reloaded.
4. A talk running in A ends and its session is recorded as ended. Start in B resumes at that slide. Revised on
   2026-09-22: the brief assumed Start already resumes, which it does not (see §2). The user chose to make it resume.
5. A tab signed in as a different user sees "in use by another account" and no button. A take-over request from it is
   refused.
6. Server and web tests cover 1–5. The build and all test suites pass.

## 2. Current state (as built)

- **One slot:** `PresenterBridge._client`, taken by CAS in `HandleAsync` (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:83-89`).
  A second authenticated socket gets `SendBusyAsync`: `{type:"error", code:"busy", message}` and then close 1013 (`:438-443`).
  The busy frame does not say who holds the slot.
- **Owner cleanup** (`:103-127`) runs when the owner's receive loop ends:
  - it waits for the start observation (up to 90 s, `StartObservationBound`);
  - if the presenter is not idle, `ObserveEndAsync` ends the talk and waits up to 5 s for idle (`:389-416`);
  - then the recorder ends and the slot is released by CAS back to `null`.
  - Nothing tells a waiting party when the slot has been released.
- **Sending:** all sends go through `ClientConnection`'s single writer loop (`:488-516`). A second concurrent
  `SendAsync` or `CloseAsync` on the socket would break the one-send-at-a-time rule, and cancelling an in-flight send
  aborts the socket.
- **Auth frame:** `{type:"auth", ticket}` is parsed in `AuthenticateAsync` (`:130-212`). It returns only the user id.
- **Web client:**
  - `BridgeClient` sends the auth frame on open (`web/app/src/ws/bridgeClient.ts:98-102`).
  - It reconnects on every close, with a busy backoff for 1013 (`:107-123`), and emits `busy` on the busy error frame
    (`:206-219`).
  - `Present.tsx` shows `busyMessage` as an amber notice (`web/app/src/routes/Present.tsx:139-141,324-328`) and
    clears it on `accepted`.
- **Protocol docs:** `AGENTS.md:75` (busy = `error{code:"busy"}` + 1013).
- **Start never resumes today.**
  - The page always sends `start(presentation.id, 0)` (`web/app/src/routes/Present.tsx:244`;
    `bridgeClient.ts:138` defaults `fromIndex = 0`).
  - The presenter's rule `fromIndex ?? (last run interrupted && same deck ? last index : 0)` (`Presenter.cs:442-443`)
    therefore never reaches the resume branch.
  - An end through `EndAsync` closes with a close-request reason, which `IsNormalClose` counts as a normal end
    (`Presenter.cs:983`).
  - So the existing log line "closed: … (Start resumes at slide N)" after an upstream drop is also not kept by the
    page.

## 3. Design

**Protocol:**
- **Auth frame:** `{type:"auth", ticket, takeOver?: true}`. The flag is optional; old clients never send it.
- **Busy frame:** gains `canTakeOver: boolean`. It is true when the slot holder is the same user, false for another
  account.
  - With `takeOver`, the frame is sent only when the take-over is refused (another account) or the wait bound
    expires.
  - The close stays 1013.
- **The replaced tab:** it gets `{type:"error", code:"taken_over", message}` and then close **4409** `taken_over`.
  4409 is in the application range and follows the existing 4401 `session.ticket_invalid`.

**Server (`PresenterBridge`):**
1. `AuthenticateAsync` returns `(userId, takeOver)`.
2. **The CAS fails and `takeOver` is set:**
   - **The holder is the same user:** call `holder.RequestTakeOver()`, then await `holder.Released` for at most
     `TakeOverBound` (15 s), then retry the CAS once.
   - **The retry fails** (someone else won the slot, or the bound expired): busy with `canTakeOver: true`.
   - **Another account holds the slot:** busy with `canTakeOver: false`.
3. **`ClientConnection.RequestTakeOver()`:**
   - enqueues a *close* item on the outbound channel, so the writer sends the `taken_over` error frame and then
     `CloseOutputAsync(4409)`, strictly after any queued frames and never concurrently with a send;
   - arms a `ServerCloseBound` (1 s) timer that aborts the socket if the browser does not answer the close.
   - The owner's receive loop then ends normally, and **the existing cleanup runs unchanged**: the talk ends, the
     recorder finishes, and the slot is released.
4. **`ClientConnection.Released`:** a `TaskCompletionSource`, completed in `HandleAsync`'s `finally` right after the
   CAS release. It is also completed when the connection never owned the slot, so no waiter hangs.
5. **Two tabs racing:** both requests target the same holder, and the first CAS after release wins. The loser gets
   busy (`canTakeOver: true`). If the loser presses again, it takes over from the winner, which then shows the
   taken-over notice. The last press wins, as agreed.

**Resume after a take-over (user decision, revision 1):**
- `IPresenter.EndAsync` gains an optional `resumable` flag (default false).
  - `Presenter` keeps it for the run being ended.
  - In `OnClosed`, a resumable end records `_lastRun` as interrupted (`EndedNormally: false`) whatever the close reason
    was. Its log line says "(Start resumes at slide N)".
- The take-over path calls `ObserveEndAsync(resumable: true)`. A plain browser disconnect keeps today's normal end: a
  reload mid-talk still starts at slide 1, which is out of scope and noted in §7.
- The page's Start sends no `fromIndex`: `BridgeClient.start(presentation, fromIndex?)` omits the field when it is
  undefined. The existing presenter rule then decides:
  - after a take-over or an upstream drop on the same deck, Start resumes at that slide;
  - after a normal End, or on a different deck, it starts at slide 1.
- This also makes the existing "Start resumes at slide N" message true after an upstream drop.

**Web:**
- `BridgeClient.takeOver()`:
  - cancels any pending reconnect;
  - resets the busy backoff;
  - sets a one-shot flag so the next auth frame carries `takeOver: true`;
  - connects now.
- `BridgeClient`, when the replaced tab receives close 4409 (or the `taken_over` error):
  - emits `taken-over`;
  - does **not** schedule a reconnect; it behaves like `disconnect()`, but its listeners stay attached.
- `busy` now carries `canTakeOver`.
- `Present.tsx`:
  - the busy notice shows a **Take over** button when `canTakeOver` is true, and "in use by another account" when
    it is false;
  - `taken-over` → `stopAudio()` and the notice "This presenter was taken over by another tab. Reload to use it
    here." (no button). Start stays disabled.

**Sequence (take-over while A is presenting):**

```
Tab B            Bridge (HandleAsync B)        ClientConnection A        Presenter / recorder      Tab A
 |  press Take over
 |--auth{ticket,takeOver}-->|
 |                          | CAS fails; A.UserId == B user
 |                          |--RequestTakeOver()--------->| enqueue close item
 |                          |                             |--error{taken_over}, close 4409----------->|
 |                          |                             |<----------------------close reply---------| emits taken-over, stops audio,
 |                          |                    A receive loop ends → cleanup:                       | no reconnect
 |                          |                             |--EndAsync, wait idle (≤5 s)-->|
 |                          |                             |--EndRecorderAsync------------>|
 |                          |                    CAS _client A → null; A.Released set
 |                          |<--Released (≤15 s)----------|
 |                          | CAS null → B
 |<--state frame (idle)-----|  (accepted: busy notice cleared; Start without fromIndex resumes at A's slide via _lastRun)
```

Parallel paths checked: `SendBusyAsync` is the only place that refuses a socket, and `HandleAsync` is the only place
that owns or releases the slot. The writer-failure path (1011) is unchanged. A test seam (`StopCurrentWriterForTestAsync`)
shows that writer failure does not release the slot. Take-over must still work when the holder's writer has failed:
the close item then cannot be sent, so the 1 s abort timer ends the receive loop.

**Alternatives considered:**
- **Kick through the presenter or a REST endpoint** (`POST /v1/presenter/take-over`): a second path into slot
  ownership, plus a race between the HTTP call and the new socket. Rejected.
- **Close A from B's thread directly** (`CloseOutputAsync` on A's socket): it races A's writer (a concurrent send) and
  can abort the socket, so A would see 1006 and reconnect. Rejected in favour of the close item on A's own writer.
- **Browser-only** (`BroadcastChannel` asking the other tab to disconnect): it cannot work across browser profiles or
  devices, and it does nothing for a crashed or hung tab. Rejected.

## 4. Impact and risk

- **Backward compatibility:** old clients never send `takeOver`, and they ignore `canTakeOver` and `taken_over`
  (an unknown error code is only logged). An old client that is taken over reconnects as today and gets busy.
- **Security:** same user only. The user id comes from the claimed ticket, never from the frame. Another account
  cannot learn anything beyond `canTakeOver: false`.
- **Data:** the talk ends through the existing cleanup, so the session row is finalised exactly as on a normal
  disconnect (acceptance criterion 4). No schema change.
- **Stuck holder:** in the worst case the cleanup waits for a start still in flight (90 s bound). A take-over then
  gives busy after 15 s, and the user can press again. This is stated in the notice, not hidden.
- **Rollback:** revert the branch. The protocol additions are optional fields.

## 5. Tasks

1. **Server protocol and slot hand-over.**
   - **Change:** `PresenterBridge.cs`:
     - `AuthenticateAsync` returns `takeOver`;
     - `ClientConnection` gets `Released`, `RequestTakeOver()`, the close item in `WriteLoopAsync`, and the 1 s abort;
     - `HandleAsync` does the take-over branch with `TakeOverBound`;
     - `SendBusyAsync(socket, canTakeOver)`.
   - **Verify with new tests:**
     - `BridgeTests.Same_user_take_over_replaces_the_holder`: B gets a state frame; A gets `taken_over` and close 4409.
     - `Take_over_by_another_account_is_refused`: busy with `canTakeOver:false`, and A keeps its slot. Tickets for two
       users come from `BridgeTestSupport`.
     - `Busy_without_take_over_offers_it_to_the_same_user`: `canTakeOver:true`.
     - `Take_over_ends_a_running_talk_and_records_it` (`BridgeSessionRecorderTests`): the recorder is ended once with
       the close reason, and B's Start (without `fromIndex`) resumes at A's slide.
     - `Take_over_when_the_holders_writer_has_failed_still_releases`: uses the existing writer-stop seam.
     - `Take_over_that_waits_past_the_bound_gets_busy`: uses a holder whose cleanup is blocked.
   - **Mutation proof per test:** drop the same-user check; skip the wait; send the close off the writer; never
     complete `Released`.
1b. **Resumable end (presenter).**
   - **Change:** `IPresenter.EndAsync(bool resumable = false, …)`, and `Presenter`, where `EndAsyncCore` stores the
     flag and `OnClosed` uses `IsNormalClose(reason) && !resumable`. The flag is reset on each Start.
   - **Verify:** new `PresenterTests`:
     - `Resumable_end_lets_start_resume_at_the_slide`;
     - `Normal_end_starts_again_at_slide_one` (presence and absence);
     - `Resumable_flag_does_not_leak_into_the_next_run`.
   - **Mutation proof:** ignore the flag; never reset it.
2. **Web client.**
   - **Change:** `bridgeClient.ts`: `takeOver()`, the one-shot auth flag, the 4409 handling with no reconnect, the
     `taken-over` event, and `start()` omitting `fromIndex` when it is undefined.
   - **Verify:** `bridgeClient.spec.ts`:
     - the auth frame carries `takeOver` only once;
     - 4409 emits `taken-over` and schedules no reconnect (fake timers advance 60 s and no new socket appears);
     - `takeOver()` cancels a pending busy backoff and connects at once;
     - `start("p")` sends no `fromIndex`, and `start("p", 2)` sends 2.
3. **Present page.**
   - **Change:** `Present.tsx`: the Take over button, the other-account text, the taken-over notice with
     `stopAudio()`, and Start without `fromIndex`.
   - **Verify:** `Present.spec.tsx`:
     - the button appears only for `canTakeOver:true` and calls `takeOver`;
     - the other-account text has no button;
     - the taken-over notice stops audio and keeps Start disabled;
     - Start sends no `fromIndex`.
4. **Docs.**
   - `AGENTS.md:75`: add `takeOver`, `canTakeOver` and 4409.
   - Add a take-over row to the README troubleshooting table.
   - Write the backlog doc `docs/requirement/002-backlog-move-live-talk-between-tabs.md`: the goal, why it is
     deferred (recorder ownership, audio and mic handover, a user gesture is needed for audio), and a sketch.
5. **Verification and wiring audit.**
   - Run the full .NET build (`-warnaserror`) and tests, then web lint, tests and build.
   - Manual runbook with the local API on 47913 and two tabs:
     1. Start a talk in A.
     2. Press Take over in B. B connects, A shows the notice and goes silent.
     3. Start in B resumes at A's slide.
     4. Tab A stays quiet for 60 s (no reconnect in the Network panel).
     5. A second account (dev sign-in) sees "in use by another account".

## 6. Test strategy

Server behaviour is covered by bridge integration tests against `FakeLiveServer`, with real WebSockets. They cover:
- the happy path;
- a different account;
- no take-over flag;
- a running talk;
- a failed writer;
- the bound expiring.

Client behaviour is covered by the `BridgeClient` unit tests (a fake WebSocket and fake timers) and the `Present`
component tests. The manual runbook above covers the two-tab flow end to end.

## 7. Open questions

None. Out of scope, noted: a browser reload or tab close mid-talk still ends normally, so Start begins at slide 1. Making
that resumable too is a one-line change in the disconnect path, if wanted later.

## Approval log

| Date | Step | By |
|---|---|---|
| 2026-09-22 | Requirement brief confirmed (G1) | user |
| 2026-09-22 | Plan approved (G2) | user |
| 2026-09-22 | Revision 1: criterion 4 relied on a wrong assumption (Start never resumed); the user chose "resume at the slide" (task 1b, Start without `fromIndex`) | user |
