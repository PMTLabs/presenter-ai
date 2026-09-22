/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, it, vi } from "vitest";
import { startAudio } from "./capture";

class Context {
  sampleRate = 48000;
  state = "running";
  destination = {};
  audioWorklet = { addModule: vi.fn().mockResolvedValue(undefined) };
  resume = vi.fn().mockResolvedValue(undefined);
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

describe("startAudio", () => {
  it("starts playback when microphone capture is denied", async () => {
    (window as any).AudioContext = Context;
    (globalThis as any).AudioWorkletNode = WorkletNode;
    Object.defineProperty(navigator, "mediaDevices", {
      value: {
        getUserMedia: vi
          .fn()
          .mockRejectedValue(new DOMException("denied", "NotAllowedError")),
      },
      configurable: true,
    });

    const audio = await startAudio();
    await Promise.resolve();
    expect((audio.context as unknown as Context).resume).toHaveBeenCalledOnce();
    expect((audio.context as unknown as Context).audioWorklet.addModule).toHaveBeenCalledOnce();
    expect(WorkletNode.instances.at(-1)!.connect).toHaveBeenCalledWith(audio.context.destination);
    expect(audio.micReady).toBe(false);
  });
});
