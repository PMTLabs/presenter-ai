import { describe, expect, it } from "vitest";
import { usePresenterStore, type Turn } from "./presenterStore";
import { MAX_TRAINING_TEXT_CHARS, groupExchanges } from "./exchanges";

const user = (text: string, slide: number): Turn => ({ role: "user", text, endMs: null, slide });
const presenter = (text: string, slide: number): Turn => ({ role: "assistant", text, endMs: null, slide });

describe("groupExchanges", () => {
  it("groups an answer split by a pause over 1 s into one exchange", () => {
    // Q1 on slide 2; its answer arrives as two presenter turns (a >1 s gap split them in presenterStore); then
    // Q2 on slide 2 with its own answer.
    const turns: Turn[] = [
      user("What about the coating?", 2),
      presenter("The coating uses a new alloy.", 2),
      presenter("It also resists corrosion.", 2),
      user("And the price?", 2),
      presenter("It costs 10% more.", 2),
    ];
    const exchanges = groupExchanges(turns);

    // Clicking either fragment of the first answer yields exactly Q1, both fragments joined.
    expect(exchanges[1]).toEqual({
      question: "What about the coating?",
      answer: "The coating uses a new alloy. It also resists corrosion.",
      slideIndex: 2,
    });
    expect(exchanges[2]).toBe(exchanges[1]);

    // Clicking Q2's answer yields only Q2 and its answer.
    expect(exchanges[4]).toEqual({
      question: "And the price?",
      answer: "It costs 10% more.",
      slideIndex: 2,
    });
  });

  it("ends an exchange at a presenter turn on another slide", () => {
    const turns: Turn[] = [
      user("What about the coating?", 2),
      presenter("The coating uses a new alloy.", 2),
      // Narration resumed after a navigation, unrelated to the question — must not be swallowed into it.
      presenter("Moving on to the next section.", 3),
    ];
    const exchanges = groupExchanges(turns);

    expect(exchanges[1]).toEqual({
      question: "What about the coating?",
      answer: "The coating uses a new alloy.",
      slideIndex: 2,
    });
    expect(exchanges[2]).toBeNull();
  });

  it("uses the slide of the question's first delta across navigation", () => {
    // Built through the store reducer so the navigation really happens between deltas: Q asked on slide 2, the
    // presenter starts answering on 2, navigates to 5 mid-answer (the answer keeps merging), then narrates slide 5.
    usePresenterStore.setState({ transcript: [], slide: 0 });
    const store = usePresenterStore.getState();
    store.message({ type: "slide", index: 2 });
    store.message({ type: "transcript", role: "user", delta: "What about", end_ms: 1000 });
    store.message({ type: "transcript", role: "user", delta: " the coating?", end_ms: 1400 });
    store.message({ type: "transcript", role: "assistant", delta: "The coating", end_ms: 3000 });
    store.message({ type: "slide", index: 5 });
    store.message({ type: "transcript", role: "assistant", delta: " uses a new alloy.", end_ms: 3500 });
    store.message({ type: "transcript", role: "assistant", delta: "Slide five covers pricing.", end_ms: 9000 });
    const turns = usePresenterStore.getState().transcript;
    expect(turns.map((turn) => turn.slide)).toEqual([2, 2, 5]);

    const exchanges = groupExchanges(turns);

    // The full answer, including the deltas after the navigation, targets the question's slide 2 — not 5.
    expect(exchanges[1]).toEqual({
      question: "What about the coating?",
      answer: "The coating uses a new alloy.",
      slideIndex: 2,
    });
    // Narration resumed on slide 5 is not the question's answer and is not trained on slide 5 either.
    expect(exchanges[2]).toBeNull();
  });

  it("targets the question's slide, never the slide an answer started on", () => {
    // Q on slide 2, but the presenter navigated to 5 before its first answer delta: under the plan's policy the
    // answer turn (stamped 5) ends the exchange, so nothing is offered — least of all an edit of slide 5 (or 2)
    // carrying an answer that was given about another slide.
    const turns: Turn[] = [user("What about the coating?", 2), presenter("Here is the pricing slide.", 5)];
    const exchanges = groupExchanges(turns);

    expect(exchanges).toEqual([null, null]);
  });

  describe("train_turn limits", () => {
    const words = (prefix: string, count: number) =>
      Array.from({ length: count }, (_, i) => `${prefix}${i}`).join(" ");

    it("keeps an overlong multi-turn question's latest words and caps both fields at 2,000 characters", () => {
      const leadIn = words("context", 250); // ~2,000 characters of lead-in over two turns
      const actualQuestion = "So what does the 2025 coating cost per unit?";
      const answer = words("answer", 400);
      const turns: Turn[] = [
        user(leadIn, 1),
        user(`${words("more", 60)} ${actualQuestion}`, 1),
        presenter(answer.slice(0, 1500), 1),
        presenter(answer.slice(1500), 1),
      ];
      expect(turns[0].text.length + turns[1].text.length).toBeGreaterThan(MAX_TRAINING_TEXT_CHARS);

      const exchange = groupExchanges(turns)[3]!;

      expect(exchange.slideIndex).toBe(1);
      expect(exchange.question.length).toBeLessThanOrEqual(MAX_TRAINING_TEXT_CHARS);
      expect(exchange.question.startsWith("…")).toBe(true);
      expect(exchange.question.endsWith(`more59 ${actualQuestion}`)).toBe(true);
      // Cut at a word boundary: the first kept word is a whole word of the lead-in.
      expect(exchange.question.slice(1).split(" ")[0]).toMatch(/^context\d+$/);
      expect(leadIn.split(" ")).toContain(exchange.question.slice(1).split(" ")[0]);

      expect(exchange.answer.length).toBeLessThanOrEqual(MAX_TRAINING_TEXT_CHARS);
      expect(exchange.answer.startsWith("answer0 answer1 ")).toBe(true);
      expect(exchange.answer.endsWith("…")).toBe(true);
      expect(answer.split(" ")).toContain(exchange.answer.slice(0, -1).split(" ").at(-1));
    });

    it("never leaves text the bridge treats as empty or escapes to more than three bytes", () => {
      // U+0085 is whitespace to .NET (the bridge rejects whitespace-only) but not to String.trim(); control
      // characters and unpaired surrogates would each be escaped to six bytes in the JSON frame.
      const turns: Turn[] = [
        user("\u0085\u0001", 0),
        presenter("An answer.", 0),
        user("Why?\u0007\ud800", 0),
        presenter("\u0085 \u001f", 0),
        user("Really\u0000why?", 0),
        presenter("Line one line\u000btwo \udc00.", 0),
      ];
      const exchanges = groupExchanges(turns);

      expect(exchanges[1]).toBeNull();
      expect(exchanges[3]).toBeNull();
      expect(exchanges[5]).toEqual({ question: "Really why?", answer: "Line one line two .", slideIndex: 0 });
    });
  });

  it("disables train on this without a preceding question", () => {
    const turns: Turn[] = [presenter("Welcome to the talk.", 0)];
    const exchanges = groupExchanges(turns);

    expect(exchanges[0]).toBeNull();
  });
});
