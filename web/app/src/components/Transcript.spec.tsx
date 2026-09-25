import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
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

const turn = (text: string) => ({ role: "assistant", text, endMs: null, slide: 0 });
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

describe("Transcript train on this", () => {
  beforeEach(() => usePresenterStore.setState({ transcript: [], trainerMode: true }));

  it("train on this sends the exchange of the clicked fragment", () => {
    usePresenterStore.setState({
      transcript: [
        { role: "user", text: "What about the coating?", endMs: null, slide: 2 },
        { role: "assistant", text: "The coating uses a new alloy.", endMs: null, slide: 2 },
        { role: "assistant", text: "It also resists corrosion.", endMs: null, slide: 2 },
      ],
    });
    const onTrainOnThis = vi.fn();
    render(<Transcript onTrainOnThis={onTrainOnThis} />);

    const buttons = screen.getAllByRole("button", { name: "Train on this" });
    expect(buttons).toHaveLength(2);
    fireEvent.click(buttons[1]);

    expect(onTrainOnThis).toHaveBeenCalledTimes(1);
    expect(onTrainOnThis).toHaveBeenCalledWith(
      "What about the coating?",
      "The coating uses a new alloy. It also resists corrosion.",
      2,
    );
  });

  it("sends the question's slide and the full answer after navigating mid-answer", () => {
    // Real frames through the store reducer: the question is asked on slide 2 and the talk moves to 5 while the
    // answer is still streaming, then narration continues on 5.
    usePresenterStore.setState({ transcript: [], slide: 0 });
    const onTrainOnThis = vi.fn();
    render(<Transcript onTrainOnThis={onTrainOnThis} />);
    const { message } = usePresenterStore.getState();
    act(() => {
      message({ type: "slide", index: 2 });
      message({ type: "transcript", role: "user", delta: "What about", end_ms: 1000 });
      message({ type: "transcript", role: "user", delta: " the coating?", end_ms: 1400 });
      message({ type: "transcript", role: "assistant", delta: "The coating", end_ms: 3000 });
      message({ type: "slide", index: 5 });
      message({ type: "transcript", role: "assistant", delta: " uses a new alloy.", end_ms: 3500 });
      message({ type: "transcript", role: "assistant", delta: "Slide five covers pricing.", end_ms: 9000 });
    });

    const buttons = screen.getAllByRole("button", { name: "Train on this" });
    expect(buttons).toHaveLength(2);
    expect(buttons[1]).toHaveProperty("disabled", true);
    fireEvent.click(buttons[0]);

    expect(onTrainOnThis).toHaveBeenCalledTimes(1);
    expect(onTrainOnThis).toHaveBeenCalledWith("What about the coating?", "The coating uses a new alloy.", 2);
  });

  it("disables train on this without a preceding question", () => {
    usePresenterStore.setState({
      transcript: [{ role: "assistant", text: "Welcome to the talk.", endMs: null, slide: 0 }],
    });
    render(<Transcript onTrainOnThis={vi.fn()} />);

    const button = screen.getByRole("button", { name: "Train on this" });
    expect(button).toHaveProperty("disabled", true);
    expect(button).toHaveProperty("title", "No question before this answer");
  });

  it("hides train on this when trainer mode is off", () => {
    usePresenterStore.setState({
      trainerMode: false,
      transcript: [
        { role: "user", text: "What about the coating?", endMs: null, slide: 2 },
        { role: "assistant", text: "The coating uses a new alloy.", endMs: null, slide: 2 },
      ],
    });
    render(<Transcript onTrainOnThis={vi.fn()} />);

    expect(screen.queryByRole("button", { name: "Train on this" })).toBeNull();
  });
});
