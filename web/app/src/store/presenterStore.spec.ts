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
});
