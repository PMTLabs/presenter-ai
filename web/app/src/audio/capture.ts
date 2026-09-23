import {
  AudioPlayback,
  createAudioContext,
  resumeWithTimeout,
} from "./playback";
import { createEchoReference } from "./echoReference";
// See playback.ts: `?worker&url` is the form Vite compiles for audioWorklet.addModule.
import captureProcessorUrl from "./worklets/capture-processor.ts?worker&url";

export type StartedAudio = {
  context: AudioContext;
  capture: AudioCapture;
  playback: AudioPlayback;
  micReady: boolean;
};
/** Starts output first; a denied microphone leaves a usable listen-only session. */
export async function startAudio(
  options: {
    onFrame?: (buffer: ArrayBuffer) => void;
    onLevel?: (level: number) => void;
    onBuffered?: (ms: number) => void;
    onMicReady?: (ready: boolean) => void;
    onBargeIn?: () => void;
    onEchoStats?: (stats: { gateOpenRatio: number; coupling: number }) => void;
    onReference?: (reference: "loopback" | "fallback", reason?: string) => void;
    echoGate?: boolean;
    signal?: AbortSignal;
  } = {},
): Promise<StartedAudio> {
  const context = createAudioContext();
  const playback = new AudioPlayback({
    context,
    onBuffered: options.onBuffered,
  });
  // This runs synchronously from the Start click, before audioWorklet loading awaits.
  const reference = createEchoReference({
    context,
    onReference: options.onReference ?? (() => undefined),
    onFallback: () => playback.useDestination(),
  });
  playback.setReference(reference);
  try {
    await resumeWithTimeout(context);
    await playback.start(reference?.destination);
  } catch (error) {
    playback.stop();
    void context.close();
    throw error;
  }
  const capture = new AudioCapture({
    context,
    onFrame: options.onFrame,
    onLevel: options.onLevel,
    onBargeIn: options.onBargeIn,
    onEchoStats: options.onEchoStats,
    echoGate: options.echoGate,
    connectFarLevels: (port) => playback.connectFarLevels(port),
  });
  const audio: StartedAudio = { context, playback, capture, micReady: false };
  void capture
    .start(options.signal)
    .then(() => {
      if (options.signal?.aborted) return;
      audio.micReady = true;
      options.onMicReady?.(true);
    })
    .catch(() => {
      if (!options.signal?.aborted) options.onMicReady?.(false);
    });
  return audio;
}

export class AudioCapture {
  private stream: MediaStream | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private node: AudioWorkletNode | null = null;
  private muted = false;
  private stopped = false;
  constructor(
    private options: {
      context: AudioContext;
      onFrame?: (buffer: ArrayBuffer) => void;
      onLevel?: (value: number) => void;
      onBargeIn?: () => void;
      onEchoStats?: (stats: { gateOpenRatio: number; coupling: number }) => void;
      echoGate?: boolean;
      connectFarLevels?: (port: MessagePort) => void;
    },
  ) {}
  async start(signal?: AbortSignal) {
    const { context } = this.options;
    const abort = () => this.stop();
    signal?.addEventListener("abort", abort, { once: true });
    if (signal?.aborted) {
      this.stop();
      return;
    }
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        channelCount: 1,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
      },
      video: false,
    });
    if (this.stopped || signal?.aborted) {
      stream.getTracks().forEach((track) => track.stop());
      return;
    }
    this.stream = stream;
    await context.audioWorklet.addModule(captureProcessorUrl);
    if (this.stopped || signal?.aborted) {
      this.stop();
      return;
    }
    this.source = context.createMediaStreamSource(this.stream);
    this.node = new AudioWorkletNode(context, "capture-processor", {
      numberOfInputs: 1,
      numberOfOutputs: 0,
      channelCount: 1,
    });
    this.node.port.onmessage = (event) => {
      const msg = event.data;
      if (msg.type === "frame") this.options.onFrame?.(msg.buffer);
      else if (msg.type === "level") this.options.onLevel?.(msg.value);
      else if (msg.type === "barge-in") this.options.onBargeIn?.();
      else if (msg.type === "echo-stats")
        this.options.onEchoStats?.({
          gateOpenRatio: msg.gateOpenRatio,
          coupling: msg.coupling,
        });
    };
    this.node.port.postMessage({ type: "echo-gate", enabled: this.options.echoGate !== false });
    if (typeof MessageChannel !== "undefined") {
      const channel = new MessageChannel();
      this.options.connectFarLevels?.(channel.port1);
      this.node.port.postMessage({ type: "far-level-port", port: channel.port2 }, [channel.port2]);
    }
    this.source.connect(this.node);
    this.setMuted(this.muted);
  }
  setMuted(value: boolean) {
    this.muted = value;
    this.node?.port.postMessage({ type: "mute", value });
  }
  stop() {
    this.stopped = true;
    try {
      this.source?.disconnect();
      this.node?.disconnect();
    } catch {
      /* already disconnected */
    }
    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = null;
    this.source = null;
    this.node = null;
  }
}
