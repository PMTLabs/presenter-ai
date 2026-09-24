import { describe, expect, it } from "vitest";
import type { Turn } from "./presenterStore";
import { groupExchanges } from "./exchanges";

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
    // Q on slide 2; the answer turn keeps the question's slide even though the talk navigated to 5 mid-answer
    // (presenterStore keeps a merged turn's slide at the first delta's value — exchanges.ts trusts that stamp).
    const turns: Turn[] = [user("What about the coating?", 2), presenter("The coating uses a new alloy.", 2)];
    const exchanges = groupExchanges(turns);

    expect(exchanges[1]?.slideIndex).toBe(2);
  });

  it("disables train on this without a preceding question", () => {
    const turns: Turn[] = [presenter("Welcome to the talk.", 0)];
    const exchanges = groupExchanges(turns);

    expect(exchanges[0]).toBeNull();
  });
});
