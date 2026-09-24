import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AskControls } from "./AskControls";
import { usePresenterStore, type PresenterAskState } from "../store/presenterStore";

describe("AskControls", () => {
  beforeEach(() => {
    usePresenterStore.setState({
      snapshot: { state: "presenting", slideIndex: 0, slideCount: 1, muted: false },
      ask: null,
    });
  });

  it("shows Listening with elapsed time, Ask done, Extend and Cancel", () => {
    const onDone = vi.fn();
    const onExtend = vi.fn();
    const onCancel = vi.fn();

    const ask: PresenterAskState = {
      state: "listening",
      elapsedMs: 23000,
      quietRemainingMs: 67000,
      speechRemainingMs: 13400,
      heard: true,
      transcribing: false,
      reason: null,
    };

    render(
      <AskControls
        onDone={onDone}
        onExtend={onExtend}
        onCancel={onCancel}
        ask={ask}
      />,
    );

    expect(screen.getByRole("status").textContent).toContain("Listening… 0:23");
    const doneBtn = screen.getByRole("button", { name: "Ask done" });
    const extendBtn = screen.getByRole("button", { name: "Extend" });
    const cancelBtn = screen.getByRole("button", { name: "Cancel" });

    expect(doneBtn).toBeTruthy();
    expect(extendBtn).toBeTruthy();
    expect(cancelBtn).toBeTruthy();

    fireEvent.click(doneBtn);
    expect(onDone).toHaveBeenCalledOnce();

    fireEvent.click(extendBtn);
    expect(onExtend).toHaveBeenCalledOnce();

    fireEvent.click(cancelBtn);
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("shows the quiet countdown under 20 s with sending or closing wording", () => {
    const heardAsk: PresenterAskState = {
      state: "listening",
      elapsedMs: 75000,
      quietRemainingMs: 15000,
      speechRemainingMs: 10000,
      heard: true,
      transcribing: false,
      reason: null,
    };

    const { rerender } = render(<AskControls ask={heardAsk} />);
    expect(screen.getByText("Sending in 0:15 if quiet")).toBeTruthy();

    const unlearnedAsk: PresenterAskState = {
      ...heardAsk,
      heard: false,
    };
    rerender(<AskControls ask={unlearnedAsk} />);
    expect(screen.getByText("Closing in 0:15 if quiet")).toBeTruthy();

    const notYetNearQuiet: PresenterAskState = {
      ...heardAsk,
      quietRemainingMs: 25000,
    };
    rerender(<AskControls ask={notYetNearQuiet} />);
    expect(screen.queryByText(/if quiet/)).toBeNull();
  });

  it("shows the remaining speech time while listening", () => {
    const ask: PresenterAskState = {
      state: "listening",
      elapsedMs: 18000,
      quietRemainingMs: 72000,
      speechRemainingMs: 7000,
      heard: true,
      transcribing: false,
      reason: null,
    };

    render(<AskControls ask={ask} />);
    expect(screen.getByText("0:07 of speech left")).toBeTruthy();
  });

  it("shows Continue next to Pause only while ask_state is answering, and it sends resume", () => {
    const onResume = vi.fn();
    const answeringAsk: PresenterAskState = {
      state: "answering",
      elapsedMs: 25000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "sent",
    };

    const { rerender } = render(
      <div>
        <button type="button">Pause</button>
        <AskControls onContinue={onResume} ask={null} />
      </div>,
    );
    expect(screen.queryByRole("button", { name: "Continue" })).toBeNull();

    rerender(
      <div>
        <button type="button">Pause</button>
        <AskControls onContinue={onResume} ask={answeringAsk} />
      </div>,
    );
    const continueBtn = screen.getByRole("button", { name: "Continue" });
    expect(continueBtn).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain("Answering…");

    fireEvent.click(continueBtn);
    expect(onResume).toHaveBeenCalledOnce();
  });

  it("frame sequence listening → answering → off drives the control; listening → off (cancel) hides it", () => {
    const listening: PresenterAskState = {
      state: "listening",
      elapsedMs: 10000,
      quietRemainingMs: 80000,
      speechRemainingMs: 20000,
      heard: true,
      transcribing: false,
      reason: null,
    };
    const answering: PresenterAskState = {
      state: "answering",
      elapsedMs: 20000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "sent",
    };
    const off: PresenterAskState = {
      state: "off",
      elapsedMs: 25000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "cancelled",
    };

    // Driven by the store
    render(<AskControls />);

    // 1. Initial idle: Ask button visible
    expect(screen.getByRole("button", { name: "Ask" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Ask done" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Continue" })).toBeNull();

    // 2. listening
    act(() => {
      usePresenterStore.setState({ ask: listening });
    });
    expect(screen.queryByRole("button", { name: "Ask" })).toBeNull();
    expect(screen.getByRole("button", { name: "Ask done" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Continue" })).toBeNull();

    // 3. answering: Ask stays available for a follow-up question (owner decision, review r1)
    act(() => {
      usePresenterStore.setState({ ask: answering });
    });
    expect(screen.getByRole("button", { name: "Ask" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Ask done" })).toBeNull();
    expect(screen.getByRole("button", { name: "Continue" })).toBeTruthy();

    // 4. off
    act(() => {
      usePresenterStore.setState({ ask: off });
    });
    expect(screen.getByRole("button", { name: "Ask" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Ask done" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Continue" })).toBeNull();

    // 5. Direct cancel: listening -> off
    act(() => {
      usePresenterStore.setState({ ask: listening });
    });
    expect(screen.getByRole("button", { name: "Ask done" })).toBeTruthy();

    act(() => {
      usePresenterStore.setState({ ask: off });
    });
    expect(screen.getByRole("button", { name: "Ask" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Ask done" })).toBeNull();
  });

  it("shows Ask during answering for a follow-up question, and it sends ask_start", () => {
    const onAsk = vi.fn();
    const answering: PresenterAskState = {
      state: "answering",
      elapsedMs: 20000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "sent",
    };
    const { rerender } = render(<AskControls onAsk={onAsk} ask={answering} muted={false} />);

    const askBtn = screen.getByRole("button", { name: "Ask" });
    expect(screen.getByRole("button", { name: "Continue" })).toBeTruthy();
    fireEvent.click(askBtn);
    expect(onAsk).toHaveBeenCalledOnce();

    rerender(<AskControls onAsk={onAsk} ask={answering} muted={true} />);
    expect(screen.getByRole("button", { name: "Ask" })).toHaveProperty("disabled", true);
    expect(screen.getByText("Unmute to ask")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Ask" }));
    expect(onAsk).toHaveBeenCalledOnce();
  });

  it("Ask is disabled with the unmute hint when muted", () => {
    const onAsk = vi.fn();
    const { rerender } = render(<AskControls onAsk={onAsk} muted={true} />);

    const askBtn = screen.getByRole("button", { name: "Ask" });
    expect(askBtn).toHaveProperty("disabled", true);
    expect(screen.getByText("Unmute to ask")).toBeTruthy();

    fireEvent.click(askBtn);
    expect(onAsk).not.toHaveBeenCalled();

    rerender(<AskControls onAsk={onAsk} muted={false} />);
    expect(askBtn).toHaveProperty("disabled", false);
    expect(screen.queryByText("Unmute to ask")).toBeNull();

    fireEvent.click(askBtn);
    expect(onAsk).toHaveBeenCalledOnce();
  });
});
