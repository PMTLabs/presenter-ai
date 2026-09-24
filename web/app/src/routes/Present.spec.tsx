import { StrictMode } from "react";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, setAuthSession, useAuthStore } from "@presenter/shared";
import { usePresenterStore } from "../store/presenterStore";

const { bridgeAskCancel, bridgeAskDone, bridgeAskExtend, bridgeAskStart, bridgeConnect, bridgeDisconnect, bridgeEnd, bridgeHandlers, bridgePause, bridgeResume, bridgeSetTrainerMode, bridgeStart, bridgeTakeOver, bridgeTrainTurn, captureStop, close, deckLogs, dispose, get, load, playbackFlush, playbackStop, post, startAudio } = vi.hoisted(() => ({
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
  bridgePause: vi.fn(),
  bridgeResume: vi.fn(),
  bridgeAskStart: vi.fn(),
  bridgeAskDone: vi.fn(),
  bridgeAskExtend: vi.fn(),
  bridgeAskCancel: vi.fn(),
  bridgeEnd: vi.fn(),
  bridgeSetTrainerMode: vi.fn(),
  bridgeTrainTurn: vi.fn(),
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
    pause() { bridgePause(); }
    resume() { bridgeResume(); }
    askStart() { bridgeAskStart(); }
    askDone() { bridgeAskDone(); }
    askExtend() { bridgeAskExtend(); }
    askCancel() { bridgeAskCancel(); }
    end() { bridgeEnd(); }
    setTrainerMode(...args: unknown[]) { bridgeSetTrainerMode(...args); }
    trainTurn(...args: unknown[]) { bridgeTrainTurn(...args); }
    sendAudio() {}
  },
}));
import { Present } from "./Present";
import { Toaster } from "../components/Toaster";
import { useToastStore } from "../store/toastStore";

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
      <Toaster />
    </MemoryRouter>,
  );
}

const notifications = () => screen.getByRole("region", { name: "Notifications" });

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
    bridgePause.mockClear();
    bridgeResume.mockClear();
    bridgeAskStart.mockClear();
    bridgeAskDone.mockClear();
    bridgeAskExtend.mockClear();
    bridgeAskCancel.mockClear();
    bridgeEnd.mockClear();
    bridgeSetTrainerMode.mockClear();
    bridgeTrainTurn.mockClear();
    bridgeHandlers.clear();
    clearAuthSession();
    window.history.replaceState({}, "", "/");
    useAuthStore.setState({ ready: false });
    for (const toast of useToastStore.getState().toasts) useToastStore.getState().dismiss(toast.id);
    usePresenterStore.setState({
      snapshot: { state: "idle", slideIndex: 0, slideCount: 0, muted: false },
      logs: [],
      limitWarning: null,
      upstreamStatus: null,
      suspended: false,
      endReason: null,
      usageConfirmed: null,
      estimatedSeconds: null,
      trainerMode: false,
      trainerAvailable: false,
      voiceTraining: true,
      scriptVersion: null,
      edits: {},
      editOrder: [],
      currentEditId: null,
      ask: null,
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
    // Requirement change (UI polish): load errors are error toasts (role=alert) instead of an inline banner.
    expect((await screen.findByText("The presentation was not found.")).closest('[role="alert"]')).not.toBeNull();
  });

  it("renders an error when the detail request rejects", async () => {
    signIn();
    get.mockRejectedValue(new Error("offline"));
    renderPresent();
    expect((await screen.findByText("Unable to load presentation.")).closest('[role="alert"]')).not.toBeNull();
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

  // Requirement change (UI polish): the limit warning is a top-center warning toast (role=status) instead of a banner.
  it("shows a countdown toast on limit_warning and clears it", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });

    vi.useFakeTimers();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("limit_warning", { type: "limit_warning", kind: "max_length", secondsLeft: 60 });
    expect(screen.getByRole("status").textContent).toContain("Time limit warning: 60s remaining");

    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(screen.getByRole("status").textContent).toContain("Time limit warning: 59s remaining");
    // A warning toast stays past the info auto-dismiss delay.
    act(() => {
      vi.advanceTimersByTime(6000);
    });
    expect(screen.getByRole("status").textContent).toContain("Time limit warning: 53s remaining");
    expect(useToastStore.getState().toasts).toHaveLength(1);

    emitBridge("limit_warning", { type: "limit_warning", kind: "max_length", secondsLeft: null });
    expect(notifications().textContent).not.toContain("Time limit warning");

    emitBridge("limit_warning", { type: "limit_warning", kind: "idle", secondsLeft: 45 });
    expect(screen.getByRole("status").textContent).toContain("Inactivity warning: 45s remaining");

    emitBridge("closed", { type: "closed", endReason: "idle" });
    expect(notifications().textContent).not.toContain("Inactivity warning");
    expect(screen.queryByRole("alert")).toBeNull();
    vi.useRealTimers();
  });

  it("a closed limit warning toast does not reopen on the next countdown tick", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });

    vi.useFakeTimers();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("limit_warning", { type: "limit_warning", kind: "max_length", secondsLeft: 60 });
    fireEvent.click(screen.getByRole("button", { name: "Dismiss notification" }));
    act(() => {
      vi.advanceTimersByTime(3000);
    });
    expect(notifications().textContent).not.toContain("Time limit warning");

    // A new warning is a new notification.
    emitBridge("limit_warning", { type: "limit_warning", kind: "idle", secondsLeft: 30 });
    expect(notifications().textContent).toContain("Inactivity warning: 30s remaining");
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
    expect(screen.getByRole("option", { name: "Default (server limit)" })).toBeTruthy();
    expect(screen.getByText("Server ceiling always applies.")).toBeTruthy();

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
    expect(screen.getByRole("option", { name: "Default (script 45 min)" })).toBeTruthy();
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
    // Requirement change (UI polish): a top-center info toast (role=status) instead of the inline status banner.
    const ended = await screen.findByText("Talk ended: Maximum talk length reached");
    expect(notifications().contains(ended)).toBe(true);
    expect(ended.closest('[role="status"]')).not.toBeNull();
    expect(useToastStore.getState().toasts.map((toast) => toast.kind)).toEqual(["info"]);
  });

  it("shows updating then updated with version and summary", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    emitBridge("script_version", {
      type: "script_version",
      presentationId: "demo",
      version: 6,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: true,
    });

    emitBridge("script_edit", {
      type: "script_edit",
      id: "edit_4",
      status: "queued",
      slideIndexes: [3],
      version: null,
      summary: null,
      error: null,
    });
    expect(screen.getByRole("status").textContent).toBe("Updating…");

    emitBridge("script_edit", {
      type: "script_edit",
      id: "edit_4",
      status: "processing",
      slideIndexes: [3],
      version: null,
      summary: null,
      error: null,
    });
    expect(screen.getByRole("status").textContent).toBe("Updating…");

    emitBridge("script_edit", {
      type: "script_edit",
      id: "edit_4",
      status: "applied",
      slideIndexes: [3],
      version: 7,
      summary: "Added the 2025 figures",
      error: null,
    });
    expect(screen.getByRole("status").textContent).toBe("Updated — v7: Added the 2025 figures");
  });

  it("shows failure reason", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    emitBridge("script_version", {
      type: "script_version",
      presentationId: "demo",
      version: 6,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: true,
    });

    emitBridge("script_edit", {
      type: "script_edit",
      id: "edit_5",
      status: "queued",
      slideIndexes: [1],
      version: null,
      summary: null,
      error: null,
    });
    emitBridge("script_edit", {
      type: "script_edit",
      id: "edit_5",
      status: "failed",
      slideIndexes: [1],
      version: null,
      summary: null,
      error: "timeout",
    });
    expect(screen.getByRole("status").textContent).toBe("Couldn't update — timed out");
  });

  it("trainer switch shows only server state: an idle toggle turns it on, End turns it off", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    // On connect, before any talk, the server reports availability and the idle value.
    emitBridge("trainer_state", { type: "trainer_state", trainerMode: false, trainerAvailable: true, voiceTraining: true });
    // Requirement change (UI polish): the checkbox became an accessible switch.
    const toggle = () => screen.getByRole("switch", { name: "Trainer mode" });
    const checked = () => toggle().getAttribute("aria-checked");
    expect(checked()).toBe("false");

    fireEvent.click(toggle());
    expect(bridgeSetTrainerMode).toHaveBeenCalledWith(true);
    emitBridge("trainer_state", { type: "trainer_state", trainerMode: true, trainerAvailable: true, voiceTraining: true });
    expect(checked()).toBe("true");

    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("script_version", {
      type: "script_version",
      presentationId: "demo",
      version: 1,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: true,
    });
    expect(checked()).toBe("true");

    emitBridge("closed", { type: "closed", endReason: "user" });
    emitBridge("trainer_state", { type: "trainer_state", trainerMode: false, trainerAvailable: true, voiceTraining: true });
    emitBridge("state", { state: "idle", slideIndex: 0, slideCount: 1, muted: false });
    expect(checked()).toBe("false");
    fireEvent.click(toggle());
    expect(bridgeSetTrainerMode).toHaveBeenLastCalledWith(true);
  });

  it("Space on the trainer switch does not pause, and Escape closes its tooltip without ending the talk", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("trainer_state", { type: "trainer_state", trainerMode: false, trainerAvailable: true, voiceTraining: true });

    const toggle = screen.getByRole("switch", { name: "Trainer mode" });
    act(() => toggle.focus());
    fireEvent.keyDown(toggle, { key: " " });
    expect(bridgePause).not.toHaveBeenCalled();

    const info = screen.getByRole("button", { name: "About Trainer mode" });
    act(() => info.focus());
    expect(screen.getByRole("tooltip").textContent).toContain("does not retrain the AI");
    fireEvent.keyDown(info, { key: "Escape" });
    expect(screen.queryByRole("tooltip")).toBeNull();
    expect(bridgeEnd).not.toHaveBeenCalled();

    // Without the tooltip open, Escape keeps its page meaning.
    fireEvent.keyDown(document.body, { key: "Escape" });
    expect(bridgeEnd).toHaveBeenCalledOnce();
  });

  it("puts the breadcrumb and the info bar in one row above the deck", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    emitBridge("trainer_state", { type: "trainer_state", trainerMode: false, trainerAvailable: true, voiceTraining: true });

    const bar = screen.getByTestId("info-bar");
    const row = bar.parentElement!;
    expect(row.contains(screen.getByRole("link", { name: "← Library" }))).toBe(true);
    expect(bar.contains(screen.getByRole("switch", { name: "Trainer mode" }))).toBe(true);
    expect(bar.textContent).toContain("buf 0 ms");
    expect(bar.className).toContain("ml-auto");
    // The row sits in the deck panel, before the deck; the controls stay below it.
    const deckPanel = document.querySelector('[data-panel][id="deck"]')!;
    expect(deckPanel.contains(row)).toBe(true);
    const deck = screen.getByTitle("Presentation deck");
    expect(row.compareDocumentPosition(deck) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(
      deck.compareDocumentPosition(screen.getByRole("button", { name: "Pause" })) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it("shows voice training unavailable on a client-mode connection", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    expect(screen.queryByText("Voice training is unavailable on this connection — use Train on this")).toBeNull();

    emitBridge("script_version", {
      type: "script_version",
      presentationId: "demo",
      version: 6,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: false,
    });

    expect(
      await screen.findByText("Voice training is unavailable on this connection — use Train on this"),
    ).toBeTruthy();
  });

  it("train on this sends an overlong multi-turn question within the bridge's limits", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    await screen.findByRole("button", { name: "Start" });
    emitBridge("script_version", {
      type: "script_version",
      presentationId: "demo",
      version: 6,
      trainerMode: true,
      trainerAvailable: true,
      voiceTraining: true,
    });
    act(() => usePresenterStore.setState({ slide: 0, transcript: [] }));
    const leadIn = Array.from({ length: 150 }, (_, i) => `background${i}`).join(" ");
    const actualQuestion = "How much does the new coating cost?";
    // Three user turns (gaps over 1 s) totalling well over 2,000 characters, then the answer.
    emitBridge("transcript", { type: "transcript", role: "user", delta: leadIn, end_ms: 1000 });
    emitBridge("transcript", { type: "transcript", role: "user", delta: leadIn, end_ms: 5000 });
    emitBridge("transcript", { type: "transcript", role: "user", delta: actualQuestion, end_ms: 9000 });
    emitBridge("transcript", { type: "transcript", role: "assistant", delta: "It costs 10% more.", end_ms: 12000 });

    fireEvent.click(screen.getByRole("button", { name: "Train on this" }));

    expect(bridgeTrainTurn).toHaveBeenCalledTimes(1);
    const [question, answer, slideIndex] = bridgeTrainTurn.mock.calls[0] as [string, string, number];
    expect(question.length).toBeLessThanOrEqual(2000);
    expect(question.startsWith("…")).toBe(true);
    expect(question.endsWith(` ${actualQuestion}`)).toBe(true);
    expect(answer).toBe("It costs 10% more.");
    expect(slideIndex).toBe(0);
  });

  it("A starts an ask and A or Enter finishes it", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });

    // A starts an ask
    fireEvent.keyDown(document.body, { key: "a" });
    expect(bridgeAskStart).toHaveBeenCalledOnce();

    // Answered by listening frame
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 1000,
      quietRemainingMs: 89000,
      speechRemainingMs: 24000,
      heard: false,
      transcribing: false,
      reason: null,
    });

    // A finishes it
    fireEvent.keyDown(document.body, { key: "a" });
    expect(bridgeAskDone).toHaveBeenCalledTimes(1);

    // Enter also finishes it
    fireEvent.keyDown(document.body, { key: "Enter" });
    expect(bridgeAskDone).toHaveBeenCalledTimes(2);
  });

  it("holding A does not finish the ask it started", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });

    // Initial press (repeat: false)
    fireEvent.keyDown(document.body, { key: "a", repeat: false });
    expect(bridgeAskStart).toHaveBeenCalledOnce();

    // Now listening
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 100,
      quietRemainingMs: 89900,
      speechRemainingMs: 25000,
      heard: false,
      transcribing: false,
      reason: null,
    });

    // Key repeat event from holding down A
    fireEvent.keyDown(document.body, { key: "a", repeat: true });
    expect(bridgeAskDone).not.toHaveBeenCalled();
  });

  it("Ctrl+A does not ask", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });

    fireEvent.keyDown(document.body, { key: "a", ctrlKey: true });
    fireEvent.keyDown(document.body, { key: "a", metaKey: true });
    expect(bridgeAskStart).not.toHaveBeenCalled();
  });

  it("A in the length select does not ask", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();

    const select = await screen.findByRole("combobox", { name: "Length" });
    act(() => select.focus());
    fireEvent.keyDown(select, { key: "a" });
    expect(bridgeAskStart).not.toHaveBeenCalled();
  });

  it("A while muted shows the unmute warning and sends no frame", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: true });

    fireEvent.keyDown(document.body, { key: "a" });
    expect(bridgeAskStart).not.toHaveBeenCalled();
    expect(notifications().textContent).toContain("Unmute the microphone to ask a question.");
  });

  it("clicking the disabled Ask button while muted sends nothing", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: true });

    const askBtn = screen.getByRole("button", { name: "Ask" });
    expect(askBtn).toHaveProperty("disabled", true);
    expect(screen.getByText("Unmute to ask")).toBeTruthy();

    fireEvent.click(askBtn);
    expect(bridgeAskStart).not.toHaveBeenCalled();
  });

  it("Space is ignored while listening and Escape still ends", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 5000,
      quietRemainingMs: 85000,
      speechRemainingMs: 20000,
      heard: false,
      transcribing: false,
      reason: null,
    });

    fireEvent.keyDown(document.body, { key: " " });
    expect(bridgePause).not.toHaveBeenCalled();
    expect(bridgeResume).not.toHaveBeenCalled();

    fireEvent.keyDown(document.body, { key: "Escape" });
    expect(bridgeEnd).toHaveBeenCalledOnce();
  });

  it("Enter on a focused End button while listening sends ask_done and not end", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 5000,
      quietRemainingMs: 85000,
      speechRemainingMs: 20000,
      heard: false,
      transcribing: false,
      reason: null,
    });

    const endBtn = screen.getByRole("button", { name: "End" });
    act(() => endBtn.focus());
    fireEvent.keyDown(endBtn, { key: "Enter" });
    expect(bridgeAskDone).toHaveBeenCalledOnce();
    expect(bridgeEnd).not.toHaveBeenCalled();
  });

  it("pressing A answered by refused_not_live shows its toast", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });

    fireEvent.keyDown(document.body, { key: "a" });
    expect(bridgeAskStart).toHaveBeenCalledOnce();

    emitBridge("ask_state", {
      type: "ask_state",
      state: "off",
      elapsedMs: 0,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: false,
      transcribing: false,
      reason: "refused_not_live",
    });

    expect(notifications().textContent).toContain("Ask isn't available right now.");
  });

  it("Ask done answered by send_failed shows the reset toast", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 15000,
      quietRemainingMs: 75000,
      speechRemainingMs: 15000,
      heard: true,
      transcribing: false,
      reason: null,
    });

    fireEvent.click(screen.getByRole("button", { name: "Ask done" }));
    expect(bridgeAskDone).toHaveBeenCalledOnce();

    emitBridge("ask_state", {
      type: "ask_state",
      state: "off",
      elapsedMs: 15100,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "send_failed",
    });

    expect(notifications().textContent).toContain(
      "Couldn't send your question — the connection was reset. Press Ask to try again.",
    );
  });

  it("Resume while listening answered by off resumed shows Ask cancelled", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("ask_state", {
      type: "ask_state",
      state: "listening",
      elapsedMs: 10000,
      quietRemainingMs: 80000,
      speechRemainingMs: 18000,
      heard: true,
      transcribing: false,
      reason: null,
    });

    fireEvent.click(screen.getByRole("button", { name: "Resume" }));
    expect(bridgeResume).toHaveBeenCalledOnce();

    emitBridge("ask_state", {
      type: "ask_state",
      state: "off",
      elapsedMs: 10050,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "resumed",
    });

    expect(notifications().textContent).toContain("Ask cancelled.");
  });

  it("quiet_cancelled and limit_sent frames show their toasts", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "paused", slideIndex: 0, slideCount: 1, muted: false });

    emitBridge("ask_state", {
      type: "ask_state",
      state: "off",
      elapsedMs: 90000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: false,
      transcribing: false,
      reason: "quiet_cancelled",
    });
    expect(notifications().textContent).toContain(
      "Nothing was heard for 90 s — Ask cancelled; still paused.",
    );

    emitBridge("ask_state", {
      type: "ask_state",
      state: "answering",
      elapsedMs: 30000,
      quietRemainingMs: null,
      speechRemainingMs: 0,
      heard: true,
      transcribing: false,
      reason: "limit_sent",
    });
    expect(notifications().textContent).toContain(
      "25-second speech limit reached — your question was sent.",
    );
  });

  it("Continue during check-in sends resume and the off continued frame clears the control", async () => {
    signIn();
    get.mockResolvedValue({
      data: { id: "demo", meta: { deck: "demo.html", driver: "sections" } },
    });
    renderPresent();
    emitBridge("state", { state: "presenting", slideIndex: 0, slideCount: 1, muted: false });
    emitBridge("ask_state", {
      type: "ask_state",
      state: "answering",
      elapsedMs: 35000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "sent",
    });

    const continueBtn = screen.getByRole("button", { name: "Continue" });
    expect(continueBtn).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain("Answering…");

    fireEvent.click(continueBtn);
    expect(bridgeResume).toHaveBeenCalledOnce();

    emitBridge("ask_state", {
      type: "ask_state",
      state: "off",
      elapsedMs: 36000,
      quietRemainingMs: null,
      speechRemainingMs: null,
      heard: true,
      transcribing: false,
      reason: "continued",
    });

    expect(screen.queryByRole("button", { name: "Continue" })).toBeNull();
    expect(screen.getByRole("button", { name: "Ask" })).toBeTruthy();
  });
});
