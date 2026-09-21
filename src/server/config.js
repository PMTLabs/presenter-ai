// Environment loading and upstream endpoint resolution.
// Pure functions so they can be unit-tested without touching process.env.

const REQUIRED = ['UPSTREAM_ENDPOINT', 'UPSTREAM_KEY'];

const AZURE_SUFFIXES = ['.azure.com', '.azure.us', '.azure.cn'];

export function isAzureHost(host) {
  const h = String(host || '').toLowerCase();
  return AZURE_SUFFIXES.some((s) => h.endsWith(s));
}

/**
 * Turn the configured endpoint into the Live sessions WebSocket URL.
 *  - a URL that already contains "/live/sessions" is used as-is (scheme normalised)
 *  - Azure hosts get "/openai/v1/live/sessions"
 *  - everything else gets "/v1/live/sessions"
 */
export function resolveLiveUrl(endpoint) {
  if (!endpoint) throw new Error('resolveLiveUrl: endpoint is empty');
  let url;
  try {
    url = new URL(endpoint);
  } catch {
    throw new Error(`UPSTREAM_ENDPOINT is not a valid URL: ${endpoint}`);
  }
  const scheme = { 'http:': 'ws:', 'https:': 'wss:', 'ws:': 'ws:', 'wss:': 'wss:' }[url.protocol];
  if (!scheme) throw new Error(`UPSTREAM_ENDPOINT has unsupported scheme: ${url.protocol}`);
  url.protocol = scheme;

  if (!url.pathname.includes('/live/sessions')) {
    url.pathname = isAzureHost(url.hostname) ? '/openai/v1/live/sessions' : '/v1/live/sessions';
  }
  return url.toString();
}

/** Bearer auth for everyone; Azure resources also accept the api-key header. */
export function authHeaders(key, liveUrl) {
  const headers = { Authorization: `Bearer ${key}` };
  if (isAzureHost(new URL(liveUrl).hostname)) headers['api-key'] = key;
  return headers;
}

function intOr(value, fallback) {
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) ? n : fallback;
}

/** Validate and normalise configuration from an env-like object. */
export function loadConfig(env = process.env) {
  for (const name of REQUIRED) {
    if (!env[name] || !String(env[name]).trim()) {
      throw new Error(`Missing required env: ${name}`);
    }
  }
  const liveUrl = resolveLiveUrl(env.UPSTREAM_ENDPOINT.trim());
  const key = env.UPSTREAM_KEY.trim();
  const primary = {
    name: 'primary',
    liveUrl,
    headers: authHeaders(key, liveUrl),
    model: (env.UPSTREAM_MODEL || 'gpt-live-1').trim(),
  };

  // Optional second route, tried when the primary fails to start a session (rate limit, outage).
  let fallback = null;
  const fbKey = env.FALLBACK_OPENAI_KEY && String(env.FALLBACK_OPENAI_KEY).trim();
  if (fbKey) {
    const fbUrl = resolveLiveUrl((env.FALLBACK_OPENAI_ENDPOINT || 'https://api.openai.com').trim());
    fallback = {
      name: 'fallback',
      liveUrl: fbUrl,
      headers: authHeaders(fbKey, fbUrl),
      model: (env.FALLBACK_OPENAI_MODEL || 'gpt-live-1').trim(),
    };
  }

  return {
    ...primary,
    upstreams: fallback ? [primary, fallback] : [primary],
    fallback,
    voice: (env.UPSTREAM_VOICE || 'marin').trim(),
    port: intOr(env.PORT, 47913),
    advanceSilenceMs: intOr(env.ADVANCE_SILENCE_MS, 3000),
    logEvents: env.LOG_EVENTS === '1' || env.LOG_EVENTS === 'true',
  };
}
