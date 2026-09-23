import { useCallback, useEffect, useRef, useState } from "react";
import { Group, Panel, Separator, useDefaultLayout } from "react-resizable-panels";
import { Link, useParams } from "react-router-dom";
import apiClient, { type components } from "@presenter/shared/api";
import { errorMessages, isProblem, useAuthStore } from "@presenter/shared";
import { BridgeClient } from "../ws/bridgeClient";
import { DeckDriver } from "../deck/deckDriver";
import { startAudio, type StartedAudio } from "../audio/capture";
import { usePresenterStore } from "../store/presenterStore";
import { Transcript } from "../components/Transcript";
import { SlidePill } from "../components/SlidePill";
import { UsagePill } from "../components/UsagePill";
import { LogPanel } from "../components/LogPanel";
type Detail = components["schemas"]["PresentationDetail"];

const startButtonClassName = [
  "rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-medium text-white",
  "hover:bg-blue-500 disabled:cursor-not-allowed disabled:opacity-40",
].join(" ");
const actionButtonClassName = [
  "rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-900",
  "hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-40",
  "dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100 dark:hover:bg-gray-700",
].join(" ");
const endButtonClassName = [
  "rounded-lg bg-red-600 px-3 py-1.5 text-sm font-medium text-white",
  "hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-40",
].join(" ");
const presenterStorage = {
  getItem(key: string) {
    try {
      const storageKey = key.replace("react-resizable-panels:", "");
      return window.localStorage.getItem(storageKey);
    } catch {
      return null;
    }
  },
  setItem(key: string, value: string) {
    try {
      const storageKey = key.replace("react-resizable-panels:", "");
      window.localStorage.setItem(storageKey, value);
    } catch {
      // Private browsing can reject localStorage writes; use the in-memory layout instead.
    }
  },
};

function useDesktopLayout() {
  const query = "(min-width: 1024px)";
  const [isDesktop, setIsDesktop] = useState(() =>
    typeof window !== "undefined" && typeof window.matchMedia === "function"
      ? window.matchMedia(query).matches
      : false,
  );

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const mediaQuery = window.matchMedia(query);
    const update = () => setIsDesktop(mediaQuery.matches);
    update();
    mediaQuery.addEventListener("change", update);
    return () => mediaQuery.removeEventListener("change", update);
  }, []);

  return isDesktop;
}

export function Present() {
  const { id = "" } = useParams();
  const frame = useRef<HTMLIFrameElement>(null);
  const client = useRef<BridgeClient | null>(null);
  const driver = useRef<DeckDriver | null>(null);
  const audio = useRef<StartedAudio | null>(null);
  const audioAbort = useRef<AbortController | null>(null);
  const [presentation, setPresentation] = useState<Detail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyMessage, setBusyMessage] = useState<string | null>(null);
  const ready = useAuthStore((state) => state.ready);
  const userId = useAuthStore((state) => state.user?.id ?? null);
  const snapshot = usePresenterStore((state) => state.snapshot);
  const bufferedMs = usePresenterStore((state) => state.bufferedMs);
  const transcript = usePresenterStore((state) => state.transcript);
  const logs = usePresenterStore((state) => state.logs);
  const applySnapshot = usePresenterStore((state) => state.applySnapshot);
  const message = usePresenterStore((state) => state.message);
  const log = usePresenterStore((state) => state.log);
  const setMicReady = usePresenterStore((state) => state.setMicReady);
  const setBuffered = usePresenterStore((state) => state.setBuffered);
  const stopAudio = useCallback(() => {
    audioAbort.current?.abort();
    audioAbort.current = null;
    audio.current?.capture.stop();
    audio.current?.playback.stop();
    void audio.current?.context.close();
    audio.current = null;
    setMicReady(false);
  }, [setMicReady]);
  useEffect(() => {
    if (!ready || !userId) {
      client.current?.disconnect();
      client.current = null;
      stopAudio();
      driver.current?.dispose();
      driver.current = null;
      return;
    }
    const bridge = new BridgeClient(undefined, undefined, async () => {
      const { data, error: requestError } = await apiClient.POST("/v1/sessions/ticket");
      if (requestError || !data) throw new Error("Unable to obtain a session ticket.");
      return data.ticket;
    });
    client.current = bridge;
    bridge.on("state", (serverSnapshot) => {
      applySnapshot(serverSnapshot);
      setBusyMessage(null);
    });
    bridge.on("slide", (index) => {
      driver.current?.goto(index);
      message({ type: "slide", index });
    });
    for (const event of [
      "transcript",
      "usage",
      "log",
      "error",
      "closed",
    ] as const)
      bridge.on(event, (eventMessage) => {
        message(eventMessage);
        if (event === "closed") stopAudio();
      });
    bridge.on("audio", (buffer) => audio.current?.playback.enqueue(buffer));
    bridge.on("open", () => log("info", "connected to server"));
    bridge.on("close", () => log("warn", "server connection closed"));
    bridge.on("busy", () => {
      setBusyMessage((current) => current ?? "The presenter is in use in another tab.");
    });
    bridge.connect();
    return () => {
      bridge.disconnect();
      stopAudio();
      driver.current?.dispose();
      driver.current = null;
    };
  }, [applySnapshot, log, message, ready, stopAudio, userId]);
  useEffect(() => {
    if (!ready || !userId || !id) {
      setPresentation(null);
      setError(null);
      driver.current?.dispose();
      driver.current = null;
      return;
    }
    let active = true;
    setError(null);
    void apiClient
      .GET("/v1/presentations/{id}", { params: { path: { id } } })
      .then(({ data, error: requestError }) => {
        if (!active) return;
        if (requestError) {
          const problem: unknown = requestError;
          setError(
            isProblem(problem)
              ? (errorMessages[problem.code] ?? problem.detail ?? problem.title)
              : "Unable to load presentation.",
          );
          return;
        }
        const detail: Detail | undefined = data;
        if (!detail || !frame.current) return;
        setPresentation(detail);
        driver.current?.dispose();
        const deck = new DeckDriver(frame.current, {
          log,
          onExternalNavigate: (index) => {
            if (
              ["presenting", "paused"].includes(
                client.current?.snapshot.state ?? "",
              )
            )
              client.current?.goto(index);
          },
        });
        driver.current = deck;
        void deck
          .load(`/${detail.meta.deck.replace(/^\/+/, "")}`, {
            driver: detail.meta.driver ?? "auto",
          })
          .then(() => {
            if (!active) deck.dispose();
          });
      })
      .catch(() => {
        if (active) setError("Unable to load presentation.");
      });
    return () => {
      active = false;
      driver.current?.dispose();
      driver.current = null;
    };
  }, [id, log, ready, userId]);
  const begin = async () => {
    if (!ready || !userId || !presentation || snapshot.state !== "idle") return;
    stopAudio();
    const controller = new AbortController();
    audioAbort.current = controller;
    const started = await startAudio({
      onFrame: (buffer) => client.current?.sendAudio(buffer),
      onBuffered: setBuffered,
      onMicReady: setMicReady,
      signal: controller.signal,
    });
    if (controller.signal.aborted) {
      started.capture.stop();
      started.playback.stop();
      void started.context.close();
      return;
    }
    audio.current = started;
    client.current?.start(presentation.id, 0);
  };
  useEffect(() => {
    const keys = (e: KeyboardEvent) => {
      if (
        ["INPUT", "SELECT", "TEXTAREA"].includes(
          (document.activeElement as HTMLElement)?.tagName,
        )
      )
        return;
      const live = ["presenting", "paused"].includes(snapshot.state);
      if (e.key === " " && live) {
        e.preventDefault();
        if (snapshot.state === "paused") client.current?.resume();
        else client.current?.pause();
      } else if ((e.key === "ArrowRight" || e.key === "PageDown") && live) {
        e.preventDefault();
        client.current?.next();
      } else if ((e.key === "ArrowLeft" || e.key === "PageUp") && live) {
        e.preventDefault();
        client.current?.prev();
      } else if (/^m$/i.test(e.key) && live) {
        if (snapshot.muted) client.current?.unmute();
        else client.current?.mute();
      } else if (/^s$/i.test(e.key) && snapshot.state === "idle") void begin();
      else if (e.key === "Escape" && snapshot.state !== "idle")
        client.current?.end();
    };
    window.addEventListener("keydown", keys);
    return () => window.removeEventListener("keydown", keys);
  }, [snapshot, presentation]);
  useEffect(() => {
    (window as Window & { __presenterDebug?: () => unknown }).__presenterDebug =
      () => ({
        snapshot: client.current?.snapshot,
        presentation: presentation?.id ?? null,
        deckAdapter: driver.current?.adapterName,
        deckCount: driver.current?.count() ?? 0,
        deckIndex: driver.current?.currentIndex ?? 0,
        audio: audio.current
          ? {
              sampleRate: audio.current.context.sampleRate,
              contextState: audio.current.context.state,
              micReady: audio.current.micReady,
              bufferedMs: audio.current.playback.bufferedMs,
            }
          : null,
        transcriptTurns: transcript.length,
        transcriptText: transcript
          .map((x) => x.text)
          .join("")
          .slice(-400),
        logTail: logs.slice(-8),
        wsOpen: snapshot.state !== "idle",
      });
  }, [presentation, snapshot, transcript, logs]);
  const live = snapshot.state === "presenting" || snapshot.state === "paused";
  const isDesktop = useDesktopLayout();
  const { defaultLayout, onLayoutChanged } = useDefaultLayout({
    id: "presenter-split",
    storage: presenterStorage,
    onlySaveAfterUserInteractions: true,
  });
  return (
    <section
      className={[
        "flex h-[calc(100vh-4rem)] min-h-0 min-w-0 flex-col overflow-hidden px-4",
        "text-gray-900 dark:text-gray-100",
      ].join(" ")}
    >
      <Link className="flex-none text-sm text-blue-600" to="/">
        ← Library
      </Link>
      {ready && !userId && (
        <p className="mt-4 flex-none text-gray-600 dark:text-gray-400">
          Please sign in to continue. <Link className="text-blue-600 hover:underline" to="/login">Sign in</Link>
        </p>
      )}
      {busyMessage && (
        <p className="mt-4 flex-none rounded-lg bg-amber-50 p-4 text-amber-800 dark:bg-amber-950 dark:text-amber-200">
          {busyMessage}
        </p>
      )}
      {error && (
        <p className="mt-4 flex-none rounded-lg bg-red-50 p-4 text-red-700 dark:bg-red-950 dark:text-red-200">
          {error}
        </p>
      )}
      <div className="mt-3 flex flex-none flex-wrap gap-2">
        <span className="rounded-full bg-blue-100 px-3 py-1 text-xs text-blue-900 dark:bg-blue-900 dark:text-blue-100">
          {snapshot.state}
        </span>
        <SlidePill />
        <UsagePill />
        <span className="rounded-full bg-gray-200 px-3 py-1 text-xs text-gray-900 dark:bg-gray-800 dark:text-gray-100">
          buf {bufferedMs} ms
        </span>
      </div>
      <Group
        orientation={isDesktop ? "horizontal" : "vertical"}
        id="presenter-split"
        defaultLayout={defaultLayout}
        onLayoutChanged={onLayoutChanged}
        className="mt-4 min-h-0 min-w-0 flex-1 overflow-hidden"
      >
        <Panel
          id="deck"
          defaultSize="70%"
          minSize={isDesktop ? 480 : "35%"}
          className="min-h-0 min-w-0 overflow-hidden"
        >
          <div
            className={[
              "flex h-full min-h-0 min-w-0 flex-col bg-gray-100",
              "text-gray-900 dark:bg-gray-950 dark:text-gray-100",
            ].join(" ")}
          >
            <div className="min-h-0 min-w-0 flex-1 p-1">
              <iframe
                ref={frame}
                title="Presentation deck"
                className={[
                  "h-full w-full rounded-xl border border-gray-200 bg-white text-gray-900",
                  "dark:border-gray-800 dark:bg-gray-900 dark:text-gray-100",
                ].join(" ")}
              />
            </div>
            <div className="flex flex-none flex-wrap gap-2 p-3">
              <button
                className={startButtonClassName}
                onClick={() => void begin()}
                disabled={!ready || !userId || !presentation || snapshot.state !== "idle"}
              >
                Start
              </button>
              <button
                className={actionButtonClassName}
                onClick={() =>
                  snapshot.state === "paused"
                    ? client.current?.resume()
                    : client.current?.pause()
                }
                disabled={!live}
              >
                {snapshot.state === "paused" ? "Resume" : "Pause"}
              </button>
              <button
                className={actionButtonClassName}
                onClick={() => client.current?.prev()}
                disabled={!live}
              >
                Prev
              </button>
              <button
                className={actionButtonClassName}
                onClick={() => client.current?.next()}
                disabled={!live}
              >
                Next
              </button>
              <button
                className={actionButtonClassName}
                onClick={() => {
                  if (snapshot.muted) client.current?.unmute();
                  else client.current?.mute();
                }}
                disabled={!live}
              >
                {snapshot.muted ? "Unmute" : "Mute"}
              </button>
              <button
                className={endButtonClassName}
                onClick={() => client.current?.end()}
                disabled={snapshot.state === "idle"}
              >
                End
              </button>
            </div>
          </div>
        </Panel>

        <Separator
          className={
            isDesktop
              ? "w-2 cursor-col-resize bg-gray-200 hover:bg-blue-500 dark:bg-gray-800"
              : "h-2 w-full cursor-row-resize bg-gray-200 hover:bg-blue-500 dark:bg-gray-800"
          }
        />
        <Panel
          id="side"
          defaultSize="30%"
          minSize={isDesktop ? 320 : "20%"}
          className="min-h-0 min-w-0 overflow-hidden"
        >
          <aside
            className={[
              "flex h-full min-h-0 flex-col gap-4 overflow-auto bg-gray-50 p-3",
              "text-gray-900 dark:bg-gray-950 dark:text-gray-100",
            ].join(" ")}
          >
            <Transcript />
            <LogPanel />
          </aside>
        </Panel>
      </Group>
    </section>
  );
}
