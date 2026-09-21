// Thin client for one GPT-Live session over WebSocket. No presentation logic here.
//
// Events emitted:
//   started(session)                      session.started payload
//   audio(Buffer, start_ms, end_ms)       decoded PCM16 output audio
//   transcript(role, delta, start_ms, end_ms)   role = 'user' | 'assistant'
//   appended(kind, clientEventId, event)  kind = 'instructions' | 'thinking' | 'commentary'
//   usage({ seconds, ratio })
//   delegation(delegation)
//   upstream-error(error)                 the `error` envelope's inner object
//   closed(reason, seconds)               exactly once, also on transport loss

import { EventEmitter } from 'node:events';
import WebSocket from 'ws';

const AUDIO_LOG_EVERY = 100;

// GPT-Live paces its output by the input-audio timeline: if no input audio arrives, the model
// never "reaches" the point where it speaks. The pump below therefore fills any gap between
// wall-clock time and the audio actually sent with PCM16 silence, at 24 kHz mono.
const SAMPLE_RATE = 24_000;
const BYTES_PER_MS = (SAMPLE_RATE * 2) / 1000; // 48 bytes per ms
const PUMP_INTERVAL_MS = 20;
const PUMP_SLACK_MS = 120; // tolerate this much lag before inserting silence
const SILENCE_FRAME = Buffer.alloc(PUMP_INTERVAL_MS * BYTES_PER_MS);

export class LiveSession extends EventEmitter {
  #ws = null;
  #state = 'idle'; // idle | connecting | open | closing | closed
  #closedEmitted = false;
  #closeResolve = null;
  #closeTimer = null;
  #audioDeltas = 0;
  #eventSeq = 0;
  #session = null;
  #pump = null;
  #pumpStartedAt = 0;
  #sentMs = 0; // ms of input audio sent since the pump started
  #silenceMs = 0;

  constructor({
    url,
    headers,
    session,
    log = (...a) => console.log(...a),
    logEvents = false,
    handshakeTimeoutMs = 10_000,
    closeTimeoutMs = 5_000,
    silencePump = true,
    WebSocketImpl = WebSocket,
  }) {
    super();
    this.url = url;
    this.headers = headers;
    this.sessionConfig = session;
    this.log = log;
    this.logEvents = logEvents;
    this.handshakeTimeoutMs = handshakeTimeoutMs;
    this.closeTimeoutMs = closeTimeoutMs;
    this.silencePump = silencePump;
    this.WebSocketImpl = WebSocketImpl;
  }

  /** Milliseconds of silence inserted by the pump so far (diagnostics). */
  get silenceMs() {
    return this.#silenceMs;
  }

  #startPump() {
    if (!this.silencePump || this.#pump) return;
    this.#pumpStartedAt = Date.now();
    this.#sentMs = 0;
    this.#pump = setInterval(() => {
      if (this.#state !== 'open') return;
      const elapsed = Date.now() - this.#pumpStartedAt;
      let frames = 0;
      while (elapsed - this.#sentMs > PUMP_SLACK_MS && frames < 25) {
        if (!this.#send({ type: 'session.input_audio.append', audio: SILENCE_FRAME.toString('base64') })) break;
        this.#sentMs += PUMP_INTERVAL_MS;
        this.#silenceMs += PUMP_INTERVAL_MS;
        frames++;
      }
    }, PUMP_INTERVAL_MS);
    this.#pump.unref?.();
  }

  #stopPump() {
    if (this.#pump) clearInterval(this.#pump);
    this.#pump = null;
  }

  get state() {
    return this.#state;
  }

  get id() {
    return this.#session?.id ?? null;
  }

  get session() {
    return this.#session;
  }

  /** Opens the socket, sends session.start, resolves with the session.started payload. */
  connect() {
    if (this.#state !== 'idle') return Promise.reject(new Error(`LiveSession.connect: state is ${this.#state}`));
    this.#state = 'connecting';
    return new Promise((resolve, reject) => {
      let settled = false;
      const settle = (fn, value) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        fn(value);
      };
      const timer = setTimeout(() => {
        settle(reject, new Error(`GPT-Live handshake timed out after ${this.handshakeTimeoutMs} ms`));
        this.#ws?.terminate();
      }, this.handshakeTimeoutMs);

      const ws = new this.WebSocketImpl(this.url, { headers: this.headers, handshakeTimeout: this.handshakeTimeoutMs });
      this.#ws = ws;

      ws.on('open', () => {
        this.log('upstream socket open, sending session.start');
        this.#send({ type: 'session.start', event_id: this.#nextEventId('start'), session: this.sessionConfig });
      });
      ws.on('message', (data) => {
        let event;
        try {
          event = JSON.parse(data.toString());
        } catch {
          this.log('upstream sent non-JSON frame, ignored');
          return;
        }
        if (event.type === 'session.started') {
          this.#state = 'open';
          this.#session = event.session ?? {};
          this.#startPump();
          settle(resolve, this.#session);
        } else if (event.type === 'error' && this.#state === 'connecting') {
          const err = new Error(`GPT-Live startup error: ${event.error?.message ?? 'unknown'}`);
          err.upstream = event.error;
          this.#handleEvent(event);
          settle(reject, err);
          // A startup error means there is no session: drop the transport.
          this.#ws?.close();
          this.#finish('startup_error', null);
          return;
        }
        this.#handleEvent(event);
      });
      ws.on('error', (err) => {
        this.log(`upstream socket error: ${err.message}`);
        settle(reject, err);
      });
      ws.on('close', (code, reasonBuf) => {
        const reason = reasonBuf?.toString?.() || '';
        this.log(`upstream socket closed (${code}${reason ? ' ' + reason : ''})`);
        settle(reject, new Error(`upstream closed before session.started (${code})`));
        this.#finish('connection_lost', null);
      });
    });
  }

  #handleEvent(event) {
    const t = event.type;
    if (t === 'session.output_audio.delta') {
      this.#audioDeltas++;
      if (this.#audioDeltas % AUDIO_LOG_EVERY === 1) this.log(`output audio delta #${this.#audioDeltas} (${event.start_ms}–${event.end_ms} ms)`);
      this.emit('audio', Buffer.from(event.delta, 'base64'), event.start_ms, event.end_ms);
      return;
    }
    if (this.logEvents) this.log(`<< ${JSON.stringify(event)}`);
    else if (!t.endsWith('transcript.delta')) this.log(`<< ${t}${event.client_event_id ? ' ' + event.client_event_id : ''}`);

    switch (t) {
      case 'session.started':
        this.log(`session started: id=${event.session?.id} model=${event.session?.model} expires_at=${event.session?.expires_at}`);
        this.emit('started', event.session);
        break;
      case 'session.input_transcript.delta':
        this.emit('transcript', 'user', event.delta, event.start_ms, event.end_ms);
        break;
      case 'session.output_transcript.delta':
        this.emit('transcript', 'assistant', event.delta, event.start_ms, event.end_ms);
        break;
      case 'session.instructions.appended':
      case 'session.thinking.appended':
      case 'session.commentary.appended':
        this.emit('appended', t.split('.')[1], event.client_event_id, event);
        break;
      case 'session.usage.updated':
        this.emit('usage', { seconds: event.usage?.seconds ?? 0, ratio: event.context_window?.usage_ratio ?? null });
        break;
      case 'session.delegation.created':
        this.emit('delegation', event.delegation);
        break;
      case 'error':
        this.log(`upstream error: ${JSON.stringify(event.error)}`);
        this.emit('upstream-error', event.error ?? { message: 'unknown error' });
        break;
      case 'session.closed':
        this.#finish(event.reason ?? 'close_requested', event.usage?.seconds ?? null);
        break;
      default:
        break;
    }
  }

  #finish(reason, seconds) {
    if (this.#closedEmitted) return;
    this.#closedEmitted = true;
    this.#state = 'closed';
    this.#stopPump();
    clearTimeout(this.#closeTimer);
    this.log(`session closed: reason=${reason} seconds=${seconds ?? 'unconfirmed'} (silence inserted: ${this.#silenceMs} ms)`);
    this.emit('closed', reason, seconds);
    this.#closeResolve?.({ reason, seconds });
    this.#closeResolve = null;
    if (this.#ws && this.#ws.readyState === this.#ws.OPEN) this.#ws.close();
  }

  #nextEventId(prefix) {
    return `${prefix}-${++this.#eventSeq}`;
  }

  #send(obj) {
    if (!this.#ws || this.#ws.readyState !== this.#ws.OPEN) return false;
    this.#ws.send(JSON.stringify(obj));
    return true;
  }

  #sendCommand(obj) {
    if (this.#state !== 'open') return false;
    if (this.logEvents) this.log(`>> ${JSON.stringify(obj)}`);
    else this.log(`>> ${obj.type}${obj.event_id ? ' ' + obj.event_id : ''}`);
    return this.#send(obj);
  }

  /** Raw PCM16 mono 24 kHz. Trailing odd byte is dropped; empty frames are not sent. */
  sendAudio(buffer) {
    if (this.#state !== 'open') return false;
    let buf = Buffer.isBuffer(buffer) ? buffer : Buffer.from(buffer);
    if (buf.length % 2 === 1) buf = buf.subarray(0, buf.length - 1);
    if (buf.length === 0) return false;
    const ok = this.#send({ type: 'session.input_audio.append', audio: buf.toString('base64') });
    if (ok) this.#sentMs += buf.length / BYTES_PER_MS;
    return ok;
  }

  #append(kind, content, { eventId, delegationId = null } = {}) {
    const text = String(content ?? '').trim();
    if (!text) return null;
    const event_id = eventId ?? this.#nextEventId(kind);
    const ok = this.#sendCommand({ type: `session.${kind}.append`, event_id, delegation_id: delegationId, content: text });
    return ok ? event_id : null;
  }

  appendInstructions(content, opts) {
    return this.#append('instructions', content, opts);
  }

  appendThinking(content, opts) {
    return this.#append('thinking', content, opts);
  }

  appendCommentary(content, opts) {
    return this.#append('commentary', content, opts);
  }

  mute() {
    return this.#sendCommand({ type: 'session.input_audio.mute', event_id: this.#nextEventId('mute') });
  }

  unmute() {
    return this.#sendCommand({ type: 'session.input_audio.unmute', event_id: this.#nextEventId('unmute') });
  }

  /** Graceful close: session.close → wait for session.closed (or terminate after closeTimeoutMs). */
  close() {
    if (this.#closedEmitted) return Promise.resolve({ reason: 'closed', seconds: null });
    if (this.#state === 'idle') {
      this.#finish('close_requested', null);
      return Promise.resolve({ reason: 'close_requested', seconds: null });
    }
    return new Promise((resolve) => {
      this.#closeResolve = resolve;
      if (this.#state === 'open') {
        this.#sendCommand({ type: 'session.close', event_id: this.#nextEventId('close') });
      }
      this.#state = 'closing';
      this.#closeTimer = setTimeout(() => {
        this.log('no session.closed within timeout; terminating socket (final usage unconfirmed)');
        this.#ws?.terminate();
        this.#finish('connection_lost', null);
      }, this.closeTimeoutMs);
    });
  }

  /** Hard drop without the close handshake (used on fatal errors). */
  terminate() {
    this.#ws?.terminate();
    this.#finish('connection_lost', null);
  }
}
