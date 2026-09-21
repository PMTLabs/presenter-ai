// A minimal stand-in for the GPT-Live WebSocket endpoint, for integration tests.
// Records every client event; answers session.start, instruction appends (with audio +
// transcript), mute/unmute, and session.close.

import { WebSocketServer } from 'ws';

// 20 ms of a tone: the presenter only counts voiced frames as speech.
function voicedDelta() {
  const buf = Buffer.alloc(960);
  for (let i = 0; i < 480; i++) buf.writeInt16LE(Math.round(Math.sin(i / 5) * 3000), i * 2);
  return buf;
}

export function startFakeLiveServer({ audioDeltasPerAppend = 3, deltaGapMs = 20 } = {}) {
  const wss = new WebSocketServer({ port: 0, path: '/v1/live/sessions' });
  const received = [];
  const connections = [];
  let headers = null;

  wss.on('connection', (ws, req) => {
    headers = req.headers;
    connections.push(ws);
    const send = (obj) => ws.readyState === ws.OPEN && ws.send(JSON.stringify(obj));
    let audioAppends = 0;

    ws.on('message', (data) => {
      const ev = JSON.parse(data.toString());
      if (ev.type === 'session.input_audio.append') {
        audioAppends++;
        // all-zero PCM16 encodes to a run of "A"s in base64
        received.push({ type: ev.type, audioLength: ev.audio.length, silent: /^A+=*$/.test(ev.audio) });
        return;
      }
      received.push(ev);
      switch (ev.type) {
        case 'session.start':
          if (ev.session?.model === 'bad-model') {
            send({ type: 'error', error: { type: 'invalid_request_error', code: 'invalid_model', message: 'unknown model', client_event_id: ev.event_id } });
            return;
          }
          send({ type: 'session.started', session: { id: 'sess_fake', expires_at: 4102444800, ...ev.session } });
          break;
        case 'session.instructions.append':
        case 'session.commentary.append':
        case 'session.thinking.append': {
          const kind = ev.type.split('.')[1];
          send({ type: `session.${kind}.appended`, client_event_id: ev.event_id, start_ms: 0, end_ms: 10 });
          if (kind === 'instructions') {
            let t = 0;
            for (let i = 0; i < audioDeltasPerAppend; i++) {
              setTimeout(() => {
                send({ type: 'session.output_audio.delta', delta: voicedDelta().toString('base64'), start_ms: t, end_ms: t + 20 });
                if (i === 0) send({ type: 'session.output_transcript.delta', delta: `speaking ${ev.event_id}`, start_ms: t, end_ms: t + 20 });
              }, i * deltaGapMs);
              t += 20;
            }
          }
          break;
        }
        case 'session.input_audio.mute':
          send({ type: 'session.input_audio.muted' });
          break;
        case 'session.input_audio.unmute':
          send({ type: 'session.input_audio.unmuted' });
          break;
        case 'session.close':
          send({ type: 'session.closed', reason: 'close_requested', usage: { seconds: 7 } });
          setTimeout(() => ws.close(1000, 'done'), 20);
          break;
        default:
          break;
      }
    });
  });

  return {
    wss,
    port: wss.address().port,
    url: `ws://127.0.0.1:${wss.address().port}/v1/live/sessions`,
    received,
    get headers() {
      return headers;
    },
    dropAll: () => connections.forEach((c) => c.terminate()),
    close: () => {
      for (const c of wss.clients) c.terminate();
      return new Promise((r) => wss.close(r));
    },
  };
}
