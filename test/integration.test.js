// Real Express + ws bridge + Presenter + LiveSession, with only GPT-Live faked.

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { WebSocket } from 'ws';
import { startFakeLiveServer } from './fake-live-server.js';
import { loadConfig } from '../src/server/config.js';
import { createServer } from '../src/server/index.js';

let fake;
let srv;
let port;

before(async () => {
  fake = startFakeLiveServer();
  const config = loadConfig({
    UPSTREAM_ENDPOINT: fake.url,
    UPSTREAM_KEY: 'test-key',
    ADVANCE_SILENCE_MS: '200',
    PORT: '0',
  });
  srv = createServer({ config });
  port = await srv.listen(0);
});

after(async () => {
  await srv.close();
  await fake.close();
});

async function upstreamReceived(pred, timeoutMs = 2000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const hit = fake.received.find(pred);
    if (hit) return hit;
    await new Promise((r) => setTimeout(r, 10));
  }
  throw new Error('timeout waiting for upstream event');
}

function client() {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  ws.binaryType = 'nodebuffer';
  const queue = [];
  const waiters = [];
  ws.on('message', (data, isBinary) => {
    const item = isBinary ? { type: '__binary', data } : JSON.parse(data.toString());
    const w = waiters.findIndex((x) => x.pred(item));
    if (w >= 0) waiters.splice(w, 1)[0].resolve(item);
    else queue.push(item);
  });
  const next = (pred, timeoutMs = 3000, label = 'message') => {
    const i = queue.findIndex(pred);
    if (i >= 0) return Promise.resolve(queue.splice(i, 1)[0]);
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error(`timeout waiting for ${label}`)), timeoutMs);
      waiters.push({
        pred,
        resolve: (v) => {
          clearTimeout(t);
          resolve(v);
        },
      });
    });
  };
  const open = new Promise((r) => ws.on('open', r));
  return {
    ws,
    open,
    next,
    send: (o) => ws.send(JSON.stringify(o)),
    sendBinary: (buf) => ws.send(buf, { binary: true }),
    close: () => ws.close(),
    drain: () => queue.splice(0),
  };
}

test('HTTP: presentations API and static deck', async () => {
  const list = await (await fetch(`http://127.0.0.1:${port}/api/presentations`)).json();
  assert.ok(list.some((p) => p.id === 'sample' && p.slideCount === 3));
  assert.ok(list.some((p) => p.id === 'ricoh-delivery-overview' && p.slideCount === 11));
  const deck = await fetch(`http://127.0.0.1:${port}/decks/sample/index.html`);
  assert.equal(deck.status, 200);
  const page = await fetch(`http://127.0.0.1:${port}/`);
  assert.equal(page.status, 200);
  assert.match(await page.text(), /Presenter/);
  const missing = await fetch(`http://127.0.0.1:${port}/api/presentations/nope`);
  assert.equal(missing.status, 404);
});

test('start → session.started → slide 0 → audio → auto-advance; next/goto; audio relay; pause; end', async () => {
  const c = client();
  await c.open;
  const first = await c.next((m) => m.type === 'state', 2000, 'initial state');
  assert.equal(first.state, 'idle');

  c.send({ type: 'start', presentation: 'sample' });
  const presenting = await c.next((m) => m.type === 'state' && m.state === 'presenting', 3000, 'presenting');
  assert.equal(presenting.sessionId, 'sess_fake');
  assert.equal(presenting.slideCount, 3);
  const slide0 = await c.next((m) => m.type === 'slide', 2000, 'slide 0');
  assert.equal(slide0.index, 0);

  // upstream received session.start with our headers and config
  const start = fake.received.find((e) => e.type === 'session.start');
  assert.ok(start, 'fake got session.start');
  assert.equal(start.session.model, 'gpt-live-1');
  assert.equal(start.session.delegation.type, 'client');
  assert.deepEqual(start.session.audio, { output: { voice: 'marin' } });
  assert.match(start.session.instructions, /Presenter-AI sample/);
  assert.equal(fake.headers.authorization, 'Bearer test-key');

  const audio = await c.next((m) => m.type === '__binary', 2000, 'binary audio');
  assert.equal(audio.data.length, 960);
  const tr = await c.next((m) => m.type === 'transcript', 2000, 'transcript');
  assert.equal(tr.role, 'assistant');
  assert.match(tr.delta, /slide-1-part-1/);

  // fake sends 3 deltas 20 ms apart, then silence → advance after 200 ms
  const slide1 = await c.next((m) => m.type === 'slide' && m.index === 1, 1500, 'auto-advance to slide 1');
  assert.equal(slide1.index, 1);

  c.send({ type: 'next' });
  const slide2 = await c.next((m) => m.type === 'slide' && m.index === 2, 1000, 'manual next');
  assert.equal(slide2.index, 2);
  const nextAppend = await upstreamReceived((e) => e.type === 'session.instructions.append' && e.event_id === 'slide-3-part-1');
  assert.match(nextAppend.content, /^Stop whatever you are saying now/);

  c.send({ type: 'goto', index: 0 });
  await c.next((m) => m.type === 'slide' && m.index === 0, 1000, 'goto 0');

  // audio relay: 960 bytes + an odd 961-byte frame (the silence pump's all-zero frames are ignored)
  const isVoice = (e) => e.type === 'session.input_audio.append' && !e.silent;
  const before = fake.received.filter(isVoice).length;
  c.sendBinary(Buffer.alloc(960, 1));
  c.sendBinary(Buffer.alloc(961, 1));
  await upstreamReceived(() => fake.received.filter(isVoice).length >= before + 2);
  const appends = fake.received.filter(isVoice).slice(before);
  assert.equal(appends.length, 2);
  assert.equal(appends[0].audioLength, Buffer.alloc(960).toString('base64').length);
  assert.equal(appends[1].audioLength, Buffer.alloc(960).toString('base64').length, 'odd byte dropped');
  // the pump keeps the input timeline moving while the browser is quiet
  await upstreamReceived((e) => e.type === 'session.input_audio.append' && e.silent);

  // pause → mute upstream, no advance
  c.send({ type: 'pause' });
  const paused = await c.next((m) => m.type === 'state' && m.state === 'paused', 1000, 'paused');
  assert.equal(paused.paused, true);
  await upstreamReceived((e) => e.type === 'session.input_audio.mute');
  await new Promise((r) => setTimeout(r, 400));
  assert.ok(!c.drain().some((m) => m.type === 'slide'), 'no slide change while paused');

  c.send({ type: 'resume' });
  await c.next((m) => m.type === 'state' && m.state === 'presenting', 1000, 'resumed');
  await upstreamReceived((e) => e.type === 'session.input_audio.unmute');

  c.send({ type: 'end' });
  const closed = await c.next((m) => m.type === 'closed', 3000, 'closed');
  assert.deepEqual(closed, { type: 'closed', reason: 'close_requested', seconds: 7 });
  await upstreamReceived((e) => e.type === 'session.close');
  const idle = await c.next((m) => m.type === 'state' && m.state === 'idle', 1000, 'idle');
  assert.equal(idle.usageSeconds, 7);
  c.close();
  await new Promise((r) => c.ws.on('close', r));
});

test('second browser is refused while one is connected', async () => {
  const a = client();
  await a.open;
  await a.next((m) => m.type === 'state');
  const b = client();
  await b.open;
  const err = await b.next((m) => m.type === 'error', 1000, 'busy error');
  assert.equal(err.code, 'busy');
  await new Promise((r) => b.ws.on('close', r));
  a.close();
  await new Promise((r) => a.ws.on('close', r));
});

test('browser disconnect ends the live session', async () => {
  const c = client();
  await c.open;
  await c.next((m) => m.type === 'state');
  const closesBefore = fake.received.filter((e) => e.type === 'session.close').length;
  c.send({ type: 'start', presentation: 'sample' });
  await c.next((m) => m.type === 'state' && m.state === 'presenting', 3000, 'presenting');
  c.close();
  await new Promise((r) => setTimeout(r, 300));
  assert.equal(fake.received.filter((e) => e.type === 'session.close').length, closesBefore + 1);
  assert.equal(srv.presenter.state, 'idle');
});

test('upstream startup error is reported and the presenter returns to idle', async () => {
  const badConfig = loadConfig({ UPSTREAM_ENDPOINT: fake.url, UPSTREAM_KEY: 'k', UPSTREAM_MODEL: 'bad-model' });
  const bad = createServer({ config: badConfig });
  const p = await bad.listen(0);
  const ws = new WebSocket(`ws://127.0.0.1:${p}/ws`);
  const messages = [];
  ws.on('message', (d, bin) => !bin && messages.push(JSON.parse(d.toString())));
  try {
    await new Promise((r) => ws.on('open', r));
    ws.send(JSON.stringify({ type: 'start', presentation: 'sample' }));
    await new Promise((r) => setTimeout(r, 500));
    const err = messages.find((m) => m.type === 'error');
    assert.ok(err, 'error message sent to browser');
    assert.match(err.message, /unknown model/);
    assert.equal(err.code, 'invalid_model');
    const states = messages.filter((m) => m.type === 'state').map((m) => m.state);
    assert.deepEqual(states, ['idle', 'connecting', 'idle']);
    assert.equal(bad.presenter.state, 'idle');
    // the failed upstream socket must be gone (no lingering, billable connection)
    await upstreamReceived((e) => e.type === 'session.start' && e.session.model === 'bad-model');
    await new Promise((r) => setTimeout(r, 100));
    assert.equal([...fakeClientsOpen()].length, 0, 'upstream socket closed after startup error');
  } finally {
    ws.close();
    await bad.close();
  }
});

function* fakeClientsOpen() {
  for (const c of fake.wss.clients) if (c.readyState === WebSocket.OPEN) yield c;
}
