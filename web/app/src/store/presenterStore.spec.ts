import { beforeEach, describe, expect, it } from "vitest";
import { usePresenterStore } from "./presenterStore";

describe("presenterStore", () => {
  beforeEach(() => {
    usePresenterStore.setState({
      snapshot: { state: "idle", slideIndex: 0, slideCount: 0, muted: false },
      slide: 0,
      usage: null,
      transcript: [],
      logs: [],
      connected: false,
      micReady: false,
      bufferedMs: 0,
      limitWarning: null,
      upstreamStatus: null,
      suspended: false,
      endReason: null,
      usageConfirmed: null,
      estimatedSeconds: null,
      trainerMode: false,
      trainerAvailable: false,
      voiceTraining: true,
      scriptVersion: null,
      edits: {},
      editOrder: [],
      currentEditId: null,
    });
  });

  it("handles limit_warning and clearing on secondsLeft: null", () => {
    const store = usePresenterStore.getState();
    store.message({ type: "limit_warning", kind: "max_length", secondsLeft: 60 });
    expect(usePresenterStore.getState().limitWarning).toEqual({
      kind: "max_length",
      secondsLeft: 60,
    });

    store.message({ type: "limit_warning", kind: "max_length", secondsLeft: null });
    expect(usePresenterStore.getState().limitWarning).toBeNull();
  });

  it("trainer_state sets Trainer mode from the server in and out of a talk", () => {
    const store = usePresenterStore.getState();
    store.message({ type: "trainer_state", trainerMode: true, trainerAvailable: true, voiceTraining: true });
    expect(usePresenterStore.getState()).toMatchObject({ trainerMode: true, trainerAvailable: true, voiceTraining: true });

    // A talk on a client-mode connection, then End: the reset clears both the switch and the voice notice.
    store.message({
      type: "script_version",
      presentationId: "demo",
      version: 3,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: false,
    });
    expect(usePresenterStore.getState().voiceTraining).toBe(false);
    store.message({ type: "trainer_state", trainerMode: false, trainerAvailable: true, voiceTraining: true });
    expect(usePresenterStore.getState()).toMatchObject({ trainerMode: false, trainerAvailable: true, voiceTraining: true });
    expect(usePresenterStore.getState().scriptVersion).toBe(3);
  });

  it("handles upstream status frames", () => {
    const store = usePresenterStore.getState();
    store.message({ type: "upstream", status: "suspended" });
    expect(usePresenterStore.getState().upstreamStatus).toBe("suspended");
    expect(usePresenterStore.getState().suspended).toBe(true);
    expect(usePresenterStore.getState().snapshot.suspended).toBe(true);

    store.message({ type: "upstream", status: "reconnecting" });
    expect(usePresenterStore.getState().upstreamStatus).toBe("reconnecting");
    expect(usePresenterStore.getState().suspended).toBe(true);

    store.message({ type: "upstream", status: "live" });
    expect(usePresenterStore.getState().upstreamStatus).toBe("live");
    expect(usePresenterStore.getState().suspended).toBe(false);
    expect(usePresenterStore.getState().snapshot.suspended).toBe(false);
  });

  it("handles closed frame with extended billing guard fields", () => {
    const store = usePresenterStore.getState();
    store.message({ type: "limit_warning", kind: "idle", secondsLeft: 30 });
    store.message({ type: "upstream", status: "suspended" });

    store.message({
      type: "closed",
      reason: "timeout",
      endReason: "max_length",
      usageConfirmed: false,
      estimatedSeconds: 3600,
    });

    const state = usePresenterStore.getState();
    expect(state.limitWarning).toBeNull();
    expect(state.upstreamStatus).toBeNull();
    expect(state.suspended).toBe(false);
    expect(state.endReason).toBe("max_length");
    expect(state.usageConfirmed).toBe(false);
    expect(state.estimatedSeconds).toBe(3600);
  });

  it("applySnapshot updates suspended from snapshot", () => {
    const store = usePresenterStore.getState();
    store.applySnapshot({
      state: "paused",
      slideIndex: 1,
      slideCount: 3,
      muted: false,
      suspended: true,
    });
    expect(usePresenterStore.getState().suspended).toBe(true);
    expect(usePresenterStore.getState().snapshot.suspended).toBe(true);

    store.applySnapshot({
      state: "idle",
      slideIndex: 0,
      slideCount: 0,
      muted: false,
    });
    expect(usePresenterStore.getState().suspended).toBe(false);
  });

  it("tracks edit status by id", () => {
    const store = usePresenterStore.getState();
    store.message({
      type: "script_edit",
      id: "edit_1",
      status: "queued",
      slideIndexes: [2],
      version: null,
      summary: null,
      error: null,
    });
    store.message({
      type: "script_edit",
      id: "edit_2",
      status: "queued",
      slideIndexes: [5],
      version: null,
      summary: null,
      error: null,
    });
    store.message({
      type: "script_edit",
      id: "edit_1",
      status: "processing",
      slideIndexes: [2],
      version: null,
      summary: null,
      error: null,
    });

    const state = usePresenterStore.getState();
    expect(state.edits.edit_1).toEqual({
      id: "edit_1",
      status: "processing",
      slideIndexes: [2],
      version: null,
      summary: null,
      error: null,
    });
    expect(state.edits.edit_2).toEqual({
      id: "edit_2",
      status: "queued",
      slideIndexes: [5],
      version: null,
      summary: null,
      error: null,
    });
    // The most recently queued edit becomes the one the chip follows.
    expect(state.currentEditId).toBe("edit_2");
  });

  it("a new talk starts with no edits, so a reused edit id is tracked again", () => {
    const store = usePresenterStore.getState();
    const frame = (status: string, extra: Record<string, unknown> = {}) =>
      store.message({
        type: "script_edit",
        id: "edit_1",
        status,
        slideIndexes: [0],
        version: null,
        summary: null,
        error: null,
        ...extra,
      });
    store.applySnapshot({ state: "presenting", slideIndex: 0, slideCount: 3, muted: false });
    frame("queued");
    frame("applied", { version: 9, summary: "Old talk" });
    store.applySnapshot({ state: "idle", slideIndex: 0, slideCount: 3, muted: false });

    // T13 live run: the next talk's edit_1 failed, but the chip kept "Updated — v9".
    store.applySnapshot({ state: "connecting", slideIndex: 0, slideCount: 3, muted: false });
    expect(usePresenterStore.getState().edits).toEqual({});
    expect(usePresenterStore.getState().currentEditId).toBeNull();
    frame("queued");
    frame("failed", { error: "timeout" });

    const state = usePresenterStore.getState();
    expect(state.currentEditId).toBe("edit_1");
    expect(state.edits.edit_1.status).toBe("failed");
    expect(state.edits.edit_1.error).toBe("timeout");
  });

  it("does not regress a terminal edit status", () => {
    const store = usePresenterStore.getState();
    store.message({
      type: "script_edit",
      id: "edit_1",
      status: "queued",
      slideIndexes: [2],
      version: null,
      summary: null,
      error: null,
    });
    store.message({
      type: "script_edit",
      id: "edit_1",
      status: "applied",
      slideIndexes: [2],
      version: 7,
      summary: "Added the 2025 figures",
      error: null,
    });
    // A stale/duplicate non-terminal frame for the same id must never overwrite the terminal status.
    store.message({
      type: "script_edit",
      id: "edit_1",
      status: "processing",
      slideIndexes: [2],
      version: null,
      summary: null,
      error: null,
    });

    expect(usePresenterStore.getState().edits.edit_1).toEqual({
      id: "edit_1",
      status: "applied",
      slideIndexes: [2],
      version: 7,
      summary: "Added the 2025 figures",
      error: null,
    });
  });

  it("stamps slide on the first delta and keeps it while merging", () => {
    const store = usePresenterStore.getState();
    store.message({ type: "slide", index: 2 });
    store.message({ type: "transcript", role: "assistant", delta: "Hello", end_ms: 100 });
    // Navigation mid-utterance must not retroactively move the turn's stamped slide.
    store.message({ type: "slide", index: 5 });
    store.message({ type: "transcript", role: "assistant", delta: " world", end_ms: 900 });

    const transcript = usePresenterStore.getState().transcript;
    expect(transcript).toHaveLength(1);
    expect(transcript[0]).toEqual({ role: "assistant", text: "Hello world", endMs: 900, slide: 2 });

    // A new turn (role change) picks up the current slide.
    store.message({ type: "transcript", role: "user", delta: "Question", end_ms: 1500 });
    expect(usePresenterStore.getState().transcript[1].slide).toBe(5);
  });
});
