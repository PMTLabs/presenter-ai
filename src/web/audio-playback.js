// Streams PCM16 (24 kHz) frames to the speakers through an AudioWorklet ring buffer.

export class AudioPlayback {
  constructor({ context, onBuffered }) {
    this.context = context;
    this.onBuffered = onBuffered;
    this.node = null;
    this.bufferedMs = 0;
  }

  async start() {
    await this.context.audioWorklet.addModule('./worklets/playback-processor.js');
    this.node = new AudioWorkletNode(this.context, 'playback-processor', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [1] });
    this.node.port.onmessage = (e) => {
      if (e.data?.type === 'buffered') {
        this.bufferedMs = e.data.ms;
        this.onBuffered?.(this.bufferedMs);
      }
    };
    this.node.connect(this.context.destination);
  }

  /** @param {ArrayBuffer} buffer raw PCM16 LE mono 24 kHz */
  enqueue(buffer) {
    if (!this.node) return;
    const even = buffer.byteLength % 2 === 0 ? buffer : buffer.slice(0, buffer.byteLength - 1);
    this.node.port.postMessage({ type: 'pcm', buffer: even }, [even]);
  }

  flush() {
    this.node?.port.postMessage({ type: 'flush' });
  }

  stop() {
    try {
      this.node?.disconnect();
    } catch {}
    this.node = null;
  }
}

/**
 * Creates the shared audio context at the device's native rate. Forcing 24 kHz makes some
 * Windows output devices never leave the "suspended" state, so both worklets resample instead.
 */
export function createAudioContext() {
  const Ctx = window.AudioContext || window.webkitAudioContext;
  return new Ctx({ latencyHint: 'interactive' });
}

/** context.resume() can hang forever on an unusable device: fail loudly instead. */
export async function resumeWithTimeout(context, ms = 4000) {
  await Promise.race([
    context.resume(),
    new Promise((_, reject) => setTimeout(() => reject(new Error(`audio output did not start within ${ms} ms (context ${context.state} @ ${context.sampleRate} Hz)`)), ms)),
  ]);
}
