import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pcmRms, isVoiced } from '../src/server/audio-util.js';

export function voicedFrame(bytes = 4800, amplitude = 3000) {
  const buf = Buffer.alloc(bytes);
  for (let i = 0; i < bytes / 2; i++) buf.writeInt16LE(Math.round(Math.sin(i / 5) * amplitude), i * 2);
  return buf;
}

test('digital silence is not voiced; a tone is', () => {
  assert.equal(pcmRms(Buffer.alloc(4800)), 0);
  assert.equal(isVoiced(Buffer.alloc(4800)), false);
  assert.equal(isVoiced(Buffer.alloc(0)), false);
  assert.ok(pcmRms(voicedFrame()) > 1000);
  assert.equal(isVoiced(voicedFrame()), true);
  assert.equal(isVoiced(voicedFrame(4800, 40)), false, 'near-silent noise floor stays below threshold');
});
