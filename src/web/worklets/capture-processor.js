// AudioWorklet: mono input → 20 ms frames of PCM16 at 24 kHz (960 bytes), posted as ArrayBuffer.
// If the context does not run at 24 kHz, samples are linearly resampled here.

const TARGET_RATE = 24000;
const FRAME_SAMPLES = 480; // 20 ms at 24 kHz

class CaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / TARGET_RATE; // input samples per output sample
    this.pos = 0; // fractional read position into `pending`
    this.pending = new Float32Array(0);
    this.frame = new Int16Array(FRAME_SAMPLES);
    this.frameFill = 0;
    this.muted = false;
    this.peak = 0;
    this.peakCounter = 0;
    this.port.onmessage = (e) => {
      if (e.data?.type === 'mute') this.muted = Boolean(e.data.value);
    };
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const ch = input[0];
    if (!ch || ch.length === 0) return true;

    // level meter (posted ~10x per second)
    for (let i = 0; i < ch.length; i++) {
      const a = Math.abs(ch[i]);
      if (a > this.peak) this.peak = a;
    }
    this.peakCounter += ch.length;
    if (this.peakCounter >= sampleRate / 10) {
      this.port.postMessage({ type: 'level', value: this.peak });
      this.peak = 0;
      this.peakCounter = 0;
    }

    if (this.muted) return true;

    // append to pending buffer
    const merged = new Float32Array(this.pending.length + ch.length);
    merged.set(this.pending, 0);
    merged.set(ch, this.pending.length);
    this.pending = merged;

    // resample (or copy) into PCM16 frames
    if (this.ratio === 1) {
      let i = 0;
      while (i < this.pending.length) {
        this.frame[this.frameFill++] = toInt16(this.pending[i++]);
        if (this.frameFill === FRAME_SAMPLES) this.flush();
      }
      this.pending = new Float32Array(0);
    } else {
      while (this.pos + this.ratio < this.pending.length) {
        const idx = Math.floor(this.pos);
        const frac = this.pos - idx;
        const s = this.pending[idx] * (1 - frac) + this.pending[idx + 1] * frac;
        this.frame[this.frameFill++] = toInt16(s);
        if (this.frameFill === FRAME_SAMPLES) this.flush();
        this.pos += this.ratio;
      }
      const consumed = Math.floor(this.pos);
      this.pending = this.pending.slice(consumed);
      this.pos -= consumed;
    }
    return true;
  }

  flush() {
    const buf = this.frame.buffer.slice(0);
    this.port.postMessage({ type: 'frame', buffer: buf }, [buf]);
    this.frameFill = 0;
  }
}

function toInt16(x) {
  const c = x < -1 ? -1 : x > 1 ? 1 : x;
  return c < 0 ? c * 0x8000 : c * 0x7fff;
}

registerProcessor('capture-processor', CaptureProcessor);
