// Presentation state machine: drives slides, injects narration into the live session,
// auto-advances on output silence, and exposes manual controls.
//
// States: idle → connecting → presenting ⇄ paused → ending → idle
//
// Events emitted (consumed by the WebSocket bridge):
//   state(snapshot) · slide(index) · audio(Buffer) · transcript({role, delta, start_ms, end_ms})
//   usage({seconds, ratio}) · closed({reason, seconds}) · log({level, message}) · upstream-error({message, code})

import { EventEmitter } from 'node:events';
import { chunkText } from './script-parser.js';
import { isVoiced } from './audio-util.js';
import {
  buildSystemInstructions,
  buildSlideInstruction,
  buildNotesContext,
  buildResumeInstruction,
  buildPauseInstruction,
  buildNudgeInstruction,
  buildWrapUpInstruction,
} from './prompt.js';

export const NUDGE_MS = 15_000;
export const WRAP_UP_FALLBACK_MS = 15_000;
export const MAX_UPSTREAM_ATTEMPTS = 4;
export const PART_GAP_MS = 2500; // silence after a part before the next part is sent (paragraph pauses reach ~2.3 s)
export const partGapFor = (advanceSilenceMs) => Math.min(PART_GAP_MS, Math.round(advanceSilenceMs * 0.8));

/** Docs say `close_requested`; the live service currently sends `client_request`. */
export const isNormalClose = (reason) => /request/.test(String(reason ?? ''));

export class Presenter extends EventEmitter {
  /**
   * @param {object} deps
   * @param {(opts:{instructions:string,voice:string}, attempt:number) => object|null} deps.createSession
   *        returns a session for upstream #attempt (0 = primary), or null when there are no more.
   */
  constructor({ createSession, loadPresentation, config, log, setTimeout: st, clearTimeout: ct }) {
    super();
    this.createSession = createSession;
    this.loadPresentation = loadPresentation;
    this.config = { advanceSilenceMs: 3000, voice: 'marin', ...config };
    this.logFn = log ?? ((level, message) => console.log(`[${level}] ${message}`));
    this._setTimeout = st;
    this._clearTimeout = ct;

    this.state = 'idle';
    this.presentation = null; // { id, meta, slides, context }
    this.session = null;
    this.slideIndex = 0;
    this.muted = false; // user-requested mic mute
    this.timers = { silence: null, nudge: null };
    this.heardOutput = false;
    this.nudged = false;
    this.wrappingUp = false;
    this.parts = []; // narration chunks of the current slide
    this.partsSent = 0;
    this.usage = { seconds: 0, ratio: null };
    this.lastRun = null; // { id, index, endedNormally }
  }

  // ---- timers -------------------------------------------------------------

  #setTimer(name, ms, fn) {
    this.#clearTimer(name);
    const set = this._setTimeout ?? globalThis.setTimeout;
    this.timers[name] = set(() => {
      this.timers[name] = null;
      fn();
    }, ms);
  }

  #clearTimer(name) {
    if (this.timers[name] == null) return;
    const clear = this._clearTimeout ?? globalThis.clearTimeout;
    clear(this.timers[name]);
    this.timers[name] = null;
  }

  clearTimers() {
    this.#clearTimer('silence');
    this.#clearTimer('nudge');
  }

  get advanceSilenceMs() {
    return this.presentation?.meta?.advanceSilenceMs ?? this.config.advanceSilenceMs;
  }

  get slideCount() {
    return this.presentation?.slides?.length ?? 0;
  }

  // ---- reporting -----------------------------------------------------------

  log(level, message) {
    this.logFn(level, message);
    this.emit('log', { level, message });
  }

  snapshot() {
    return {
      state: this.state,
      presentationId: this.presentation?.id ?? null,
      title: this.presentation?.meta?.title ?? null,
      slideIndex: this.slideIndex,
      slideCount: this.slideCount,
      paused: this.state === 'paused',
      muted: this.muted,
      sessionId: this.session?.id ?? null,
      expiresAt: this.session?.session?.expires_at ?? null,
      usageSeconds: this.usage.seconds,
      advanceSilenceMs: this.advanceSilenceMs,
    };
  }

  #setState(state) {
    if (this.state === state) return;
    this.state = state;
    this.log('info', `state → ${state}`);
    this.emit('state', this.snapshot());
  }

  #emitState() {
    this.emit('state', this.snapshot());
  }

  // ---- lifecycle -----------------------------------------------------------

  /** Loads the presentation, opens the live session and presents the first slide. */
  async start(id, { fromIndex } = {}) {
    if (this.state !== 'idle') {
      this.log('warn', `start ignored: state is ${this.state}`);
      return false;
    }
    this.#setState('connecting');
    try {
      this.presentation = await this.loadPresentation(id);
    } catch (err) {
      this.log('error', `cannot load presentation "${id}": ${err.message}`);
      this.emit('upstream-error', { message: `cannot load presentation: ${err.message}`, code: 'presentation' });
      this.#setState('idle');
      return false;
    }
    const { meta, slides, context } = this.presentation;
    const instructions = buildSystemInstructions({
      title: meta.title,
      slides,
      context,
      onWarn: (m) => this.log('warn', m),
    });
    // Try each upstream in turn (primary, then fallback) until one starts a session.
    let session = null;
    for (let attempt = 0; attempt < MAX_UPSTREAM_ATTEMPTS; attempt++) {
      const candidate = this.createSession({ instructions, voice: meta.voice ?? this.config.voice }, attempt);
      if (!candidate) break;
      const label = candidate.name ?? `upstream #${attempt + 1}`;
      try {
        await candidate.connect();
        session = candidate;
        if (attempt > 0) this.log('warn', `connected via ${label}`);
        break;
      } catch (err) {
        this.log('error', `session start via ${label} failed: ${err.message}`);
        this.emit('upstream-error', { message: `${label}: ${err.message}`, code: err.upstream?.code ?? 'connect', upstream: err.upstream });
      }
    }
    if (!session) {
      this.log('error', 'no upstream could start a session');
      this.#setState('idle');
      return false;
    }
    this.session = session;
    this.#wireSession(session);

    let startAt = 0;
    if (Number.isInteger(fromIndex)) startAt = fromIndex;
    else if (this.lastRun && this.lastRun.id === id && !this.lastRun.endedNormally) startAt = this.lastRun.index;
    startAt = Math.max(0, Math.min(startAt, slides.length - 1));

    this.muted = false;
    this.usage = { seconds: 0, ratio: null };
    this.#setState('presenting');
    this.presentSlide(startAt);
    return true;
  }

  #wireSession(session) {
    session.on('audio', (buf, startMs, endMs) => this.#onAudio(buf, startMs, endMs));
    session.on('transcript', (role, delta, startMs, endMs) => this.#onTranscript(role, delta, startMs, endMs));
    session.on('appended', (kind, clientEventId) => this.log('debug', `appended ${kind} ${clientEventId ?? ''}`.trim()));
    session.on('usage', (u) => {
      this.usage = u;
      this.emit('usage', u);
    });
    session.on('delegation', (d) => this.log('info', `delegation created (${d?.target}) id=${d?.id} — ignored (client delegation carries no task text)`));
    session.on('upstream-error', (e) => {
      this.log('error', `upstream error${e?.client_event_id ? ` for ${e.client_event_id}` : ''}: ${e?.code ?? ''} ${e?.message ?? ''}`.trim());
      this.emit('upstream-error', { message: e?.message ?? 'unknown', code: e?.code ?? null, clientEventId: e?.client_event_id ?? null });
    });
    session.on('closed', (reason, seconds) => this.#onClosed(reason, seconds));
  }

  /** Shows slide i and injects its notes + narration. */
  presentSlide(i, { interrupt = false } = {}) {
    if (!this.presentation) return;
    const slides = this.presentation.slides;
    if (i < 0 || i >= slides.length) return;
    this.clearTimers();
    this.slideIndex = i;
    this.heardOutput = false;
    this.nudged = false;
    this.wrappingUp = false;
    const slide = slides[i];
    const total = slides.length;
    this.log('info', `slide ${i + 1}/${total}${slide.title ? ` — ${slide.title}` : ''}`);
    this.emit('slide', i);
    this.#emitState();

    if (slide.notes) {
      this.session?.appendThinking(buildNotesContext({ index: i, total, title: slide.title, notes: slide.notes }), {
        eventId: `slide-${i + 1}-notes`,
      });
    }
    this.parts = chunkText(slide.narration, this.presentation.meta.chunkChars);
    this.partsSent = 0;
    if (this.parts.length === 0) {
      // Nothing to narrate: treat as "already spoken" and advance after the silence window.
      this.log('info', `slide ${i + 1} has no narration; advancing after ${this.advanceSilenceMs} ms`);
      this.heardOutput = true;
      this.#armSilence();
      return;
    }
    if (this.parts.length > 1) this.log('info', `slide ${i + 1} narration is sent in ${this.parts.length} parts`);
    this.#sendNextPart({ interrupt });
    this.#armNudge();
  }

  /** Sends the next narration part. Parts after the first go out only once the previous one was spoken. */
  #sendNextPart({ interrupt = false } = {}) {
    if (this.partsSent >= this.parts.length) return false;
    const i = this.slideIndex;
    const slide = this.presentation.slides[i];
    const k = this.partsSent;
    this.session?.appendInstructions(
      buildSlideInstruction({
        index: i,
        total: this.slideCount,
        title: slide.title,
        chunk: this.parts[k],
        part: k + 1,
        parts: this.parts.length,
        interrupt: interrupt && k === 0,
      }),
      { eventId: `slide-${i + 1}-part-${k + 1}` },
    );
    this.partsSent = k + 1;
    if (k > 0) this.log('info', `slide ${i + 1}: sent part ${k + 1}/${this.parts.length}`);
    return true;
  }

  get partsPending() {
    return this.partsSent < this.parts.length;
  }

  /** After voiced audio: wait for the next part (short gap) or for the advance (full window). */
  #armAfterVoice() {
    if (this.partsPending) {
      this.#setTimer('silence', partGapFor(this.advanceSilenceMs), () => this.#onPartGap());
    } else {
      this.#armSilence();
    }
  }

  #onPartGap() {
    if (this.state !== 'presenting' || !this.partsPending) return;
    this.#sendNextPart();
  }

  #armSilence() {
    this.#setTimer('silence', this.advanceSilenceMs, () => this.#onSilence());
  }

  #armNudge() {
    this.#setTimer('nudge', NUDGE_MS, () => this.#onNudge());
  }

  #onAudio(buf, startMs, endMs) {
    this.emit('audio', buf, startMs, endMs);
    if (this.state !== 'presenting' && this.state !== 'paused') return;
    // GPT-Live streams output audio continuously, silence included: only voiced frames count.
    if (!isVoiced(buf)) return;
    if (!this.heardOutput) {
      this.heardOutput = true;
      this.#clearTimer('nudge');
    }
    if (this.state === 'presenting') this.#armAfterVoice();
  }

  #onTranscript(role, delta, startMs, endMs) {
    this.emit('transcript', { role, delta, start_ms: startMs, end_ms: endMs });
    // The audience is speaking: hold the advance / next part until the exchange has settled.
    if (role === 'user' && this.state === 'presenting' && this.heardOutput) this.#armAfterVoice();
  }

  #onSilence() {
    if (this.state !== 'presenting' || !this.heardOutput || this.partsPending) return;
    if (this.wrappingUp) {
      this.log('info', 'wrap-up finished; ending session');
      this.end();
      return;
    }
    if (this.slideIndex < this.slideCount - 1) {
      this.log('info', `advance → slide ${this.slideIndex + 2}`);
      this.presentSlide(this.slideIndex + 1);
    } else {
      this.#startWrapUp();
    }
  }

  #onNudge() {
    if (this.state !== 'presenting' || this.heardOutput || this.nudged) return;
    this.nudged = true;
    const slide = this.presentation.slides[this.slideIndex];
    this.log('warn', `no output audio ${NUDGE_MS} ms after slide ${this.slideIndex + 1} was sent; nudging the model`);
    this.session?.appendInstructions(
      buildNudgeInstruction({ index: this.slideIndex, total: this.slideCount, title: slide.title }),
      { eventId: `slide-${this.slideIndex + 1}-nudge` },
    );
  }

  #startWrapUp() {
    if (this.wrappingUp) return;
    this.clearTimers();
    this.wrappingUp = true;
    this.parts = [];
    this.partsSent = 0;
    this.heardOutput = false;
    this.log('info', 'last slide finished; sending wrap-up');
    this.session?.appendInstructions(buildWrapUpInstruction(), { eventId: 'wrap-up' });
    // If the model never speaks the wrap-up, end anyway.
    this.#setTimer('nudge', WRAP_UP_FALLBACK_MS, () => {
      if (this.state === 'presenting' && this.wrappingUp && !this.heardOutput) {
        this.log('warn', 'no wrap-up audio; ending session');
        this.end();
      }
    });
  }

  // ---- manual controls -----------------------------------------------------

  #canNavigate() {
    return this.state === 'presenting' || this.state === 'paused';
  }

  #leavePauseForNavigation() {
    if (this.state !== 'paused') return;
    if (!this.muted) this.session?.unmute();
    this.#setState('presenting');
  }

  next() {
    if (!this.#canNavigate()) return false;
    this.#leavePauseForNavigation();
    if (this.slideIndex >= this.slideCount - 1) {
      this.#startWrapUp();
      return true;
    }
    this.log('info', `manual next → slide ${this.slideIndex + 2}`);
    this.presentSlide(this.slideIndex + 1, { interrupt: true });
    return true;
  }

  prev() {
    if (!this.#canNavigate()) return false;
    this.#leavePauseForNavigation();
    const target = Math.max(0, this.slideIndex - 1);
    this.log('info', `manual prev → slide ${target + 1}`);
    this.presentSlide(target, { interrupt: true });
    return true;
  }

  goto(index) {
    if (!this.#canNavigate()) return false;
    if (!Number.isInteger(index) || index < 0 || index >= this.slideCount) {
      this.log('warn', `goto ignored: index ${index} out of range`);
      return false;
    }
    this.#leavePauseForNavigation();
    this.log('info', `goto → slide ${index + 1}`);
    this.presentSlide(index, { interrupt: true });
    return true;
  }

  pause() {
    if (this.state !== 'presenting') return false;
    this.clearTimers();
    this.session?.mute();
    this.session?.appendInstructions(buildPauseInstruction(), { eventId: `pause-${this.slideIndex + 1}` });
    this.#setState('paused');
    return true;
  }

  resume() {
    if (this.state !== 'paused') return false;
    if (!this.muted) this.session?.unmute();
    const slide = this.presentation.slides[this.slideIndex];
    this.heardOutput = false;
    this.nudged = false;
    this.#setState('presenting');
    if (this.wrappingUp) {
      this.session?.appendInstructions(buildWrapUpInstruction(), { eventId: 'wrap-up-resume' });
    } else {
      this.session?.appendInstructions(
        buildResumeInstruction({ index: this.slideIndex, total: this.slideCount, title: slide.title }),
        { eventId: `resume-${this.slideIndex + 1}` },
      );
    }
    this.#armNudge();
    return true;
  }

  mute() {
    this.muted = true;
    if (this.state === 'presenting') this.session?.mute();
    this.#emitState();
    return true;
  }

  unmute() {
    this.muted = false;
    if (this.state === 'presenting') this.session?.unmute();
    this.#emitState();
    return true;
  }

  /** Mic audio from the browser; dropped while muted or paused. */
  sendAudio(buf) {
    if (this.state !== 'presenting' || this.muted) return false;
    return this.session?.sendAudio(buf) ?? false;
  }

  async end() {
    if (this.state === 'idle' || this.state === 'ending') return false;
    this.clearTimers();
    const session = this.session;
    this.#setState('ending');
    if (!session) {
      this.#onClosed('close_requested', null);
      return true;
    }
    await session.close();
    return true;
  }

  #onClosed(reason, seconds) {
    this.clearTimers();
    const endedNormally = isNormalClose(reason);
    if (this.presentation) this.lastRun = { id: this.presentation.id, index: this.slideIndex, endedNormally };
    if (seconds != null) this.usage = { ...this.usage, seconds };
    this.session = null;
    this.log('info', `closed: reason=${reason} usage=${seconds ?? 'unconfirmed'} s${endedNormally ? '' : ` (Start resumes at slide ${this.slideIndex + 1})`}`);
    this.emit('closed', { reason, seconds });
    this.#setState('idle');
  }
}
