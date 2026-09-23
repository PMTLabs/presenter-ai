/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterEach, describe, expect, it, vi } from "vitest";
import { createEchoReference, mungeOpus } from "./echoReference";
import { AudioPlayback } from "./playback";

class FakePeerConnection {
  static instances: FakePeerConnection[] = [];
  onicecandidate: ((event: { candidate: unknown }) => void) | null = null;
  ontrack: ((event: { streams: MediaStream[]; track: MediaStreamTrack }) => void) | null = null;
  localDescription: RTCSessionDescriptionInit | null = null;
  close = vi.fn();
  addTrack = vi.fn();
  addIceCandidate = vi.fn().mockResolvedValue(undefined);
  createOffer = vi.fn().mockResolvedValue({ type: "offer", sdp: "v=0\r\na=rtpmap:111 opus/48000/2\r\n" });
  createAnswer = vi.fn().mockResolvedValue({ type: "answer", sdp: "v=0\r\na=rtpmap:111 opus/48000/2\r\n" });
  setLocalDescription = vi.fn().mockImplementation(async (description) => {
    this.localDescription = description;
  });
  setRemoteDescription = vi.fn().mockResolvedValue(undefined);
  constructor(..._args: unknown[]) {
    FakePeerConnection.instances.push(this);
  }
}

function context(track = { stop: vi.fn() }) {
  return {
    destination: { name: "speakers" },
    createMediaStreamDestination: vi.fn().mockReturnValue({
      stream: {
        getAudioTracks: () => [track],
        getTracks: () => [track],
      },
    }),
  } as any;
}

describe("createEchoReference", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
    FakePeerConnection.instances = [];
    (window as any).RTCPeerConnection = undefined;
    vi.unstubAllGlobals();
  });

  it("keeps loopback Opus mono, DTX-off, and near 96 kbit/s", () => {
    const description = mungeOpus({
      type: "offer",
      sdp: "v=0\r\na=rtpmap:111 opus/48000/2\r\na=fmtp:111 minptime=10\r\n",
    });
    expect(description.sdp).toContain(
      "stereo=0;sprop-stereo=0;usedtx=0;maxaveragebitrate=96000;cbr=1",
    );
  });

  it("adds a missing Opus fmtp line inside the media section, not after the trailing empty line", () => {
    const description = mungeOpus({
      type: "offer",
      sdp: "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\na=sendrecv\r\n",
    });
    expect(description.sdp).toBe(
      "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\n" +
        "a=fmtp:111 stereo=0;sprop-stereo=0;usedtx=0;maxaveragebitrate=96000;cbr=1\r\na=sendrecv\r\n",
    );
  });

  it("puts the received loopback track on the detached audio element and closes both peers", async () => {
    (window as any).RTCPeerConnection = FakePeerConnection;
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    const received = { getTracks: () => [{ stop: vi.fn() }] } as any;
    const reference = createEchoReference({
      context: context(),
      onFallback: vi.fn(),
      onReference: vi.fn(),
    });

    expect(reference).not.toBeNull();
    FakePeerConnection.instances[1].ontrack?.({ streams: [received], track: {} as MediaStreamTrack });
    expect(reference!.element.srcObject).toBe(received);
    reference!.element.dispatchEvent(new Event("playing"));
    reference!.close();

    expect(FakePeerConnection.instances[0].close).toHaveBeenCalledOnce();
    expect(FakePeerConnection.instances[1].close).toHaveBeenCalledOnce();
    expect(reference!.element.srcObject).toBeNull();
  });

  it("uses only the loopback path until fallback disconnects it", async () => {
    vi.useFakeTimers();
    (window as any).RTCPeerConnection = FakePeerConnection;
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    const audioContext = {
      ...context(),
      audioWorklet: { addModule: vi.fn().mockResolvedValue(undefined) },
    };
    class WorkletNode {
      static instance: WorkletNode;
      port = { postMessage: vi.fn(), onmessage: null };
      connect = vi.fn();
      disconnect = vi.fn();
      constructor(..._args: unknown[]) { WorkletNode.instance = this; }
    }
    vi.stubGlobal("AudioWorkletNode", WorkletNode);
    const playback = new AudioPlayback({ context: audioContext as any });
    const reference = createEchoReference({
      context: audioContext as any,
      onFallback: () => playback.useDestination(),
      onReference: vi.fn(),
    });
    playback.setReference(reference);
    await playback.start(reference!.destination);
    expect(WorkletNode.instance.connect).toHaveBeenCalledWith(reference!.destination);
    expect(WorkletNode.instance.connect).not.toHaveBeenCalledWith(audioContext.destination);

    vi.advanceTimersByTime(2000);
    expect(WorkletNode.instance.disconnect).toHaveBeenCalledTimes(2);
    expect(WorkletNode.instance.connect).toHaveBeenLastCalledWith(audioContext.destination);
    expect(WorkletNode.instance.disconnect.mock.invocationCallOrder[1]).toBeLessThan(
      WorkletNode.instance.connect.mock.invocationCallOrder[1],
    );
  });

  it("falls back to the direct context destination when WebRTC is unavailable", () => {
    const directConnect = vi.fn();
    const onFallback = () => directConnect(context().destination);
    const reference = createEchoReference({ context: context(), onFallback, onReference: vi.fn() });

    expect(reference).toBeNull();
    expect(directConnect).toHaveBeenCalledWith(expect.objectContaining({ name: "speakers" }));
    expect(directConnect).toHaveBeenCalledOnce();
  });

  it("falls back once after two seconds without playback", () => {
    vi.useFakeTimers();
    (window as any).RTCPeerConnection = FakePeerConnection;
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    const onFallback = vi.fn();
    const reference = createEchoReference({ context: context(), onFallback, onReference: vi.fn() });

    vi.advanceTimersByTime(2000);
    expect(onFallback).toHaveBeenCalledOnce();
    expect(FakePeerConnection.instances[0].close).toHaveBeenCalledOnce();
    expect(FakePeerConnection.instances[1].close).toHaveBeenCalledOnce();
    reference!.close();
    expect(onFallback).toHaveBeenCalledOnce();
  });
});
