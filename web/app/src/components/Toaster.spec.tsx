import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useToastStore } from "../store/toastStore";
import { Toaster } from "./Toaster";
import { InfoTooltip } from "./InfoTooltip";

describe("Toaster", () => {
  beforeEach(() => {
    for (const toast of useToastStore.getState().toasts) useToastStore.getState().dismiss(toast.id);
  });

  it("renders errors as alerts and the rest as polite statuses, each with a close button", () => {
    render(<Toaster />);
    act(() => {
      useToastStore.getState().show({ kind: "error", message: "Unable to load." });
      useToastStore.getState().show({ kind: "warning", message: "60s left" });
    });
    const region = screen.getByRole("region", { name: "Notifications" });
    expect(region.className).toContain("fixed");
    expect(region.className).toContain("top-4");
    expect(screen.getByRole("alert").textContent).toContain("Unable to load.");
    const status = screen.getByRole("status");
    expect(status.textContent).toContain("60s left");
    expect(status.getAttribute("aria-live")).toBe("polite");

    fireEvent.click(screen.getAllByRole("button", { name: "Dismiss notification" })[0]);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("status")).toBeTruthy();
  });
});

describe("InfoTooltip", () => {
  afterEach(() => vi.useRealTimers());

  it("opens on hover and on focus, never via the title attribute, and closes on Escape", () => {
    vi.useFakeTimers();
    render(<InfoTooltip label="About Trainer mode">Explains Trainer mode.</InfoTooltip>);
    const trigger = screen.getByRole("button", { name: "About Trainer mode" });
    expect(trigger.getAttribute("title")).toBeNull();
    expect(screen.queryByRole("tooltip")).toBeNull();

    fireEvent.mouseEnter(trigger);
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip.textContent).toBe("Explains Trainer mode.");
    expect(trigger.getAttribute("aria-describedby")).toBe(tooltip.id);
    // Moving the pointer onto the tooltip keeps it open.
    fireEvent.mouseLeave(trigger);
    fireEvent.mouseEnter(tooltip);
    act(() => {
      vi.advanceTimersByTime(500);
    });
    expect(screen.getByRole("tooltip")).toBeTruthy();
    fireEvent.mouseLeave(tooltip);
    act(() => {
      vi.advanceTimersByTime(500);
    });
    expect(screen.queryByRole("tooltip")).toBeNull();

    act(() => trigger.focus());
    expect(screen.getByRole("tooltip")).toBeTruthy();
    fireEvent.keyDown(trigger, { key: "Escape" });
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("stays inside a narrow viewport", () => {
    const width = window.innerWidth;
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 200 });
    try {
      render(<InfoTooltip label="About">Text</InfoTooltip>);
      const trigger = screen.getByRole("button", { name: "About" });
      trigger.getBoundingClientRect = () => ({ top: 10, bottom: 30, left: 20, right: 40, width: 20, height: 20, x: 20, y: 10, toJSON() {} });
      act(() => trigger.focus());
      const style = screen.getByRole("tooltip").style;
      expect(style.left).toBe("8px");
      expect(style.width).toBe("184px");
      expect(style.top).toBe("38px");
    } finally {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: width });
    }
  });
});
