const SOURCE_RATE = 24000;
const RING_SECONDS = 8;
const FAR_LEVEL_SAMPLES = sampleRate / 50;
class PlaybackProcessor extends AudioWorkletProcessor {
  ring = new Float32Array(SOURCE_RATE * RING_SECONDS);
  readPos = 0;
  writePos = 0;
  available = 0;
  step = SOURCE_RATE / sampleRate;
  reportCounter = 0;
  farPort: MessagePort | null = null;
  farSum = 0;
  farSamples = 0;
  constructor() {
    super();
    this.port.onmessage = (e) => {
      if (e.data?.type === "pcm") this.enqueue(new Int16Array(e.data.buffer));
      else if (e.data?.type === "flush") {
        this.readPos = this.writePos = this.available = 0;
      } else if (e.data?.type === "far-level-port") this.farPort = e.data.port as MessagePort;
    };
  }
  enqueue(samples: Int16Array) {
    if (samples.length > this.ring.length) return;
    if (this.available + samples.length > this.ring.length) {
      const drop = this.available + samples.length - this.ring.length;
      this.readPos = (this.readPos + drop) % this.ring.length;
      this.available -= drop;
    }
    for (const value of samples) {
      this.ring[this.writePos] = value / 0x8000;
      this.writePos = (this.writePos + 1) % this.ring.length;
    }
    this.available += samples.length;
  }
  process(_inputs: Float32Array[][], outputs: Float32Array[][]) {
    const ch0 = outputs[0]?.[0];
    if (!ch0) return true;
    for (let i = 0; i < ch0.length; i++) {
      if (this.available < 2) {
        ch0[i] = 0;
      } else {
        const index = Math.floor(this.readPos);
        const frac = this.readPos - index;
        ch0[i] =
          this.ring[index % this.ring.length] * (1 - frac) +
          this.ring[(index + 1) % this.ring.length] * frac;
        const before = Math.floor(this.readPos);
        this.readPos += this.step;
        this.available -= Math.floor(this.readPos) - before;
        if (this.readPos >= this.ring.length) this.readPos -= this.ring.length;
      }
      this.farSum += ch0[i] * ch0[i];
      this.farSamples++;
    }
    for (let i = 1; i < outputs[0].length; i++) outputs[0][i].set(ch0);
    if (this.farSamples >= FAR_LEVEL_SAMPLES) {
      this.farPort?.postMessage({
        type: "far-level",
        value: Math.sqrt(this.farSum / this.farSamples),
      });
      this.farSum = 0;
      this.farSamples = 0;
    }
    this.reportCounter += ch0.length;
    if (this.reportCounter >= sampleRate / 4) {
      this.port.postMessage({
        type: "buffered",
        ms: Math.round((this.available / SOURCE_RATE) * 1000),
      });
      this.reportCounter = 0;
    }
    return true;
  }
}
registerProcessor("playback-processor", PlaybackProcessor);
