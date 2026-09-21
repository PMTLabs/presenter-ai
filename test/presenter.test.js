import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { Presenter, NUDGE_MS, WRAP_UP_FALLBACK_MS, partGapFor } from '../src/server/presenter.js';
import { voicedFrame } from './audio-util.test.js';

class FakeSession extends EventEmitter {
  constructor(opts) {
    super();
    this.opts = opts;
    this.sent = [];
    this.state = 'idle';
    this.id = 'sess_test';
    this.session = { id: 'sess_test', expires_at: 123 };
    this.failConnect = false;
  }
  async connect() {
    if (this.failConnect) {
      const e = new Error('startup error');
      e.upstream = { code: 'invalid_model' };
      throw e;
    }
    this.state = 'open';
    this.emit('started', this.session);
    return this.session;
  }
  sendAudio(buf) {
    this.sent.push({ type: 'audio', bytes: buf.length });
    return true;
  }
  appendInstructions(content, o = {}) {
    this.sent.push({ type: 'instructions', content, eventId: o.eventId });
    return o.eventId;
  }
  appendThinking(content, o = {}) {
    this.sent.push({ type: 'thinking', content, eventId: o.eventId });
    return o.eventId;
  }
  appendCommentary(content, o = {}) {
    this.sent.push({ type: 'commentary', content, eventId: o.eventId });
    return o.eventId;
  }
  mute() {
    this.sent.push({ type: 'mute' });
    return true;
  }
  unmute() {
    this.sent.push({ type: 'unmute' });
    return true;
  }
  async close() {
    this.sent.push({ type: 'close' });
    this.state = 'closed';
    this.emit('closed', 'close_requested', 7);
    return { reason: 'close_requested', seconds: 7 };
  }
  // test helpers
  speak(ms = 100) {
    this.emit('audio', voicedFrame(ms * 48), 0, ms);
  }
  silence(ms = 100) {
    this.emit('audio', Buffer.alloc(ms * 48), 0, ms);
  }
  hear(text = 'hi') {
    this.emit('transcript', 'user', text, 0, 100);
  }
  drop() {
    this.emit('closed', 'connection_lost', null);
  }
}

const SLIDES = [
  { index: 0, number: 1, title: 'One', narration: 'First slide text.', notes: 'n1' },
  { index: 1, number: 2, title: 'Two', narration: 'Second slide text.', notes: '' },
  { index: 2, number: 3, title: 'Three', narration: 'Third slide text.', notes: 'n3' },
];

function makePresenter({ slides = SLIDES, chunkChars = 1400, advanceSilenceMs = 2000, upstreams = 1, failAttempts = [] } = {}) {
  const sessions = [];
  const events = [];
  const presenter = new Presenter({
    createSession: (opts, attempt) => {
      if (attempt >= upstreams) return null;
      const s = new FakeSession(opts);
      s.name = attempt === 0 ? 'primary' : 'fallback';
      s.failConnect = failAttempts.includes(attempt);
      sessions.push(s);
      return s;
    },
    loadPresentation: async (id) => {
      if (id === 'missing') throw new Error('no such file');
      return { id, meta: { title: 'T', chunkChars, advanceSilenceMs }, slides, context: 'ctx' };
    },
    config: { advanceSilenceMs: 2000, voice: 'marin' },
    log: () => {},
  });
  for (const name of ['state', 'slide', 'closed', 'upstream-error', 'usage']) {
    presenter.on(name, (p) => events.push({ name, p }));
  }
  return { presenter, sessions, events, session: () => sessions[sessions.length - 1] };
}

const sentTypes = (s) => s.sent.map((x) => x.type);
const slideEvents = (events) => events.filter((e) => e.name === 'slide').map((e) => e.p);

test('started → presents slide 0 with thinking then instructions appends', async () => {
  const { presenter, session, events } = makePresenter();
  assert.equal(await presenter.start('p'), true);
  const s = session();
  assert.equal(presenter.state, 'presenting');
  assert.deepEqual(slideEvents(events), [0]);
  assert.deepEqual(sentTypes(s), ['thinking', 'instructions']);
  assert.equal(s.sent[0].eventId, 'slide-1-notes');
  assert.match(s.sent[0].content, /Speaker notes for slide 1 of 3/);
  assert.equal(s.sent[1].eventId, 'slide-1-part-1');
  assert.match(s.sent[1].content, /Present slide 1 of 3 \("One"\) now[\s\S]*First slide text\./);
  assert.match(s.opts.instructions, /titled "T"/);
  assert.equal(s.opts.voice, 'marin');
});

test('output audio then silence → advances to slide 1; without audio it does not', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p');
    const s = session();
    mock.timers.tick(2100); // no audio yet → no advance
    assert.deepEqual(slideEvents(events), [0]);
    s.speak();
    mock.timers.tick(1000);
    s.speak();
    mock.timers.tick(1999);
    assert.deepEqual(slideEvents(events), [0]);
    mock.timers.tick(2);
    assert.deepEqual(slideEvents(events), [0, 1]);
    assert.equal(presenter.slideIndex, 1);
    assert.equal(s.sent.at(-1).eventId, 'slide-2-part-1');
    assert.doesNotMatch(s.sent.at(-1).content, /Stop whatever/);
  } finally {
    mock.timers.reset();
  }
});

test('silent output frames neither start nor extend the narration', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p');
    const s = session();
    // the service streams silence continuously before the model speaks
    for (let i = 0; i < 30; i++) {
      s.silence();
      mock.timers.tick(100);
    }
    assert.deepEqual(slideEvents(events), [0], 'silence alone must not advance');
    assert.equal(presenter.heardOutput, false);
    s.speak();
    for (let i = 0; i < 15; i++) {
      s.silence(); // silence keeps flowing after speech: it must not reset the timer
      mock.timers.tick(100);
    }
    mock.timers.tick(600);
    assert.deepEqual(slideEvents(events), [0, 1]);
  } finally {
    mock.timers.reset();
  }
});

test('audience speech during the silence window delays the advance', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p');
    const s = session();
    s.speak();
    mock.timers.tick(1500);
    s.hear('question?');
    mock.timers.tick(1500); // 3000 since audio, but only 1500 since speech
    assert.deepEqual(slideEvents(events), [0]);
    mock.timers.tick(600);
    assert.deepEqual(slideEvents(events), [0, 1]);
  } finally {
    mock.timers.reset();
  }
});

test('next() clears the timer and presents the next slide immediately with interrupt', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p');
    const s = session();
    s.speak();
    assert.equal(presenter.next(), true);
    assert.deepEqual(slideEvents(events), [0, 1]);
    assert.match(s.sent.at(-1).content, /^Stop whatever you are saying now\. Present slide 2 of 3/);
    mock.timers.tick(5000); // old silence timer must not fire
    assert.deepEqual(slideEvents(events), [0, 1]);
    assert.equal(presenter.prev(), true);
    assert.deepEqual(slideEvents(events), [0, 1, 0]);
    assert.equal(presenter.goto(2), true);
    assert.deepEqual(slideEvents(events), [0, 1, 0, 2]);
    assert.equal(presenter.goto(7), false);
    assert.equal(presenter.goto(-1), false);
  } finally {
    mock.timers.reset();
  }
});

test('pause mutes and blocks the advance; resume unmutes and re-arms', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p');
    const s = session();
    s.speak();
    assert.equal(presenter.pause(), true);
    assert.equal(presenter.state, 'paused');
    assert.deepEqual(sentTypes(s).slice(-2), ['mute', 'instructions']);
    assert.match(s.sent.at(-1).content, /Pause now/);
    mock.timers.tick(30_000);
    assert.deepEqual(slideEvents(events), [0]);
    assert.equal(presenter.sendAudio(Buffer.alloc(960)), false);

    assert.equal(presenter.resume(), true);
    assert.equal(presenter.state, 'presenting');
    assert.deepEqual(sentTypes(s).slice(-2), ['unmute', 'instructions']);
    assert.match(s.sent.at(-1).content, /Resume slide 1 of 3/);
    s.speak();
    mock.timers.tick(2001);
    assert.deepEqual(slideEvents(events), [0, 1]);
  } finally {
    mock.timers.reset();
  }
});

test('user mute is kept across pause/resume and gates sendAudio', async () => {
  const { presenter, session } = makePresenter();
  await presenter.start('p');
  const s = session();
  assert.equal(presenter.sendAudio(Buffer.alloc(960)), true);
  presenter.mute();
  assert.deepEqual(sentTypes(s).at(-1), 'mute');
  assert.equal(presenter.sendAudio(Buffer.alloc(960)), false);
  presenter.pause();
  presenter.resume();
  assert.ok(!sentTypes(s).slice(-2).includes('unmute'), 'resume must not unmute a user-muted mic');
  presenter.unmute();
  assert.deepEqual(sentTypes(s).at(-1), 'unmute');
  assert.equal(presenter.sendAudio(Buffer.alloc(960)), true);
});

test('last slide silence → wrap-up, then close', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session, events } = makePresenter();
    await presenter.start('p', { fromIndex: 2 });
    const s = session();
    assert.deepEqual(slideEvents(events), [2]);
    s.speak();
    mock.timers.tick(2001);
    assert.equal(s.sent.at(-1).eventId, 'wrap-up');
    assert.equal(presenter.state, 'presenting');
    s.speak();
    mock.timers.tick(2001);
    await flush();
    assert.equal(sentTypes(s).at(-1), 'close');
    assert.equal(presenter.state, 'idle');
    const closed = events.find((e) => e.name === 'closed');
    assert.deepEqual(closed.p, { reason: 'close_requested', seconds: 7 });
    assert.equal(presenter.usage.seconds, 7);
  } finally {
    mock.timers.reset();
  }
});

test('wrap-up without any audio ends after the fallback', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session } = makePresenter();
    await presenter.start('p', { fromIndex: 2 });
    const s = session();
    presenter.next(); // at last slide → wrap-up
    assert.equal(s.sent.at(-1).eventId, 'wrap-up');
    mock.timers.tick(WRAP_UP_FALLBACK_MS + 1);
    await flush();
    assert.equal(sentTypes(s).at(-1), 'close');
    assert.equal(presenter.state, 'idle');
  } finally {
    mock.timers.reset();
  }
});

test('no output audio for 15 s → exactly one nudge', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const { presenter, session } = makePresenter();
    await presenter.start('p');
    const s = session();
    mock.timers.tick(NUDGE_MS + 1);
    assert.equal(s.sent.at(-1).eventId, 'slide-1-nudge');
    mock.timers.tick(NUDGE_MS * 2);
    assert.equal(s.sent.filter((x) => x.eventId === 'slide-1-nudge').length, 1);
    s.speak(); // audio arrives late: normal flow resumes
    mock.timers.tick(2001);
    assert.equal(presenter.slideIndex, 1);
  } finally {
    mock.timers.reset();
  }
});

test('upstream drop → idle, slide index kept, next start resumes there', async () => {
  const { presenter, session, events } = makePresenter();
  await presenter.start('p');
  presenter.goto(1);
  session().drop();
  assert.equal(presenter.state, 'idle');
  assert.equal(presenter.slideIndex, 1);
  assert.deepEqual(events.find((e) => e.name === 'closed').p, { reason: 'connection_lost', seconds: null });

  await presenter.start('p');
  assert.equal(presenter.slideIndex, 1);
  assert.equal(slideEvents(events).at(-1), 1);
});

test('normal end → next start begins at slide 0', async () => {
  const { presenter } = makePresenter();
  await presenter.start('p');
  presenter.goto(2);
  await presenter.end();
  assert.equal(presenter.state, 'idle');
  await presenter.start('p');
  assert.equal(presenter.slideIndex, 0);
});

test('long narration → parts are sent one at a time, gated by speech gaps; advance only after the last', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const long = Array.from({ length: 60 }, (_, i) => `Sentence ${i} is here.`).join(' ');
    const slides = [
      { index: 0, number: 1, title: 'L', narration: long, notes: '' },
      { index: 1, number: 2, title: 'M', narration: 'short', notes: '' },
    ];
    const { presenter, session, events } = makePresenter({ slides, chunkChars: 300 });
    await presenter.start('p');
    const s = session();
    const instr = () => s.sent.filter((x) => x.type === 'instructions');
    const K = presenter.parts.length;
    assert.ok(K >= 3);
    assert.equal(instr().length, 1, 'only part 1 is sent up front');
    assert.equal(instr()[0].eventId, 'slide-1-part-1');
    assert.match(instr()[0].content, new RegExp(`comes in ${K} parts; this is part 1`));

    // no speech yet → nothing more is sent, and no advance
    mock.timers.tick(5000);
    assert.equal(instr().length, 1);

    const gap = partGapFor(2000);
    for (let k = 2; k <= K; k++) {
      s.speak();
      mock.timers.tick(gap - 1);
      assert.equal(instr().length, k - 1, `part ${k} waits for the gap`);
      mock.timers.tick(2);
      assert.equal(instr().length, k, `part ${k} sent after the gap`);
      assert.equal(instr().at(-1).eventId, `slide-1-part-${k}`);
    }
    assert.match(instr().at(-1).content, new RegExp(`Part ${K} of ${K}.*the last part`));
    assert.deepEqual(slideEvents(events), [0], 'no advance while parts were pending');

    // after the last part: the full silence window advances
    s.speak();
    mock.timers.tick(1999);
    assert.deepEqual(slideEvents(events), [0]);
    mock.timers.tick(2);
    assert.deepEqual(slideEvents(events), [0, 1]);

    const joined = instr()
      .filter((p) => p.eventId.startsWith('slide-1-'))
      .map((p) => p.content.match(/"""\n([\s\S]*)\n"""/)[1])
      .join(' ');
    assert.equal(joined.replace(/\s+/g, ' '), long.replace(/\s+/g, ' '));
  } finally {
    mock.timers.reset();
  }
});

test('manual navigation drops pending parts', async () => {
  const long = Array.from({ length: 60 }, (_, i) => `Sentence ${i} is here.`).join(' ');
  const slides = [
    { index: 0, number: 1, title: 'L', narration: long, notes: '' },
    { index: 1, number: 2, title: 'M', narration: 'short', notes: '' },
  ];
  const { presenter, session } = makePresenter({ slides, chunkChars: 300 });
  await presenter.start('p');
  assert.equal(presenter.partsPending, true);
  presenter.next();
  assert.equal(presenter.partsPending, false);
  assert.equal(session().sent.filter((x) => x.type === 'instructions').at(-1).eventId, 'slide-2-part-1');
});

test('empty narration slide advances after the silence window', async () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const slides = [
      { index: 0, number: 1, title: 'A', narration: '', notes: '' },
      { index: 1, number: 2, title: 'B', narration: 'b', notes: '' },
    ];
    const { presenter, events } = makePresenter({ slides, advanceSilenceMs: 500 });
    await presenter.start('p');
    mock.timers.tick(501);
    assert.deepEqual(slideEvents(events), [0, 1]);
  } finally {
    mock.timers.reset();
  }
});

test('start while busy is ignored; load failure and connect failure return to idle', async () => {
  const { presenter, sessions, events } = makePresenter();
  await presenter.start('p');
  assert.equal(await presenter.start('p'), false);
  await presenter.end();

  assert.equal(await presenter.start('missing'), false);
  assert.equal(presenter.state, 'idle');
  assert.match(events.filter((e) => e.name === 'upstream-error').at(-1).p.message, /no such file/);

  const failing = makePresenter({ failAttempts: [0] });
  assert.equal(await failing.presenter.start('p'), false);
  assert.equal(failing.presenter.state, 'idle');
  const errs = failing.events.filter((e) => e.name === 'upstream-error');
  assert.equal(errs[0].p.code, 'invalid_model');
  assert.match(errs[0].p.message, /^primary: /);
  assert.equal(failing.sessions.length, 1);
  assert.equal(sessions.length, 1);
});

test('primary startup failure falls back to the second upstream', async () => {
  const { presenter, sessions, events } = makePresenter({ upstreams: 2, failAttempts: [0] });
  assert.equal(await presenter.start('p'), true);
  assert.equal(presenter.state, 'presenting');
  assert.equal(sessions.length, 2);
  assert.equal(sessions[1].name, 'fallback');
  assert.equal(sentTypes(sessions[0]).length, 0, 'nothing sent to the failed primary');
  assert.deepEqual(sentTypes(sessions[1]), ['thinking', 'instructions']);
  assert.equal(events.filter((e) => e.name === 'upstream-error').length, 1);

  const both = makePresenter({ upstreams: 2, failAttempts: [0, 1] });
  assert.equal(await both.presenter.start('p'), false);
  assert.equal(both.sessions.length, 2);
  assert.equal(both.presenter.state, 'idle');
});

test('close reason client_request counts as a normal end', async () => {
  const { presenter, session } = makePresenter();
  await presenter.start('p');
  presenter.goto(2);
  session().emit('closed', 'client_request', 3);
  assert.equal(presenter.lastRun.endedNormally, true);
  await presenter.start('p');
  assert.equal(presenter.slideIndex, 0);
});

function flush() {
  return new Promise((r) => setImmediate(r));
}
