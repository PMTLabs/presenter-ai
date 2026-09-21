# 001 — Work log: AI Presenter MVP (plan 001)

## 2026-09-21 — Implementation and live verification

Plan: `docs/plan/001-ai-presenter-gpt-live-mvp.md` (approved 2026-09-21). All tasks T1–T10 done.

### Result

- `npm test`: 52 tests, all passing (unit: config, script parser, prompt, audio-util, presenter
  state machine with mock timers; integration: real server + bridge + presenter + LiveSession
  against a fake GPT-Live server).
- Live smoke test (`scripts/live-smoke.mjs`): Azure Foundry primary and OpenAI fallback both start
  a session, speak the requested sentence, and close with confirmed usage.
- Headless run (`scripts/headless-run.mjs sample`): 3 slides presented, auto-advanced, chunked
  slide delivered in parts, wrap-up spoken, `session.closed` with usage.
- Real-browser run (Chrome via the Claude-in-Chrome extension, Ricoh deck): Start → slide 1
  narrated verbatim → auto-advance → manual → (Next) → Space pause/resume → deck's own › button
  followed → microphone joined after permission → Esc → `session.closed`, usage 141 s.

### Acceptance criteria

| AC | Status | Evidence |
|---|---|---|
| 1 missing env exits naming the variable | pass | `Missing required env: UPSTREAM_KEY`, exit 1 |
| 2 Start → `session.started` ≤ 5 s | pass | browser run: connecting → presenting in ~1 s |
| 3 narration audible + transcript | pass | browser run; transcript panel matched the script |
| 4 hands-off advance after narration | pass | slide 1 → 2 at 14:38:18 (3 s window) |
| 5 → key shows next slide immediately | pass | `manual next → slide 3` |
| 6 spoken question answered, script resumes | not exercised in the browser run (user in a meeting, did not speak); interruption is native to the model and the hold-while-audience-speaks logic is unit-tested | — |
| 7 Pause stops advance + mutes; Resume restores | pass | `state → paused` / `resume-3` |
| 8 End → `session.close` → usage shown | pass | usage pill 141 s; server `session.closed` |
| 9 >500-token narration delivered in parts | pass | headless run, sample slide 2 (2 parts) |
| 10 unit tests | pass | 52/52 |

### Findings that changed the design (all from the live service)

1. **Output is paced by the input-audio timeline.** With no input audio the model never spoke.
   `LiveSession` now runs a silence pump that fills any gap between wall-clock and audio sent.
2. **Output audio is continuous, silence included** (100 ms deltas forever). Advance detection
   now uses RMS of the audio (`audio-util.js`), not the presence of deltas.
3. **Paragraph pauses reach ~2.3 s.** Defaults retuned: `advanceSilenceMs` 3000, part gap 2400.
4. **Sending all narration parts at once makes the model skip to the last part.** Parts are now
   sent one at a time after the previous part's speech ends.
5. Close reason from the service is `client_request` (docs: `close_requested`); both accepted.
6. `session.output_audio.delta` carries no `start_ms/end_ms` on this endpoint.
7. Browser: forcing `AudioContext({sampleRate: 24000})` never leaves `suspended` on this machine.
   The context now uses the device rate and both worklets resample; `resume()` has a timeout.
8. Browser: Start no longer blocks on the microphone permission prompt (listen-only until allowed).

### Deviations from the plan

- Default port 47913 (plan said 3000; 3000 is used by another local project).
- Env names `UPSTREAM_*` + optional `FALLBACK_OPENAI_*` (per user's `.env`).
- Event ids are 1-based (`slide-3-part-1`) for readability; the plan's runbook used 0-based.
- Added `scripts/` (live-smoke, headless-run, browser-e2e) as diagnostics — not in the plan.
- `.env.example` documents the silence pacing constraints.

### Open items / v2 candidates

- AC6 should be exercised once with a real spoken question (expected to work: interruption is
  native; the presenter holds the advance while the user transcript is active).
- Output transcript from the service is occasionally lossy; audio is authoritative.
- Voice-driven navigation would need Responses delegation + function tools.
- Flush the playback buffer when the user starts speaking (≈200 ms of overlap today).

## 2026-09-21 — Follow-ups after the MVP

- New-deck guide: `docs/guides/001-presenting-a-new-deck.md` (linked from README → Presentations).
- Automated pipeline proposal (proposal only, not implemented; rev. 3 adds phase 0 re-platform + knowledge base): `docs/research/001-auto-script-pipeline-proposal.md`,
  synthesised from a three-agent brainstorm (internal Opus, `pi` gpt-5.6-sol, `agy` Gemini 3.8 Flash; two rounds).
  Raw reports: `docs/research/001-auto-script-pipeline-proposal-appendix-agent-reports.md`.
  Next step if approved: run the `planning` skill for phase 0 (re-platform), then phase 1.
- Phase 0 platform architecture (React/TS + .NET 10 + Postgres/pgvector + Redis + Google OAuth2 + admin site, patterned on
  `D:\sources\work\inkspoke\inkspoke-main`): `docs/research/002-phase-0-platform-architecture.md`.
- Plan 002 (phase 0a: .NET core port + API bridge + React app) written via the planning skill (G1 brief for plans
  002–004, 3 question rounds), externally reviewed by `pi` gpt-5.6-sol:medium (9 findings folded in —
  `docs/review/001-plan-002-external-review.md`, ledger `docs/agentic/review-rounds-ledger.md`) and approved 2026-09-21.
  Implementation mode: external `pi` agents per plan §8. Not started.
- API and code conventions adopted before implementation: `docs/reference/001-api-and-code-conventions.md`
  (RFC 9457 Problem Details + `code`, bare resources, `{items,page,pageSize,total}` lists, `area.reason` error
  catalogue generated into a TS union, WebSocket/SSE/header rules, frozen `/api` parity trio until plan 004).
  Plan 002 amended to reference it (T2/T10/T13/T14).
