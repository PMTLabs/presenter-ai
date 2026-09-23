/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BridgeClient, type BridgeMessage, type Snapshot } from "./bridgeClient";

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
});
