import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resolveLiveUrl, authHeaders, isAzureHost, loadConfig } from '../src/server/config.js';

test('azure foundry host without path -> /openai/v1/live/sessions', () => {
  assert.equal(
    resolveLiveUrl('https://x.services.ai.azure.com'),
    'wss://x.services.ai.azure.com/openai/v1/live/sessions',
  );
  assert.equal(
    resolveLiveUrl('https://x.services.ai.azure.com/'),
    'wss://x.services.ai.azure.com/openai/v1/live/sessions',
  );
});

test('azure openai host -> /openai/v1/live/sessions', () => {
  assert.equal(
    resolveLiveUrl('https://x.openai.azure.com'),
    'wss://x.openai.azure.com/openai/v1/live/sessions',
  );
});

test('openai direct -> /v1/live/sessions', () => {
  assert.equal(resolveLiveUrl('https://api.openai.com'), 'wss://api.openai.com/v1/live/sessions');
});

test('gateway host -> /v1/live/sessions', () => {
  assert.equal(
    resolveLiveUrl('https://ai-gateway.vercel.sh'),
    'wss://ai-gateway.vercel.sh/v1/live/sessions',
  );
});

test('explicit live/sessions path is kept', () => {
  assert.equal(
    resolveLiveUrl('wss://example.com/custom/live/sessions'),
    'wss://example.com/custom/live/sessions',
  );
  assert.equal(
    resolveLiveUrl('http://127.0.0.1:4321/v1/live/sessions'),
    'ws://127.0.0.1:4321/v1/live/sessions',
  );
});

test('http -> ws', () => {
  assert.equal(resolveLiveUrl('http://localhost:9000'), 'ws://localhost:9000/v1/live/sessions');
});

test('invalid endpoint throws', () => {
  assert.throws(() => resolveLiveUrl('not a url'), /not a valid URL/);
  assert.throws(() => resolveLiveUrl('ftp://x.com'), /unsupported scheme/);
});

test('auth headers: azure gets api-key too, others bearer only', () => {
  assert.deepEqual(authHeaders('k', 'wss://x.services.ai.azure.com/openai/v1/live/sessions'), {
    Authorization: 'Bearer k',
    'api-key': 'k',
  });
  assert.deepEqual(authHeaders('k', 'wss://api.openai.com/v1/live/sessions'), {
    Authorization: 'Bearer k',
  });
  assert.equal(isAzureHost('foo.azure.us'), true);
  assert.equal(isAzureHost('azure.com.evil.io'), false);
});

test('loadConfig throws naming the missing variable', () => {
  assert.throws(() => loadConfig({ UPSTREAM_KEY: 'k' }), /Missing required env: UPSTREAM_ENDPOINT/);
  assert.throws(
    () => loadConfig({ UPSTREAM_ENDPOINT: 'https://api.openai.com', UPSTREAM_KEY: '  ' }),
    /Missing required env: UPSTREAM_KEY/,
  );
});

test('loadConfig applies defaults and parses numbers', () => {
  const cfg = loadConfig({ UPSTREAM_ENDPOINT: 'https://api.openai.com', UPSTREAM_KEY: 'k' });
  assert.equal(cfg.liveUrl, 'wss://api.openai.com/v1/live/sessions');
  assert.equal(cfg.model, 'gpt-live-1');
  assert.equal(cfg.voice, 'marin');
  assert.equal(cfg.port, 47913);
  assert.equal(cfg.advanceSilenceMs, 3000);
  assert.equal(cfg.logEvents, false);

  const cfg2 = loadConfig({
    UPSTREAM_ENDPOINT: 'https://x.services.ai.azure.com',
    UPSTREAM_KEY: 'k',
    UPSTREAM_MODEL: 'my-deployment',
    UPSTREAM_VOICE: 'gleam',
    PORT: '4000',
    ADVANCE_SILENCE_MS: '1500',
    LOG_EVENTS: '1',
  });
  assert.equal(cfg2.model, 'my-deployment');
  assert.equal(cfg2.voice, 'gleam');
  assert.equal(cfg2.port, 4000);
  assert.equal(cfg2.advanceSilenceMs, 1500);
  assert.equal(cfg2.logEvents, true);
  assert.equal(cfg2.headers['api-key'], 'k');
});

test('fallback upstream from FALLBACK_OPENAI_KEY', () => {
  const none = loadConfig({ UPSTREAM_ENDPOINT: 'https://x.services.ai.azure.com', UPSTREAM_KEY: 'k' });
  assert.equal(none.fallback, null);
  assert.equal(none.upstreams.length, 1);
  assert.equal(none.upstreams[0].name, 'primary');

  const cfg = loadConfig({
    UPSTREAM_ENDPOINT: 'https://x.services.ai.azure.com',
    UPSTREAM_KEY: 'k',
    UPSTREAM_MODEL: 'dep',
    FALLBACK_OPENAI_KEY: 'sk-fb',
  });
  assert.equal(cfg.upstreams.length, 2);
  assert.equal(cfg.upstreams[0].model, 'dep');
  assert.deepEqual(cfg.fallback, {
    name: 'fallback',
    liveUrl: 'wss://api.openai.com/v1/live/sessions',
    headers: { Authorization: 'Bearer sk-fb' },
    model: 'gpt-live-1',
  });

  const custom = loadConfig({
    UPSTREAM_ENDPOINT: 'https://api.openai.com',
    UPSTREAM_KEY: 'k',
    FALLBACK_OPENAI_KEY: 'sk-fb',
    FALLBACK_OPENAI_ENDPOINT: 'https://gw.example.com',
    FALLBACK_OPENAI_MODEL: 'openai/gpt-live-1',
  });
  assert.equal(custom.fallback.liveUrl, 'wss://gw.example.com/v1/live/sessions');
  assert.equal(custom.fallback.model, 'openai/gpt-live-1');
});
