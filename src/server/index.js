// HTTP server (static UI + decks + presentations API) and the browser ⇄ presenter WebSocket bridge.

import http from 'node:http';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readFile, readdir } from 'node:fs/promises';
import express from 'express';
import { WebSocketServer, WebSocket } from 'ws';
import 'dotenv/config';

import { loadConfig } from './config.js';
import { parsePresentation } from './script-parser.js';
import { LiveSession } from './live-client.js';
import { Presenter } from './presenter.js';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

const ts = () => new Date().toISOString().slice(11, 23);
const logLine = (tag, msg) => console.log(`${ts()} [${tag}] ${msg}`);

// ---- presentations --------------------------------------------------------

export async function listPresentations(rootDir = ROOT) {
  const dir = path.join(rootDir, 'presentations');
  const files = (await readdir(dir)).filter((f) => f.endsWith('.md') && !f.endsWith('-context.md'));
  const out = [];
  for (const f of files) {
    const id = f.slice(0, -3);
    try {
      const { meta, slides } = parsePresentation(await readFile(path.join(dir, f), 'utf8'), { id });
      out.push({ id, title: meta.title, slideCount: slides.length, deck: meta.deck, driver: meta.driver });
    } catch (err) {
      out.push({ id, title: id, error: err.message });
    }
  }
  return out.sort((a, b) => a.id.localeCompare(b.id));
}

export async function loadPresentation(id, rootDir = ROOT) {
  if (!/^[\w.-]+$/.test(id)) throw new Error(`invalid presentation id "${id}"`);
  const file = path.join(rootDir, 'presentations', `${id}.md`);
  const { meta, slides } = parsePresentation(await readFile(file, 'utf8'), { id });
  let context = '';
  if (meta.context) {
    const ctxPath = path.resolve(rootDir, meta.context);
    if (!ctxPath.startsWith(rootDir)) throw new Error(`context path escapes the project: ${meta.context}`);
    context = await readFile(ctxPath, 'utf8');
  }
  return { id, meta, slides, context };
}

// ---- server ---------------------------------------------------------------

export function createApp({ config, rootDir = ROOT }) {
  const app = express();
  app.disable('x-powered-by');

  app.get('/api/presentations', async (req, res) => {
    try {
      res.json(await listPresentations(rootDir));
    } catch (err) {
      res.status(500).json({ error: err.message });
    }
  });
  app.get('/api/presentations/:id', async (req, res) => {
    try {
      const p = await loadPresentation(req.params.id, rootDir);
      res.json({ id: p.id, meta: p.meta, slides: p.slides, hasContext: Boolean(p.context) });
    } catch (err) {
      res.status(err.code === 'ENOENT' ? 404 : 400).json({ error: err.message });
    }
  });
  app.get('/api/config', (req, res) => {
    res.json({ model: config.model, voice: config.voice, advanceSilenceMs: config.advanceSilenceMs });
  });

  app.use('/decks', express.static(path.join(rootDir, 'decks'), { fallthrough: true }));
  app.use('/decks', (req, res) => res.status(404).send(`Deck not found: ${req.path}. Put your deck under decks/<name>/.`));
  // Always revalidate UI files so edits show up on a plain reload.
  app.use('/', express.static(path.join(rootDir, 'src', 'web'), { etag: true, cacheControl: true, maxAge: 0, setHeaders: (res) => res.setHeader('Cache-Control', 'no-cache') }));
  return app;
}

export function createPresenter({ config, rootDir = ROOT }) {
  return new Presenter({
    config: { advanceSilenceMs: config.advanceSilenceMs, voice: config.voice },
    log: (level, msg) => logLine(level, msg),
    loadPresentation: (id) => loadPresentation(id, rootDir),
    createSession: ({ instructions, voice }, attempt = 0) => {
      const upstream = (config.upstreams ?? [config])[attempt];
      if (!upstream) return null;
      const session = new LiveSession({
        url: upstream.liveUrl,
        headers: upstream.headers,
        logEvents: config.logEvents,
        log: (msg) => logLine(`live:${upstream.name ?? 'primary'}`, msg),
        session: {
          model: upstream.model,
          instructions,
          audio: { output: { voice } },
          delegation: { type: 'client' },
        },
      });
      session.name = `${upstream.name ?? 'primary'} (${upstream.liveUrl}, ${upstream.model})`;
      return session;
    },
  });
}

/** Attaches the /ws bridge between exactly one browser client and the presenter. */
export function attachBridge(server, presenter) {
  const wss = new WebSocketServer({ server, path: '/ws' });
  let client = null;

  const sendJson = (obj) => {
    if (client && client.readyState === WebSocket.OPEN) client.send(JSON.stringify(obj));
  };
  const sendBinary = (buf) => {
    if (client && client.readyState === WebSocket.OPEN) client.send(buf, { binary: true });
  };

  presenter.on('state', (snap) => sendJson({ type: 'state', ...snap }));
  presenter.on('slide', (index) => sendJson({ type: 'slide', index }));
  presenter.on('audio', (buf) => sendBinary(buf));
  presenter.on('transcript', (t) => sendJson({ type: 'transcript', ...t }));
  presenter.on('usage', (u) => sendJson({ type: 'usage', ...u }));
  presenter.on('closed', (c) => sendJson({ type: 'closed', ...c }));
  presenter.on('log', (l) => sendJson({ type: 'log', ...l }));
  presenter.on('upstream-error', (e) => sendJson({ type: 'error', message: e.message, code: e.code ?? null }));

  wss.on('connection', (ws, req) => {
    if (client && client.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'error', message: 'Another presenter page is already connected. Close it first.', code: 'busy' }));
      ws.close(1013, 'busy');
      return;
    }
    client = ws;
    logLine('ws', `browser connected from ${req.socket.remoteAddress}`);
    sendJson({ type: 'state', ...presenter.snapshot() });

    ws.on('message', (data, isBinary) => {
      if (isBinary) {
        presenter.sendAudio(Buffer.isBuffer(data) ? data : Buffer.from(data));
        return;
      }
      let msg;
      try {
        msg = JSON.parse(data.toString());
      } catch {
        sendJson({ type: 'error', message: 'invalid JSON', code: 'protocol' });
        return;
      }
      handleCommand(presenter, msg, sendJson);
    });
    ws.on('close', () => {
      if (client === ws) client = null;
      logLine('ws', 'browser disconnected');
      if (presenter.state !== 'idle') {
        logLine('ws', 'ending live session because the browser went away');
        presenter.end();
      }
    });
    ws.on('error', (err) => logLine('ws', `client socket error: ${err.message}`));
  });

  return wss;
}

function handleCommand(presenter, msg, sendJson) {
  switch (msg.type) {
    case 'start':
      presenter.start(String(msg.presentation ?? 'sample'), { fromIndex: msg.fromIndex }).catch((err) => {
        logLine('error', `start failed: ${err.stack ?? err.message}`);
        sendJson({ type: 'error', message: err.message, code: 'start' });
      });
      break;
    case 'next':
      presenter.next();
      break;
    case 'prev':
      presenter.prev();
      break;
    case 'goto':
      presenter.goto(Number(msg.index));
      break;
    case 'pause':
      presenter.pause();
      break;
    case 'resume':
      presenter.resume();
      break;
    case 'mute':
      presenter.mute();
      break;
    case 'unmute':
      presenter.unmute();
      break;
    case 'end':
      presenter.end();
      break;
    case 'ping':
      sendJson({ type: 'pong' });
      break;
    default:
      sendJson({ type: 'error', message: `unknown command: ${msg.type}`, code: 'protocol' });
  }
}

/** Builds everything; call .listen(port) to start. */
export function createServer({ config, rootDir = ROOT }) {
  const app = createApp({ config, rootDir });
  const server = http.createServer(app);
  const presenter = createPresenter({ config, rootDir });
  attachBridge(server, presenter);
  return {
    app,
    server,
    presenter,
    listen: (port = config.port) =>
      new Promise((resolve) => server.listen(port, () => resolve(server.address().port))),
    close: async () => {
      if (presenter.state !== 'idle') await presenter.end();
      await new Promise((resolve) => server.close(resolve));
    },
  };
}

// ---- main -----------------------------------------------------------------

async function main() {
  let config;
  try {
    config = loadConfig(process.env);
  } catch (err) {
    console.error(err.message);
    console.error('Copy .env.example to .env and fill in UPSTREAM_ENDPOINT and UPSTREAM_KEY.');
    process.exit(1);
  }
  const srv = createServer({ config });
  const port = await srv.listen(config.port);
  logLine('server', `presenter-ai listening on http://localhost:${port} (upstream: ${config.liveUrl}, model: ${config.model}, voice: ${config.voice}${config.fallback ? `; fallback: ${config.fallback.liveUrl}, ${config.fallback.model}` : ''})`);

  const shutdown = async (signal) => {
    logLine('server', `${signal} received; closing`);
    try {
      await srv.close();
    } finally {
      process.exit(0);
    }
  };
  process.on('SIGINT', () => shutdown('SIGINT'));
  process.on('SIGTERM', () => shutdown('SIGTERM'));
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isMain) main();
