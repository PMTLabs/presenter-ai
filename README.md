# presenter-ai

Presenter AI presents an HTML slide deck by voice through **GPT-Live-1** (OpenAI Live API, full duplex): a
Markdown script says what to narrate on each slide, the app shows the slide, the model speaks it,
you can interrupt with a question at any time, and the deck advances when the narration ends.

The primary stack is the .NET 10 API with the React workspaces. The Node MVP was retired in plan 003; its last
commit is `2a0b6a1`.

| Feature | Details |
|---|---|
| External tools | Signed-in users can connect public HTTPS MCP servers, require spoken confirmation for non-read-only actions, and optionally enable hosted web search. See [External tools](docs/guides/003-external-tools.md). |

## Quick start — .NET + React

Prerequisites:

- .NET 10 SDK
- Bun 1.3.x (the workspace is pinned to Bun 1.3.14)

Store development secrets with `dotnet user-secrets`, separately for the API and CLI. Do not put secrets in
`appsettings.json`; the supported keys are `Upstream:Endpoint`, `Upstream:Key`, and the optional
`Upstream:Fallback:Key`.

```bash
# API user-secrets
dotnet user-secrets --project src/PresenterAi.Api set "Upstream:Endpoint" "https://your-upstream.example"
dotnet user-secrets --project src/PresenterAi.Api set "Upstream:Key" "<your-key>"
dotnet user-secrets --project src/PresenterAi.Api set "Upstream:Fallback:Key" "<your-openai-key>"

# CLI user-secrets: repeat the same keys for presenter-cli
dotnet user-secrets --project src/PresenterAi.Cli set "Upstream:Endpoint" "https://your-upstream.example"
dotnet user-secrets --project src/PresenterAi.Cli set "Upstream:Key" "<your-key>"
dotnet user-secrets --project src/PresenterAi.Cli set "Upstream:Fallback:Key" "<your-openai-key>"
```

Start the .NET API from the repository root:

```bash
dotnet run --project src/PresenterAi.Api
```

The API listens at <http://localhost:47913> and serves the built React app from `web/app/dist` when it exists.
The React development workspaces run through Bun:

```bash
cd web
bun install
bun run dev
```

The React app is at <http://localhost:47914> and the admin workspace is at <http://localhost:47915>. The app
proxy sends `/api`, `/ws`, `/decks`, and `/health` to the API on 47913; the admin proxy sends `/api` there.
The presenter route is `/present/<id>`.

Use the .NET CLI from the repository root:

```bash
dotnet run --project src/PresenterAi.Cli -- smoke --provider azure
dotnet run --project src/PresenterAi.Cli -- smoke --provider openai
dotnet run --project src/PresenterAi.Cli -- run <id> [--max-seconds N] [--stop-after-slide N] [--content-root DIR]
```

The CLI exit codes are `0` for success, `1` for an upstream or run failure, and `2` for a usage or
configuration error.

### Docker

Copy `.env.example` to `.env` and fill the upstream values. Compose reads `.env` for interpolation; the .NET
container receives configuration keys with the `__` form. The mappings in `docker-compose.yml` include
`UPSTREAM_ENDPOINT` → `Upstream__Endpoint`, `UPSTREAM_KEY` → `Upstream__Key`, `UPSTREAM_MODEL` →
`Upstream__Model`, `UPSTREAM_VOICE` → `Upstream__Voice`, `FALLBACK_OPENAI_KEY` → `Upstream__Fallback__Key`,
`UPSTREAM_DELEGATION_MODEL` → `Upstream__DelegationModel`, `FALLBACK_DELEGATION_MODEL` →
`Upstream__Fallback__DelegationModel`, `ADVANCE_SILENCE_MS` → `Presenter__AdvanceSilenceMs`, `FOLLOW_UP_WAIT_MS` → `Presenter__FollowUpWaitMs`, and `LOG_EVENTS` → `Presenter__LogEvents`.

```bash
cp .env.example .env
# fill .env, then:
docker compose up -d postgres redis
docker compose --profile full up --build
```

The default services expose Postgres on host port `5433` and Redis on `6382`; the full-profile API exposes
port `47913`. The compose file declares the Postgres password as a dev-only placeholder.

### Verification and generated API client

```bash
dotnet test PresenterAi.slnx
cd web && bun run lint && bun run test && bun run build
bash scripts/secrets-guard.sh
```

The checked-in OpenAPI snapshot is `web/shared/openapi/v1.json`. With the API running, refresh it and the
checked-in generated client with:

```bash
cd web
bun run generate:api -- --url http://localhost:47913/openapi/v1.json
```

Without `--url`, generation reads the checked-in snapshot. CI regenerates the client and fails when
`web/shared/src/api` has drift (`git diff --exit-code -- web/shared/src/api`).

## Use the presenter

Open the React app at <http://localhost:47914> (or the API's built app at <http://localhost:47913>), pick a
presentation, and press **Start**. Allow the microphone when prompted. Use headphones so the microphone does
not pick up the model's own voice.

| Key | Action |
|---|---|
| Space | Pause / resume (microphone stays open while paused; press M to mute) |
| → / ← | Next / previous slide (narration follows) |
| M | Mute / unmute the microphone |
| Esc | End the session (stops billing) |
| S | Start (when idle) |

The deck's own navigation buttons also work: the presenter follows the deck. Spoken commands are listed below. The presenter listens while paused; press **M** to stop listening.

| Voice command | Effect |
|---|---|
| Pause: `stop`, `pause`, `wait`, `hold on`, `stop talking`, `stop there`, `pause there` | Pauses immediately. While the model is speaking, Pause is the only voice command accepted. |
| Resume: `continue`, `carry on`, `keep going`, `go on`, `go ahead`, `resume`, `keep continue` | Resumes from the interrupted point. |
| Next: `next`, `next slide`, `go next`, `move on` | Advances one slide. |
| Previous: `back`, `go back`, `previous`, `previous slide`, `last slide` | Goes back one slide. |
| Go to: `go to slide N`, `slide N` | Goes to a numbered slide (1–20 by number word, or any positive digit number); the target must exist. |
| End: `end`, `end meeting`, `end presentation`, `end talk`, `finish`, `stop presentation` | Pauses and asks for confirmation. Say `yes`, `yeah`, `yep`, `sure`, or `do it` to end; say `no`, `nope`, `not yet`, or `don't` to resume. |
| Check-in: `yes`, `yeah`, `yep`, `sure`, `do it` / `no`, `nope`, `not yet`, `don't` | Answers “Shall I carry on?” with yes/resume or no/stay on the slide. |

Commands must be a complete utterance, not just a word within a question. Except Pause, commands are ignored if they overlap the model's voiced speech. Punctuation, filler words, and common forms such as “can you” / “could you” are normalized; see [Audience questions](docs/guides/002-audience-questions.md).

## Configuration (`.env`)

The names used by Compose are mapped above. The .NET local path uses the `Upstream:*` user-secrets above; the
.NET container receives the mapped `Upstream__*` settings.

| Variable | Required | Meaning |
|---|---|---|
| `UPSTREAM_ENDPOINT` | yes | Base URL (or full `.../live/sessions` URL). `*.azure.com` hosts → `/openai/v1/live/sessions`, others → `/v1/live/sessions`. |
| `UPSTREAM_KEY` | yes | API key. Sent as `Authorization: Bearer` (plus `api-key` on Azure). |
| `UPSTREAM_MODEL` | no | `gpt-live-1` (OpenAI) or the Azure deployment name. Default `gpt-live-1`. |
| `UPSTREAM_VOICE` | no | Output voice, default `marin`. |
| `UPSTREAM_DELEGATION_MODEL` | no | Model that answers audience questions the deck does not cover: model id (OpenAI) or Azure deployment name in the same resource. Default `gpt-5.6-luna`; empty = deck-only answers. If it is unavailable, the session starts deck-only and logs `delegation: backend unavailable`. |
| `FALLBACK_DELEGATION_MODEL` | no | The same for the OpenAI fallback upstream. Default `gpt-5.6-luna`. |
| `FALLBACK_OPENAI_KEY` | no | If set, `api.openai.com` is tried when the primary cannot start a session (rate limit, outage). `FALLBACK_OPENAI_ENDPOINT` / `FALLBACK_OPENAI_MODEL` override the route/model. |
| `ADVANCE_SILENCE_MS` | no | Silence after the model stops speaking before the next slide. Default `3000`. |
| `FOLLOW_UP_WAIT_MS` | no | Quiet after an answer to an audience question before the slide resumes, so a follow-up can be asked. `2500`–`60000`, default `5000`. See `docs/guides/002-audience-questions.md`. |
| `LOG_EVENTS` | no | Log every upstream JSON event (audio deltas excluded); the .NET host binds `true`/`false`. |

External tools use API configuration keys (not `.env` values by default): `Tools:CredentialKey` (secret; base64 of
32 bytes), `Tools:OAuthRedirectUri` (optional), `Tools:Mcp:StartBudgetMs` (default `3000`),
`Tools:Mcp:CallTimeoutSeconds` (default `10`), `Tools:MaxInlineTools` (default `16`), and `OAuth:ApiBaseUrl`
(for public OAuth metadata registration). The credential key is optional until storing credentials/OAuth tokens;
see the [external tools guide](docs/guides/003-external-tools.md#configuration) for defaults, validation and setup.

Sessions cost about $0.05 per minute of session time, silence included. The app closes the session after the
last slide, when you press End/Esc, and when the browser tab goes away.

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
- Keep each slide's narration under ~1400 characters (one paragraph, ~90 s). Longer text is split into parts sent
  one after another; the model occasionally skips the tail of a part when the next one arrives.
- Two presentations ship: `sample` (3 slides) and `ricoh-delivery-overview` (11 slides, drafted from the Ricoh
  deck — edit the narration to taste).

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

- Output is paced by the **input audio timeline**: if no input audio arrives, the model never speaks. The server
  therefore streams silence whenever the browser is quiet (mic pending, muted, paused).
- Output audio is a **continuous stream, silence included**, so "finished speaking" is detected by RMS on the
  audio, not by the absence of events. The model pauses up to ~2.3 s at paragraph breaks; keep
  `advanceSilenceMs` ≥ 2500.
- Client delegation events carry no task text. Common English voice commands are handled locally; richer requests that need managed tools are unavailable in deck-only mode.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Configuration invalid: Missing required setting: …` on .NET start | Set the corresponding `Upstream:*` key with `dotnet user-secrets` for the API project. |
| Startup `error` mentioning the model | Azure: set `UPSTREAM_MODEL` to your deployment name. |
| Start does nothing, log stops at "requesting microphone…" | Chrome is showing the mic permission prompt; the narration still plays, the mic joins when you allow it. |
| `audio output did not start within 4000 ms` | The default output device could not be opened; pick another output device in Windows and reload. |
| Slides advance mid-narration | Raise `advanceSilenceMs` (frontmatter or `.env`). |
| Model goes quiet and nothing happens | The app nudges it after 15 s and again after 30 s; at 45 s it pauses the talk with a warning. Press Resume, → to re-inject the slide, or End. |
| Deck not driven (`deck adapter: none matched`) | Deck is not same-origin or has no `show()`/`Reveal`/`section.slide`; set `driver:` explicitly or adapt the deck. |
| Echo / the model answers itself | Use headphones. During model speech only a complete Pause command is eligible; other commands are ignored. Press M to stop the microphone from listening. |
| Rate limit on Azure (10 RPM) | Set `FALLBACK_OPENAI_KEY`; the app fails over automatically at session start. |
| Presenter is in use in another tab | For the same account, press **Take over** to end the other tab's talk and resume at its slide. Another account cannot take it over. |
