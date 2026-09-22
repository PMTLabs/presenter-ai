import { DEV_TICKET } from "@presenter/shared";

export interface Snapshot {
  state: string;
  slideIndex: number;
  slideCount: number;
  muted: boolean;
  sessionId?: string;
  usageSeconds?: number;
}
export type BridgeEventMap = {
  open: [];
  close: [];
  state: [Snapshot];
  slide: [number];
  transcript: [BridgeMessage];
  usage: [BridgeMessage];
  log: [BridgeMessage];
  error: [BridgeMessage];
  closed: [BridgeMessage];
  pong: [];
  busy: [BridgeMessage];
  audio: [ArrayBuffer];
};
export type BridgeMessage = { type: string; [key: string]: unknown };
type Handler<T extends keyof BridgeEventMap> = (
  ...args: BridgeEventMap[T]
) => void;

/** Browser protocol client. The server contract deliberately remains JSON + PCM frames. */
export class BridgeClient {
  private ws: WebSocket | null = null;
  private retry = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private listeners = new Map<
    keyof BridgeEventMap,
    Set<(...args: unknown[]) => void>
  >();
  snapshot: Snapshot = {
    state: "idle",
    slideIndex: 0,
    slideCount: 0,
    muted: false,
  };
  constructor(
    private readonly url = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`,
    private readonly WebSocketImpl: typeof WebSocket = WebSocket,
  ) {}
  on<T extends keyof BridgeEventMap>(event: T, listener: Handler<T>) {
    const set = this.listeners.get(event) ?? new Set();
    set.add(listener as unknown as (...args: unknown[]) => void);
    this.listeners.set(event, set);
    return () =>
      set.delete(listener as unknown as (...args: unknown[]) => void);
  }
  private emit<T extends keyof BridgeEventMap>(
    event: T,
    ...args: BridgeEventMap[T]
  ) {
    this.listeners
      .get(event)
      ?.forEach((fn) => (fn as unknown as Handler<T>)(...args));
  }
  connect() {
    if (this.ws) return;
    const ws = new this.WebSocketImpl(this.url);
    this.ws = ws;
    ws.binaryType = "arraybuffer";
    ws.addEventListener("open", () => {
      if (this.ws !== ws) return;
      this.retry = 0;
      ws.send(JSON.stringify({ type: "auth", ticket: DEV_TICKET }));
      this.emit("open");
    });
    ws.addEventListener("message", (event) => {
      if (this.ws !== ws) return;
      this.receive(event.data);
    });
    ws.addEventListener("close", () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.snapshot = { ...this.snapshot, state: "idle" };
      this.emit("state", this.snapshot);
      this.emit("close");
      const delay = Math.min(10_000, 500 * 2 ** this.retry++);
      this.reconnectTimer = setTimeout(() => this.connect(), delay);
    });
  }
  disconnect() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    const ws = this.ws;
    this.ws = null;
    ws?.close();
  }
  send(message: BridgeMessage) {
    if (this.ws?.readyState === this.WebSocketImpl.OPEN)
      this.ws.send(JSON.stringify(message));
  }
  start(presentation: string, fromIndex = 0) {
    this.send({ type: "start", presentation, fromIndex });
  }
  next() {
    this.send({ type: "next" });
  }
  prev() {
    this.send({ type: "prev" });
  }
  goto(index: number) {
    this.send({ type: "goto", index });
  }
  pause() {
    this.send({ type: "pause" });
  }
  resume() {
    this.send({ type: "resume" });
  }
  mute() {
    this.send({ type: "mute" });
  }
  unmute() {
    this.send({ type: "unmute" });
  }
  end() {
    this.send({ type: "end" });
  }
  ping() {
    this.send({ type: "ping" });
  }
  sendAudio(buffer: ArrayBuffer) {
    if (
      this.ws?.readyState === this.WebSocketImpl.OPEN &&
      this.snapshot.state === "presenting"
    )
      this.ws.send(buffer);
  }
  private receive(data: unknown) {
    if (data instanceof ArrayBuffer) {
      this.emit("audio", data);
      return;
    }
    let message: BridgeMessage;
    try {
      message = JSON.parse(String(data)) as BridgeMessage;
    } catch {
      return;
    }
    switch (message.type) {
      case "state":
        this.snapshot = message as unknown as Snapshot;
        this.emit("state", this.snapshot);
        break;
      case "slide":
        this.emit("slide", message.index as number);
        break;
      case "transcript":
        this.emit("transcript", message);
        break;
      case "usage":
        this.emit("usage", message);
        break;
      case "log":
        this.emit("log", message);
        break;
      case "upstream-error":
      case "error":
        this.emit("error", message);
        break;
      case "closed":
        this.emit("closed", message);
        break;
      case "pong":
        this.emit("pong");
        break;
      case "busy":
        this.emit("busy", message);
        break;
    }
  }
}
