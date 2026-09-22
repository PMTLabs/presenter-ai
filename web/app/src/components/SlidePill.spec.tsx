import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { usePresenterStore } from "../store/presenterStore";
import { SlidePill } from "./SlidePill";

describe("SlidePill", () => {
  afterEach(() => {
    act(() => {
      usePresenterStore.setState({
        slide: 0,
        snapshot: { state: "idle", slideIndex: 0, slideCount: 0, muted: false },
      });
    });
  });

  it("renders with stable Zustand selectors", () => {
    const consoleError = vi
      .spyOn(console, "error")
      .mockImplementation(() => {});
    act(() => {
      usePresenterStore.setState({
        slide: 1,
        snapshot: { state: "idle", slideIndex: 1, slideCount: 3, muted: false },
      });
    });

    render(<SlidePill />);

    expect(screen.getByText("slide 2/3")).toBeTruthy();
    expect(consoleError).not.toHaveBeenCalledWith(
      expect.stringContaining("Maximum update depth exceeded"),
    );
    consoleError.mockRestore();
  });
});
