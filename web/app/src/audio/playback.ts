// `?worker&url` makes Vite compile the worklet to its own JS chunk and hand back its URL. A bare
// `new URL("./x.ts", import.meta.url)` is only compiled inside `new Worker(...)`; for audioWorklet.addModule
// the production build inlined the raw .ts as `data:video/mp2t`, which Chrome refuses to load as a module.
import playbackProcessorUrl from "./worklets/playback-processor.ts?worker&url";

export class AudioPlayback {
  private node: AudioWorkletNode | null = null;
  private output: AudioNode | null = null;
  private reference: { close: () => void } | null = null;
  bufferedMs = 0;
  constructor(
    private options: {
      context: AudioContext;
      onBuffered?: (ms: number) => void;
    },
  ) {}
  async start(output: AudioNode = this.options.context.destination) {
    this.output = output;
    await this.options.context.audioWorklet.addModule(playbackProcessorUrl);
    this.node = new AudioWorkletNode(
      this.options.context,
      "playback-processor",
      { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [1] },
    );
    this.node.port.onmessage = (event) => {
      if (event.data?.type === "buffered") {
        this.bufferedMs = event.data.ms;
        this.options.onBuffered?.(this.bufferedMs);
      }
    };
    this.connectOutput();
  }
  setReference(reference: { close: () => void } | null) {
    this.reference = reference;
  }
  useDestination() {
    this.output = this.options.context.destination;
    this.connectOutput();
  }
  connectFarLevels(port: MessagePort) {
    if (!this.node) {
      port.close();
      return;
    }
    this.node.port.postMessage({ type: "far-level-port", port }, [port]);
  }
  private connectOutput() {
    if (!this.node || !this.output) return;
    try {
      this.node.disconnect();
    } catch {
      /* not connected yet */
    }
    this.node.connect(this.output);
  }
  enqueue(buffer: ArrayBuffer) {
    if (!this.node) return;
    const even = buffer.byteLength % 2 ? buffer.slice(0, -1) : buffer;
    this.node.port.postMessage({ type: "pcm", buffer: even }, [even]);
  }
  flush() {
    this.node?.port.postMessage({ type: "flush" });
  }
  stop() {
    this.reference?.close();
    this.reference = null;
    try {
      this.node?.disconnect();
    } catch {
      /* noop */
    }
    this.node = null;
    this.output = null;
  }
}
export function createAudioContext() {
  const Ctx =
    window.AudioContext ||
    (window as Window & { webkitAudioContext?: typeof AudioContext })
      .webkitAudioContext;
  if (!Ctx) throw new Error("Web Audio is unavailable");
  return new Ctx({ latencyHint: "interactive" });
}
export async function resumeWithTimeout(context: AudioContext, ms = 4000) {
  await Promise.race([
    context.resume(),
    new Promise<never>((_, reject) =>
      setTimeout(
        () =>
          reject(
            new Error(
              `audio output did not start within ${ms} ms (context ${context.state} @ ${context.sampleRate} Hz)`,
            ),
          ),
        ms,
      ),
    ),
  ]);
}
