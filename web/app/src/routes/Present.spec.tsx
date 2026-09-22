import { StrictMode } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { captureStop, close, deckLogs, dispose, get, load, playbackStop, startAudio } = vi.hoisted(() => ({
  get: vi.fn(),
  load: vi.fn().mockResolvedValue({ adapter: "sections", count: 1 }),
  dispose: vi.fn(),
  close: vi.fn().mockResolvedValue(undefined),
  captureStop: vi.fn(),
  deckLogs: vi.fn(),
  playbackStop: vi.fn(),
  startAudio: vi.fn(),
}));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get } }));
vi.mock("../deck/deckDriver", () => ({
  DeckDriver: class {
    constructor(_frame: HTMLIFrameElement, options: { log?: (level: string, message: string) => void }) {
      options.log?.("info", "deck adapter: sections, 1 slides");
      deckLogs("deck adapter: sections, 1 slides");
    }
    load = load;
    dispose = dispose;
  },
}));
vi.mock("../audio/capture", () => ({ startAudio }));
vi.mock("../ws/bridgeClient", () => ({
  BridgeClient: class {
    snapshot = { state: "idle" };
    on() {}
    connect() {}
    disconnect() {}
    start() {}
    sendAudio() {}
  },
}));
import { Present } from "./Present";

function renderPresent() {
  return render(
    <MemoryRouter initialEntries={["/present/demo"]}>
      <Routes><Route path="/present/:id" element={<Present />} /></Routes>
    </MemoryRouter>,
  );
}

describe("Present", () => {
  beforeEach(() => {
    get.mockReset();
    load.mockClear();
    dispose.mockClear();
    deckLogs.mockClear();
    close.mockClear();
    captureStop.mockClear();
    playbackStop.mockClear();
    startAudio.mockResolvedValue({
      context: { close, sampleRate: 48000, state: "running" },
      capture: { stop: captureStop },
      playback: { stop: playbackStop, bufferedMs: 0, enqueue() {} },
      micReady: true,
    });
  });

  it("renders mapped Problem Details copy", async () => {
    get.mockResolvedValue({
      error: { code: "presentation.not_found", detail: "server detail", title: "Not found" },
    });
    renderPresent();
    expect(await screen.findByText("The presentation was not found.")).toBeTruthy();
  });

  it("renders an error when the detail request rejects", async () => {
    get.mockRejectedValue(new Error("offline"));
    renderPresent();
    expect(await screen.findByText("Unable to load presentation.")).toBeTruthy();
  });

  it("closes audio and stops capture after start when unmounted", async () => {
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    const view = renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());
    view.unmount();
    expect(captureStop).toHaveBeenCalledOnce();
    expect(playbackStop).toHaveBeenCalledOnce();
    expect(close).toHaveBeenCalledOnce();
  });

  it("loads one deck in StrictMode after the stale request is cleaned up", async () => {
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    render(
      <StrictMode>
        <MemoryRouter initialEntries={["/present/demo"]}>
          <Routes><Route path="/present/:id" element={<Present />} /></Routes>
        </MemoryRouter>
      </StrictMode>,
    );
    expect(await screen.findByTitle("Presentation deck")).toBeTruthy();
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    expect(deckLogs).toHaveBeenCalledTimes(1);
  });
});
