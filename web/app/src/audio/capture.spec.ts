/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, it, vi } from "vitest";
import { AudioCapture } from "./capture";

describe("AudioCapture", () => {
  it("transfers direct playback-level wiring and surfaces worklet barge-in", async () => {
    const track = { stop: vi.fn() };
    const source = { connect: vi.fn(), disconnect: vi.fn() };
    const postMessage = vi.fn();
    class WorkletNode {
      static instance: WorkletNode;
      port = { postMessage, onmessage: null as ((event: MessageEvent) => void) | null };
      disconnect = vi.fn();
      constructor(..._args: unknown[]) { WorkletNode.instance = this; }
    }
    (globalThis as any).AudioWorkletNode = WorkletNode;
    Object.defineProperty(navigator, "mediaDevices", {
      value: { getUserMedia: vi.fn().mockResolvedValue({ getTracks: () => [track] }) },
      configurable: true,
    });
    const connectFarLevels = vi.fn();
    const onBargeIn = vi.fn();
    const capture = new AudioCapture({
      context: {
        audioWorklet: { addModule: vi.fn() },
        createMediaStreamSource: vi.fn().mockReturnValue(source),
      } as any,
      connectFarLevels,
      onBargeIn,
      echoGate: false,
    });

    await capture.start();
    expect(connectFarLevels).toHaveBeenCalledOnce();
    expect(postMessage).toHaveBeenCalledWith({ type: "echo-gate", enabled: false });
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ type: "far-level-port" }),
      expect.any(Array),
    );
    WorkletNode.instance.port.onmessage?.({ data: { type: "barge-in" } } as MessageEvent);
    expect(onBargeIn).toHaveBeenCalledOnce();
  });

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
