export class AudioPlayback {
  private node: AudioWorkletNode | null = null;
  bufferedMs = 0;
  constructor(
    private options: {
      context: AudioContext;
      onBuffered?: (ms: number) => void;
    },
  ) {}
  async start() {
    await this.options.context.audioWorklet.addModule(
      new URL("./worklets/playback-processor.ts", import.meta.url),
    );
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
    this.node.connect(this.options.context.destination);
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
    try {
      this.node?.disconnect();
    } catch {
      /* noop */
    }
    this.node = null;
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
