# presenter-ai

Presents an HTML slide deck by voice through **GPT-Live-1** (OpenAI Live API, full duplex): a
Markdown script says what to narrate on each slide, the app shows the slide, the model speaks it,
you can interrupt with a question at any time, and the deck advances when the narration ends.

## Quick start

```bash
npm install
cp .env.example .env      # then fill UPSTREAM_ENDPOINT and UPSTREAM_KEY
npm start                 # → http://localhost:47913
```

Open the page, pick a presentation, press **Start**, allow the microphone. Use headphones so the
microphone does not pick up the model's own voice.

| Key | Action |
|---|---|
| Space | Pause / resume (pause also mutes the mic) |
| → / ← | Next / previous slide (narration follows) |
| M | Mute / unmute the microphone |
| Esc | End the session (stops billing) |
| S | Start (when idle) |

The deck's own navigation buttons also work: the presenter follows the deck.

## Run with Docker

Start the local Postgres and Redis services with:

```bash
docker compose up -d postgres redis
```

Start the API as well with the `full` profile. Export `UPSTREAM_ENDPOINT` and `UPSTREAM_KEY` in the shell first; they are passed through from the host and are not stored in compose:

```bash
export UPSTREAM_ENDPOINT=https://your-upstream.example
export UPSTREAM_KEY=your-key
docker compose --profile full up --build -d api
```

Postgres is available on host port `5433`, Redis on `6382`, and the API on `47913`. The Postgres password is a dev-only placeholder. The API container refuses to start unless both upstream variables are set in the shell (`Missing required setting: Upstream:Key`).

## Configuration (`.env`)

| Variable | Required | Meaning |
|---|---|---|
| `UPSTREAM_ENDPOINT` | yes | Base URL (or full `.../live/sessions` URL). `*.azure.com` hosts → `/openai/v1/live/sessions`, others → `/v1/live/sessions`. |
| `UPSTREAM_KEY` | yes | API key. Sent as `Authorization: Bearer` (plus `api-key` on Azure). |
| `UPSTREAM_MODEL` | no | `gpt-live-1` (OpenAI) or the Azure deployment name. Default `gpt-live-1`. |
| `UPSTREAM_VOICE` | no | Output voice, default `marin`. |
| `FALLBACK_OPENAI_KEY` | no | If set, `api.openai.com` is tried when the primary cannot start a session (rate limit, outage). `FALLBACK_OPENAI_ENDPOINT` / `FALLBACK_OPENAI_MODEL` override the route/model. |
| `PORT` | no | Default `47913` (deliberately unusual). |
| `ADVANCE_SILENCE_MS` | no | Silence after the model stops speaking before the next slide. Default `3000`. |
| `LOG_EVENTS` | no | `1` dumps every upstream JSON event. |

Sessions cost about $0.05 per minute of session time, silence included. The app closes the
session after the last slide, when you press End/Esc, and when the browser tab goes away.

## Presentations

Step-by-step for a new deck: [docs/guides/001-presenting-a-new-deck.md](docs/guides/001-presenting-a-new-deck.md).

A presentation is `presentations/<id>.md`:

```markdown
---
title: My talk
deck: decks/my-deck/index.html      # any same-origin HTML deck under decks/
driver: auto                        # auto | showFn | reveal | sections
context: presentations/my-context.md   # optional background for Q&A (not recited)
voice: marin                        # optional
advanceSilenceMs: 3000              # optional
chunkChars: 1400                    # optional; ≈ 350 tokens per instruction (limit is 500)
---

## Slide 1 — Title
Narration the presenter says for this slide…
> notes: quiet facts for answering questions about this slide (optional)

## Slide 2 — Next
…
```

- `## Slide N` headings must be contiguous from 1 and match the deck's slide order.
- Keep each slide's narration under ~1400 characters (one paragraph, ~90 s). Longer text is
  split into parts sent one after another; the model occasionally skips the tail of a part when
  the next one arrives.
- Two presentations ship: `sample` (3 slides) and `ricoh-delivery-overview` (11 slides, drafted
  from the Ricoh deck — edit the narration to taste).

### Supported decks

Put the deck under `decks/<name>/` (it must be same-origin to be driven). Auto-detected drivers:

| Driver | Deck type | How it is driven |
|---|---|---|
| `showFn` | single-file decks with a global `show(n)` and `#slide-N` hash (the Ricoh deck, the sample) | `show(i)`; deck navigation is synced back via `hashchange` |
| `reveal` | reveal.js | `Reveal.slide(i, 0)` |
| `sections` | plain `<section class="slide">` elements | toggles `.active` |

## How it works

```
browser mic ──20 ms PCM16──▶ server ──session.input_audio.append──▶ GPT-Live
browser speakers ◀── PCM16 ── server ◀── session.output_audio.delta ── GPT-Live
slide i shown ◀── server: presentSlide(i): thinking.append(notes) + instructions.append(narration)
advance: no *voiced* output audio for ADVANCE_SILENCE_MS → presentSlide(i+1)
```

Things learned from the live service that the code relies on:

- Output is paced by the **input audio timeline**: if no input audio arrives, the model never
  speaks. The server therefore streams silence whenever the browser is quiet (mic pending, muted,
  paused).
- Output audio is a **continuous stream, silence included**, so "finished speaking" is detected
  by RMS on the audio, not by the absence of events. The model pauses up to ~2.3 s at paragraph
  breaks; keep `advanceSilenceMs` ≥ 2500.
- Client delegation events carry no task text, so voice commands like "go to slide 3" are not
  implemented (would need Responses delegation with tools).

## Scripts

| Command | Purpose |
|---|---|
| `npm test` | Unit + integration tests (integration uses a fake GPT-Live server). |
| `node scripts/live-smoke.mjs [--fallback]` | Connectivity check against the real endpoint: one sentence, ~20 s of session time. |
| `node scripts/headless-run.mjs [id] [--stop-after-slide N]` | Dry run of a presentation through the real server without a browser; prints slide changes, transcript and a speech/silence bar per slide. |
| `node scripts/browser-e2e.mjs` | Drives the real page in headless Edge/Chrome with a fake mic (server must be running). |

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Missing required env: …` on start | Fill that variable in `.env`. |
| Startup `error` mentioning the model | Azure: set `UPSTREAM_MODEL` to your deployment name. |
| Start does nothing, log stops at "requesting microphone…" | Chrome is showing the mic permission prompt; the narration still plays, the mic joins when you allow it. |
| `audio output did not start within 4000 ms` | The default output device could not be opened; pick another output device in Windows and reload. |
| Slides advance mid-narration | Raise `advanceSilenceMs` (frontmatter or `.env`). |
| Model goes quiet and nothing happens | After 15 s the app nudges it once; press → to re-inject the slide. |
| Deck not driven (`deck adapter: none matched`) | Deck is not same-origin or has no `show()`/`Reveal`/`section.slide`; set `driver:` explicitly or adapt the deck. |
| Echo / the model answers itself | Use headphones or press M while it speaks. |
| Rate limit on Azure (10 RPM) | Set `FALLBACK_OPENAI_KEY`; the app fails over automatically at session start. |
