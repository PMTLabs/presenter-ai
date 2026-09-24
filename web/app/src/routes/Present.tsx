import { useCallback, useEffect, useRef, useState } from "react";
import { Group, Panel, Separator, useDefaultLayout } from "react-resizable-panels";
import { Link, useParams } from "react-router-dom";
import apiClient, { type components } from "@presenter/shared/api";
import { errorMessages, isProblem, useAuthStore } from "@presenter/shared";
import { BridgeClient } from "../ws/bridgeClient";
import { DeckDriver } from "../deck/deckDriver";
import { startAudio, type StartedAudio } from "../audio/capture";
import { usePresenterStore, type PresenterLog } from "../store/presenterStore";
import { Transcript } from "../components/Transcript";
import { SlidePill } from "../components/SlidePill";
import { UsagePill } from "../components/UsagePill";
import { LogPanel } from "../components/LogPanel";
import { TrainerControls } from "../components/TrainerControls";
import { AskControls } from "../components/AskControls";
import { ScriptVersions } from "../components/ScriptVersions";
import { formatEndReason } from "../utils/endReasons";
import { useToastStore } from "../store/toastStore";
type Detail = components["schemas"]["PresentationDetail"];

/** Toast keys this page owns: one toast per notification kind, updated in place and cleared on leave. */
const TOAST_KEYS = {
  load: "present:load-error",
  ended: "present:talk-ended",
  limit: "present:limit-warning",
  ask: "present:ask",
} as const;

const ASK_TOASTS: Record<string, { kind: "info" | "warning" | "error"; message: string }> = {
  refused_muted: { kind: "warning", message: "Unmute the microphone to ask a question." },
  refused_not_live: { kind: "warning", message: "Ask isn't available right now." },
  unavailable: { kind: "warning", message: "Ask isn't available right now." },
  empty: { kind: "info", message: "No question was heard — still paused." },
  quiet_cancelled: { kind: "info", message: "Nothing was heard for 90 s — Ask cancelled; still paused." },
  quiet_sent: { kind: "info", message: "Sent your question after 90 s of quiet." },
  limit_sent: { kind: "info", message: "25-second speech limit reached — your question was sent." },
  send_failed: {
    kind: "error",
    message: "Couldn't send your question — the connection was reset. Press Ask to try again.",
  },
  resumed: { kind: "info", message: "Ask cancelled." },
  navigated: { kind: "info", message: "Ask cancelled." },
  muted: { kind: "info", message: "Ask cancelled." },
  cancelled: { kind: "info", message: "Ask cancelled." },
};

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
  const echo = useRef({
    reference: "fallback" as "loopback" | "fallback",
    gateOpenRatio: 0,
    coupling: 0.5,
    bargeIns: 0,
  });
  const [presentation, setPresentation] = useState<Detail | null>(null);
  const [busyMessage, setBusyMessage] = useState<string | null>(null);
  const [canTakeOver, setCanTakeOver] = useState(false);
  const [selectedLength, setSelectedLength] = useState<number | undefined>(undefined);
  const [countdown, setCountdown] = useState<number | null>(null);
  const ready = useAuthStore((state) => state.ready);
  const userId = useAuthStore((state) => state.user?.id ?? null);
  const snapshot = usePresenterStore((state) => state.snapshot);
  const bufferedMs = usePresenterStore((state) => state.bufferedMs);
  const transcript = usePresenterStore((state) => state.transcript);
  const logs = usePresenterStore((state) => state.logs);
  const limitWarning = usePresenterStore((state) => state.limitWarning);
  const upstreamStatus = usePresenterStore((state) => state.upstreamStatus);
  const suspended = usePresenterStore((state) => state.suspended);
  const endReason = usePresenterStore((state) => state.endReason);
  const trainerMode = usePresenterStore((state) => state.trainerMode);
  const ask = usePresenterStore((state) => state.ask);
  const applySnapshot = usePresenterStore((state) => state.applySnapshot);
  const message = usePresenterStore((state) => state.message);
  const log = usePresenterStore((state) => state.log);
  const setMicReady = usePresenterStore((state) => state.setMicReady);
  const setBuffered = usePresenterStore((state) => state.setBuffered);
  const showToast = useToastStore((state) => state.show);
  const dismissToast = useToastStore((state) => state.dismissKey);
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
    bridge.on("state", applySnapshot);
    bridge.on("accepted", () => {
      setBusyMessage(null);
      setCanTakeOver(false);
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
      "limit_warning",
      "upstream",
      "script_edit",
      "script_version",
      "trainer_state",
      "ask_state",
    ] as const)
      bridge.on(event, (eventMessage) => {
        message(eventMessage);
        if (event === "closed") stopAudio();
        if (event === "ask_state") {
          const reason = eventMessage.reason as string | undefined;
          if (reason && ASK_TOASTS[reason]) {
            showToast({
              key: TOAST_KEYS.ask,
              kind: ASK_TOASTS[reason].kind,
              message: ASK_TOASTS[reason].message,
            });
          }
        }
      });
    bridge.on("audio", (buffer) => audio.current?.playback.enqueue(buffer));
    bridge.on("flush", () => audio.current?.playback.flush());
    bridge.on("open", () => log("info", "connected to server"));
    bridge.on("close", () => log("warn", "server connection closed"));
    bridge.on("busy", (busy) => {
      const allowed = busy.canTakeOver === true;
      setCanTakeOver(allowed);
      setBusyMessage(busy.canTakeOver === false ? "The presenter is in use by another account." : "The presenter is in use in another tab.");
    });
    bridge.on("taken-over", () => {
      setCanTakeOver(false);
      setBusyMessage("This presenter was taken over by another tab. Reload to use it here.");
      stopAudio();
    });
    bridge.connect();
    return () => {
      bridge.disconnect();
      stopAudio();
      driver.current?.dispose();
      driver.current = null;
    };
  }, [applySnapshot, log, message, ready, showToast, stopAudio, userId]);
  useEffect(() => {
    if (!ready || !userId || !id) {
      setPresentation(null);
      dismissToast(TOAST_KEYS.load);
      driver.current?.dispose();
      driver.current = null;
      return;
    }
    let active = true;
    dismissToast(TOAST_KEYS.load);
    void apiClient
      .GET("/v1/presentations/{id}", { params: { path: { id } } })
      .then(({ data, error: requestError }) => {
        if (!active) return;
        if (requestError) {
          const problem: unknown = requestError;
          showToast({
            key: TOAST_KEYS.load,
            kind: "error",
            message: isProblem(problem)
              ? (errorMessages[problem.code] ?? problem.detail ?? problem.title)
              : "Unable to load presentation.",
          });
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
        if (active)
          showToast({ key: TOAST_KEYS.load, kind: "error", message: "Unable to load presentation." });
      });
    return () => {
      active = false;
      driver.current?.dispose();
      driver.current = null;
    };
  }, [dismissToast, id, log, ready, showToast, userId]);
  useEffect(() => {
    setSelectedLength(undefined);
  }, [id]);
  useEffect(
    () => () => {
      for (const key of Object.values(TOAST_KEYS)) dismissToast(key);
    },
    [dismissToast],
  );
  useEffect(() => {
    if (!limitWarning || limitWarning.secondsLeft === null || limitWarning.secondsLeft === undefined) {
      setCountdown(null);
      return;
    }
    setCountdown(limitWarning.secondsLeft);
    const timer = setInterval(() => {
      setCountdown((prev) => (prev !== null && prev > 0 ? prev - 1 : 0));
    }, 1000);
    return () => clearInterval(timer);
  }, [limitWarning]);
  // A limit warning is a toast that counts down in place and stays until the warning clears or the user closes it.
  const limitToastFor = useRef<typeof limitWarning>(null);
  useEffect(() => {
    if (countdown === null || !limitWarning) {
      limitToastFor.current = null;
      dismissToast(TOAST_KEYS.limit);
      return;
    }
    const open = useToastStore.getState().toasts.some((toast) => toast.key === TOAST_KEYS.limit);
    if (limitToastFor.current === limitWarning && !open) return; // closed by the user: do not reopen this warning
    limitToastFor.current = limitWarning;
    showToast({
      key: TOAST_KEYS.limit,
      kind: "warning",
      message:
        limitWarning.kind === "max_length"
          ? `Time limit warning: ${countdown}s remaining`
          : `Inactivity warning: ${countdown}s remaining`,
    });
  }, [countdown, dismissToast, limitWarning, showToast]);
  const endReasonText = formatEndReason(endReason);
  const idle = snapshot.state === "idle";
  useEffect(() => {
    if (endReasonText && idle) showToast({ key: TOAST_KEYS.ended, kind: "info", message: `Talk ended: ${endReasonText}` });
  }, [endReasonText, idle, showToast]);
  const begin = async () => {
    if (!ready || !userId || !presentation || snapshot.state !== "idle") return;
    stopAudio();
    const controller = new AbortController();
    audioAbort.current = controller;
    const echoGate = new URLSearchParams(window.location.search).get("echoGate") !== "off";
    const started = await startAudio({
      onFrame: (buffer) => client.current?.sendAudio(buffer),
      onBuffered: setBuffered,
      onMicReady: setMicReady,
      onBargeIn: () => {
        audio.current?.playback.flush();
        echo.current.bargeIns++;
        log("info", "barge-in: playback flushed");
      },
      onEchoStats: (stats) => {
        echo.current.gateOpenRatio = stats.gateOpenRatio;
        echo.current.coupling = stats.coupling;
      },
      onReference: (reference, reason) => {
        echo.current.reference = reference;
        log(
          "info",
          reference === "loopback"
            ? "echo reference: loopback"
            : `echo reference: fallback (${reason ?? "unknown"})`,
        );
      },
      echoGate,
      signal: controller.signal,
    });
    if (controller.signal.aborted) {
      started.capture.stop();
      started.playback.stop();
      void started.context.close();
      return;
    }
    audio.current = started;
    if (selectedLength !== undefined) {
      client.current?.start(presentation.id, undefined, selectedLength);
    } else {
      client.current?.start(presentation.id);
    }
  };
  useEffect(() => {
    const keys = (e: KeyboardEvent) => {
      const active = document.activeElement as HTMLElement | null;
      // Form fields and the Trainer mode switch own their keys (Space toggles the switch, not Pause).
      if (["INPUT", "SELECT", "TEXTAREA"].includes(active?.tagName ?? "") || active?.getAttribute("role") === "switch")
        return;
      const live = ["presenting", "paused"].includes(snapshot.state);
      const isListening = ask?.state === "listening";

      if (isListening) {
        if (e.key === " ") {
          e.preventDefault();
          return;
        }
        if ((/^a$/i.test(e.key) || e.key === "Enter") && !e.ctrlKey && !e.metaKey && !e.altKey) {
          e.preventDefault();
          if (!e.repeat) {
            client.current?.askDone();
          }
          return;
        }
      } else {
        if (/^a$/i.test(e.key) && !e.ctrlKey && !e.metaKey && !e.altKey) {
          if (!e.repeat && live) {
            e.preventDefault();
            if (snapshot.muted) {
              showToast({
                key: TOAST_KEYS.ask,
                kind: "warning",
                message: "Unmute the microphone to ask a question.",
              });
            } else {
              client.current?.askStart();
            }
          }
          return;
        }
      }

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
  }, [snapshot, presentation, ask, showToast]);
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
        echo: echo.current,
        wsOpen: snapshot.state !== "idle",
        limitWarning,
        upstreamStatus,
        suspended: snapshot.suspended === true || suspended || upstreamStatus === "suspended",
        endReason,
      });
  }, [presentation, snapshot, transcript, logs, limitWarning, upstreamStatus, suspended, endReason]);
  const live = snapshot.state === "presenting" || snapshot.state === "paused";
  const pausedWarning = snapshot.state === "paused" ? warningSincePause(logs) : null;
  const isDesktop = useDesktopLayout();
  const { defaultLayout, onLayoutChanged } = useDefaultLayout({
    id: "presenter-split",
    storage: presenterStorage,
    onlySaveAfterUserInteractions: true,
  });
  const isSuspended =
    upstreamStatus === "live"
      ? false
      : snapshot.suspended === true || suspended || upstreamStatus === "suspended";
  const isReconnecting = upstreamStatus === "reconnecting";
  const LENGTH_OPTIONS = [5, 10, 15, 20, 30, 45, 60, 90] as const;
  const scriptMinutes =
    (presentation?.meta as { maxMinutes?: number } | undefined)?.maxMinutes;
  return (
    <section
      className={[
        "flex h-[calc(100vh-4rem)] min-h-0 min-w-0 flex-col overflow-hidden px-4",
        "text-gray-900 dark:text-gray-100",
      ].join(" ")}
    >
      <Group
        orientation={isDesktop ? "horizontal" : "vertical"}
        id="presenter-split"
        defaultLayout={defaultLayout}
        onLayoutChanged={onLayoutChanged}
        className="min-h-0 min-w-0 flex-1 overflow-hidden"
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
            {/* One row above the deck: the breadcrumb on the left, the info bar flush with the deck's right edge. */}
            <div className="flex flex-none flex-wrap items-center gap-x-4 gap-y-2 px-1 py-2">
              <Link className="text-sm text-blue-600 hover:underline dark:text-blue-400" to="/">
                ← Library
              </Link>
              <div
                data-testid="info-bar"
                className="ml-auto flex min-w-0 flex-wrap items-center justify-end gap-2"
              >
                <span className="rounded-full bg-blue-100 px-3 py-1 text-xs text-blue-900 dark:bg-blue-900 dark:text-blue-100">
                  {snapshot.state}
                </span>
                <SlidePill />
                <UsagePill />
                <span className="rounded-full bg-gray-200 px-3 py-1 text-xs text-gray-900 dark:bg-gray-800 dark:text-gray-100">
                  buf {bufferedMs} ms
                </span>
                <TrainerControls onToggle={() => client.current?.setTrainerMode(!trainerMode)} />
              </div>
            </div>
            {ready && !userId && (
              <p className="m-3 mb-0 flex-none text-gray-600 dark:text-gray-400">
                Please sign in to continue. <Link className="text-blue-600 hover:underline" to="/login">Sign in</Link>
              </p>
            )}
            {busyMessage && (
              <div className="m-3 mb-0 flex flex-none items-center justify-between gap-3 rounded-lg bg-amber-50 p-4 text-amber-800 dark:bg-amber-950 dark:text-amber-200">
                <p>{busyMessage}</p>
                {canTakeOver && (
                  <button className={actionButtonClassName} onClick={() => client.current?.takeOver()}>
                    Take over
                  </button>
                )}
              </div>
            )}
            {isReconnecting && (
              <p role="alert" className="m-3 mb-0 flex-none rounded-lg bg-blue-50 p-3 text-blue-800 dark:bg-blue-950 dark:text-blue-200">
                Reconnecting…
              </p>
            )}
            {!isReconnecting && isSuspended && (
              <p role="alert" className="m-3 mb-0 flex-none rounded-lg bg-amber-50 p-3 text-amber-800 dark:bg-amber-950 dark:text-amber-200">
                Paused — disconnected to save cost; Resume reconnects
              </p>
            )}
            {!isSuspended && !isReconnecting && pausedWarning && (
              <p role="alert" className="m-3 mb-0 flex-none rounded-lg bg-amber-50 p-3 text-amber-800 dark:bg-amber-950 dark:text-amber-200">
                {pausedWarning.message}
              </p>
            )}
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
            <div className="flex flex-none flex-wrap items-center gap-2 p-3">
              <button
                className={startButtonClassName}
                onClick={() => void begin()}
                disabled={!ready || !userId || !presentation || busyMessage !== null || snapshot.state !== "idle"}
              >
                Start
              </button>
              {snapshot.state === "idle" && (
                <label className="flex items-center gap-1.5 text-sm font-medium text-gray-700 dark:text-gray-300">
                  <span>Length</span>
                  <select
                    id="length-select"
                    aria-label="Length"
                    className="rounded-lg border border-gray-300 bg-white px-2 py-1.5 text-sm text-gray-900 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    value={selectedLength ?? ""}
                    onChange={(e) => setSelectedLength(e.target.value ? Number(e.target.value) : undefined)}
                  >
                    <option value="">{scriptMinutes ? `Default (script ${scriptMinutes} min)` : "Default (server limit)"}</option>
                    {LENGTH_OPTIONS.map((m) => (
                      <option key={m} value={m}>
                        {m} min
                      </option>
                    ))}
                  </select>
                  <span className="text-xs text-gray-500 dark:text-gray-400">Server ceiling always applies.</span>
                </label>
              )}
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
              <AskControls
                onAsk={() => client.current?.askStart()}
                onDone={() => client.current?.askDone()}
                onExtend={() => client.current?.askExtend()}
                onCancel={() => client.current?.askCancel()}
                onContinue={() => client.current?.resume()}
                disabled={!live}
              />
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
            <Transcript
              onTrainOnThis={(question, answer, slideIndex) =>
                client.current?.trainTurn(question, answer, slideIndex)
              }
            />
            {presentation && <ScriptVersions presentationId={presentation.id} />}
            <LogPanel />
          </aside>
        </Panel>
      </Group>
    </section>
  );
}

/**
 * The newest server warning logged after the run entered its current state. The server logs "state → paused" as it
 * pauses and a stall adds its warning after that, so a manual pause shows no banner and an older warning (an earlier
 * nudge, say) is never promoted.
 */
function warningSincePause(logs: PresenterLog[]) {
  for (let index = logs.length - 1; index >= 0; index--) {
    const entry = logs[index];
    if (entry.source !== "server") continue;
    if (entry.level === "warn") return entry;
    if (entry.message.startsWith("state → ")) return null;
  }
  return null;
}
