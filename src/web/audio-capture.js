// Microphone capture → 20 ms PCM16 frames (24 kHz) via an AudioWorklet.

export class AudioCapture {
  constructor({ context, onFrame, onLevel }) {
    this.context = context;
    this.onFrame = onFrame;
    this.onLevel = onLevel;
    this.stream = null;
    this.source = null;
    this.node = null;
    this.muted = false;
  }

  async start() {
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      video: false,
    });
    await this.context.audioWorklet.addModule('./worklets/capture-processor.js');
    this.source = this.context.createMediaStreamSource(this.stream);
    this.node = new AudioWorkletNode(this.context, 'capture-processor', { numberOfInputs: 1, numberOfOutputs: 0, channelCount: 1 });
    this.node.port.onmessage = (e) => {
      const msg = e.data;
      if (msg.type === 'frame') this.onFrame?.(msg.buffer);
      else if (msg.type === 'level') this.onLevel?.(msg.value);
    };
    this.source.connect(this.node);
    this.setMuted(this.muted);
  }

  setMuted(value) {
    this.muted = Boolean(value);
    this.node?.port.postMessage({ type: 'mute', value: this.muted });
  }

  stop() {
    try {
      this.source?.disconnect();
      this.node?.disconnect();
    } catch {}
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
    this.source = null;
    this.node = null;
  }
}
