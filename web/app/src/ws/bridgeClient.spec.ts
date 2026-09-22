/* eslint-disable @typescript-eslint/no-explicit-any */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BridgeClient } from "./bridgeClient";
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
  it("start sends presentation id", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any);
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    c.start("sample");
    expect(ws.sent[1]).toBe(
      '{"type":"start","presentation":"sample","fromIndex":0}',
    );
  });
  it("sends auth frame first", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any);
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    ws.fire("open", {});
    expect(ws.sent[0]).toBe('{"type":"auth","ticket":"dev"}');
  });
  it("reconnects with backoff", () => {
    vi.useFakeTimers();
    const c = new BridgeClient("ws://test", FakeSocket as any);
    c.connect();
    FakeSocket.instances.at(-1)!.fire("close", {});
    vi.advanceTimersByTime(499);
    expect(FakeSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeSocket.instances).toHaveLength(2);
    FakeSocket.instances.at(-1)!.fire("close", {});
    vi.advanceTimersByTime(1000);
    expect(FakeSocket.instances).toHaveLength(3);
    c.disconnect();
    vi.useRealTimers();
  });
  it("snapshot → idle after reconnect", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any);
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
  it("parses every message and binary audio", () => {
    const c = new BridgeClient("ws://test", FakeSocket as any);
    c.connect();
    const ws = FakeSocket.instances.at(-1)!;
    const audio = new Uint8Array([1, 2]).buffer;
    let heard: ArrayBuffer | undefined;
    c.on("audio", (x) => {
      heard = x;
    });
    ws.fire("message", { data: audio });
    for (const type of [
      "state",
      "slide",
      "transcript",
      "usage",
      "log",
      "upstream-error",
      "closed",
      "pong",
      "busy",
    ])
      ws.fire("message", {
        data: JSON.stringify({
          type,
          index: 0,
          state: "idle",
          slideIndex: 0,
          slideCount: 0,
          muted: false,
        }),
      });
    expect(heard).toBe(audio);
  });
});
