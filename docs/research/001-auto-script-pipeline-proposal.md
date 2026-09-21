# 001 — Proposal: automated deck → script → present pipeline

**Status:** proposal only (nothing implemented). A feature plan would go through the `planning` skill next.
**Date:** 2026-09-21 · **Revision 2** (same day): added §6 *Interaction design* after the user's review — the
first revision generated the script straight from the deck, with no way to capture the presenter's intent,
audience or emphasis, and no Q&A preparation. **Revision 3** (same day): added **phase 0** — re-platform to
React/TypeScript + .NET 10 with Postgres (pgvector), Redis and Google OAuth2, detailed in
`002-phase-0-platform-architecture.md` — and §7 *Knowledge base*: user-uploaded documents in a vector store as
context for script generation and Q&A. The phase table (§9) is renumbered.
**Method:** three agents were given the same brief (`docs/research/001-auto-script-pipeline-proposal-appendix-agent-reports.md`
has the brief and all six reports verbatim): an internal Claude Opus agent, `pi` (GPT-5.6 sol, medium) and `agy`
(Gemini 3.8 Flash, high). Round 1: independent proposals. Round 2: each rebutted the other two on the ten points
where they split. This document is my synthesis; where I overrule an agent I say so and why.

## 1. Summary

Build a **server-side pipeline inside the existing Node process**: upload a deck → extract slide content locally
(text first, images only where they exist) → a short **preparation interview** captures who is presenting, to
whom, why, what to emphasise and how it should sound, and the LLM asks a handful of deck-specific questions →
the user picks a provider/model → one structured request produces the script and context file in the *existing*
`presentations/<id>.md` format → the user refines it in a plain in-app editor or any external editor → a **Q&A
prep** step has the LLM anticipate the audience's questions and the user supplies or approves the answers, which
become the slide notes and context the live presenter answers from → the unchanged `Presenter` presents it.
PPTX and PDF are presented through a pdf.js-based `show(n)` deck template so `deck-driver.js` does not change.

Before any of that, **phase 0** moves the prototype onto the platform it will ship on — React + TypeScript,
.NET 10 (Clean Architecture, minimal APIs in the `InkSpoke.Api` style), Google OAuth2 login, PostgreSQL 17 with
`pgvector`, Redis, an admin site for users/providers/models borrowed from inkspoke, Docker for dev/test — porting
the live-audio core against the existing test suite as a parity oracle (`002-phase-0-platform-architecture.md`). The vector store then powers a **knowledge base** (§7): users
upload reports, specs and notes; retrieved passages ground the script, the deck questions and the Q&A answers.

Effort (one developer): phase 0 **5–7 weeks** (incl. the admin site); phase 1 (HTML upload → interview → script → Q&A prep → present, one
provider family) **7–9 days** on the new stack; the full feature set (knowledge base, PDF, PPTX via LibreOffice,
refinement chat, session question log, privacy/cost UX, hardening) **another 6–8 weeks**, with a support tail for
arbitrary decks. The architecture debate in §2–§5 was held against the Node codebase; its conclusions (server-side
LLM calls, text-first extraction, files-as-interchange, no present-while-generating, deterministic lint) carry over
unchanged — only "files as the database" becomes "Postgres, with the Markdown script as the interchange format".

## 2. Where the three agents ended up

Unanimous after round 1, not re-argued:

| Point | Consensus |
|---|---|
| LLM calls | Server-side only. Browser-held keys were rejected by all (CORS, key exposure, no CLI path). |
| Extraction | Text/tables/notes parsed locally (exact, free); vision only for slides that have images or almost no text. |
| Present while generating | No. Review comes first; the whole-deck outline goes into GPT-Live's immutable `instructions`; a 60–90 s generation is shorter than putting on headphones. Streaming is for editor progress only. |
| Storage | Files, no database. `presentations/<id>.md` stays canonical and is hot-read on Start today. |
| Edits | Per-slide regenerate must never overwrite user-edited text. |
| PPTX | Never convert PPTX → HTML (`pptx2html`-class libraries break layouts). LibreOffice headless → PDF is the fidelity path; PowerPoint's own PDF export is the fallback. |
| Quality | Deterministic lint (slide count, ≤1400 chars, no bullets/markup, numbers on the slide appear in narration or notes) plus a rubric in the prompt. |
| Footprint | One process, no bundler, no framework, no queue, no Playwright. |

Round-2 splits and how they resolved (→ = my decision):

| Split | Opus | pi | agy | → Decision |
|---|---|---|---|---|
| Unknown HTML decks | shim into our copy → conceded "not in v1" | fail clearly in v1; explicit previewed shim later | auto-shim (switched *to* Opus's r1 view) | **No automatic injection in v1.** Adapters cover the decks we have; the guide documents the manual shim; phase 4 adds an explicit "add navigation shim" action with preview. |
| PDF display / vision input | pdf.js in browser; PDF natively to the LLM | switched to Opus | pdf.js for display; thumbnails for vision | **pdf.js template for display.** PDF bytes natively where the provider accepts them (Anthropic `document`, OpenAI Responses `input_file`); otherwise thumbnails rendered by the same pdf.js *in the browser at upload time* — no Poppler, no native canvas. |
| Provider layer | SDKs → switched to `fetch` | `fetch` → switched to SDKs | `fetch` → switched to SDKs | **Official `openai` + `@anthropic-ai/sdk`** (pure JS). Hand-rolled SSE parsing, retries and structured-output plumbing for two wire formats is ~300 lines that break silently; Azure OpenAI is the same `openai` client with `baseURL`. Contract tests with recorded fixtures guard SDK upgrades (pi's caveat). |
| Upload / zip | `express.raw` → conceded `busboy` | `busboy` + `jszip` | `express.raw` + `fflate` | **`busboy`** streaming to disk (a 100 MB PPTX must not be buffered in the process that relays 20 ms audio frames). `fflate` for zip (smaller; either is fine), `fast-xml-parser` for OOXML. |
| HTML safety | `linkedom`, no script execution | headless Chromium → conceded `linkedom` | `linkedom` | **`linkedom`.** Nothing executes during extraction. Keep pi's cheap rules: never fetch remote assets, sanitise zip entry paths, size and child-process limits. If CDP screenshots are ever added, block network and use a temp profile. |
| LLM critique pass | same-model "Polish" → withdrawn | different-model critic → withdrawn | none | **None in v1.** Lint + rubric + measured timing. If added later: opt-in, cross-model, shown as a diff, never gating Save. |
| External-edit detection | `fs.watch` → conceded no | explicit Reload | keep `fs.watch` | **Explicit Reload in v1** (Start already hot-reads). `fs.watch` fires twice on Windows and collides with unsaved in-app edits; a later mtime poll can show a "changed on disk" banner. agy's "poor UX" point is fair but not worth conflict handling now. |
| Generation call shape | one request per ≤20 slides | same; sequential windows beyond | two-stage outline + narration | **One structured request for ≤20 slides**; for larger decks a global brief first, then sequential ~10-slide windows carrying the brief and the previous window's last narration. agy's 4,096-token output ceiling no longer applies to current models; the two-stage split is only for large decks. |
| Effort | 5–7 d → "2–3 weeks for phases 1–3" | 4–6 w → 3–5 w | 3.5 d → 5–7 d | **Phase 1: 3–5 days for the pipeline alone; 5–7 days with the interview and Q&A prep of §6. Full: 4–5 weeks.** agy's numbers are happy-path spikes; pi's assume hardening for arbitrary decks, which a single-user local tool can pay on demand. |
| Phase-1 format | HTML (+PDF) | PDF → switched to HTML | PPTX+HTML → switched to HTML | **Single-file HTML** — the Ricoh deck is the acceptance test and needs no converter. PDF in phase 3, PPTX in phase 4 (numbering after the phase-0 insertion). |

## 3. Candidate architectures

### A — Native-first server pipeline, text-first extraction, selective vision (recommended)

All three agents converged here by round 2 (Opus and pi proposed it in round 1; agy moved to it after conceding
that rasterising native HTML throws away the deck the user built).

```
browser ──multipart──▶ POST /api/decks            decks/<name>/{index.html | deck.pdf + index.html(template) | source.pptx …}
                       extractDeck()              decks/<name>/.extract.json   (cached by content hash; local, nothing leaves)
browser ◀──JSON──────  GET  /api/providers        configured providers/models (never keys)
browser ──JSON───────▶ PUT  /api/prep/:name/brief  brief form (§6 step 2) → decks/<name>/prep.json
browser ◀──JSON──────  POST /api/prep/:name/questions   5–8 deck-specific questions with suggested answers (1 LLM call)
browser ──JSON───────▶ PUT  /api/prep/:name/answers     user's answers → prep.json
browser ──JSON───────▶ POST /api/generate         pre-flight {provider, model, host, slides, images, bytes, est. tokens} → confirm
browser ◀──SSE───────  progress per slide          one structured-output request (windows for >20 slides), brief + answers in the prompt
                       script-writer.js            presentations/<name>.md + <name>-context.md (or .drafts/ if the file exists)
browser ◀──JSON──────  POST /api/prep/:name/qa     anticipated questions (1 LLM call) → user sets policy/answers → <name>-qa.md
                       script-writer.js            approved Q&A → slide notes + context (budgeted)
browser ──JSON───────▶ PUT  /api/presentations/:id validated by parsePresentation() before write
                       Start → existing Presenter / DeckDriver; session log → decks/<name>/sessions/
```

New modules (names indicative):

| File | Role |
|---|---|
| `src/server/upload.js` | `busboy` stream → `decks/<name>/`; name sanitising; size/entry limits; zip for multi-file HTML |
| `src/server/extract/html.js` | `linkedom`; slide split with the *same selectors* `deck-driver.js` uses (`.slide`, `.slides > section`, `section.slide`); headings/lists/tables → Markdown; `<aside class="notes">`; `<img>`/data-URI images |
| `src/server/extract/pdf.js` | `pdfjs-dist` `getTextContent()` per page; PDF bytes kept for native model input |
| `src/server/extract/pptx.js` | `fflate` + `fast-xml-parser`: order from `ppt/presentation.xml`, text runs per shape, `<a:tbl>`, `notesSlideN.xml`, `ppt/media/*` |
| `src/server/llm/{index,openai,anthropic}.js` | adapter interface `generate(deck, opts) → AsyncIterable<progress>`; OpenAI Responses API (`gpt-5.x`, Azure via `baseURL`+`api-key`), Anthropic Messages (`claude-opus-5`, `claude-sonnet-5`); JSON-schema output; model ids in a server config table, not the UI |
| `src/server/gen-prompt.js` | rubric + hard limits (see §5 "Live-session limits"); deck outline; per-slide content blocks; prompt caching on the deck text |
| `src/server/script-writer.js` | `formatPresentation()` — the tested inverse of `parsePresentation()`; `replaceSlide()`; `lint()` |
| `decks/_template/pdf-deck.html` + vendored pdf.js | renders `deck.pdf` page N into `.slide` divs, exposes `show(n)` and `#slide-N` → existing `showFn` adapter drives it |
| `src/web/generate.js`, `src/web/editor.js` | upload dialog → provider/model → pre-flight → progress; per-slide textareas with char counters, Save, Reload, Regenerate slide |

Unchanged: `presenter.js`, `prompt.js`, `deck-driver.js`, `script-parser.js`, the `/ws` bridge, the manual path.
New dependencies: `busboy`, `fflate`, `fast-xml-parser`, `linkedom`, `pdfjs-dist`, `openai`, `@anthropic-ai/sdk`
(all pure JS). Optional external: LibreOffice (`SOFFICE_PATH`).

### B — Render everything to an image deck

agy's round-1 recommendation; pi's second option. Every input → per-slide PNG (HTML via headless Chromium, PPTX via
LibreOffice → PDF → `pdftoppm`); the model sees only images; present `<img>` slides through the `showFn` template.
One code path and perfect "what you see", but it needs a headless browser for *every* HTML deck, needs Poppler
(absent on this Windows machine), loses the deck's own navigation and vector crispness, and gives no exact text
for the fact lint — vision misreads figures. agy withdrew it in round 2. **Kept only as the fallback inside A**
for text-poor slides and for PPTX without LibreOffice.

### C — Agentic sidecar / client-side BYOK

Opus's C (spawn `claude -p` / Agent SDK with "read the deck, write the script") and agy's Arch 3 (keys in the
browser, Playwright on the server). Rejected by everyone: single vendor contradicts "several LLMs"; opaque cost;
no per-slide regenerate; keys in `localStorage`; a 300 MB Playwright install. The sidecar is, in effect, how the
Ricoh script was drafted by hand — fine once, wrong as the product path.

## 4. Positions on the ten debate points (final)

1. **Fidelity vs cost — text first, vision targeted.** Exact text is what makes "professional" checkable (numbers
   must match the slide). Attach images only for slides that have them or have <~40 chars of text; downscale to
   ≤1024 px; drop icons <64 px. A 15-slide deck with 10 images is roughly $0.15–0.35 per generation — noise next
   to $3/hour of live session.
2. **Where calls run — server.** Separate `GEN_OPENAI_KEY`, `GEN_ANTHROPIC_KEY`, `GEN_AZURE_OPENAI_{ENDPOINT,KEY,DEPLOYMENT}`.
   Generation must **never** route through the GPT-Live deployment: its 10k TPM is smaller than one deck request.
3. **Streaming — progress only.** Slides fill the editor as they arrive; presenting waits for Save.
4. **Caching/versioning — files.** Extraction cached by content hash; every generation written to
   `presentations/.drafts/<id>/<ISO>-<provider>-<model>.md`; canonical file written only if absent or on explicit
   Save; a job record `decks/<name>/generation.json` so a restart shows failed/retry (pi). Regenerate-one-slide
   replaces a section via `replaceSlide()`; slides whose text differs from their last generated hash are "edited"
   and skipped unless overridden.
5. **Review/edit — both, cheaply.** Plain per-slide textareas + char counter + notes + Save; external editors keep
   working because Start hot-reads. Explicit Reload; no rich editor.
6. **Rate limits/cost.** 1–4 requests per deck; one in-flight job; pre-flight token estimate and a
   `GEN_MAX_INPUT_TOKENS` cap; SDK retries honour `Retry-After`.
7. **Privacy.** Nothing leaves at upload. The pre-flight panel names provider, model, endpoint host, slide/image
   counts and bytes, with `images: off` / `notes: off` toggles. Deck bodies, prompts and responses are not logged.
   Uploaded decks can be deleted from the UI. Vendors' no-training defaults are stated as their claim.
8. **PPTX.** OOXML for exact text/notes/tables/images; `soffice --headless --convert-to pdf` when installed, else
   the UI asks for PowerPoint's PDF export; display via the pdf.js template. No PPTX→HTML converters.
9. **Quality — measured, not judged.** Lint in phase 1; a timing report from `scripts/headless-run.mjs`
   (spoken seconds per slide vs the ~15 chars/s estimate) is the only ground truth; LLM critique later, opt-in,
   cross-model, diffed.
10. **Small.** Seven pure-JS deps, two vendored pdf.js files, no native modules, no Playwright, no DB.

## 5. Things the round-1 reports missed (all three raised one; verified against the code)

- **Live-session limits on generated output** (Opus — confirmed). `presenter.js:219` sends `> notes:` as a *single*
  `session.thinking.append` with no chunking, under the 500-token-per-append cap; `prompt.js:3` truncates the context
  file at 48k chars inside the 16,384-token `instructions` budget shared with the outline. So the generator prompt
  and the lint must enforce **notes ≤ ~1400 chars per slide** and **context ≤ ~40k chars**, or the upstream rejects
  the event / the context loses its tail silently. Also: synchronous extraction in the same process would stall the
  20 ms audio relay — refuse generation unless `presenter.state === 'idle'` (or run extraction in a `worker_thread`).
- **Deck-borne prompt injection** (pi — valid). Hidden DOM, speaker notes, alt text or visible "instructions" in an
  uploaded deck reach the generation model. Treat all extracted material as quoted, untrusted data: delimit it
  structurally, exclude hidden DOM by default, tell the model not to follow embedded instructions or URLs, and
  validate the output independently (`parsePresentation` + lint), never trusting structured output alone.
- **Paragraph pauses vs auto-advance** (agy — partly right). GPT-Live pauses ~2.3 s at paragraph breaks and the
  default `advanceSilenceMs` is 3000; the sample's three-paragraph slide already auto-advances correctly, so this is
  not the "trap" agy describes, but the 0.7 s margin is thin. The generator should produce at most two paragraphs per
  slide and no ellipses or "(pause)" directions; lint flags both.

## 6. Interaction design: preparation interview and Q&A prep

A script generated from the deck alone is a well-read slide. What makes a talk sound like a person is everything
that is *not* on the slides: who is speaking, to whom, why now, which three things must land, what to skate over,
which story opens it, what not to say. The app has to ask. The preparation is a six-step wizard whose state is
saved after every step (`decks/<name>/prep.json`, resumable), so a 20-slide deck can be prepared in two sittings.

```
1 Upload ─▶ 2 Brief ─▶ 3 Deck questions ─▶ 4 Draft & refine ─▶ 5 Q&A prep ─▶ 6 Rehearse
              form      LLM asks 5–8         per-slide editor     LLM anticipates,     existing page
              (2 min)   deck-specific Qs     + "make slide 4…"    user answers         + session log
```

### Step 2 — Brief (a form, every field optional, sensible defaults)

| Field | Why it changes the script |
|---|---|
| Presenter: name, role, relationship to the audience ("delivery lead presenting to the customer's steering committee") | first person, ownership, what the speaker can credibly claim |
| Audience: who, how many, seniority, what they already know, what they care about, language | vocabulary, which acronyms to expand, what to skip, what to defend |
| Purpose: inform / persuade / decide / train, and *what the audience should do afterwards* | the closing, the call to action, how much argument vs description |
| Time budget (minutes) | words per slide, derived from ~15 chars/s and the emphasis weights below |
| Emphasis: 2–3 key messages; slides that matter most; slides to skim; things **not** to say (confidential, don't over-promise) | where the script slows down and repeats, where it moves, what the live model must refuse |
| Persona and tone: formal ↔ conversational, energy, humour, "I" vs "we", GPT-Live voice | sentence rhythm, contractions, rhetorical questions, voice choice |
| Stories: opening hook, an anecdote or customer example, the closing line | the human parts a model cannot invent honestly |
| Terminology: glossary, pronunciations, names to introduce | correctness on the things the audience will notice first |
| Fidelity: `verbatim` / `close` (default) / `free` | how tightly the live model follows the words (see *Runtime* below) |

Three presets (executive briefing, technical deep-dive, workshop) pre-fill the form; the Ricoh deck would use
"executive briefing, 20 min, persuade → approve the delivery plan".

### Step 3 — Deck-specific questions (one LLM call)

With the extracted deck and the brief, the model returns 5–8 questions *about this deck*, each with the slide it
concerns, why it matters and a **suggested answer guessed from the deck** so the user can accept, edit or skip in
seconds. Examples for the Ricoh deck: "Slide 6 shows a budget variance — frame it as a risk you are managing or
as already under control?", "Slide 3 is an org chart — introduce each lead by name, or just the structure?",
"Slide 9 lists three options — is there a preferred one, and may I say so?". This is the step that turns "robot"
into "person": the answers are the presenter's judgement, not the slide's content.

### Step 4 — Draft and refine

Generation (one structured request, as in §4.8) now receives the brief and the answers, and the prompt rubric is
explicitly about spoken delivery: open by addressing the room, never read bullets — say what the slide *means* and
quote only the numbers that matter; signpost and call back ("the three risks from slide 4 are why the timeline
looks like this"); vary sentence length, use contractions; place the supplied anecdotes where they belong;
pre-empt an obvious objection where the brief says so; land each key message once, with emphasis; write the
transition into the next slide as a spoken bridge; honour the per-slide time weight; at most two paragraphs per
slide, no ellipses or stage directions (the auto-advance constraint from §5).

The editor shows each slide with its narration, `notes`, `delivery` cue and a character/seconds counter, and a
one-line refinement box per slide ("punchier", "add the outage anecdote here", "cut to 40 s") that calls
`replaceSlide()` with the neighbours as context. Edited slides are protected from regenerate-all (§4.4).

### Step 5 — Q&A prep (one LLM call, then the user)

The model anticipates questions the *stated audience* would ask — 2–3 per slide and 5–10 for the whole deck —
tagged by type (clarifying, sceptical, decision, off-topic-but-likely), by persona ("the CFO asks…") and, most
usefully, **whether the deck can answer it**. Questions the deck cannot answer are exactly where the presenter's
knowledge is needed, so they are listed first. For each question the user sets a policy and, where needed, the text:

| Policy | What the live presenter does |
|---|---|
| **answer** — user supplies or approves the answer | answers from it, in 1–3 sentences, then returns to the script |
| **deck** — answerable from the slide/context | answers from the extracted material |
| **defer** — "we'll follow up after the session" (optional reason) | says so, offers to follow up, does not speculate |
| **avoid** — topic must not be discussed (confidential, legal, not yet announced) | declines politely and moves on |
| **pre-empt** — fold the answer into the narration of slide N | the script addresses it before it is asked |

Answers can also be pasted or uploaded as reference text (a status report, a pricing sheet); it goes into the
context file, subject to the limits below. The full bank is kept in `presentations/<name>-qa.md` (human-editable);
the compact, approved form is written into the script and context:

- slide-scoped Q&A → that slide's `> notes:` (≤ ~1400 chars, because `presenter.js` sends notes as one
  500-token `thinking.append`);
- deck-level Q&A, the "do not say" list and the brief's intent → the context file, with a fixed budget split
  (intent ≈2k chars, approved Q&A ≈15k, extracted background ≈20k, under the 40k cap from §5). Each question has
  a priority so overflow drops the least important, with a warning, instead of truncating silently.

### Step 6 — Rehearse, then learn from the session

The presenter page is unchanged. After each session the app stores the user's transcript turns (already
produced by the live service) and the model's answers in `decks/<name>/sessions/<ISO>.json`; the Q&A screen then
shows **"asked last time, not in the bank"** so the user adds answers before the real talk. Over two or three
rehearsals the bank converges on what the audience actually asks.

### Runtime changes (small, and the only ones outside the pipeline)

- `script-parser.js`: a `> delivery:` blockquote per slide (tone, pace, emphasis), parsed like `> notes:`;
  frontmatter `brief:`, `fidelity:`.
- `prompt.js`: `buildSystemInstructions` gets an *Intent* paragraph (audience, purpose, key messages) and an
  *Answering policy* paragraph (use approved answers; if not covered, say what the slide shows and offer to follow
  up; never invent numbers; decline "avoid" topics; keep answers under ~30 s, then bridge back). `buildSlideInstruction`
  appends the delivery cue. `fidelity` selects the delivery rule: `verbatim` is today's "as written";
  `close` allows "small natural rewording, every fact and number unchanged"; `free` gives the model the slide's
  key points and lets it talk. `close` needs a live A/B with the headless timing report before it becomes the
  default — the model already skips the tail of a part occasionally under `verbatim`.
- `presenter.js`: unchanged apart from writing the session log.

### Optional later: a voice interview

Steps 2–3 could be a three-minute conversation through GPT-Live itself ("tell me about this talk; who is in the
room?"), with the transcript turned into the brief. The infrastructure exists (`LiveSession`), and it fits a voice
product, but the service's transcripts are lossy and a session costs money, so the text form stays the default and
the voice interview is a phase-4 experiment.

### Cost and calls

Deck questions (1) + draft (1–4) + Q&A bank (1) + refinements (≈1 per edited slide) ≈ 4–8 requests per deck, well
under 10 RPM, roughly $0.30–0.80 per prepared deck on `claude-opus-5` / `gpt-5.x`.

## 7. Knowledge base: the user's documents as the source of truth

The deck says *what* the presenter claims; the documents behind it — the status report, the SOW, the risk
register, last quarter's numbers, the design spec — say *why*, and they are where most audience questions are
actually answered. The user uploads them once into a per-user library, attaches the relevant ones to a
presentation, and three steps of the wizard draw on them. Storage is Postgres + `pgvector` from phase 0
(`002-phase-0-platform-architecture.md` §2.5).

### Ingestion

```
upload (PDF, DOCX, PPTX, Markdown, TXT, HTML; ≤50 MB) ─▶ extract text per page/section
   ─▶ chunk (heading-aware, ~600 tokens, 15 % overlap, keep page/heading in meta)
   ─▶ embed (text-embedding-3-small, 1536 d; cached by chunk hash in Redis) ─▶ document_chunks (vector + tsvector)
```

Extraction reuses the deck extractors (PDF, PPTX, HTML) plus DOCX via Open XML; Markdown/TXT are split on
headings. Ingestion is a `generation_jobs` job with SSE progress like script generation; a document is `ready`
only when every chunk is embedded, and a re-upload re-embeds only changed chunks.

### Retrieval

Hybrid search: cosine similarity on the HNSW index **and** Postgres full-text (`tsvector`) fused with reciprocal
rank fusion — numbers, product names and acronyms (exactly what a presenter is asked about) are where pure vector
search is weakest. Scope is always the documents attached to the presentation, never the whole library. Top-k 5–8
chunks per query, each carrying `[document, page/heading]` so the model can cite and the user can check.

### Where it is used

| Step | Query | Effect |
|---|---|---|
| Draft (§6 step 4) | per slide: title + slide text + the brief's key messages | retrieved passages are attached to that slide's content block as *reference material — use for accuracy, do not recite*; the model quotes figures from the documents rather than inventing them, and the lint's "numbers grounded" check accepts a number found in a cited passage |
| Deck questions (§6 step 3) | the deck-specific questions the model drafts | suggested answers are pre-filled from the documents with citations, so the user confirms instead of typing |
| Q&A prep (§6 step 5) | each anticipated question | `answerable` becomes three-valued: *deck*, *documents* (with the passage shown and the citation kept in `sources`), *neither* — the last group is where the presenter must answer; **document-grounded answers still require the user's approval**, they are not auto-approved |
| Context file | the approved Q&A plus the most-cited passages | compact form within the 40k-char budget of §5; a "Sources" section lists the documents so the live presenter can say where a figure comes from |

### Runtime limitation, stated plainly

During the live talk the presenter answers only from what is already in the session (`instructions` + slide
notes); GPT-Live's client delegation carries no task text, so **there is no dynamic retrieval mid-presentation
in v1**. The knowledge base is used at preparation time to make the notes and context as good as possible.
Runtime retrieval needs the Responses-delegation + tool route noted in the work log as v2; when that exists the
same hybrid search becomes a tool the live model can call, with the answer read from the retrieved passage.

### Privacy and control

Chunks and embeddings leave the machine only to the embedding provider chosen for the library (the same
pre-flight disclosure as generation); a local ONNX embedding model (`bge-small` on `Microsoft.ML.OnnxRuntime`,
which inkspoke already ships) is the privacy option for a later phase. Documents are per user, deletable, and
deletion removes chunks and cached embeddings. Every generated sentence that relies on a passage keeps its citation
in the draft metadata so the user can see what came from where.

## 8. Recommendation

**Do phase 0 first, then build A with the preparation wizard of §6 and the knowledge base of §7, in the phases
below, starting with single-file HTML.** The interview and Q&A prep are in phase 1, not later: without them the
generated script is not something the user would present, so a pipeline without them does not demonstrate the
feature. The knowledge base is its own phase right after, because the Q&A answers are only as good as their
sources. Explicitly not building: PPTX→HTML conversion, browser-side LLM calls, automatic shim injection, a job
queue beyond `generation_jobs` + Redis pub/sub, presenting while generating, a rich editor, Playwright, an LLM
judge in v1, voice navigation, runtime retrieval (v2, needs Responses delegation), the voice interview (phase 4
experiment at most).

## 9. Phased plan sketch

Phase 0 is specified in `002-phase-0-platform-architecture.md` §3 (seven sub-steps). Phases 1–5 below assume that
stack: `PresenterAi.Application` slices instead of `src/server/*.js`, EF tables instead of files, React screens
instead of `src/web/*.js`. Their estimates are ~⅓ higher than the Node numbers the agents debated.

| Phase | Scope | Acceptance | Effort |
|---|---|---|---|
| **0 — Platform** | React 19 + TS web app; .NET 10 Clean Architecture API with a byte-identical `/ws` bridge; port of parser/prompt/`LiveSession`/`Presenter` against the existing tests as a parity oracle; Google OAuth2 (inkspoke `SsoService` pattern) + JWT + API keys + WebSocket tickets; Postgres 17 + pgvector via EF Core migrations; Redis (`HybridCache`, job pub/sub → SSE, upstream session lease); **admin site** (`web/admin` + `/v1/admin/*`, copied from inkspoke and pruned) for users + sign-in policy, upstream providers (encrypted keys, test connection, rotate), models + bindings (= failover priority), usage and live sessions, audit log; docker-compose for dev/test; Node retired. | The Ricoh deck presents from the React app for two different Google users with the same behaviour as the Node MVP (AC1–AC10); `dotnet test` incl. Testcontainers green; a second user pressing Start while the slot is busy gets "slots busy"; an admin disables the Azure provider in the UI and the next Start fails over to OpenAI. | 5–7 w |
| **1 — HTML → interview → script → Q&A → present** | `POST /v1/decks` (multipart, single `.html`, `IFileStore`), HTML extractor (AngleSharp, deck-driver selectors); brief form with three presets persisted in `prep`; deck-specific questions with suggested answers (1 call); one provider family (OpenAI Responses first; Azure OpenAI is the same client), generation prompt with the spoken-delivery rubric, `ScriptWriter` + lint (notes ≤1400 chars, context budget, ≤2 paragraphs); Q&A bank with the five policies → notes/context; `POST /v1/presentations/{id}/generate` job with pre-flight confirmation and SSE progress; script editor with per-slide textareas (narration, notes, delivery); `> delivery:` in the parser and the intent/answering-policy paragraphs in `PromptBuilder`; refuse while a session is live. | Upload the Ricoh HTML → answer the brief and 6 deck questions → script in <90 s that opens by addressing the steering committee and lands the three key messages → answer 5 Q&A items (one `defer`, one `avoid`) → Start; ask the deferred question aloud and hear "I'll follow up after the session". | 7–9 d |
| **2 — Knowledge base** | Document library (upload PDF/DOCX/PPTX/MD/TXT/HTML), ingestion job (extract → chunk → embed, Redis embedding cache), hybrid retrieval (pgvector HNSW + tsvector, RRF), attach documents to a presentation; retrieval wired into draft generation (per-slide reference material with citations), deck questions (pre-filled answers) and Q&A prep (`answerable: documents`, passage shown, approval still required); "Sources" in the context file; deletion cascades. | Attach the Ricoh status report → the generated slide-6 narration quotes the variance figure from the report with a citation → an anticipated question about the go-live date is pre-answered from the SOW and shown for approval → the live presenter answers it in rehearsal. | 6–9 d |
| **3 — PDF + second provider + refinement loop** | PDF extractor (PdfPig) + the pdf.js `show(n)` deck template, native PDF input with browser-rendered thumbnail fallback; Anthropic adapter + provider/model picker; per-slide refinement box (`ReplaceSlide`), `presentation_versions`, edited-slide protection; session log → "asked last time, not in the bank"; `fidelity: close` A/B with the CLI timing report. | Upload a PDF export → present it; "make slide 4 punchier" changes only slide 4; a question asked in rehearsal appears in the Q&A screen afterwards. | 5–8 d |
| **4 — PPTX + unknown HTML + voice interview experiment** | PPTX extractor (Open XML: speaker notes feed the brief and Q&A automatically), `soffice` conversion with the PDF-export fallback in the UI copy, zip upload for multi-file HTML decks, explicit "add navigation shim" action (previewed, written to our copy only), privacy/cost panel polish; optional voice interview spike. | Upload a `.pptx` → its speaker notes appear as suggested answers → present through the PDF template. | 6–9 d |
| **5 — fidelity & quality extras (on demand)** | Headless-browser screenshots for text-poor HTML slides (network blocked), opt-in cross-model critique with diff, BYO-key settings UI (encrypted at rest), local ONNX embeddings as the privacy option, "changed on disk" for exported scripts, hardening (zip limits, process timeouts, provider conformance fixtures), session affinity / realtime service split if scale demands it. | — | 2–3 w |

## 10. Risks / open questions

- Decks with slides in plain `<div>`s: extraction falls back to whole-page text and the deck is undriveable until
  the shim action exists (phase 4). Reveal vertical stacks are still not handled by the driver.
- Structured-output support differs across providers and Azure `api-version`s; fallback = ask for Markdown and
  validate with `parsePresentation`. Exact `gpt-5.x` ids belong in a server config table.
- Decks >30 slides: windowed generation risks tonal drift; the global brief plus the previous window's last
  narration mitigates it but is unmeasured.
- Should `<name>-context.md` contain the full extracted deck text (as `ricoh-context.md` does)? Yes — it is what
  makes Q&A work and it is free, subject to the 40k-char cap.
- Language: detect from the deck text; `prompt.js` already instructs the model to speak the narration's language.
- LibreOffice absent or wrong fonts on Windows: the PDF-export instruction must be in the UI, not only docs.
- **Interview friction.** Nine brief fields plus 5–8 questions plus a Q&A bank is real work; the presets, the
  suggested answers and "skip" everywhere keep the minimum path at ~5 minutes, but this must be measured with a
  real deck, and the wizard must be resumable and never lose an answer.
- **`fidelity: close` is unproven.** Letting the live model reword may make it drop or reorder content; keep
  `verbatim` as the default until the headless timing report shows `close` delivers every fact.
- **Answers that overflow the budgets.** A generous Q&A bank plus the full deck text exceeds 40k chars of context
  quickly; the priority-based trimming must be visible in the UI, and slide-scoped notes must stay under one
  500-token append or the upstream rejects the event.
- **Sceptical questions the presenter would rather not pre-answer.** The Q&A screen must make "avoid" and "defer"
  as easy as "answer", and the live model's refusal wording needs a rehearsal check (AC6 was never exercised live).
- **Session log privacy.** Storing audience transcripts locally is fine for rehearsal; for a real audience the
  user should be able to turn the log off.
- **Re-platform before product proof (phase 0).** Four to six weeks of porting before any new user-visible
  feature, and the live-audio behaviour can regress silently; mitigations are the parity suite, live smoke in
  every sub-step and keeping the Node MVP runnable until 0.7. If the feature needs to be demonstrated sooner,
  phase 1 could be spiked on Node first and re-implemented in phase 0 — that costs the spike twice.
- **Knowledge-base grounding is only as good as the chunks.** Tables in PDFs and numbers split across chunks
  produce confident wrong answers; keep page/heading metadata, prefer larger chunks for tabular sections, and
  never auto-approve a document-grounded Q&A answer.
- **Embedding provider lock-in and privacy.** Vectors are tied to one model; documents leave the machine for
  embedding unless the local ONNX option (phase 5) is used. Say so in the upload dialog.
