import { StrictMode } from "react";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, setAuthSession, useAuthStore } from "@presenter/shared";
import { usePresenterStore } from "../store/presenterStore";

const { bridgeConnect, bridgeDisconnect, bridgeHandlers, bridgeStart, bridgeTakeOver, captureStop, close, deckLogs, dispose, get, load, playbackFlush, playbackStop, post, startAudio } = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  load: vi.fn().mockResolvedValue({ adapter: "sections", count: 1 }),
  dispose: vi.fn(),
  close: vi.fn().mockResolvedValue(undefined),
  captureStop: vi.fn(),
  deckLogs: vi.fn(),
  playbackStop: vi.fn(),
  playbackFlush: vi.fn(),
  startAudio: vi.fn(),
  bridgeConnect: vi.fn(),
  bridgeDisconnect: vi.fn(),
  bridgeStart: vi.fn(),
  bridgeTakeOver: vi.fn(),
  bridgeHandlers: new Map<string, ((...args: unknown[]) => void)[]>(),
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
    on(event: string, handler: (...args: unknown[]) => void) {
      bridgeHandlers.set(event, [...(bridgeHandlers.get(event) ?? []), handler]);
    }
    connect() {
      bridgeConnect();
      void this.ticketProvider();
    }
    disconnect() { bridgeDisconnect(); }
    start(...args: unknown[]) { bridgeStart(...args); }
    takeOver() { bridgeTakeOver(); }
    sendAudio() {}
  },
}));
import { Present } from "./Present";

const user = { id: "usr_test", email: "test@example.invalid", displayName: null, role: "user" };

function signIn() {
  useAuthStore.setState({ ready: true, user });
}

function emitBridge(event: string, ...args: unknown[]) {
  act(() => bridgeHandlers.get(event)?.forEach((handler) => handler(...args)));
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
    playbackFlush.mockClear();
    startAudio.mockClear();
    bridgeConnect.mockClear();
    bridgeDisconnect.mockClear();
    bridgeStart.mockClear();
    bridgeTakeOver.mockClear();
    bridgeHandlers.clear();
    clearAuthSession();
    window.history.replaceState({}, "", "/");
    useAuthStore.setState({ ready: false });
    usePresenterStore.setState({
      snapshot: { state: "idle", slideIndex: 0, slideCount: 0, muted: false },
      logs: [],
      limitWarning: null,
      upstreamStatus: null,
      suspended: false,
      endReason: null,
      usageConfirmed: null,
      estimatedSeconds: null,
    });
    post.mockResolvedValue({ data: { ticket: "ticket" } });
    startAudio.mockResolvedValue({
      context: { close, sampleRate: 48000, state: "running" },
      capture: { stop: captureStop },
      playback: { stop: playbackStop, flush: playbackFlush, bufferedMs: 0, enqueue() {} },
      micReady: true,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    clearAuthSession();
  });

  it("flushes playback once and logs when capture reports barge-in", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());

    act(() => startAudio.mock.calls[0][0].onBargeIn());
    expect(playbackFlush).toHaveBeenCalledOnce();
    expect(usePresenterStore.getState().logs.at(-1)?.message).toBe(
      "barge-in: playback flushed",
    );
  });

  it("flushes queued playback when the server sends flush", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(bridgeStart).toHaveBeenCalledOnce());
    emitBridge("flush");
    expect(playbackFlush).toHaveBeenCalledOnce();
  });

  it("passes echoGate=off to startAudio", async () => {
    window.history.pushState({}, "", "/present/demo?echoGate=off");
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());
    expect(startAudio.mock.calls[0][0].echoGate).toBe(false);
  });

  it("shows only the newest server warning while paused", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("log", { type: "log", level: "warn", message: "first warning" });
    emitBridge("log", { type: "log", level: "warn", message: "newest warning" });
    expect((await screen.findByRole("alert")).textContent).toContain("newest warning");

    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("shows no banner for a manual pause after an earlier warning, and the stall warning after its pause", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    const serverLog = (level: string, message: string) => emitBridge("log", { type: "log", level, message });
    serverLog("warn", "no output audio 15000 ms after slide 1 was sent; nudging the model");
    serverLog("info", "state → paused");
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    expect(screen.queryByRole("alert")).toBeNull();

    serverLog("info", "state → presenting");
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    serverLog("info", "state → paused");
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    serverLog("warn", "The model stopped responding on slide 1 — Resume or End.");
    expect((await screen.findByRole("alert")).textContent).toContain("stopped responding on slide 1");
  });

  it("never promotes the page-local connection closed line to the paused banner", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("close");
    expect(screen.queryByRole("alert")).toBeNull();
  });

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

  it("shows Take over only when the busy slot belongs to this user and calls takeOver", async () => {
    signIn();
    get.mockResolvedValue({ data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } } });
    renderPresent();
    emitBridge("busy", { type: "error", code: "busy", canTakeOver: true });
    fireEvent.click(await screen.findByRole("button", { name: "Take over" }));
    expect(bridgeTakeOver).toHaveBeenCalledOnce();
  });

  it("shows the other-account notice without a Take over button", async () => {
    signIn();
    get.mockResolvedValue({ data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } } });
    renderPresent();
    emitBridge("busy", { type: "error", code: "busy", canTakeOver: false });
    expect(await screen.findByText("The presenter is in use by another account.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Take over" })).toBeNull();
  });

  it("taken-over stops audio and keeps Start disabled", async () => {
    signIn();
    get.mockResolvedValue({ data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } } });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());
    emitBridge("taken-over");
    expect(captureStop).toHaveBeenCalledOnce();
    expect(playbackStop).toHaveBeenCalledOnce();
    expect(screen.getByText("This presenter was taken over by another tab. Reload to use it here.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Start" })).toHaveProperty("disabled", true);
  });

  it("Start sends no fromIndex", async () => {
    signIn();
    get.mockResolvedValue({ data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } } });
    renderPresent();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(bridgeStart).toHaveBeenCalledWith("demo"));
    expect(bridgeStart.mock.calls[0]).toEqual(["demo"]);
  });

  it("keeps the busy notice through the close until a server state frame is accepted", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await vi.waitFor(() => expect(bridgeConnect).toHaveBeenCalled());
    const notice = "The presenter is in use in another tab.";

    emitBridge("busy", { type: "error", code: "busy" });
    // A busy close makes the client report a synthetic idle state; that must not clear the notice.
    emitBridge("state", { state: "idle", slideIndex: 0, slideCount: 0, muted: false });
    emitBridge("close");
    expect(screen.getByText(notice)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Start" })).toHaveProperty("disabled", true);

    emitBridge("state", { state: "idle", slideIndex: 0, slideCount: 0, muted: false });
    emitBridge("accepted");
    expect(screen.queryByText(notice)).toBeNull();
    expect(screen.getByRole("button", { name: "Start" })).toHaveProperty("disabled", false);
  });

  it("shows a countdown banner on limit_warning and clears it", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });

    vi.useFakeTimers();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("limit_warning", { type: "limit_warning", kind: "max_length", secondsLeft: 60 });
    expect(screen.getByRole("alert").textContent).toContain("Time limit warning: 60s remaining");

    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(screen.getByRole("alert").textContent).toContain("Time limit warning: 59s remaining");

    emitBridge("limit_warning", { type: "limit_warning", kind: "max_length", secondsLeft: null });
    expect(screen.queryByRole("alert")).toBeNull();

    emitBridge("limit_warning", { type: "limit_warning", kind: "idle", secondsLeft: 45 });
    expect(screen.getByRole("alert").textContent).toContain("Inactivity warning: 45s remaining");

    emitBridge("closed", { type: "closed", endReason: "idle" });
    expect(screen.queryByRole("alert")).toBeNull();
    vi.useRealTimers();
  });

  it("shows the disconnected banner while suspended", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false, suspended: true });
    expect((await screen.findByRole("alert")).textContent).toContain(
      "Paused — disconnected to save cost; Resume reconnects",
    );

    emitBridge("upstream", { type: "upstream", status: "reconnecting" });
    expect((await screen.findByRole("alert")).textContent).toContain("Reconnecting…");

    emitBridge("upstream", { type: "upstream", status: "live" });
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("length picker sends the chosen override", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    const select = await screen.findByRole("combobox", { name: "Length" });
    expect(select).toBeTruthy();
    expect(screen.getByRole("option", { name: "Default (60 min)" })).toBeTruthy();

    fireEvent.change(select, { target: { value: "15" } });
    fireEvent.click(screen.getByRole("button", { name: "Start" }));
    await vi.waitFor(() => expect(startAudio).toHaveBeenCalledOnce());
    expect(bridgeStart).toHaveBeenCalledWith("demo", undefined, 15);
  });

  it("length picker uses presentation maxMinutes as default", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections", maxMinutes: 45 } },
    });
    renderPresent();
    const select = await screen.findByRole("combobox", { name: "Length" });
    expect(select).toBeTruthy();
    expect(screen.getByRole("option", { name: "Default (45 min)" })).toBeTruthy();
  });

  it("hides length picker while presenting", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    expect(await screen.findByRole("combobox", { name: "Length" })).toBeTruthy();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    expect(screen.queryByRole("combobox", { name: "Length" })).toBeNull();
  });

  it("shows the end reason in plain words when a talk closes", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("closed", {
      type: "closed",
      endReason: "max_length",
      usageConfirmed: true,
      estimatedSeconds: 3600,
    });
    emitBridge("state", { state: "idle", slideIndex: 0, slideCount: 1, muted: false });
    expect(await screen.findByText("Talk ended: Maximum talk length reached")).toBeTruthy();
  });
});
