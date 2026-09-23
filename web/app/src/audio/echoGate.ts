// Included in periodic worklet stats, so check-dist can prove this pure module was bundled.
export const ECHO_GATE_WORKLET_SENTINEL = "presenter-echo-gate-v1";
export const FRAME_MS = 20;
export const FAR_FLOOR_RMS = 0.004;
export const FAR_TAIL_MS = 250;
export const INITIAL_COUPLING = 0.5;
export const MIN_COUPLING = 0.01;
export const COUPLING_WINDOW_MS = 2000;
export const COUPLING_PERCENTILE = 0.1;
export const OPEN_CONSECUTIVE_FRAMES = 2;
export const HANGOVER_MS = 300;
export const BARGE_IN_MS = 300;

const farTailFrames = Math.ceil(FAR_TAIL_MS / FRAME_MS);
const couplingWindowFrames = Math.ceil(COUPLING_WINDOW_MS / FRAME_MS);
const hangoverFrames = Math.ceil(HANGOVER_MS / FRAME_MS);
const bargeInFrames = Math.ceil(BARGE_IN_MS / FRAME_MS);

export type EchoGateResult = {
  pass: boolean;
  farActive: boolean;
  open: boolean;
  coupling: number;
  bargeIn: boolean;
};

/** Pure 20 ms far-end echo gate used by the capture worklet. */
export class EchoGate {
  private couplingSamples: number[] = [];
  private openingFrames = 0;
  private hangover = 0;
  // Far-end levels of the last FAR_TAIL_MS. The echo reaches the mic tens of milliseconds after playout, so the gate
  // compares against the loudest recent far level rather than the current one.
  private farHistory: number[] = [];
  private speechFrames = 0;
  private bargeInSent = false;
  private openCount = 0;
  private frameCount = 0;
  coupling = INITIAL_COUPLING;

  process(near: number, far: number, bypass = false): EchoGateResult {
    if (bypass) {
      this.frameCount++;
      return this.result(true, far >= FAR_FLOOR_RMS, false, false);
    }
    this.farHistory.push(far);
    if (this.farHistory.length > farTailFrames) this.farHistory.shift();
    const activeFar = Math.max(...this.farHistory);
    const farActive = activeFar >= FAR_FLOOR_RMS;
    this.frameCount++;

    if (!farActive) {
      this.openingFrames = 0;
      this.hangover = 0;
      this.speechFrames = 0;
      this.bargeInSent = false;
      return this.result(true, false, false, false);
    }

    const threshold = Math.max(MIN_COUPLING, 4 * this.coupling * activeFar);
    const aboveThreshold = near > threshold;
    let open = this.hangover > 0;

    if (!open) {
      // Learn only once the far end has played for the whole tail window. Before the delayed echo reaches the mic,
      // and just after a pause in the far end, the mic is quiet against a loud far level, which would drive the
      // coupling to its floor and let the echo that follows through.
      const farSteady =
        this.farHistory.length === farTailFrames && Math.min(...this.farHistory) >= FAR_FLOOR_RMS;
      if (farSteady) this.updateCoupling(near, activeFar);
      if (aboveThreshold) this.openingFrames++;
      else this.openingFrames = 0;
      if (this.openingFrames >= OPEN_CONSECUTIVE_FRAMES) {
        open = true;
        this.hangover = hangoverFrames;
      }
    }

    if (open) {
      if (aboveThreshold) this.hangover = hangoverFrames;
      else if (this.hangover > 0) this.hangover--;
      if (this.hangover === 0) {
        this.openingFrames = 0;
        this.speechFrames = 0;
        this.bargeInSent = false;
        return this.result(false, true, false, false);
      }
      // Barge-in needs BARGE_IN_MS of speech in this open period; hangover frames keep the gate open but do not count.
      if (aboveThreshold) this.speechFrames++;
      const bargeIn = !this.bargeInSent && this.speechFrames >= bargeInFrames;
      if (bargeIn) this.bargeInSent = true;
      return this.result(true, true, true, bargeIn);
    }

    return this.result(false, true, false, false);
  }

  get openRatio() {
    return this.frameCount === 0 ? 0 : this.openCount / this.frameCount;
  }

  private updateCoupling(near: number, far: number) {
    this.couplingSamples.push(near / Math.max(far, FAR_FLOOR_RMS));
    if (this.couplingSamples.length > couplingWindowFrames)
      this.couplingSamples.shift();
    const sorted = [...this.couplingSamples].sort((a, b) => a - b);
    const index = Math.floor((sorted.length - 1) * COUPLING_PERCENTILE);
    this.coupling = Math.max(MIN_COUPLING, sorted[index]);
  }

  private result(
    pass: boolean,
    farActive: boolean,
    open: boolean,
    bargeIn: boolean,
  ): EchoGateResult {
    if (open) this.openCount++;
    return { pass, farActive, open, coupling: this.coupling, bargeIn };
  }
}
