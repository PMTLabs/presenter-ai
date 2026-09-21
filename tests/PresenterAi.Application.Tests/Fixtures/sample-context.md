# Background context for the sample presentation

- The presenter tool is a Node.js application: an Express server, a WebSocket bridge, and a plain HTML page.
- The voice model is GPT-Live-1, used through the Live API over a WebSocket. Audio is 24 kHz 16-bit PCM in both directions.
- Slides advance automatically after about two seconds of silence following the narration. The delay is configurable per presentation (advanceSilenceMs).
- Keyboard: Space = pause/resume, ArrowRight = next, ArrowLeft = previous, M = mute microphone, Esc = end session.
- The session is billed per second of session time, including silence; ending the session stops the charge.
