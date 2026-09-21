import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildSystemInstructions,
  buildSlideInstruction,
  buildNotesContext,
  buildResumeInstruction,
  buildWrapUpInstruction,
  buildNudgeInstruction,
  buildPauseInstruction,
} from '../src/server/prompt.js';

const slides = [
  { index: 0, title: 'Cover' },
  { index: 1, title: '' },
  { index: 2, title: 'End' },
];

test('system instructions contain title, outline and context', () => {
  const text = buildSystemInstructions({ title: 'My Talk', slides, context: 'Facts here.' });
  assert.match(text, /titled "My Talk"/);
  assert.match(text, /1\. Cover\n2\. \(untitled\)\n3\. End/);
  assert.match(text, /Background context[\s\S]*Facts here\./);
  assert.match(text, /Back to the slide/);
});

test('system instructions omit context section when empty', () => {
  const text = buildSystemInstructions({ title: 'T', slides, context: '' });
  assert.doesNotMatch(text, /Background context/);
});

test('context is truncated to the budget with a warning', () => {
  const warnings = [];
  const text = buildSystemInstructions({
    title: 'T',
    slides,
    context: 'x'.repeat(1000),
    maxContextChars: 100,
    onWarn: (m) => warnings.push(m),
  });
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /truncated from 1000 to 100/);
  assert.match(text, /\[context truncated\]/);
  assert.ok(text.includes('x'.repeat(100)));
  assert.ok(!text.includes('x'.repeat(101)));
});

test('single-part slide instruction contains narration verbatim', () => {
  const text = buildSlideInstruction({ index: 1, total: 3, title: 'Two', chunk: 'Say this. And that.' });
  assert.match(text, /Present slide 2 of 3 \("Two"\) now/);
  assert.match(text, /"""\nSay this\. And that\.\n"""/);
  assert.doesNotMatch(text, /Stop whatever/);
});

test('multi-part slide instructions carry part k of K labels', () => {
  const p1 = buildSlideInstruction({ index: 0, total: 2, title: '', chunk: 'A', part: 1, parts: 3 });
  const p2 = buildSlideInstruction({ index: 0, total: 2, title: '', chunk: 'B', part: 2, parts: 3 });
  const p3 = buildSlideInstruction({ index: 0, total: 2, title: '', chunk: 'C', part: 3, parts: 3 });
  assert.match(p1, /comes in 3 parts; this is part 1/);
  assert.match(p2, /Part 2 of 3 of slide 1 of 2\./);
  assert.match(p2, /finish it first/);
  assert.match(p2, /part 3 will follow/);
  assert.match(p3, /Part 3 of 3 of slide 1 of 2, the last part/);
  assert.match(p3, /then stop and wait/);
});

test('interrupt flag prefixes a stop instruction', () => {
  const text = buildSlideInstruction({ index: 0, total: 1, chunk: 'x', interrupt: true });
  assert.match(text, /^Stop whatever you are saying now\. Present slide 1 of 1 now/);
});

test('other instruction builders', () => {
  assert.match(buildNotesContext({ index: 0, total: 2, title: 'A', notes: 'n1' }), /Speaker notes for slide 1 of 2 \("A"\)[\s\S]*n1/);
  assert.match(buildResumeInstruction({ index: 2, total: 5 }), /Resume slide 3 of 5 from where you left off/);
  assert.match(buildWrapUpInstruction(), /last slide/);
  assert.match(buildNudgeInstruction({ index: 0, total: 1 }), /Begin presenting slide 1 of 1 now/);
  assert.match(buildPauseInstruction(), /Pause now/);
});
