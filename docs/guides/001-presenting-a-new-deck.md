# 001 — Presenting a new deck

What to do when you have a new slide deck and want presenter-ai to narrate it. Six steps,
about 30 minutes for a 10–15 slide deck (most of it is writing the narration).

## 1. Put the deck under `decks/`

```
decks/<name>/index.html      # plus any css/js/images it references, same folder
```

The deck must be served from this app (same origin) so the page can drive it. Copy the whole
folder, not just the HTML, if the deck has external assets.

- **PowerPoint / Keynote / Google Slides:** export to HTML first (or to PDF → HTML). The app
  does not read `.pptx` today.
- **Single-file HTML with its own JS** (like the Ricoh deck): copy as-is.

## 2. Check how the deck can be driven

Open the deck's HTML and match it against the auto-detected drivers (details in
[README → Supported decks](../../README.md#supported-decks)):

| Deck looks like | Driver |
|---|---|
| a global `show(n)` function and elements with class `slide` (`#slide-1`, `#slide-2`, …) | `showFn` |
| reveal.js (`Reveal.initialize(...)`) | `reveal` |
| plain `<section class="slide">` blocks toggled by an `active` class | `sections` |

If none matches, either add a tiny shim to the deck —

```html
<script>
  // presenter-ai shim: expose show(n) and mark slides with class "slide"
  function show(i) {
    document.querySelectorAll('.slide').forEach((el, k) => (el.style.display = k === i ? '' : 'none'));
    location.hash = `#slide-${i + 1}`;
  }
</script>
```

— or restructure the deck into `<section class="slide">` blocks and set `driver: sections`
in step 3. Without a driver the narration still plays but you flip slides by hand.

## 3. Write the script `presentations/<name>.md`

```markdown
---
title: Q3 Business Review
deck: decks/<name>/index.html
driver: auto                          # or showFn | reveal | sections
context: presentations/<name>-context.md   # optional, see step 4
advanceSilenceMs: 3000                # optional; raise if it advances mid-slide
---

## Slide 1 — Title
What the presenter says on slide 1. One or two paragraphs, spoken style.
> notes: facts the presenter may use to answer questions on this slide (not read aloud)

## Slide 2 — Agenda
…
```

Rules the parser enforces or that matter in practice:

- One `## Slide N — Title` section per deck slide, **contiguous from 1**, in deck order. The
  title after the dash is free text (any of `—`, `–`, `:` or `-` works as the separator).
- Keep each slide's narration under **~1400 characters** (≈90 s of speech). Longer text is
  split into parts sent one after another; the model occasionally clips the tail of a part.
- `> notes:` blockquotes are Q&A background for that slide only; they are never recited.
- Write for the ear: short sentences, no bullet fragments, spell out numbers and acronyms the
  first time. Paragraph breaks become ~2 s pauses.

A quick way to draft: paste the slide text into the section and rewrite it as something you
would actually say. `presentations/ricoh-delivery-overview.md` is a worked example (11 slides).

## 4. Optional: background context `presentations/<name>-context.md`

Plain Markdown with facts about the project, team, numbers, glossary — anything the audience
may ask about that is not on a slide. It goes into the model's system instructions once per
session (cap ≈48 k characters; longer files are truncated with a warning). Point to it with
`context:` in the frontmatter.

## 5. Load it and check the numbers

Start the .NET API from the repository root:

```bash
dotnet run --project src/PresenterAi.Api   # → http://localhost:47913
```

For the React path, run `cd web && bun run dev`, then open `http://localhost:47914/present/<name>`. When
`web/app/dist` has been built, the API serves the app at <http://localhost:47913>.

Pick the presentation in the dropdown. Before pressing Start, read the header line and the log:

- `<title> — N slides · showFn driver` → good.
- `deck 12 slides / script 11 slides (mismatch)` → a `## Slide` section is missing or the deck
  has a hidden/extra slide; fix the script so the counts match.
- `deck adapter: none matched` → go back to step 2 (set `driver:` or add the shim).

Dry run without a browser (costs a few cents of session time, stops after slide 2):

From the repository root, run the .NET `presenter-cli` equivalent:

```bash
dotnet run --project src/PresenterAi.Cli -- run <name> --stop-after-slide 2
```

It prints each slide change, the transcript of what was spoken and a speech/silence bar, so you
can see whether the narration is read verbatim and whether the advance timing fits.


## 6. Rehearse

Headphones on (otherwise the mic hears the model and it answers itself). Press **Start**, allow
the microphone. Keys: **Space** pause/resume, **→ / ←** next/previous, **M** mute, **Esc** end.
Ask a question out loud at any time — the model answers, then returns to the script.

Tuning after the first pass:

| Symptom | Change |
|---|---|
| Advances in the middle of a slide | raise `advanceSilenceMs` (frontmatter) to 3500–4500 |
| Long dead air between slides | lower `advanceSilenceMs`, not below 2500 |
| A slide is cut short or rushed | shorten that slide's narration below ~1400 chars |
| Wrong facts in Q&A | add them to `> notes:` (per slide) or the context file (global) |

More: [README](../../README.md) for keys, configuration and troubleshooting.
