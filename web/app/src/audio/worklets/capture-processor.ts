import { ECHO_GATE_WORKLET_SENTINEL, EchoGate, FRAME_MS } from "../echoGate";

const TARGET_RATE = 24000;
const FRAME_SAMPLES = 480;
const STATS_FRAMES = 100 / FRAME_MS;
class CaptureProcessor extends AudioWorkletProcessor {
  ratio = sampleRate / TARGET_RATE;
  pos = 0;
  pending = new Float32Array(0);
  frame = new Int16Array(FRAME_SAMPLES);
  frameFill = 0;
  muted = false;
  gateEnabled = true;
  gate = new EchoGate();
  farLevel = 0;
  peak = 0;
  peakCounter = 0;
  statsFrames = 0;
  constructor() {
    super();
    this.port.onmessage = (e) => {
      if (e.data?.type === "mute") this.muted = Boolean(e.data.value);
      else if (e.data?.type === "echo-gate") this.gateEnabled = Boolean(e.data.enabled);
      else if (e.data?.type === "far-level-port") {
        const farPort = e.data.port as MessagePort;
        farPort.onmessage = (event) => {
          if (event.data?.type === "far-level") this.farLevel = event.data.value;
        };
      }
    };
  }
  process(inputs: Float32Array[][]) {
    const ch = inputs[0]?.[0];
    if (!ch?.length) return true;
    for (const x of ch) this.peak = Math.max(this.peak, Math.abs(x));
    this.peakCounter += ch.length;
    if (this.peakCounter >= sampleRate / 10) {
      this.port.postMessage({ type: "level", value: this.peak });
      this.peak = 0;
      this.peakCounter = 0;
    }
    if (this.muted) return true;
    const merged = new Float32Array(this.pending.length + ch.length);
    merged.set(this.pending);
    merged.set(ch, this.pending.length);
    this.pending = merged;
    if (this.ratio === 1) {
      let i = 0;
      while (i < this.pending.length) {
        this.frame[this.frameFill++] = toInt16(this.pending[i++]);
        if (this.frameFill === FRAME_SAMPLES) this.flush();
      }
      this.pending = new Float32Array(0);
    } else {
      while (this.pos + this.ratio < this.pending.length) {
        const i = Math.floor(this.pos);
        const f = this.pos - i;
        this.frame[this.frameFill++] = toInt16(
          this.pending[i] * (1 - f) + this.pending[i + 1] * f,
        );
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
    let output = this.frame.buffer.slice(0);
    const result = this.gate.process(rms(this.frame), this.farLevel, !this.gateEnabled);
    if (!result.pass) output = new ArrayBuffer(this.frame.byteLength);
    if (result.bargeIn) this.port.postMessage({ type: "barge-in" });
    this.statsFrames++;
    if (this.statsFrames >= STATS_FRAMES) {
      this.port.postMessage({
        type: "echo-stats",
        gateOpenRatio: this.gate.openRatio,
        coupling: result.coupling,
        worklet: ECHO_GATE_WORKLET_SENTINEL,
      });
      this.statsFrames = 0;
    }
    this.port.postMessage({ type: "frame", buffer: output }, [output]);
    this.frameFill = 0;
  }
}
function rms(frame: Int16Array) {
  let sum = 0;
  for (const sample of frame) {
    const value = sample / 0x8000;
    sum += value * value;
  }
  return Math.sqrt(sum / frame.length);
}
function toInt16(x: number) {
  const c = Math.max(-1, Math.min(1, x));
  return c < 0 ? c * 0x8000 : c * 0x7fff;
}
registerProcessor("capture-processor", CaptureProcessor);
