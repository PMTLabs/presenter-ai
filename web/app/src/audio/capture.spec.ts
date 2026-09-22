/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, it, vi } from "vitest";
import { AudioCapture } from "./capture";

describe("AudioCapture", () => {
  it("stops a stream that resolves after its owner aborts", async () => {
    let resolveStream!: (stream: MediaStream) => void;
    const pendingStream = new Promise<MediaStream>((resolve) => (resolveStream = resolve));
    const stop = vi.fn();
    const track = { stop };
    Object.defineProperty(navigator, "mediaDevices", {
      value: { getUserMedia: vi.fn().mockReturnValue(pendingStream) },
      configurable: true,
    });
    const context = {
      audioWorklet: { addModule: vi.fn() },
      createMediaStreamSource: vi.fn(),
    };
    const capture = new AudioCapture({ context: context as any });
    const controller = new AbortController();
    const starting = capture.start(controller.signal);

    controller.abort();
    resolveStream({ getTracks: () => [track] } as any);
    await starting;

    expect(stop).toHaveBeenCalledOnce();
    expect(context.audioWorklet.addModule).not.toHaveBeenCalled();
  });
});
