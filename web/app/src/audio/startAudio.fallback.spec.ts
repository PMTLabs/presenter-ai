/* eslint-disable @typescript-eslint/no-explicit-any */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { startAudio } from "./capture";
import { createEchoReference } from "./echoReference";

vi.mock("./echoReference", () => ({ createEchoReference: vi.fn() }));

let resolveResume: () => void = () => undefined;

class Context {
  sampleRate = 48000;
  state = "running";
  destination = { name: "speakers" };
  audioWorklet = { addModule: vi.fn().mockResolvedValue(undefined) };
  resume = vi.fn(() => new Promise<void>((resolve) => (resolveResume = resolve)));
  close = vi.fn().mockResolvedValue(undefined);
  createMediaStreamSource = vi.fn();
}

class WorkletNode {
  static instances: WorkletNode[] = [];
  port = { postMessage: vi.fn(), onmessage: null };
  connect = vi.fn();
  disconnect = vi.fn();

  constructor(..._args: unknown[]) {
    WorkletNode.instances.push(this);
  }
}

const loopback = { name: "loopback destination" };
let fallBack: () => void = () => undefined;

describe("startAudio with an echo reference", () => {
  beforeEach(() => {
    (window as any).AudioContext = Context;
    (globalThis as any).AudioWorkletNode = WorkletNode;
    Object.defineProperty(navigator, "mediaDevices", {
      value: { getUserMedia: vi.fn().mockRejectedValue(new DOMException("denied", "NotAllowedError")) },
      configurable: true,
    });
    vi.mocked(createEchoReference).mockImplementation((options) => {
      fallBack = options.onFallback;
      return { destination: loopback as any, element: {} as any, close: vi.fn() };
    });
  });

  it("plays through the loopback when it comes up", async () => {
    const started = startAudio();
    resolveResume();
    await started;

    expect(WorkletNode.instances.at(-1)!.connect).toHaveBeenLastCalledWith(loopback);
  });

  it("plays to the speakers when the loopback falls back while the context is still resuming", async () => {
    const started = startAudio();
    fallBack();
    resolveResume();
    const audio = await started;

    const node = WorkletNode.instances.at(-1)!;
    expect(node.connect).toHaveBeenLastCalledWith(audio.context.destination);
    expect(node.connect).not.toHaveBeenCalledWith(loopback);
  });
});
