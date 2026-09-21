// Parses presentation.md (YAML frontmatter + "## Slide N" sections) and chunks narration.

import matter from 'gray-matter';

const SLIDE_HEADING = /^##\s+Slide\s+(\d+)\s*(?:[—–:-]\s*(.*?))?\s*$/i;
const NOTES_START = /^>\s*notes:\s*(.*)$/i;
const BLOCKQUOTE_LINE = /^>\s?(.*)$/;

export const DEFAULT_CHUNK_CHARS = 1400;

/**
 * @param {string} markdown
 * @param {{ id?: string }} [opts]
 * @returns {{ meta: object, slides: Array<{index:number,number:number,title:string,narration:string,notes:string}> }}
 */
export function parsePresentation(markdown, opts = {}) {
  const { data, content } = matter(markdown);
  const meta = normaliseMeta(data, opts.id);

  const lines = content.split(/\r?\n/);
  const sections = [];
  let current = null;
  for (const line of lines) {
    const m = line.match(SLIDE_HEADING);
    if (m) {
      current = { number: Number(m[1]), title: (m[2] || '').trim(), lines: [] };
      sections.push(current);
    } else if (current) {
      current.lines.push(line);
    }
  }

  if (sections.length === 0) {
    throw new Error(`${meta.id}: no "## Slide N" sections found`);
  }
  sections.forEach((s, i) => {
    if (s.number !== i + 1) {
      throw new Error(
        `${meta.id}: slide headings must be contiguous from 1; found "## Slide ${s.number}" at position ${i + 1}`,
      );
    }
  });
  if (!meta.deck) throw new Error(`${meta.id}: frontmatter "deck" is required`);

  const slides = sections.map((s, i) => {
    const { narration, notes } = splitNotes(s.lines);
    return { index: i, number: s.number, title: s.title, narration, notes };
  });
  return { meta, slides };
}

function normaliseMeta(data, id) {
  const meta = {
    id: id || data.id || 'presentation',
    title: String(data.title || id || 'Presentation'),
    deck: data.deck ? String(data.deck) : '',
    driver: data.driver ? String(data.driver) : 'auto',
    voice: data.voice ? String(data.voice) : undefined,
    context: data.context ? String(data.context) : undefined,
    advanceSilenceMs: toInt(data.advanceSilenceMs),
    chunkChars: toInt(data.chunkChars) ?? DEFAULT_CHUNK_CHARS,
  };
  if (meta.chunkChars < 200) throw new Error(`${meta.id}: chunkChars must be >= 200`);
  return meta;
}

function toInt(v) {
  if (v === undefined || v === null || v === '') return undefined;
  const n = Number.parseInt(v, 10);
  return Number.isFinite(n) ? n : undefined;
}

/** Separates "> notes:" blockquotes from narration lines. */
function splitNotes(lines) {
  const narration = [];
  const notes = [];
  let inNotes = false;
  for (const raw of lines) {
    const start = raw.match(NOTES_START);
    if (start) {
      inNotes = true;
      if (start[1]) notes.push(start[1]);
      continue;
    }
    const bq = raw.match(BLOCKQUOTE_LINE);
    if (inNotes && bq) {
      notes.push(bq[1]);
      continue;
    }
    inNotes = false;
    narration.push(raw);
  }
  return {
    narration: tidy(narration.join('\n')),
    notes: tidy(notes.join('\n')),
  };
}

function tidy(text) {
  return text
    .replace(/\r/g, '')
    .split('\n')
    .map((l) => l.trimEnd())
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/**
 * Splits text into chunks of at most maxChars, preferring sentence boundaries and
 * never cutting inside a word. chunks.join(' ') equals the text modulo whitespace.
 */
export function chunkText(text, maxChars = DEFAULT_CHUNK_CHARS) {
  const clean = String(text || '').replace(/\s+/g, ' ').trim();
  if (!clean) return [];
  if (clean.length <= maxChars) return [clean];

  const sentences = clean.match(/[^.!?]+(?:[.!?]+|$)/g)?.map((s) => s.trim()).filter(Boolean) ?? [clean];
  const chunks = [];
  let buf = '';
  const flush = () => {
    if (buf) chunks.push(buf);
    buf = '';
  };
  for (const sentence of sentences) {
    const pieces = sentence.length > maxChars ? splitLong(sentence, maxChars) : [sentence];
    for (const piece of pieces) {
      if (!buf) buf = piece;
      else if (buf.length + 1 + piece.length <= maxChars) buf += ' ' + piece;
      else {
        flush();
        buf = piece;
      }
    }
  }
  flush();
  return chunks;
}

function splitLong(sentence, maxChars) {
  const out = [];
  let rest = sentence;
  while (rest.length > maxChars) {
    let cut = rest.lastIndexOf(' ', maxChars);
    if (cut <= 0) cut = maxChars; // no whitespace at all: hard cut
    out.push(rest.slice(0, cut).trim());
    rest = rest.slice(cut).trim();
  }
  if (rest) out.push(rest);
  return out;
}
