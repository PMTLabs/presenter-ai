// Presenter page controller: WebSocket to the backend, audio in/out, deck driving, UI.

import { DeckDriver } from './deck-driver.js';
import { AudioCapture } from './audio-capture.js';
import { AudioPlayback, createAudioContext, resumeWithTimeout } from './audio-playback.js';

const $ = (id) => document.getElementById(id);
const els = {
  presentation: $('presentation'),
  start: $('btn-start'),
  pause: $('btn-pause'),
  prev: $('btn-prev'),
  next: $('btn-next'),
  mute: $('btn-mute'),
  end: $('btn-end'),
  pillState: $('pill-state'),
  pillSlide: $('pill-slide'),
  pillUsage: $('pill-usage'),
  pillConn: $('pill-conn'),
  pillBuffer: $('pill-buffer'),
  micLevel: $('mic-level'),
  deck: $('deck'),
  deckInfo: $('deck-info'),
  transcript: $('transcript'),
  log: $('log'),
  keys: $('keys'),
};

const params = new URLSearchParams(location.search);
const LOOPBACK = params.get('loopback') === '1';

const state = {
  ws: null,
  wsRetry: 0,
  snapshot: { state: 'idle', slideIndex: 0, slideCount: 0, muted: false },
  presentations: [],
  current: null, // { id, meta, slides }
  deckCount: 0,
  audio: null, // { context, capture, playback }
  lastTurn: null,
};

// ---- logging -------------------------------------------------------------

function log(level, message) {
  const line = document.createElement('div');
  line.className = `line ${level}`;
  line.textContent = `${new Date().toTimeString().slice(0, 8)} ${message}`;
  els.log.appendChild(line);
  while (els.log.childElementCount > 400) els.log.removeChild(els.log.firstChild);
  els.log.scrollTop = els.log.scrollHeight;
}

// ---- deck ----------------------------------------------------------------

const deck = new DeckDriver(els.deck, {
  log,
  onExternalNavigate: (index) => {
    log('info', `external navigate → slide ${index + 1}`);
    if (state.snapshot.state === 'presenting' || state.snapshot.state === 'paused') send({ type: 'goto', index });
    updateSlidePill(index);
  },
});

async function loadPresentationList() {
  const res = await fetch('/api/presentations');
  state.presentations = await res.json();
  els.presentation.innerHTML = '';
  for (const p of state.presentations) {
    const opt = document.createElement('option');
    opt.value = p.id;
    opt.textContent = p.error ? `${p.id} (error: ${p.error})` : `${p.title} (${p.slideCount} slides)`;
    opt.disabled = Boolean(p.error);
    els.presentation.appendChild(opt);
  }
  const wanted = params.get('p') || localStorage.getItem('presenter-ai:presentation');
  if (wanted && state.presentations.some((p) => p.id === wanted && !p.error)) els.presentation.value = wanted;
  await selectPresentation(els.presentation.value);
}

async function selectPresentation(id) {
  if (!id) return;
  localStorage.setItem('presenter-ai:presentation', id);
  const res = await fetch(`/api/presentations/${encodeURIComponent(id)}`);
  if (!res.ok) {
    log('error', `cannot load presentation ${id}: ${(await res.json()).error}`);
    return;
  }
  state.current = await res.json();
  const { meta, slides } = state.current;
  els.deckInfo.textContent = `Loading deck ${meta.deck}…`;
  const { adapter, count } = await deck.load(`/${meta.deck.replace(/^\/+/, '')}`, { driver: meta.driver });
  state.deckCount = count;
  if (!adapter) {
    els.deckInfo.textContent = `Deck loaded but not driveable (${meta.deck}).`;
  } else if (count !== slides.length) {
    log('warn', `deck has ${count} slides but the script has ${slides.length}`);
    els.deckInfo.textContent = `${meta.title} — deck ${count} slides / script ${slides.length} slides (mismatch)`;
  } else {
    els.deckInfo.textContent = `${meta.title} — ${slides.length} slides · ${adapter} driver`;
  }
  deck.goto(0);
  updateSlidePill(0);
  focusKeys();
}

// ---- websocket -----------------------------------------------------------

function connect() {
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  state.ws = ws;
  ws.addEventListener('open', () => {
    state.wsRetry = 0;
    setConn(true);
    log('info', 'connected to server');
  });
  ws.addEventListener('message', (e) => {
    if (e.data instanceof ArrayBuffer) {
      state.audio?.playback.enqueue(e.data);
      return;
    }
    let msg;
    try {
      msg = JSON.parse(e.data);
    } catch {
      return;
    }
    handleMessage(msg);
  });
  ws.addEventListener('close', () => {
    setConn(false);
    state.ws = null;
    const delay = Math.min(10_000, 500 * 2 ** state.wsRetry++);
    log('warn', `server connection closed; retrying in ${Math.round(delay / 1000)} s`);
    applySnapshot({ ...state.snapshot, state: 'idle' });
    setTimeout(connect, delay);
  });
  ws.addEventListener('error', () => {});
}

function send(obj) {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(obj));
  else log('error', 'not connected to server');
}

function sendAudioFrame(buffer) {
  if (LOOPBACK) {
    state.audio?.playback.enqueue(buffer);
    return;
  }
  if (state.snapshot.state !== 'presenting') return;
  if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(buffer);
}

function handleMessage(msg) {
  switch (msg.type) {
    case 'state':
      applySnapshot(msg);
      break;
    case 'slide':
      deck.goto(msg.index);
      updateSlidePill(msg.index);
      focusKeys();
      break;
    case 'transcript':
      appendTranscript(msg);
      break;
    case 'usage':
      els.pillUsage.textContent = `${msg.seconds} s`;
      break;
    case 'closed':
      log('info', `session closed (${msg.reason}); usage ${msg.seconds ?? 'unconfirmed'} s`);
      if (msg.seconds != null) els.pillUsage.textContent = `${msg.seconds} s`;
      stopAudio();
      break;
    case 'log':
      log(msg.level, msg.message);
      break;
    case 'error':
      log('error', `${msg.code ? `[${msg.code}] ` : ''}${msg.message}`);
      break;
    case 'pong':
      break;
    default:
      log('warn', `unknown message ${msg.type}`);
  }
}

// ---- UI state ------------------------------------------------------------

function setConn(ok) {
  els.pillConn.dataset.ok = ok ? '1' : '0';
  els.pillConn.textContent = ok ? 'server: connected' : 'server: disconnected';
}

function applySnapshot(snap) {
  state.snapshot = snap;
  const s = snap.state;
  els.pillState.dataset.state = s;
  els.pillState.textContent = s === 'presenting' && snap.sessionId ? `presenting · ${snap.sessionId}` : s;
  if (snap.slideCount) updateSlidePill(snap.slideIndex, snap.slideCount);
  if (typeof snap.usageSeconds === 'number') els.pillUsage.textContent = `${snap.usageSeconds} s`;

  const live = s === 'presenting' || s === 'paused';
  els.start.disabled = s !== 'idle';
  els.presentation.disabled = s !== 'idle';
  els.pause.disabled = !live;
  els.pause.textContent = s === 'paused' ? 'Resume' : 'Pause';
  els.pause.classList.toggle('active', s === 'paused');
  els.prev.disabled = !live;
  els.next.disabled = !live;
  els.mute.disabled = !live;
  els.mute.textContent = snap.muted ? 'Unmute' : 'Mute';
  els.mute.classList.toggle('active', Boolean(snap.muted));
  els.end.disabled = s === 'idle' || s === 'ending';
  state.audio?.capture.setMuted(Boolean(snap.muted) || s !== 'presenting');
}

function updateSlidePill(index, count = state.snapshot.slideCount || state.current?.slides.length || 0) {
  els.pillSlide.textContent = `slide ${index + 1}/${count || '–'}`;
}

function appendTranscript({ role, delta, start_ms, end_ms }) {
  const last = state.lastTurn;
  if (last && last.role === role && start_ms - last.endMs < 1000) {
    last.el.querySelector('.text').textContent += delta;
    last.endMs = end_ms;
  } else {
    const el = document.createElement('div');
    el.className = `turn ${role}`;
    el.innerHTML = `<div class="who"></div><div class="text"></div>`;
    el.querySelector('.who').textContent = role === 'user' ? 'You' : 'Presenter';
    el.querySelector('.text').textContent = delta;
    els.transcript.appendChild(el);
    while (els.transcript.childElementCount > 200) els.transcript.removeChild(els.transcript.firstChild);
    state.lastTurn = { role, el, endMs: end_ms };
  }
  els.transcript.scrollTop = els.transcript.scrollHeight;
}

function focusKeys() {
  els.keys.focus({ preventScroll: true });
}

// ---- audio ---------------------------------------------------------------

async function startAudio() {
  if (state.audio) return state.audio;
  const context = createAudioContext();
  const playback = new AudioPlayback({ context, onBuffered: (ms) => (els.pillBuffer.textContent = `buf ${ms} ms`) });
  try {
    await resumeWithTimeout(context);
    await playback.start();
  } catch (err) {
    context.close().catch(() => {});
    throw err;
  }
  const capture = new AudioCapture({
    context,
    onFrame: countedSend,
    onLevel: (v) => (els.micLevel.style.width = `${Math.min(100, Math.round(v * 140))}%`),
  });
  state.audio = { context, capture, playback, micReady: false };
  log('info', `audio started (context ${context.sampleRate} Hz${LOOPBACK ? ', LOOPBACK' : ''}); requesting microphone…`);
  // Do not block the presentation on the permission prompt: the mic joins whenever it is granted.
  capture
    .start()
    .then(() => {
      if (state.audio?.capture !== capture) {
        capture.stop(); // audio was stopped meanwhile: release the mic
        return;
      }
      state.audio.micReady = true;
      capture.setMuted(Boolean(state.snapshot.muted) || state.snapshot.state !== 'presenting');
      log('info', 'microphone ready — you can interrupt by voice');
    })
    .catch((err) => log('warn', `microphone unavailable (${err.name}: ${err.message}) — listen-only mode; allow the mic and press Start next time`));
  return state.audio;
}

function stopAudio() {
  if (!state.audio) return;
  state.audio.capture.stop();
  state.audio.playback.stop();
  state.audio.context.close().catch(() => {});
  state.audio = null;
  els.micLevel.style.width = '0%';
  els.pillBuffer.textContent = 'buf 0 ms';
}

// ---- actions -------------------------------------------------------------

async function onStart() {
  if (!state.current) return log('error', 'no presentation selected');
  try {
    await startAudio();
  } catch (err) {
    log('error', `microphone/audio failed: ${err.message}`);
    return;
  }
  els.transcript.innerHTML = '';
  state.lastTurn = null;
  send({ type: 'start', presentation: state.current.id });
  focusKeys();
}

function onPauseToggle() {
  const s = state.snapshot.state;
  if (s === 'presenting') send({ type: 'pause' });
  else if (s === 'paused') send({ type: 'resume' });
}

function onMuteToggle() {
  send({ type: state.snapshot.muted ? 'unmute' : 'mute' });
}

els.start.addEventListener('click', onStart);
els.pause.addEventListener('click', onPauseToggle);
els.prev.addEventListener('click', () => send({ type: 'prev' }));
els.next.addEventListener('click', () => send({ type: 'next' }));
els.mute.addEventListener('click', onMuteToggle);
els.end.addEventListener('click', () => send({ type: 'end' }));
els.presentation.addEventListener('change', (e) => selectPresentation(e.target.value));

window.addEventListener('keydown', (e) => {
  const tag = document.activeElement?.tagName;
  if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
  const live = state.snapshot.state === 'presenting' || state.snapshot.state === 'paused';
  switch (e.key) {
    case ' ':
      e.preventDefault();
      if (live) onPauseToggle();
      break;
    case 'ArrowRight':
    case 'PageDown':
      e.preventDefault();
      if (live) send({ type: 'next' });
      break;
    case 'ArrowLeft':
    case 'PageUp':
      e.preventDefault();
      if (live) send({ type: 'prev' });
      break;
    case 'm':
    case 'M':
      if (live) onMuteToggle();
      break;
    case 's':
    case 'S':
      if (state.snapshot.state === 'idle') onStart();
      break;
    case 'Escape':
      if (state.snapshot.state !== 'idle') send({ type: 'end' });
      break;
    default:
      return;
  }
});

window.addEventListener('beforeunload', () => {
  if (state.snapshot.state !== 'idle') send({ type: 'end' });
});

// ---- boot ----------------------------------------------------------------

// Read-only status for automated checks (scripts/browser-e2e.mjs) and quick console debugging.
let audioFramesSent = 0;
const _sendAudioFrame = sendAudioFrame;
window.__presenterDebug = () => ({
  snapshot: state.snapshot,
  presentation: state.current?.id ?? null,
  deckAdapter: deck.adapterName,
  deckCount: state.deckCount,
  deckIndex: deck.currentIndex,
  audio: state.audio
    ? { sampleRate: state.audio.context.sampleRate, contextState: state.audio.context.state, micReady: state.audio.micReady, bufferedMs: state.audio.playback.bufferedMs, framesSent: audioFramesSent }
    : null,
  transcriptTurns: els.transcript.childElementCount,
  transcriptText: els.transcript.textContent.slice(-400),
  logTail: [...els.log.children].slice(-8).map((el) => el.textContent),
  wsOpen: Boolean(state.ws && state.ws.readyState === WebSocket.OPEN),
});
// count frames actually shipped to the server
function countedSend(buffer) {
  if (!LOOPBACK && state.snapshot.state === 'presenting' && state.ws?.readyState === WebSocket.OPEN) audioFramesSent++;
  _sendAudioFrame(buffer);
}

log('info', LOOPBACK ? 'loopback mode: microphone is routed to the speakers' : 'ready');
loadPresentationList().catch((err) => log('error', `cannot list presentations: ${err.message}`));
connect();
