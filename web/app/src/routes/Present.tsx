import { useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import apiClient, { type components } from "@presenter/shared/api";
import { BridgeClient } from "../ws/bridgeClient";
import { DeckDriver } from "../deck/deckDriver";
import { startAudio, type StartedAudio } from "../audio/capture";
import { usePresenterStore } from "../store/presenterStore";
import { Transcript } from "../components/Transcript";
import { SlidePill } from "../components/SlidePill";
import { UsagePill } from "../components/UsagePill";
import { LogPanel } from "../components/LogPanel";
type Detail = components["schemas"]["PresentationDetail"];
export function Present() {
  const { id = "" } = useParams();
  const frame = useRef<HTMLIFrameElement>(null);
  const client = useRef<BridgeClient | null>(null);
  const driver = useRef<DeckDriver | null>(null);
  const audio = useRef<StartedAudio | null>(null);
  const [presentation, setPresentation] = useState<Detail | null>(null);
  const snapshot = usePresenterStore((state) => state.snapshot);
  const bufferedMs = usePresenterStore((state) => state.bufferedMs);
  const transcript = usePresenterStore((state) => state.transcript);
  const logs = usePresenterStore((state) => state.logs);
  const applySnapshot = usePresenterStore((state) => state.applySnapshot);
  const message = usePresenterStore((state) => state.message);
  const log = usePresenterStore((state) => state.log);
  const setMicReady = usePresenterStore((state) => state.setMicReady);
  const setBuffered = usePresenterStore((state) => state.setBuffered);
  useEffect(() => {
    const bridge = new BridgeClient();
    client.current = bridge;
    bridge.on("state", applySnapshot);
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
    bridge.connect();
    return () => bridge.disconnect();
  }, []);
  useEffect(() => {
    if (!id) return;
    void apiClient
      .GET("/api/presentations/{id}", { params: { path: { id } } })
      .then(({ data }) => {
        const detail: Detail | undefined = data;
        if (!detail || !frame.current) return;
        setPresentation(detail);
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
        void deck.load(`/${detail.meta.deck.replace(/^\/+/, "")}`, {
          driver: detail.meta.driver ?? "auto",
        });
      });
  }, [id]);
  const stopAudio = () => {
    audio.current?.capture.stop();
    audio.current?.playback.stop();
    void audio.current?.context.close();
    audio.current = null;
    setMicReady(false);
  };
  const begin = async () => {
    if (!presentation) return;
    audio.current = await startAudio({
      onFrame: (buffer) => client.current?.sendAudio(buffer),
      onBuffered: setBuffered,
      onMicReady: setMicReady,
    });
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
  return (
    <section>
      <Link className="text-sm text-blue-600" to="/">
        ← Library
      </Link>
      <div className="mt-3 flex flex-wrap gap-2">
        <span className="rounded-full bg-blue-100 px-3 py-1 text-xs">
          {snapshot.state}
        </span>
        <SlidePill />
        <UsagePill />
        <span className="rounded-full bg-gray-200 px-3 py-1 text-xs dark:bg-gray-800">
          buf {bufferedMs} ms
        </span>
      </div>
      <div className="mt-4 grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(20rem,1fr)]">
        <div>
          <iframe
            ref={frame}
            title="Presentation deck"
            className="h-[70vh] w-full rounded-xl border bg-white"
          />
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              onClick={() => void begin()}
              disabled={snapshot.state !== "idle"}
            >
              Start
            </button>
            <button
              onClick={() =>
                snapshot.state === "paused"
                  ? client.current?.resume()
                  : client.current?.pause()
              }
              disabled={!live}
            >
              {" "}
              {snapshot.state === "paused" ? "Resume" : "Pause"}{" "}
            </button>
            <button onClick={() => client.current?.prev()} disabled={!live}>
              Prev
            </button>
            <button onClick={() => client.current?.next()} disabled={!live}>
              Next
            </button>
            <button
              onClick={() => {
                if (snapshot.muted) client.current?.unmute();
                else client.current?.mute();
              }}
              disabled={!live}
            >
              {snapshot.muted ? "Unmute" : "Mute"}
            </button>
            <button
              onClick={() => client.current?.end()}
              disabled={snapshot.state === "idle"}
            >
              End
            </button>
          </div>
        </div>
        <aside className="space-y-4">
          <Transcript />
          <LogPanel />
        </aside>
      </div>
    </section>
  );
}
