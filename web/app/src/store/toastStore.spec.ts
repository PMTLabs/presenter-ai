import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MAX_TOASTS, TOAST_AUTO_DISMISS_MS, useToastStore } from "./toastStore";

const toasts = () => useToastStore.getState().toasts;

describe("toastStore", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    for (const toast of toasts()) useToastStore.getState().dismiss(toast.id);
  });
  afterEach(() => vi.useRealTimers());

  it("auto-dismisses info and success after the delay, keeps warnings and errors", () => {
    const { show } = useToastStore.getState();
    show({ kind: "info", message: "info" });
    show({ kind: "success", message: "success" });
    show({ kind: "warning", message: "warning" });
    vi.advanceTimersByTime(TOAST_AUTO_DISMISS_MS - 1);
    expect(toasts().map((toast) => toast.message)).toEqual(["info", "success", "warning"]);

    vi.advanceTimersByTime(1);
    expect(toasts().map((toast) => toast.message)).toEqual(["warning"]);

    show({ kind: "error", message: "error" });
    vi.advanceTimersByTime(TOAST_AUTO_DISMISS_MS * 10);
    expect(toasts().map((toast) => toast.message)).toEqual(["warning", "error"]);
  });

  it("stacks at most three, dropping the oldest", () => {
    const { show } = useToastStore.getState();
    for (const message of ["one", "two", "three", "four"]) show({ kind: "error", message });
    expect(MAX_TOASTS).toBe(3);
    expect(toasts().map((toast) => toast.message)).toEqual(["two", "three", "four"]);
  });

  it("updates a keyed toast in place and dismisses it by key", () => {
    const { show, dismissKey } = useToastStore.getState();
    const id = show({ key: "limit", kind: "warning", message: "60s" });
    expect(show({ key: "limit", kind: "warning", message: "59s" })).toBe(id);
    expect(toasts()).toEqual([{ id, key: "limit", kind: "warning", message: "59s" }]);

    dismissKey("limit");
    expect(toasts()).toEqual([]);
  });

  it("a dismissed info toast's timer does not remove a later toast", () => {
    const { show, dismiss } = useToastStore.getState();
    const first = show({ kind: "info", message: "first" });
    dismiss(first);
    show({ kind: "warning", message: "second" });
    vi.advanceTimersByTime(TOAST_AUTO_DISMISS_MS);
    expect(toasts().map((toast) => toast.message)).toEqual(["second"]);
  });
});
