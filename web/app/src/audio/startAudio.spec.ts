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
describe("startAudio", () => {
  it("continues listen-only when mic denied", async () => {
    (window as any).AudioContext = Context;
    (globalThis as any).AudioWorkletNode = class {
      port = { postMessage() {}, onmessage: null };
      connect() {}
      disconnect() {}
    };
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
    expect(audio.playback).toBeTruthy();
    expect(audio.micReady).toBe(false);
  });
});
