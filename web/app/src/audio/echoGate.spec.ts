import { describe, expect, it } from "vitest";
import {
  BARGE_IN_MS,
  EchoGate,
  FAR_TAIL_MS,
  FRAME_MS,
  HANGOVER_MS,
  OPEN_CONSECUTIVE_FRAMES,
} from "./echoGate";

const frames = (ms: number) => Math.ceil(ms / FRAME_MS);

describe("EchoGate", () => {
  it("blocks echo-level near audio while the far end is active", () => {
    const gate = new EchoGate();
    expect(gate.process(0.02, 0.1).pass).toBe(false);
    expect(gate.process(0.02, 0.1).pass).toBe(false);
  });

  it("opens only after two frames 12 dB above the learned echo", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    expect(gate.process(0.081, 0.1).pass).toBe(false);
    expect(gate.process(0.081, 0.1).pass).toBe(true);
    expect(OPEN_CONSECUTIVE_FRAMES * FRAME_MS).toBe(40);
  });

  it("holds an open gate for its 300 ms hangover then closes", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    gate.process(0.081, 0.1);
    gate.process(0.081, 0.1);
    for (let i = 1; i < frames(HANGOVER_MS); i++)
      expect(gate.process(0.02, 0.1).pass).toBe(true);
    expect(gate.process(0.02, 0.1).pass).toBe(false);
  });

  it("passes through when the far end becomes inactive", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    for (let i = 0; i < frames(FAR_TAIL_MS); i++) gate.process(0.001, 0);
    expect(gate.process(0.001, 0).pass).toBe(true);
  });

  it("adapts coupling down for headphones so normal speech opens", () => {
    const gate = new EchoGate();
    for (let i = 0; i < 4; i++) gate.process(0.0001, 0.1);
    expect(gate.coupling).toBeLessThan(0.01 + Number.EPSILON);
    expect(gate.process(0.02, 0.1).pass).toBe(false);
    expect(gate.process(0.02, 0.1).pass).toBe(true);
  });

  it("fires barge-in once for each sustained open period", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    gate.process(0.081, 0.1);
    gate.process(0.081, 0.1);
    let bargeIns = 0;
    for (let i = 0; i < frames(BARGE_IN_MS); i++) {
      if (gate.process(0.081, 0.1).bargeIn) bargeIns++;
    }
    for (let i = 0; i < 5; i++) if (gate.process(0.081, 0.1).bargeIn) bargeIns++;
    expect(bargeIns).toBe(1);
  });

  it("never fires barge-in for a short noise that only the hangover keeps open", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    gate.process(0.081, 0.1);
    expect(gate.process(0.081, 0.1).open).toBe(true);
    let bargeIns = 0;
    for (let i = 0; i < frames(HANGOVER_MS) + 5; i++) if (gate.process(0.02, 0.1).bargeIn) bargeIns++;
    expect(bargeIns).toBe(0);
  });

  it("keeps delayed echo of a loud syllable blocked while the current far level is quiet", () => {
    const gate = new EchoGate();
    gate.process(0.02, 0.1);
    // The far end has dropped to a quiet syllable, but the mic still hears the loud one from 100 ms ago.
    for (let i = 0; i < 5; i++) expect(gate.process(0.02, 0.01).pass).toBe(false);
  });

  it("passes everything when the bypass flag is set", () => {
    const gate = new EchoGate();
    expect(gate.process(0.02, 0.1, true).pass).toBe(true);
  });
});
