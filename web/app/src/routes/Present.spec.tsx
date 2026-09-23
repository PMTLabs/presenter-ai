import { StrictMode } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, setAuthSession, useAuthStore } from "@presenter/shared";

const { bridgeConnect, bridgeDisconnect, captureStop, close, deckLogs, dispose, get, load, playbackStop, post, startAudio } = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  load: vi.fn().mockResolvedValue({ adapter: "sections", count: 1 }),
  dispose: vi.fn(),
  close: vi.fn().mockResolvedValue(undefined),
  captureStop: vi.fn(),
  deckLogs: vi.fn(),
  playbackStop: vi.fn(),
  startAudio: vi.fn(),
  bridgeConnect: vi.fn(),
  bridgeDisconnect: vi.fn(),
}));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get, POST: post } }));
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
    constructor(
      _url: string | undefined,
      _webSocket: undefined,
      private readonly ticketProvider: () => string | Promise<string>,
    ) {}
    on() {}
    connect() {
      bridgeConnect();
      void this.ticketProvider();
    }
    disconnect() { bridgeDisconnect(); }
    start() {}
    sendAudio() {}
  },
}));
import { Present } from "./Present";

const user = { id: "usr_test", email: "test@example.invalid", displayName: null, role: "user" };

function signIn() {
  useAuthStore.setState({ ready: true, user });
}

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
    post.mockReset();
    load.mockClear();
    dispose.mockClear();
    deckLogs.mockClear();
    close.mockClear();
    captureStop.mockClear();
    playbackStop.mockClear();
    startAudio.mockClear();
    bridgeConnect.mockClear();
    bridgeDisconnect.mockClear();
    clearAuthSession();
    useAuthStore.setState({ ready: false });
    post.mockResolvedValue({ data: { ticket: "ticket" } });
    startAudio.mockResolvedValue({
      context: { close, sampleRate: 48000, state: "running" },
      capture: { stop: captureStop },
      playback: { stop: playbackStop, bufferedMs: 0, enqueue() {} },
      micReady: true,
    });
  });

  afterEach(() => clearAuthSession());

  it("renders mapped Problem Details copy", async () => {
    signIn();
    get.mockResolvedValue({
      error: { code: "presentation.not_found", detail: "server detail", title: "Not found" },
    });
    renderPresent();
    expect(await screen.findByText("The presentation was not found.")).toBeTruthy();
  });

  it("renders an error when the detail request rejects", async () => {
    signIn();
    get.mockRejectedValue(new Error("offline"));
    renderPresent();
    expect(await screen.findByText("Unable to load presentation.")).toBeTruthy();
  });

  it("closes audio and stops capture after start when unmounted", async () => {
    signIn();
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
    signIn();
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

  it("does not reconnect for a refreshed session with the same user id", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));

    setAuthSession({
      accessToken: "refreshed-token",
      expiresAt: "2099-01-01T00:00:00Z",
      user: { ...user },
    });
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(bridgeDisconnect).not.toHaveBeenCalled();
    expect(captureStop).not.toHaveBeenCalled();
    expect(playbackStop).not.toHaveBeenCalled();
    expect(dispose).not.toHaveBeenCalled();
    expect(load).toHaveBeenCalledTimes(1);

    setAuthSession({
      accessToken: "different-user-token",
      expiresAt: "2099-01-01T00:00:00Z",
      user: { ...user, id: "usr_other" },
    });

    await vi.waitFor(() => expect(bridgeDisconnect).toHaveBeenCalled());
    expect(captureStop).toHaveBeenCalled();
    expect(playbackStop).toHaveBeenCalled();
    expect(dispose).toHaveBeenCalled();
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(2));
  });

  it("does not obtain a ticket while signed out and disables Start", async () => {
    useAuthStore.setState({ ready: true, user: null });
    renderPresent();

    expect(await screen.findByText("Please sign in to continue.")).toBeTruthy();
    expect(post).not.toHaveBeenCalled();
    expect(bridgeConnect).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Start" })).toHaveProperty("disabled", true);
  });

  it("disconnects, stops audio, and clears the deck when the user signs out", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());

    useAuthStore.setState({ user: null });

    await vi.waitFor(() => expect(bridgeDisconnect).toHaveBeenCalled());
    expect(captureStop).toHaveBeenCalled();
    expect(playbackStop).toHaveBeenCalled();
    expect(close).toHaveBeenCalled();
    expect(dispose).toHaveBeenCalled();
    expect(await screen.findByText("Please sign in to continue.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Start" })).toHaveProperty("disabled", true);
  });
});
