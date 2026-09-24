/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BridgeClient, type BridgeMessage, type Snapshot } from "./bridgeClient";
import { groupExchanges } from "../store/exchanges";

class FakeSocket {
  static OPEN = 1;
  static instances: FakeSocket[] = [];
  readyState = 1;
  binaryType = "";
  sent: (string | ArrayBuffer)[] = [];
  listeners = new Map<string, ((event: any) => void)[]>();

  constructor(_url: string) {
    FakeSocket.instances.push(this);
  }

  addEventListener(type: string, fn: (event: any) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }

  send(value: string | ArrayBuffer) {
    this.sent.push(value);
  }

  close() {
    this.fire("close", {});
  }

  fire(type: string, event: any) {
    this.listeners.get(type)?.forEach((fn) => fn(event));
  }
}

describe("BridgeClient", () => {
  beforeEach(() => {
    FakeSocket.instances = [];
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("start omits an undefined fromIndex and sends an explicit index", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.start("sample");
    c.start("sample", 2);
    expect(ws.sent[1]).toBe('{"type":"start","presentation":"sample"}');
    expect(ws.sent[2]).toBe('{"type":"start","presentation":"sample","fromIndex":2}');
  });

  it("take-over auth is sent only once", () => {
    vi.useFakeTimers();
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.takeOver();
    FakeSocket.instances[0].fire("open", {});
    expect(FakeSocket.instances[0].sent[0]).toBe('{"type":"auth","ticket":"test-ticket","takeOver":true}');
    FakeSocket.instances[0].fire("close", { code: 1013 });
    vi.advanceTimersByTime(5000);
    FakeSocket.instances[1].fire("open", {});
    expect(FakeSocket.instances[1].sent[0]).toBe('{"type":"auth","ticket":"test-ticket"}');
    c.disconnect();
  });

  it("sends auth frame first", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    expect(ws.sent[0]).toBe('{"type":"auth","ticket":"test-ticket"}');
  });

  it("4409 emits taken-over and never reconnects", () => {
    vi.useFakeTimers();
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const events: string[] = [];
    c.on("taken-over", () => events.push("taken-over"));
    c.connect();
    FakeSocket.instances[0].fire("open", {});
    FakeSocket.instances[0].fire("close", { code: 4409 });
    vi.advanceTimersByTime(60_000);
    expect(events).toEqual(["taken-over"]);
    expect(FakeSocket.instances).toHaveLength(1);
  });

  it("takeOver cancels a busy backoff and connects immediately", () => {
    vi.useFakeTimers();
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    FakeSocket.instances[0].fire("close", { code: 1013 });
    c.takeOver();
    expect(FakeSocket.instances).toHaveLength(2);
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(60_000);
    expect(FakeSocket.instances).toHaveLength(2);
  });

  it("reconnects with the exponential schedule capped at ten seconds", () => {
    vi.useFakeTimers();
    const setTimeoutSpy = vi.spyOn(globalThis, "setTimeout");
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const delays = [500, 1000, 2000, 4000, 8000, 10000, 10000];
    c.connect();

    for (const [attempt, delay] of delays.entries()) {
      FakeSocket.instances.at(-1)!.fire("close", {});
      vi.advanceTimersByTime(delay - 1);
      expect(FakeSocket.instances).toHaveLength(attempt + 1);
      vi.advanceTimersByTime(1);
    }

    expect(FakeSocket.instances).toHaveLength(delays.length + 1);
    expect(setTimeoutSpy.mock.calls.map(([, delay]) => delay)).toEqual(delays);
    c.disconnect();
  });

  it("backs busy closes off from five seconds without resetting on socket open", () => {
    vi.useFakeTimers();
    const setTimeoutSpy = vi.spyOn(globalThis, "setTimeout");
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const busy: BridgeMessage[] = [];
    c.on("busy", (message) => busy.push(message));
    c.connect();

    for (const delay of [5000, 10000, 20000]) {
      const ws = FakeSocket.instances.at(-1)!;
      ws.fire("open", {});
      ws.fire("message", { data: JSON.stringify({ type: "error", code: "busy" }) });
      ws.fire("close", { code: 1013 });
      vi.advanceTimersByTime(delay);
    }

    expect(busy).toHaveLength(3);
    expect(setTimeoutSpy.mock.calls.map(([, delay]) => delay)).toEqual([5000, 10000, 20000]);
    c.disconnect();
  });

  it("resets the retry counter only after the accepted state frame", () => {
    vi.useFakeTimers();
    const setTimeoutSpy = vi.spyOn(globalThis, "setTimeout");
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    FakeSocket.instances.at(-1)!.fire("close", {});
    vi.advanceTimersByTime(500);

    const unaccepted = FakeSocket.instances.at(-1)!;
    unaccepted.fire("open", {});
    unaccepted.fire("message", { data: JSON.stringify({ type: "error", code: "busy" }) });
    unaccepted.fire("close", { code: 1013 });
    vi.advanceTimersByTime(10000);

    const accepted = FakeSocket.instances.at(-1)!;
    accepted.fire("open", {});
    accepted.fire("message", {
      data: JSON.stringify({ type: "state", state: "idle", slideIndex: 0, slideCount: 0, muted: false }),
    });
    accepted.fire("message", { data: JSON.stringify({ type: "error", code: "busy" }) });
    accepted.fire("close", { code: 1013 });

    expect(setTimeoutSpy.mock.calls.map(([, delay]) => delay)).toEqual([500, 10000, 5000]);
    c.disconnect();
  });

  it("emits accepted for a server state frame but not for the idle state reported on close", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const events: string[] = [];
    c.on("state", (snapshot) => events.push(`state:${snapshot.state}`));
    c.on("accepted", () => events.push("accepted"));
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("message", { data: JSON.stringify({ type: "error", code: "busy" }) });
    ws.fire("close", { code: 1013 });
    expect(events).toEqual(["state:idle"]);

    c.disconnect();
    c.connect();
    FakeSocket.instances.at(-1)!.fire("message", {
      data: JSON.stringify({ type: "state", state: "presenting", slideIndex: 0, slideCount: 1, muted: false }),
    });
    expect(events).toEqual(["state:idle", "state:presenting", "accepted"]);
    c.disconnect();
  });

  it("snapshot → idle after reconnect", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("message", {
      data: JSON.stringify({
        type: "state",
        state: "presenting",
        slideIndex: 1,
        slideCount: 2,
        muted: false,
      }),
    });
    ws.fire("close", {});
    expect(c.snapshot.state).toBe("idle");
  });

  it("sends microphone audio while presenting or paused, but not idle", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const audio = new Uint8Array([1, 2]).buffer;
    const state = (value: string) => ws.fire("message", { data: JSON.stringify({
      type: "state", state: value, slideIndex: 0, slideCount: 1, muted: false,
    }) });
    ws.fire("open", {});
    c.sendAudio(audio);
    state("presenting");
    c.sendAudio(audio);
    state("paused");
    c.sendAudio(audio);
    state("idle");
    c.sendAudio(audio);
    expect(ws.sent).toEqual(['{"type":"auth","ticket":"test-ticket"}', audio, audio]);
  });

  it("emits a flush event for the server flush frame", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const flushed = vi.fn();
    c.on("flush", flushed);
    c.connect();
    FakeSocket.instances.at(-1)!.fire("message", { data: '{"type":"flush"}' });
    expect(flushed).toHaveBeenCalledOnce();
  });

  it("parses every text message and binary audio", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const audio = new Uint8Array([1, 2]).buffer;
    const received: Record<string, BridgeMessage[]> = {};
    const events = ["transcript", "usage", "log", "error", "closed", "busy"] as const;
    for (const event of events) c.on(event, (message) => (received[event] ??= []).push(message));
    const slides: number[] = [];
    const pongs: number[] = [];
    let state: Snapshot | undefined;
    let heard: ArrayBuffer | undefined;
    c.on("slide", (index) => slides.push(index));
    c.on("pong", () => pongs.push(1));
    c.on("state", (snapshot) => (state = snapshot));
    c.on("audio", (buffer) => (heard = buffer));

    const stateMessage = { type: "state", state: "presenting", slideIndex: 2, slideCount: 4, muted: true };
    const messages: BridgeMessage[] = [
      stateMessage,
      { type: "slide", index: 3 },
      { type: "transcript", text: "spoken" },
      { type: "usage", seconds: 7 },
      { type: "log", message: "adapter" },
      { type: "upstream-error", detail: "upstream" },
      { type: "error", detail: "protocol" },
      { type: "closed", reason: "finished" },
      { type: "pong" },
      { type: "busy", reason: "occupied" },
    ];
    ws.fire("message", { data: audio });
    for (const message of messages) ws.fire("message", { data: JSON.stringify(message) });

    expect(heard).toBe(audio);
    expect(state).toEqual(stateMessage);
    expect(c.snapshot).toEqual(stateMessage);
    expect(slides).toEqual([3]);
    expect(received.transcript).toEqual([messages[2]]);
    expect(received.usage).toEqual([messages[3]]);
    expect(received.log).toEqual([messages[4]]);
    expect(received.error).toEqual([messages[5], messages[6]]);
    expect(received.closed).toEqual([messages[7]]);
    expect(pongs).toEqual([1]);
    expect(received.busy).toEqual([messages[9]]);
  });

  it("ignores events from a socket disconnected while connecting", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    const opens: number[] = [];
    const states: Snapshot[] = [];
    const logs: BridgeMessage[] = [];
    c.on("open", () => opens.push(1));
    c.on("state", (snapshot) => states.push(snapshot));
    c.on("log", (message) => logs.push(message));
    c.connect();
    const first = FakeSocket.instances[0];
    c.disconnect();
    c.connect();
    const second = FakeSocket.instances[1];

    first.fire("open", {});
    first.fire("message", {
      data: JSON.stringify({ type: "state", state: "presenting", slideIndex: 1, slideCount: 1, muted: false }),
    });
    first.fire("message", { data: JSON.stringify({ type: "log", message: "stale" }) });
    first.fire("close", {});

    expect(first.sent).toEqual([]);
    expect(opens).toEqual([]);
    expect(states).toEqual([]);
    expect(logs).toEqual([]);
    expect(c.snapshot.state).toBe("idle");

    second.fire("open", {});
    expect(second.sent[0]).toBe('{"type":"auth","ticket":"test-ticket"}');
  });

  it("answers server ping with pong", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    ws.fire("message", { data: JSON.stringify({ type: "ping" }) });
    expect(ws.sent).toContain('{"type":"pong"}');
  });

  it("start sends maxMinutes only when chosen", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.start("sample");
    c.start("sample", 1);
    c.start("sample", undefined, 15);
    c.start("sample", 2, 30);
    expect(ws.sent[1]).toBe('{"type":"start","presentation":"sample"}');
    expect(ws.sent[2]).toBe('{"type":"start","presentation":"sample","fromIndex":1}');
    expect(ws.sent[3]).toBe('{"type":"start","presentation":"sample","maxMinutes":15}');
    expect(ws.sent[4]).toBe('{"type":"start","presentation":"sample","fromIndex":2,"maxMinutes":30}');
  });

  it("sends trainer_mode and train_turn", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.setTrainerMode(true);
    c.setTrainerMode(false);
    c.trainTurn("What about 2025?", "The 2025 figures show growth.", 3);
    expect(ws.sent[1]).toBe('{"type":"trainer_mode","on":true}');
    expect(ws.sent[2]).toBe('{"type":"trainer_mode","on":false}');
    expect(ws.sent[3]).toBe(
      '{"type":"train_turn","question":"What about 2025?","answer":"The 2025 figures show growth.","slideIndex":3}',
    );
  });

  it("sends a worst-case exchange inside every train_turn limit of the bridge", () => {
    // Vietnamese (3 UTF-8 bytes per code unit), JSON-escaped quotes/backslashes and control characters, over
    // several user turns: the frame must satisfy PresenterBridge.TryReadTrainTurn and its 16 KiB text cap.
    const heavy = '\u0001\u0002\u0003\u0004\u0005\u0006Ệ"\\\u0085 '.repeat(700);
    const turns = [
      { role: "user", text: heavy, endMs: null, slide: 1 },
      { role: "user", text: `${heavy} Giá bao nhiêu?`, endMs: null, slide: 1 },
      { role: "assistant", text: heavy, endMs: null, slide: 1 },
      { role: "assistant", text: heavy, endMs: null, slide: 1 },
    ];
    const exchange = groupExchanges(turns)[3]!;
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});

    c.trainTurn(exchange.question, exchange.answer, exchange.slideIndex);

    const frame = ws.sent.at(-1) as string;
    expect(new TextEncoder().encode(frame).length).toBeLessThanOrEqual(16 * 1024);
    const sent = JSON.parse(frame) as { type: string; question: string; answer: string; slideIndex: number };
    expect(sent.type).toBe("train_turn");
    expect(sent.slideIndex).toBe(1);
    for (const text of [sent.question, sent.answer]) {
      expect(text.length).toBeGreaterThan(0);
      expect(text.length).toBeLessThanOrEqual(2000);
      // .NET string.IsNullOrWhiteSpace also counts U+0085 and the C0 separators as whitespace.
      // eslint-disable-next-line no-control-regex -- matching control characters is the point
      expect(text.replace(/[\s\u0085\u001c-\u001f]/g, "")).not.toBe("");
    }
    expect(sent.question.endsWith("Giá bao nhiêu?")).toBe(true);
  });

  it("emits script_edit and script_version", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const edits: BridgeMessage[] = [];
    const versions: BridgeMessage[] = [];
    c.on("script_edit", (message) => edits.push(message));
    c.on("script_version", (message) => versions.push(message));
    const editMessage = {
      type: "script_edit",
      id: "edit_4",
      status: "applied",
      slideIndexes: [3],
      version: 7,
      summary: "Added the 2025 figures",
      error: null,
    };
    const versionMessage = {
      type: "script_version",
      presentationId: "prs_1",
      version: 7,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: true,
    };
    ws.fire("message", { data: JSON.stringify(editMessage) });
    ws.fire("message", { data: JSON.stringify(versionMessage) });
    // Frames this client does not yet know stay ignored (no throw, no emit).
    ws.fire("message", { data: JSON.stringify({ type: "future_frame", value: 1 }) });

    expect(edits).toEqual([editMessage]);
    expect(versions).toEqual([versionMessage]);
  });

  it("emits trainer_state", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const states: BridgeMessage[] = [];
    c.on("trainer_state", (message) => states.push(message));
    const message = { type: "trainer_state", trainerMode: true, trainerAvailable: true, voiceTraining: true };
    ws.fire("message", { data: JSON.stringify(message) });
    expect(states).toEqual([message]);
  });

  it("emits limit_warning and upstream", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const warnings: BridgeMessage[] = [];
    const upstreams: BridgeMessage[] = [];
    c.on("limit_warning", (msg) => warnings.push(msg));
    c.on("upstream", (msg) => upstreams.push(msg));
    const warningMsg = { type: "limit_warning", kind: "max_length", secondsLeft: 60 };
    const upstreamMsg = { type: "upstream", status: "suspended" };
    ws.fire("message", { data: JSON.stringify(warningMsg) });
    ws.fire("message", { data: JSON.stringify(upstreamMsg) });
    expect(warnings).toEqual([warningMsg]);
    expect(upstreams).toEqual([upstreamMsg]);
  });

  it("sends the four ask commands", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.askStart();
    c.askDone();
    c.askExtend();
    c.askCancel();
    expect(ws.sent[1]).toBe('{"type":"ask_start"}');
    expect(ws.sent[2]).toBe('{"type":"ask_done"}');
    expect(ws.sent[3]).toBe('{"type":"ask_extend"}');
    expect(ws.sent[4]).toBe('{"type":"ask_cancel"}');
  });

  it("emits ask_state", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const states: BridgeMessage[] = [];
    c.on("ask_state", (message) => states.push(message));
    const message = {
      type: "ask_state",
      state: "listening",
      elapsedMs: 23000,
      quietRemainingMs: 67000,
      speechRemainingMs: 13400,
      heard: true,
      transcribing: false,
      reason: null,
    };
    ws.fire("message", { data: JSON.stringify(message) });
    expect(states).toEqual([message]);
  });
});

