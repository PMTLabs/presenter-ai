import { useMemo } from "react";
import { usePresenterStore } from "./presenterStore";
import type { Turn } from "./presenterStore";

export type Exchange = {
  question: string;
  answer: string;
  slideIndex: number;
};

/**
 * `train_turn` accepts `question` and `answer` of 1…2,000 characters (UTF-16 code units, as both `String.length` and
 * .NET `string.Length` count them) that are not whitespace-only, inside a 16 KiB text frame (plan 010 §4.3,
 * `PresenterBridge.TryReadTrainTurn`). The exchange is built to satisfy every one of those limits so a click can
 * never send a frame the bridge rejects.
 */
export const MAX_TRAINING_TEXT_CHARS = 2000;
const ELLIPSIS = "…";

/**
 * Transcript text is speech, so control characters carry no meaning; JSON would also escape each to six bytes, and
 * U+0085 is whitespace to .NET but not to `String.trim()`. Replace them (and any unpaired surrogate, which JSON escapes
 * the same way) with a space, then collapse runs of whitespace. After this every code unit costs at most three bytes
 * on the wire, so two 2,000-character fields always fit the 16 KiB frame.
 */
function normalise(text: string): string {
  return text
    // eslint-disable-next-line no-control-regex -- matching control characters is the point
    .replace(/[\u0000-\u001f\u007f-\u009f]|[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

const isHighSurrogate = (code: number) => code >= 0xd800 && code <= 0xdbff;

/** The answer keeps its beginning (what the presenter said first): cut at a word boundary and mark the cut. */
function keepStart(text: string): string {
  if (text.length <= MAX_TRAINING_TEXT_CHARS) return text;
  let end = MAX_TRAINING_TEXT_CHARS - ELLIPSIS.length;
  if (isHighSurrogate(text.charCodeAt(end - 1))) end--;
  const space = text.lastIndexOf(" ", end);
  if (space > end / 2) end = space;
  return text.slice(0, end).trimEnd() + ELLIPSIS;
}

/**
 * The question keeps its end: its user turns are joined oldest first, and the last of them — what the presenter went
 * on to answer — holds the actual question, while the oldest words are lead-in. Cut at a word boundary, mark the cut.
 */
function keepEnd(text: string): string {
  if (text.length <= MAX_TRAINING_TEXT_CHARS) return text;
  let start = text.length - (MAX_TRAINING_TEXT_CHARS - ELLIPSIS.length);
  if (isHighSurrogate(text.charCodeAt(start - 1))) start++;
  const space = text.indexOf(" ", start - 1);
  if (space !== -1 && space - start < (text.length - start) / 2) start = space + 1;
  return ELLIPSIS + text.slice(start).trimStart();
}

/**
 * Groups transcript turns into trainer exchanges: a run of user turns followed by every presenter turn up to the
 * next user turn. A presenter turn stamped with a different slide than the question also ends the exchange, so
 * narration resumed after a navigation is never swallowed into an older exchange (plan 010 §4.1 Web).
 *
 * Returns one entry per input turn: the `Exchange` a presenter turn belongs to, or `null` for a user turn or for a
 * presenter turn with no preceding question in the talk (or one whose slide has already moved on).
 */
export function groupExchanges(turns: readonly Turn[]): (Exchange | null)[] {
  const result: (Exchange | null)[] = new Array(turns.length).fill(null);
  let i = 0;
  while (i < turns.length) {
    if (turns[i].role !== "user") {
      i++;
      continue;
    }
    const questionStart = i;
    while (i < turns.length && turns[i].role === "user") i++;
    const questionEnd = i;
    const slideIndex = turns[questionStart].slide;
    const answerStart = i;
    while (i < turns.length && turns[i].role !== "user" && turns[i].slide === slideIndex) i++;
    const answerEnd = i;
    if (answerEnd > answerStart) {
      const question = keepEnd(
        normalise(
          turns
            .slice(questionStart, questionEnd)
            .map((turn) => turn.text)
            .join(" "),
        ),
      );
      const answer = keepStart(
        normalise(
          turns
            .slice(answerStart, answerEnd)
            .map((turn) => turn.text)
            .join(" "),
        ),
      );
      // Whitespace-only speech has nothing to train on and the bridge would reject it: no exchange, button disabled.
      if (question.length === 0 || answer.length === 0) continue;
      const exchange: Exchange = { question, answer, slideIndex };
      for (let k = answerStart; k < answerEnd; k++) result[k] = exchange;
    }
  }
  return result;
}

/**
 * Memoises the grouping on the transcript array's own reference (a zustand selector returns the stored array
 * unchanged until `message()` replaces it), so re-renders that do not touch the transcript never recompute it.
 */
export function useExchanges(): (Exchange | null)[] {
  const turns = usePresenterStore((state) => state.transcript);
  return useMemo(() => groupExchanges(turns), [turns]);
}
