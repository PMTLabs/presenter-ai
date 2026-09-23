import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { usePresenterStore } from "../store/presenterStore";
import { LogPanel } from "./LogPanel";
import { Transcript } from "./Transcript";

/** jsdom has no layout, so give the panel a fixed viewport and a content height that grows with each line. */
function fakeScrollBox(element: HTMLElement) {
  let scrollTop = 0;
  Object.defineProperty(element, "clientHeight", { configurable: true, get: () => 100 });
  Object.defineProperty(element, "scrollHeight", {
    configurable: true,
    get: () => 100 + element.childElementCount * 40,
  });
  Object.defineProperty(element, "scrollTop", {
    configurable: true,
    get: () => scrollTop,
    set: (value: number) => {
      scrollTop = value;
    },
  });
  return element;
}

const turn = (text: string) => ({ role: "assistant", text, endMs: null });
const line = (message: string) => ({ level: "info", message, time: "20:00:00" });

describe.each([
  {
    name: "Transcript",
    Panel: Transcript,
    heading: "Transcript",
    add: (text: string) =>
      usePresenterStore.setState((state) => ({ transcript: [...state.transcript, turn(text)] })),
  },
  {
    name: "LogPanel",
    Panel: LogPanel,
    heading: "Log",
    add: (text: string) => usePresenterStore.setState((state) => ({ logs: [...state.logs, line(text)] })),
  },
])("$name auto-scroll", ({ Panel, heading, add }) => {
  beforeEach(() => usePresenterStore.setState({ transcript: [], logs: [] }));

  it("follows new text while the reader is at the bottom", () => {
    render(<Panel />);
    const box = fakeScrollBox(screen.getByRole("heading", { name: heading }).parentElement!);

    act(() => add("first"));
    expect(box.scrollTop).toBe(box.scrollHeight);
    act(() => add("second"));
    expect(box.scrollTop).toBe(box.scrollHeight);
  });

  it("stays put while the reader has scrolled up, and follows again once back at the bottom", () => {
    render(<Panel />);
    const box = fakeScrollBox(screen.getByRole("heading", { name: heading }).parentElement!);
    act(() => add("first"));
    act(() => add("second"));

    box.scrollTop = 0;
    fireEvent.scroll(box);
    act(() => add("third"));
    expect(box.scrollTop).toBe(0);

    box.scrollTop = box.scrollHeight - box.clientHeight;
    fireEvent.scroll(box);
    act(() => add("fourth"));
    expect(box.scrollTop).toBe(box.scrollHeight);
  });
});
