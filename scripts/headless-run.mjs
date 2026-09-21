// Headless dry run of a presentation through the real server chain, without a browser or mic:
// boots the server in-process, connects as the browser would, starts the presentation and prints
// slide changes, transcript and audio stats until the session closes.
// Usage: node scripts/headless-run.mjs [presentation-id] [--max-seconds N] [--stop-after-slide N]

import 'dotenv/config';
import { WebSocket } from 'ws';
import { loadConfig } from '../src/server/config.js';
import { createServer } from '../src/server/index.js';
import { isVoiced } from '../src/server/audio-util.js';

const args = process.argv.slice(2);
const presentation = args.find((a) => !a.startsWith('--')) ?? 'sample';
const flag = (name, def) => {
  const i = args.indexOf(name);
  return i >= 0 ? Number(args[i + 1]) : def;
};
const maxSeconds = flag('--max-seconds', 300);
const stopAfterSlide = flag('--stop-after-slide', 0); // 1-based; 0 = run to the end

const config = loadConfig(process.env);
const srv = createServer({ config });
const port = await srv.listen(0);
const t0 = Date.now();
const at = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
ws.binaryType = 'nodebuffer';
let voicedMs = 0;
let silentMs = 0;
let lastRole = null;
let line = '';
let bar = ''; // one char per 100 ms of output audio for the current slide: # voiced, . silent
let barAcc = { ms: 0, voiced: 0 };
const flushBar = () => {
  if (!bar) return;
  const gaps = (bar.match(/\.{20,}/g) ?? []).map((g) => (g.length / 10).toFixed(1) + 's');
  console.log(`${at()} audio bar (100 ms/char): ${bar.replace(/\.+$/, '')}\n${at()} pauses ≥2s inside this slide: ${gaps.length ? gaps.join(', ') : 'none'}`);
  bar = '';
  barAcc = { ms: 0, voiced: 0 };
};
const flushLine = () => {
  if (line) console.log(`${at()} ${lastRole === 'user' ? 'YOU ' : 'MODEL'}: ${line.trim()}`);
  line = '';
};
const send = (o) => ws.send(JSON.stringify(o));

ws.on('open', () => {
  console.log(`${at()} connected; starting "${presentation}" (max ${maxSeconds}s)`);
  send({ type: 'start', presentation });
});
ws.on('message', (data, isBinary) => {
  if (isBinary) {
    const ms = data.length / 48;
    const v = isVoiced(data);
    if (v) voicedMs += ms;
    else silentMs += ms;
    barAcc.ms += ms;
    if (v) barAcc.voiced += ms;
    while (barAcc.ms >= 100) {
      bar += barAcc.voiced > 0 ? '#' : '.';
      barAcc.ms -= 100;
      barAcc.voiced = 0;
    }
    return;
  }
  const m = JSON.parse(data.toString());
  switch (m.type) {
    case 'state':
      console.log(`${at()} state=${m.state} slide=${m.slideIndex + 1}/${m.slideCount}${m.sessionId ? ` session=${m.sessionId}` : ''}`);
      if (m.state === 'idle' && m.sessionId === null && Date.now() - t0 > 2000) finish();
      break;
    case 'slide':
      flushLine();
      flushBar();
      console.log(`${at()} ===== SLIDE ${m.index + 1} =====`);
      if (stopAfterSlide && m.index + 1 > stopAfterSlide) {
        console.log(`${at()} stop-after-slide reached; ending`);
        send({ type: 'end' });
      }
      break;
    case 'transcript':
      if (m.role !== lastRole) {
        flushLine();
        lastRole = m.role;
      }
      line += m.delta;
      break;
    case 'log':
      if (m.level !== 'debug') console.log(`${at()} [${m.level}] ${m.message}`);
      break;
    case 'error':
      console.log(`${at()} [ERROR] ${m.code ?? ''} ${m.message}`);
      break;
    case 'usage':
      console.log(`${at()} usage: ${m.seconds}s`);
      break;
    case 'closed':
      flushLine();
      flushBar();
      console.log(`${at()} closed: reason=${m.reason} usage=${m.seconds}s; audio received: ${(voicedMs / 1000).toFixed(1)}s voiced / ${(silentMs / 1000).toFixed(1)}s silent`);
      break;
    default:
      break;
  }
});

const guard = setTimeout(() => {
  console.log(`${at()} max-seconds reached; ending`);
  send({ type: 'end' });
  setTimeout(finish, 8000);
}, maxSeconds * 1000);

async function finish() {
  clearTimeout(guard);
  flushLine();
  ws.close();
  await srv.close();
  process.exit(0);
}
process.on('SIGINT', () => {
  send({ type: 'end' });
  setTimeout(finish, 6000);
});
