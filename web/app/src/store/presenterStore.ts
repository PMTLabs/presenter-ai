import { create } from "zustand";
import type { BridgeMessage, Snapshot } from "../ws/bridgeClient";
export type Turn = { role: string; text: string; endMs: number | null };
export type PresenterLog = {
  level: string;
  message: string;
  time: string;
  source?: "local" | "server";
};
export type LimitWarning = {
  kind: "max_length" | "idle";
  secondsLeft: number;
};
type State = {
  snapshot: Snapshot;
  slide: number;
  usage: number | null;
  transcript: Turn[];
  logs: PresenterLog[];
  connected: boolean;
  micReady: boolean;
  bufferedMs: number;
  limitWarning: LimitWarning | null;
  upstreamStatus: "suspended" | "reconnecting" | "live" | null;
  suspended: boolean;
  endReason: string | null;
  usageConfirmed: boolean | null;
  estimatedSeconds: number | null;
  setMicReady: (ready: boolean) => void;
  setBuffered: (ms: number) => void;
  applySnapshot: (s: Snapshot) => void;
  message: (m: BridgeMessage) => void;
  log: (level: string, message: string) => void;
};
export const usePresenterStore = create<State>((set) => ({
  snapshot: { state: "idle", slideIndex: 0, slideCount: 0, muted: false },
  slide: 0,
  usage: null,
  transcript: [],
  logs: [],
  connected: false,
  micReady: false,
  bufferedMs: 0,
  limitWarning: null,
  upstreamStatus: null,
  suspended: false,
  endReason: null,
  usageConfirmed: null,
  estimatedSeconds: null,
  setMicReady: (micReady) => set({ micReady }),
  setBuffered: (bufferedMs) => set({ bufferedMs }),
  applySnapshot: (snapshot) =>
    set((s) => ({
      snapshot,
      slide: snapshot.slideIndex,
      usage: snapshot.usageSeconds ?? null,
      suspended: snapshot.suspended ?? (snapshot.state === "idle" ? false : s.suspended),
      ...(snapshot.state === "idle"
        ? { limitWarning: null, upstreamStatus: null, suspended: false }
        : {}),
      ...(snapshot.state === "presenting"
        ? { endReason: null, usageConfirmed: null, estimatedSeconds: null }
        : {}),
    })),
  log: (level, message) =>
    set((s) => ({
      logs: [
        ...s.logs,
        {
          level,
          message,
          time: new Date().toTimeString().slice(0, 8),
          source: "local" as const,
        },
      ].slice(-400),
    })),
  message: (m) =>
    set((s) => {
      if (m.type === "slide") return { slide: m.index as number };
      if (m.type === "usage") return { usage: m.seconds as number };
      if (m.type === "transcript") {
        const endMs = (m.end_ms as number | null) ?? null;
        const old = s.transcript.at(-1);
        if (
          old &&
          old.role === m.role &&
          endMs !== null &&
          old.endMs !== null &&
          endMs - old.endMs < 1000
        )
          return {
            transcript: [
              ...s.transcript.slice(0, -1),
              { ...old, text: old.text + String(m.delta), endMs },
            ],
          };
        return {
          transcript: [
            ...s.transcript,
            { role: String(m.role), text: String(m.delta), endMs },
          ].slice(-200),
        };
      }
      if (m.type === "limit_warning") {
        if (m.secondsLeft === null || m.secondsLeft === undefined) {
          return { limitWarning: null };
        }
        return {
          limitWarning: {
            kind: m.kind as "max_length" | "idle",
            secondsLeft: Number(m.secondsLeft),
          },
        };
      }
      if (m.type === "upstream") {
        const status = m.status as "suspended" | "reconnecting" | "live";
        const isSuspended = status === "suspended" ? true : status === "live" ? false : s.suspended;
        return {
          upstreamStatus: status,
          suspended: isSuspended,
          snapshot: {
            ...s.snapshot,
            suspended: isSuspended,
          },
        };
      }
      if (m.type === "closed") {
        return {
          limitWarning: null,
          upstreamStatus: null,
          suspended: false,
          endReason: (m.endReason as string) ?? null,
          usageConfirmed: (m.usageConfirmed as boolean) ?? null,
          estimatedSeconds: (m.estimatedSeconds as number) ?? null,
        };
      }
      if (m.type === "log")
        return {
          logs: [
            ...s.logs,
            {
              level: String(m.level),
              message: String(m.message),
              time: new Date().toTimeString().slice(0, 8),
              source: "server" as const,
            },
          ].slice(-400),
        };
      return {};
    }),
}));
