import { create } from "zustand";
import type { BridgeMessage, Snapshot } from "../ws/bridgeClient";
export type Turn = { role: string; text: string; endMs: number | null; slide: number };
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
export type ScriptEditStatus = "queued" | "processing" | "applied" | "failed";
export type ScriptEdit = {
  id: string;
  status: ScriptEditStatus;
  slideIndexes: number[];
  version: number | null;
  summary: string | null;
  error: string | null;
};
const TERMINAL_EDIT_STATUSES: readonly ScriptEditStatus[] = ["applied", "failed"];
const MAX_TRACKED_EDITS = 20;
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
  trainerMode: boolean;
  trainerAvailable: boolean;
  voiceTraining: boolean;
  scriptVersion: number | null;
  edits: Record<string, ScriptEdit>;
  editOrder: string[];
  currentEditId: string | null;
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
  trainerMode: false,
  trainerAvailable: false,
  voiceTraining: true,
  scriptVersion: null,
  edits: {},
  editOrder: [],
  currentEditId: null,
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
            { role: String(m.role), text: String(m.delta), endMs, slide: s.slide },
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
      if (m.type === "script_version") {
        return {
          scriptVersion: (m.version as number | undefined) ?? null,
          trainerMode: Boolean(m.trainerMode),
          trainerAvailable: Boolean(m.trainerAvailable),
          voiceTraining: Boolean(m.voiceTraining),
        };
      }
      if (m.type === "trainer_state") {
        // The switch shows only what the server holds: idle requests, refusals and the reset at End arrive here.
        return {
          trainerMode: Boolean(m.trainerMode),
          trainerAvailable: Boolean(m.trainerAvailable),
          voiceTraining: m.voiceTraining === undefined ? true : Boolean(m.voiceTraining),
        };
      }
      if (m.type === "script_edit") {
        const id = String(m.id);
        const existing = s.edits[id];
        // A non-terminal frame never overwrites a terminal status for the same id (out-of-order or duplicate delivery).
        if (existing && TERMINAL_EDIT_STATUSES.includes(existing.status)) return {};
        const status = m.status as ScriptEditStatus;
        const edit: ScriptEdit = {
          id,
          status,
          slideIndexes: (m.slideIndexes as number[] | undefined) ?? [],
          version: (m.version as number | null | undefined) ?? null,
          summary: (m.summary as string | null | undefined) ?? null,
          error: (m.error as string | null | undefined) ?? null,
        };
        const edits = { ...s.edits, [id]: edit };
        let editOrder = s.editOrder.includes(id) ? s.editOrder : [...s.editOrder, id];
        if (editOrder.length > MAX_TRACKED_EDITS) {
          const drop = editOrder.length - MAX_TRACKED_EDITS;
          for (const droppedId of editOrder.slice(0, drop)) delete edits[droppedId];
          editOrder = editOrder.slice(drop);
        }
        return {
          edits,
          editOrder,
          // A fresh id (always announced "queued" first) becomes the one shown; later frames for it keep updating in place.
          currentEditId: status === "queued" ? id : s.currentEditId,
        };
      }
      return {};
    }),
}));
