import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parsePresentation, chunkText } from '../src/server/script-parser.js';

const norm = (s) => s.replace(/\s+/g, ' ').trim();

const SAMPLE = `---
title: Demo
deck: decks/x/index.html
voice: gleam
context: ctx.md
advanceSilenceMs: 1500
chunkChars: 900
---
intro text that is ignored

## Slide 1 — Cover
Hello there.
Second line.

> notes: first note line
> second note line
after notes still narration

## Slide 2
Just narration.
## Slide 3: Colon title
> notes: only notes
`;

test('parses frontmatter, headings, narration and notes', () => {
  const { meta, slides } = parsePresentation(SAMPLE, { id: 'demo' });
  assert.equal(meta.id, 'demo');
  assert.equal(meta.title, 'Demo');
  assert.equal(meta.deck, 'decks/x/index.html');
  assert.equal(meta.voice, 'gleam');
  assert.equal(meta.context, 'ctx.md');
  assert.equal(meta.advanceSilenceMs, 1500);
  assert.equal(meta.chunkChars, 900);
  assert.equal(meta.driver, 'auto');

  assert.equal(slides.length, 3);
  assert.deepEqual(slides.map((s) => s.index), [0, 1, 2]);
  assert.equal(slides[0].title, 'Cover');
  assert.equal(slides[0].narration, 'Hello there.\nSecond line.\n\nafter notes still narration');
  assert.equal(slides[0].notes, 'first note line\nsecond note line');
  assert.equal(slides[1].title, '');
  assert.equal(slides[1].narration, 'Just narration.');
  assert.equal(slides[1].notes, '');
  assert.equal(slides[2].title, 'Colon title');
  assert.equal(slides[2].narration, '');
  assert.equal(slides[2].notes, 'only notes');
});

test('defaults: chunkChars 1400, driver auto, title falls back to id', () => {
  const { meta } = parsePresentation('---\ndeck: d.html\n---\n## Slide 1\nx', { id: 'abc' });
  assert.equal(meta.chunkChars, 1400);
  assert.equal(meta.title, 'abc');
  assert.equal(meta.advanceSilenceMs, undefined);
});

test('rejects non-contiguous slide numbers', () => {
  assert.throws(
    () => parsePresentation('---\ndeck: d\n---\n## Slide 1\na\n## Slide 3\nb', { id: 'p' }),
    /contiguous.*Slide 3.*position 2/,
  );
});

test('rejects missing slides and missing deck', () => {
  assert.throws(() => parsePresentation('---\ndeck: d\n---\nno slides', { id: 'p' }), /no "## Slide N"/);
  assert.throws(() => parsePresentation('## Slide 1\nx', { id: 'p' }), /"deck" is required/);
});

test('chunkText: short text is one chunk, empty is none', () => {
  assert.deepEqual(chunkText('Hello world.', 100), ['Hello world.']);
  assert.deepEqual(chunkText('   ', 100), []);
});

test('chunks long narration at sentence boundaries <= maxChars and round-trips', () => {
  const sentences = [];
  for (let i = 0; i < 40; i++) sentences.push(`Sentence number ${i} says something moderately long about topic ${i % 7}.`);
  const text = sentences.join(' ');
  const chunks = chunkText(text, 300);
  assert.ok(chunks.length > 1);
  for (const c of chunks) {
    assert.ok(c.length <= 300, `chunk too long: ${c.length}`);
    assert.match(c, /[.!?]$/, 'chunk should end at a sentence boundary');
  }
  assert.equal(norm(chunks.join(' ')), norm(text));
});

test('chunkText never cuts inside a word for an over-long sentence', () => {
  const words = Array.from({ length: 200 }, (_, i) => `w${i}`);
  const text = words.join(' ') + '.';
  const chunks = chunkText(text, 120);
  for (const c of chunks) {
    assert.ok(c.length <= 120);
    assert.ok(!c.startsWith(' ') && !c.endsWith(' '));
  }
  assert.equal(norm(chunks.join(' ')), norm(text));
  const rejoined = chunks.join(' ').split(' ').filter(Boolean);
  assert.deepEqual(rejoined.slice(0, 5), ['w0', 'w1', 'w2', 'w3', 'w4']);
});

test('parses the ricoh presentation into 11 contiguous slides', () => {
  const md = readFileSync(new URL('../presentations/ricoh-delivery-overview.md', import.meta.url), 'utf8');
  const { meta, slides } = parsePresentation(md, { id: 'ricoh-delivery-overview' });
  assert.equal(slides.length, 11);
  assert.equal(meta.deck, 'decks/ricoh/index.html');
  assert.equal(meta.context, 'presentations/ricoh-context.md');
  assert.deepEqual(slides.map((s) => s.number), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
  for (const s of slides) {
    assert.ok(s.title, `slide ${s.number} has a title`);
    assert.ok(s.narration.length > 50, `slide ${s.number} has narration`);
    assert.ok(s.notes.length > 0, `slide ${s.number} has notes`);
    const chunks = chunkText(s.narration, meta.chunkChars);
    assert.ok(chunks.length <= 3, `slide ${s.number} needs ${chunks.length} chunks`);
    assert.equal(norm(chunks.join(' ')), norm(s.narration));
  }
});

test('sample presentation parses and its long slide chunks', () => {
  const md = readFileSync(new URL('../presentations/sample.md', import.meta.url), 'utf8');
  const { meta, slides } = parsePresentation(md, { id: 'sample' });
  assert.equal(slides.length, 3);
  assert.equal(meta.deck, 'decks/sample/index.html');
  const chunks = chunkText(slides[1].narration, meta.chunkChars);
  assert.ok(chunks.length >= 2, 'slide 2 should need chunking');
  assert.equal(norm(chunks.join(' ')), norm(slides[1].narration));
});
