# 002 — Handoff: presenter-ai MVP done → follow-up requests

**Written:** 2026-09-21 14:55 CDT, at ctx 45 %, clean boundary (no edits in flight).

## Goal

presenter-ai narrates an HTML slide deck through GPT-Live-1 (OpenAI Live API, full duplex) from a
Markdown script. The MVP (plan 001) is **implemented and live-verified**. The user now wants two
follow-ups (see *Next steps*): a manual "how do I present a new deck" guide, and a **proposal**
for an automated end-to-end pipeline (upload deck → LLM analyses it and proposes the script → present),
produced by orchestrating subagents that debate/brainstorm.

## Environment

- Working directory: `D:\sources\demo\presenter-ai` — **not a git repository** (no branch, no worktree).
- Node 22.23.2, npm. `npm test` → 52/52 (≈16 s, mostly process spawn). `npm start` → http://localhost:47913.
- A server instance I started may still be running in the background: pid file
  `%TEMP%\presenter-ai-47913.pid` (pid 298179), log `%TEMP%\presenter-ai-47913.log`. It is mine — safe to kill.
  **Never touch port 3000** — it belongs to another project of the user (Vite/React, pid 55348).
- Chrome (Claude-in-Chrome MCP) tab `1324748688` may still be open on the presenter page; page DPR is 1.5,
  so screenshot coordinates miss buttons — click via `javascript_tool` (`el.click()`) or `find` refs.
- `.env` holds real secrets: `UPSTREAM_ENDPOINT` (Azure AI Foundry host, 10 RPM / 10k TPM), `UPSTREAM_KEY`,
  `FALLBACK_OPENAI_KEY`. Never print values; list names only with `sed -E 's/=.*/=<set>/'`.
- Memory file: `C:\Users\tamtr\.claude\projects\D--sources-demo-presenter-ai\memory\project-presenter-ai-context.md`.

## Done (all in the repo)

- Plan: `docs/plan/001-ai-presenter-gpt-live-mvp.md` (Status: Approved; approval log has an "Implemented" row).
- Work log with AC table, live-service findings and deviations: `docs/progress/001-work-log.md`.
- `README.md`: quick start, keys, script format, supported decks (`showFn` / `reveal` / `sections`), how it works, scripts, troubleshooting.
- Server: `src/server/{config,script-parser,prompt,live-client,presenter,audio-util,index}.js`.
- Web: `src/web/{index.html,styles.css,app.js,deck-driver.js,audio-capture.js,audio-playback.js,worklets/*}`.
- Decks: `decks/ricoh/index.html` (copy of `D:\sources\ricoh\management\plan\Ricoh_Project_Delivery_Overview_Slide_Deck_v1.0.html`, custom `show(n)` deck), `decks/sample/index.html`.
- Presentations: `presentations/ricoh-delivery-overview.md` (11 slides, narration + `> notes:` per slide, drafted from slide text — user still to review wording), `presentations/ricoh-context.md`, `sample.md`, `sample-context.md`.
- Scripts: `scripts/live-smoke.mjs [--fallback]`, `scripts/headless-run.mjs [id] [--stop-after-slide N]`, `scripts/browser-e2e.mjs`.
- Tests: `test/{config,script-parser,prompt,audio-util,presenter,integration}.test.js`, `test/fake-live-server.js`.
- Live verification: Azure primary + OpenAI fallback smoke OK; headless sample run OK; real-Chrome Ricoh run slides 1–5 OK
  (auto-advance, → key, Space pause/resume, deck's own › button, mic joined after permission, Esc close, usage 141 s).

## In progress

Nothing. The user's last question ("did you do the full transcript of ricoh-delivery-overview?") was answered: yes, all 11 slides.

## Next steps (the user's two requests, in order)

1. **Manual guide — "I have a new deck; what steps make presenter-ai work?"**
   Write `docs/guides/001-presenting-a-new-deck.md` (create `docs/guides/`; numbering starts at 001) and add a short
   pointer in README. Steps to cover, concretely:
   1. Put the deck under `decks/<name>/index.html` (same-origin requirement). If it is PPTX: export/convert to HTML first
      (out of scope for the app today). Check the deck type: has global `show(n)` + `#slide-N` → `showFn`; reveal.js → `reveal`;
      plain `<section class="slide">` → `sections`; otherwise add a tiny `show(n)` script or set `driver:`.
   2. Create `presentations/<name>.md`: frontmatter (`title`, `deck`, optional `context`, `driver`, `voice`, `advanceSilenceMs`),
      then `## Slide N — Title` sections, contiguous from 1, one per deck slide; narration ≤ ~1400 chars per slide; `> notes:` for Q&A facts.
   3. Optional `presentations/<name>-context.md` with background facts (goes into system instructions, ≤ ~48k chars).
   4. `npm start` → open http://localhost:47913 → pick the presentation → check the log line `deck adapter: <driver>, N slides`
      and that N matches the script (mismatch warning otherwise).
   5. Dry run without a browser: `node scripts/headless-run.mjs <name> --stop-after-slide 2` (prints transcript + speech/silence bar).
   6. Rehearse: headphones, Start, allow mic; Space/→/←/M/Esc; tune `advanceSilenceMs` if it advances mid-slide.
   Keep it to one page; link to README sections rather than repeating them.

2. **Automated end-to-end proposal — "upload deck → auto-analyse → propose script (user-selectable LLM) → present".**
   The user explicitly asked to *orchestrate subagents*: an **internal Opus agent** (Agent tool, `model: "opus"`) plus **external
   agents `agy` and `pi`** ("sol/medium" = their model tiers) via the `agent-research` skill (load it first to see how `agy`/`pi`
   are launched in a terminal and how results come back — the `claude-channel` MCP delivers research results as
   `<channel source="claude-channel">` messages; use termflow to run them). Make them **debate/brainstorm**, then synthesise.
   Deliverable: `docs/research/001-auto-script-pipeline-proposal.md` (create `docs/research/`) with 2–3 candidate architectures,
   trade-offs, a recommendation, and a phased plan sketch — **proposal only, do not implement** (a new feature would go through the
   `planning` skill later). Give every agent this brief:
   - Current system: Node/Express + `ws` backend; `Presenter` state machine; `presentations/*.md` format (frontmatter + `## Slide N`
     + `> notes:`); decks are same-origin HTML driven by `deck-driver.js` adapters; GPT-Live-1 speaks the narration (≤500 tokens per
     instruction append, ~1400 chars/slide); no PPTX support today.
   - Inputs the pipeline must handle: HTML decks (any framework) and PPTX (and probably PDF); extract per-slide text, tables, images
     (org charts, diagrams → need vision), speaker notes if present.
   - Script generation: user-selectable LLM ("professional script"): candidates OpenAI GPT-5.x via Responses API, Claude
     (`claude-opus-5` / `claude-sonnet-5`, see `claude-api` skill for current ids), Azure-hosted models; support BYO key per provider;
     vision for image slides; produce the Markdown script + context file in the existing format; let the user edit before presenting.
   - Deck driving for arbitrary HTML/PPTX: options are (a) convert PPTX → HTML via a converter (LibreOffice headless, `pptx2html`,
     or render to per-slide images and wrap in the `show(n)` template), (b) inject a generic `show(n)` shim into unknown HTML decks,
     (c) render slides to images server-side (Playwright) and present an image deck.
   - Debate points to force: extraction fidelity vs cost; where LLM calls run (server vs client); streaming the script per slide so
     presenting can start before the whole script exists; caching/versioning of generated scripts; how the user reviews/edits;
     rate limits (Azure 10 RPM); privacy of deck content; PPTX conversion fidelity; evaluation of script quality.
   - Constraints: keep it small (single Node process preferred), no new build toolchain unless justified, must keep the current
     manual path working.
   After the agents report, write the synthesis yourself; do not forward their claims uncritically; attribute which agent argued what.

3. Report both deliverables to the user with paths; end with the `Task done:` line (global CLAUDE.md rule).

## Gotchas / settled decisions (do not relitigate)

- GPT-Live facts baked into the code: output paced by the *input* audio timeline (server silence pump in `live-client.js`);
  output audio is continuous silence-included (advance = RMS-voiced silence via `audio-util.js`); paragraph pauses ≈2.3 s
  (`advanceSilenceMs` default 3000, part gap 2400); narration parts are sent one at a time; close reason is `client_request`;
  no `start_ms/end_ms` on output deltas; client delegation carries no task text (voice navigation = v2 via Responses delegation + tools).
- Browser facts: forcing `AudioContext({sampleRate:24000})` hangs on this machine (use device rate, worklets resample);
  Start must not block on the mic prompt; UI files served with `Cache-Control: no-cache`.
- Default port 47913 (user asked for a rare port). Env names `UPSTREAM_*`, `FALLBACK_OPENAI_*`.
- Event ids are 1-based (`slide-3-part-1`).
- AC6 (spoken question mid-narration) was not exercised in the browser run; expected to work; mention it if relevant.
- Windows: large heredocs fail with ENAMETOOLONG — use the Write tool for big files. `node --test` needs a glob (`"test/*.test.js"`).
- The Agent tool / workflows are normally off-limits unless the user asks — for step 2 the user **explicitly asked** for subagents.

## Outcome (2026-09-21 15:20 CDT)

Both next steps done: `docs/guides/001-presenting-a-new-deck.md` and `docs/research/001-auto-script-pipeline-proposal.md`
(+ appendix with the agents' reports). Nothing implemented; feature work awaits a plan.
