// Drives the real presenter page in headless Edge/Chrome (fake microphone, no prompts) over the
// Chrome DevTools Protocol: opens the page, clicks Start, watches audio/transcript/slides, ends.
// Requires the server to be running (npm start). Usage:
//   node scripts/browser-e2e.mjs [--url http://localhost:47913] [--presentation sample] [--seconds 40]
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { WebSocket } from 'ws';

const arg = (name, def) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : def;
};
const baseUrl = arg('--url', 'http://localhost:47913');
const presentation = arg('--presentation', 'sample');
const seconds = Number(arg('--seconds', 40));
const debugPort = 47914;

const browsers = [
  process.env.BROWSER_PATH,
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
].filter((p) => p && existsSync(p));
if (!browsers.length) {
  console.error('no Edge/Chrome found; set BROWSER_PATH');
  process.exit(1);
}
const profile = mkdtempSync(path.join(tmpdir(), 'presenter-e2e-'));
const browser = spawn(browsers[0], [
  '--headless=new',
  `--remote-debugging-port=${debugPort}`,
  `--user-data-dir=${profile}`,
  '--no-first-run',
  '--disable-extensions',
  '--disable-gpu',
  '--use-fake-device-for-media-stream',
  '--use-fake-ui-for-media-stream',
  '--autoplay-policy=no-user-gesture-required',
  'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const cleanup = () => {
  try { browser.kill(); } catch {}
  setTimeout(() => { try { rmSync(profile, { recursive: true, force: true }); } catch {} }, 500);
};
process.on('exit', cleanup);

// wait for the devtools endpoint
let targets;
for (let i = 0; i < 50; i++) {
  try {
    targets = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
    if (targets.some((t) => t.type === 'page')) break;
  } catch {}
  await sleep(200);
}
const page = targets?.find((t) => t.type === 'page');
if (!page) {
  console.error('browser did not expose a page target');
  process.exit(1);
}

// minimal CDP client
const cdp = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => cdp.on('open', r));
let seq = 0;
const pending = new Map();
const consoleLines = [];
cdp.on('message', (d) => {
  const m = JSON.parse(d.toString());
  if (m.id && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  } else if (m.method === 'Runtime.consoleAPICalled') {
    consoleLines.push(`${m.params.type}: ${m.params.args.map((a) => a.value ?? a.description ?? '').join(' ')}`);
  } else if (m.method === 'Runtime.exceptionThrown') {
    consoleLines.push(`EXCEPTION: ${m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text}`);
  }
});
const call = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, (m) => (m.error ? reject(new Error(m.error.message)) : resolve(m.result)));
    cdp.send(JSON.stringify({ id, method, params }));
  });
const evaluate = async (expression) => {
  const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'evaluate failed');
  return r.result.value;
};
const status = () => evaluate('window.__presenterDebug ? JSON.stringify(window.__presenterDebug()) : "null"').then(JSON.parse);

await call('Runtime.enable');
await call('Page.enable');
console.log(`opening ${baseUrl}/?p=${presentation}`);
await call('Page.navigate', { url: `${baseUrl}/?p=${presentation}` });
await sleep(2500);

let s = await status();
console.log(`page: ws=${s?.wsOpen} deck=${s?.deckAdapter}/${s?.deckCount} presentation=${s?.presentation}`);
if (!s?.wsOpen || !s?.deckAdapter) {
  console.error('page did not initialise; console:', consoleLines);
  process.exit(1);
}

console.log('clicking Start');
await evaluate("document.getElementById('btn-start').click(); 'ok'");
await sleep(3000);
s = await status();
console.log(`after start: state=${s.snapshot.state} audio=${JSON.stringify(s.audio)} session=${s.snapshot.sessionId}`);
if (s.snapshot.state !== 'presenting') {
  console.error('did not reach presenting; log tail:', s.logTail, 'console:', consoleLines);
  process.exit(1);
}
// The fake microphone emits a tone: mute it so the model is not interrupted by beeps.
await evaluate("document.getElementById('btn-mute').click(); 'ok'");

const t0 = Date.now();
let maxBuffered = 0;
let lastSlide = -1;
while (Date.now() - t0 < seconds * 1000) {
  await sleep(2000);
  s = await status();
  maxBuffered = Math.max(maxBuffered, s.audio?.bufferedMs ?? 0);
  if (s.deckIndex !== lastSlide) {
    lastSlide = s.deckIndex;
    console.log(`+${((Date.now() - t0) / 1000).toFixed(0)}s deck on slide ${lastSlide + 1}, state=${s.snapshot.state}, muted=${s.snapshot.muted}, buffered=${s.audio?.bufferedMs}ms, framesSent=${s.audio?.framesSent}, turns=${s.transcriptTurns}`);
  }
  if (s.snapshot.state === 'idle') break;
}
s = await status();
console.log(`summary: state=${s.snapshot.state} slide=${s.deckIndex + 1}/${s.deckCount} maxBuffered=${maxBuffered}ms framesSent=${s.audio?.framesSent ?? 'n/a'} transcriptTurns=${s.transcriptTurns}`);
console.log(`transcript tail: ${JSON.stringify(s.transcriptText.slice(-300))}`);
console.log(`log tail:\n  ${s.logTail.join('\n  ')}`);
if (consoleLines.length) console.log(`browser console:\n  ${consoleLines.slice(0, 20).join('\n  ')}`);

if (s.snapshot.state !== 'idle') {
  console.log('clicking End');
  await evaluate("document.getElementById('btn-end').click(); 'ok'");
  await sleep(3000);
  s = await status();
  console.log(`after end: state=${s.snapshot.state} usage=${s.snapshot.usageSeconds}s`);
}
const exceptions = consoleLines.filter((l) => l.startsWith('EXCEPTION') || l.startsWith('error'));
console.log(exceptions.length ? `FAIL: ${exceptions.length} browser error(s)` : 'OK: no browser errors');
cdp.close();
process.exit(exceptions.length ? 1 : 0);
