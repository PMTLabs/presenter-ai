import { create } from "zustand";

export type ToastKind = "info" | "success" | "warning" | "error";
export type Toast = { id: number; key: string | null; kind: ToastKind; message: string };
export type ToastInput = { kind: ToastKind; message: string; key?: string };

/** Info and success toasts dismiss themselves; warnings and errors stay until closed. */
export const TOAST_AUTO_DISMISS_MS = 5000;
export const MAX_TOASTS = 3;

type ToastState = {
  toasts: Toast[];
  /** Shows a toast; a toast with the same `key` is updated in place (same id, same timer) instead of stacking. */
  show: (toast: ToastInput) => number;
  dismiss: (id: number) => void;
  dismissKey: (key: string) => void;
};

const timers = new Map<number, ReturnType<typeof setTimeout>>();
let nextId = 1;

function clearTimer(id: number) {
  const timer = timers.get(id);
  if (timer !== undefined) clearTimeout(timer);
  timers.delete(id);
}

export const useToastStore = create<ToastState>((set, get) => ({
  toasts: [],
  show: ({ kind, message, key }) => {
    const existing = key ? get().toasts.find((toast) => toast.key === key) : undefined;
    if (existing) {
      if (existing.kind !== kind || existing.message !== message)
        set((s) => ({
          toasts: s.toasts.map((toast) => (toast.id === existing.id ? { ...toast, kind, message } : toast)),
        }));
      return existing.id;
    }
    const id = nextId++;
    set((s) => {
      const toasts = [...s.toasts, { id, key: key ?? null, kind, message }];
      const dropped = toasts.slice(0, Math.max(0, toasts.length - MAX_TOASTS));
      for (const toast of dropped) clearTimer(toast.id);
      return { toasts: toasts.slice(dropped.length) };
    });
    if (kind === "info" || kind === "success")
      timers.set(id, setTimeout(() => get().dismiss(id), TOAST_AUTO_DISMISS_MS));
    return id;
  },
  dismiss: (id) => {
    clearTimer(id);
    if (get().toasts.some((toast) => toast.id === id))
      set((s) => ({ toasts: s.toasts.filter((toast) => toast.id !== id) }));
  },
  dismissKey: (key) => {
    const toast = get().toasts.find((candidate) => candidate.key === key);
    if (toast) get().dismiss(toast.id);
  },
}));
