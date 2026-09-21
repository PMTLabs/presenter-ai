// Pure builders for the system instructions and per-slide instruction texts sent to GPT-Live.

export const CONTEXT_CHAR_BUDGET = 48_000; // ~12k tokens; session.instructions max is 16,384 tokens

export function buildSystemInstructions({ title, slides, context = '', maxContextChars = CONTEXT_CHAR_BUDGET, onWarn }) {
  const outline = slides
    .map((s) => `${s.index + 1}. ${s.title || '(untitled)'}`)
    .join('\n');

  let ctx = String(context || '').trim();
  if (ctx.length > maxContextChars) {
    onWarn?.(`context truncated from ${ctx.length} to ${maxContextChars} chars`);
    ctx = ctx.slice(0, maxContextChars) + '\n[context truncated]';
  }

  const parts = [
    `You are the presenter delivering a talk titled "${title}" to a live audience. A presentation controller shows the slides on screen and, for each slide, sends you an instruction that contains that slide's narration.`,
    '',
    'Delivery rules:',
    '- When you receive a slide\'s narration, speak it in full and in order, as written, with only minimal natural rephrasing. Do not add content that is not in the narration or the background context. Never read labels, headings, or stage directions such as "Slide 3" or "part 1 of 2" aloud.',
    '- Keep a steady, natural presenter pace. Do not pause for more than a second or two in the middle of a slide.',
    '- When you finish a slide\'s narration, stop speaking and wait silently. Do not announce the next slide and do not ask whether to continue; the controller sends the next slide.',
    '- Long narrations arrive in numbered parts. Continue from one part to the next immediately, without a break.',
    '',
    'Audience interaction (you can hear the audience):',
    '- If someone speaks to you, stop, listen, and answer briefly, in one to three sentences, using the narration and the background context. If the answer is not in your material, say so honestly.',
    '- After answering, say a short bridge such as "Back to the slide" and resume the current slide\'s narration from where you left off.',
    '- Never start the next slide on your own.',
    '',
    'Speak in the same language as the narration.',
    '',
    'Slide outline:',
    outline,
  ];
  if (ctx) {
    parts.push('', '--- Background context (for answering questions; do not recite it) ---', ctx);
  }
  return parts.join('\n');
}

function slideLabel(index, total, title) {
  return `slide ${index + 1} of ${total}${title ? ` ("${title}")` : ''}`;
}

/**
 * Instruction that makes the model speak one chunk of a slide's narration.
 * @param {{index:number,total:number,title?:string,chunk:string,part:number,parts:number,interrupt?:boolean}} p
 */
export function buildSlideInstruction({ index, total, title, chunk, part = 1, parts = 1, interrupt = false }) {
  const head = [];
  if (interrupt) head.push('Stop whatever you are saying now.');
  if (parts === 1) {
    head.push(`Present ${slideLabel(index, total, title)} now. Say the following narration in full, in order, with only minimal natural rephrasing, then stop and wait:`);
  } else if (part === 1) {
    head.push(`Present ${slideLabel(index, total, title)} now. Its narration comes in ${parts} parts; this is part 1. Say it in full and in order, then pause briefly; part 2 will follow:`);
  } else if (part < parts) {
    head.push(`Part ${part} of ${parts} of ${slideLabel(index, total, title)}. If you have not finished the previous part, finish it first. Then continue with this text, in full and in order, and pause briefly; part ${part + 1} will follow:`);
  } else {
    head.push(`Part ${part} of ${parts} of ${slideLabel(index, total, title)}, the last part. If you have not finished the previous part, finish it first. Then say this text in full and in order, then stop and wait:`);
  }
  return `${head.join(' ')}\n\n"""\n${chunk}\n"""`;
}

export function buildNotesContext({ index, total, title, notes }) {
  return `Speaker notes for ${slideLabel(index, total, title)} (background for questions, do not read aloud):\n${notes}`;
}

export function buildResumeInstruction({ index, total, title }) {
  return `Resume ${slideLabel(index, total, title)} from where you left off, then stop and wait.`;
}

export function buildPauseInstruction() {
  return 'Pause now. Stay silent and do not speak until you are told to resume.';
}

export function buildNudgeInstruction({ index, total, title }) {
  return `Begin presenting ${slideLabel(index, total, title)} now, using the narration you were given.`;
}

export function buildWrapUpInstruction() {
  return 'That was the last slide. Thank the audience in one or two sentences, then stop speaking.';
}
