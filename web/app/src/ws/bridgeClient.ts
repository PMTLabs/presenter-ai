export interface Snapshot {
  state: string;
  slideIndex: number;
  slideCount: number;
  muted: boolean;
  sessionId?: string;
  usageSeconds?: number;
  suspended?: boolean;
}
export type BridgeEventMap = {
  open: [];
  close: [];
  state: [Snapshot];
  /** A server state frame arrived, so the ticket was accepted; the synthetic idle state on close does not count. */
  accepted: [];
  slide: [number];
  transcript: [BridgeMessage];
  usage: [BridgeMessage];
  log: [BridgeMessage];
  error: [BridgeMessage];
  closed: [BridgeMessage];
  pong: [];
  ping: [];
  busy: [BridgeMessage];
  "taken-over": [];
  audio: [ArrayBuffer];
  flush: []; // Server {type:"flush"}: discard queued playback audio.
  limit_warning: [BridgeMessage];
  upstream: [BridgeMessage];
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
  private connecting = false;
  private connectGeneration = 0;
  private takeOverOnNextAuth = false;
  private takenOver = false;
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
    private readonly ticketProvider: () => string | Promise<string> = () => {
      throw new Error("A session ticket provider is required.");
    },
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
    if (this.ws || this.connecting) return;
    this.connecting = true;
    const generation = this.connectGeneration;
    let ticket: string | Promise<string>;
    try {
      ticket = this.ticketProvider();
    } catch {
      this.scheduleReconnect();
      return;
    }
    if (typeof ticket === "string") {
      this.openSocket(ticket, generation);
      return;
    }
    void ticket.then(
      (value) => this.openSocket(value, generation),
      () => {
        if (generation === this.connectGeneration) this.scheduleReconnect();
      },
    );
  }

  private openSocket(ticket: string, generation: number) {
    if (generation !== this.connectGeneration || this.ws) return;
    this.connecting = false;
    const ws = new this.WebSocketImpl(this.url);
    this.ws = ws;
    ws.binaryType = "arraybuffer";
    ws.addEventListener("open", () => {
      if (this.ws !== ws) return;
      const takeOver = this.takeOverOnNextAuth;
      this.takeOverOnNextAuth = false;
      ws.send(JSON.stringify({ type: "auth", ticket, ...(takeOver ? { takeOver: true } : {}) }));
      this.emit("open");
    });
    ws.addEventListener("message", (event) => {
      if (this.ws !== ws) return;
      this.receive(event.data);
    });
    ws.addEventListener("close", (event) => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.snapshot = { ...this.snapshot, state: "idle", suspended: false };
      this.emit("state", this.snapshot);
      this.emit("close");
      if (event.code === 4409) this.markTakenOver();
      if (!this.takenOver) this.scheduleReconnect(event.code === 1013);
    });
  }

  private scheduleReconnect(busy = false) {
    this.connecting = false;
    const delay = busy
      ? Math.min(30_000, 5_000 * 2 ** this.retry++)
      : Math.min(10_000, 500 * 2 ** this.retry++);
    this.reconnectTimer = setTimeout(() => this.connect(), delay);
  }

  disconnect() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.connectGeneration++;
    this.connecting = false;
    const ws = this.ws;
    this.ws = null;
    ws?.close();
  }
  takeOver() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.retry = 0;
    this.connectGeneration++;
    this.connecting = false;
    const ws = this.ws;
    this.ws = null;
    ws?.close();
    this.takeOverOnNextAuth = true;
    this.connect();
  }
  send(message: BridgeMessage) {
    if (this.ws?.readyState === this.WebSocketImpl.OPEN)
      this.ws.send(JSON.stringify(message));
  }
  start(presentation: string, fromIndex?: number, maxMinutes?: number) {
    this.send({
      type: "start",
      presentation,
      ...(fromIndex === undefined ? {} : { fromIndex }),
      ...(maxMinutes === undefined ? {} : { maxMinutes }),
    });
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
  pong() {
    this.send({ type: "pong" });
  }
  sendAudio(buffer: ArrayBuffer) {
    if (
      this.ws?.readyState === this.WebSocketImpl.OPEN &&
      (this.snapshot.state === "presenting" || this.snapshot.state === "paused")
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
        // The initial state frame follows a successful ticket authentication, unlike the WebSocket open event.
        this.retry = 0;
        this.snapshot = message as unknown as Snapshot;
        this.emit("state", this.snapshot);
        this.emit("accepted");
        break;
      case "flush":
        this.emit("flush");
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
        if (message.code === "busy") this.emit("busy", message);
        if (message.code === "taken_over") this.markTakenOver();
        break;
      case "closed":
        this.emit("closed", message);
        break;
      case "ping":
        this.send({ type: "pong" });
        this.emit("ping");
        break;
      case "pong":
        this.emit("pong");
        break;
      case "limit_warning":
        this.emit("limit_warning", message);
        break;
      case "upstream":
        this.emit("upstream", message);
        break;
      case "busy":
        this.emit("busy", message);
        break;
    }
  }
  private markTakenOver() {
    if (this.takenOver) return;
    this.takenOver = true;
    this.emit("taken-over");
  }
}
