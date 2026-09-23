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

  it("start sends presentation id", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.start("sample");
    expect(ws.sent[1]).toBe(
      '{"type":"start","presentation":"sample","fromIndex":0}',
    );
  });

  it("sends auth frame first", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any, () => "test-ticket");
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    expect(ws.sent[0]).toBe('{"type":"auth","ticket":"test-ticket"}');
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
