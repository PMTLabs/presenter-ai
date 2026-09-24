import { useMemo } from "react";
import { usePresenterStore } from "./presenterStore";
import type { Turn } from "./presenterStore";

export type Exchange = {
  question: string;
  answer: string;
  slideIndex: number;
};

/** `train_turn` caps `answer` at 2,000 chars (plan 010 §4.3); trim here so every caller sends a valid frame. */
const MAX_ANSWER_CHARS = 2000;

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
      const question = turns
        .slice(questionStart, questionEnd)
        .map((turn) => turn.text)
        .join(" ")
        .trim();
      const answer = turns
        .slice(answerStart, answerEnd)
        .map((turn) => turn.text)
        .join(" ")
        .trim()
        .slice(0, MAX_ANSWER_CHARS);
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
