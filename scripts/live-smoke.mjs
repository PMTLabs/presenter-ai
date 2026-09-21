// Connectivity check against the real GPT-Live endpoint from .env — no browser needed.
// Opens a session, asks the model to say one sentence, prints transcript + audio timing, closes.
// Usage: node scripts/live-smoke.mjs [--fallback]   (costs a few seconds of session time)

import 'dotenv/config';
import { writeFileSync } from 'node:fs';
import { loadConfig } from '../src/server/config.js';
import { LiveSession } from '../src/server/live-client.js';

const config = loadConfig(process.env);
const useFallback = process.argv.includes('--fallback');
const upstream = useFallback ? config.fallback : config.upstreams[0];
if (!upstream) {
  console.error('no fallback configured (set FALLBACK_OPENAI_KEY in .env)');
  process.exit(1);
}
console.log(`upstream: ${upstream.name}  ${upstream.liveUrl}`);
console.log(`model: ${upstream.model}  voice: ${config.voice}`);

const session = new LiveSession({
  url: upstream.liveUrl,
  headers: upstream.headers,
  logEvents: config.logEvents,
  log: (m) => console.log(`  [live] ${m}`),
  session: {
    model: upstream.model,
    instructions: 'You are a test presenter. Speak only when instructed, briefly, in English.',
    audio: { output: { voice: config.voice } },
    delegation: { type: 'client' },
  },
});

const t0 = Date.now();
let audioBytes = 0;
let deltas = 0;
let transcript = '';
let firstAudioAt = 0;
let lastAudioAt = 0;
const chunks = [];
const timeline = [];

session.on('audio', (buf, startMs, endMs) => {
  const now = Date.now();
  if (!firstAudioAt) firstAudioAt = now;
  audioBytes += buf.length;
  deltas++;
  lastAudioAt = now;
  chunks.push(buf);
  timeline.push(`+${now - t0}ms ${(buf.length / 48).toFixed(0)}ms-of-audio${startMs != null ? ` [${startMs}-${endMs}]` : ''}`);
});
session.on('transcript', (role, delta) => {
  if (role === 'assistant') transcript += delta;
});
session.on('upstream-error', (e) => console.log(`  upstream error: ${JSON.stringify(e)}`));

try {
  const started = await session.connect();
  console.log(`session.started: id=${started.id} expires_at=${started.expires_at} (+${Date.now() - t0}ms)`);
  session.appendInstructions('Say exactly: "Presenter AI smoke test successful, one two three four five." Then stop.', { eventId: 'smoke-1' });

  // wait until audio has been silent for 3 s (max 25 s)
  const deadline = Date.now() + 25_000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 200));
    if (deltas > 0 && Date.now() - lastAudioAt > 3000) break;
  }
  const result = await session.close();
  console.log(`closed: reason=${result.reason} usage=${result.seconds}s silence-inserted=${session.silenceMs}ms`);
  const audioSec = audioBytes / 48000;
  const wallSec = firstAudioAt ? (lastAudioAt - firstAudioAt) / 1000 : 0;
  console.log(`audio: ${deltas} deltas, ${audioBytes} bytes ≈ ${audioSec.toFixed(2)} s of speech, received over ${wallSec.toFixed(2)} s wall time (first delta +${firstAudioAt - t0}ms)`);
  console.log(`delta timeline: ${timeline.slice(0, 12).join(' | ')}${timeline.length > 12 ? ' | …' : ''}`);
  console.log(`transcript: ${JSON.stringify(transcript.trim())}`);
  if (chunks.length) {
    writeFileSync('smoke-output.pcm', Buffer.concat(chunks));
    console.log('wrote smoke-output.pcm (play: ffplay -f s16le -ar 24000 -ac 1 smoke-output.pcm)');
  }
  process.exit(deltas > 0 ? 0 : 2);
} catch (err) {
  console.error(`FAILED: ${err.message}`);
  if (err.upstream) console.error(JSON.stringify(err.upstream, null, 2));
  session.terminate();
  process.exit(1);
}
