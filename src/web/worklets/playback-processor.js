// AudioWorklet: plays PCM16 24 kHz frames from a ring buffer; silence when empty.
// If the context does not run at 24 kHz, samples are linearly resampled on the way out.

const SOURCE_RATE = 24000;
const RING_SECONDS = 8;

class PlaybackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ring = new Float32Array(SOURCE_RATE * RING_SECONDS);
    this.readPos = 0; // fractional, in source samples
    this.writePos = 0;
    this.available = 0; // whole source samples buffered
    this.step = SOURCE_RATE / sampleRate; // source samples per output sample
    this.reportCounter = 0;
    this.port.onmessage = (e) => {
      const msg = e.data;
      if (msg?.type === 'pcm') this.enqueue(new Int16Array(msg.buffer));
      else if (msg?.type === 'flush') {
        this.readPos = 0;
        this.writePos = 0;
        this.available = 0;
      }
    };
  }

  enqueue(int16) {
    const n = int16.length;
    if (n > this.ring.length) return; // absurdly large frame: drop
    // overflow: drop the oldest audio
    if (this.available + n > this.ring.length) {
      const drop = this.available + n - this.ring.length;
      this.readPos = (this.readPos + drop) % this.ring.length;
      this.available -= drop;
    }
    for (let i = 0; i < n; i++) {
      this.ring[this.writePos] = int16[i] / 0x8000;
      this.writePos = (this.writePos + 1) % this.ring.length;
    }
    this.available += n;
  }

  process(inputs, outputs) {
    const out = outputs[0];
    if (!out || out.length === 0) return true;
    const ch0 = out[0];
    const len = this.ring.length;
    for (let i = 0; i < ch0.length; i++) {
      if (this.available < 2) {
        ch0[i] = 0;
        continue;
      }
      const idx = Math.floor(this.readPos);
      const frac = this.readPos - idx;
      const a = this.ring[idx % len];
      const b = this.ring[(idx + 1) % len];
      ch0[i] = a * (1 - frac) + b * frac;
      const before = Math.floor(this.readPos);
      this.readPos += this.step;
      const consumed = Math.floor(this.readPos) - before;
      this.available -= consumed;
      if (this.readPos >= len) this.readPos -= len;
    }
    for (let c = 1; c < out.length; c++) out[c].set(ch0);

    this.reportCounter += ch0.length;
    if (this.reportCounter >= sampleRate / 4) {
      this.port.postMessage({ type: 'buffered', ms: Math.round((this.available / SOURCE_RATE) * 1000) });
      this.reportCounter = 0;
    }
    return true;
  }
}

registerProcessor('playback-processor', PlaybackProcessor);
