/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, it } from "vitest";
import { DeckDriver } from "./deckDriver";
function fixture(body: string) {
  const iframe = document.createElement("iframe");
  document.body.append(iframe);
  Object.defineProperty(iframe, "src", { set() {}, get: () => "" });
  iframe.contentDocument!.body.innerHTML = body;
  return iframe;
}
describe("DeckDriver", () => {
  it("detects showFn deck", async () => {
    const frame = fixture('<div class="slide"></div>');
    (frame.contentWindow as any).show = () => {};
    const d = new DeckDriver(frame);
    const p = d.load("/deck", { detectTimeoutMs: 1 });
    frame.dispatchEvent(new Event("load"));
    expect((await p).adapter).toBe("showFn");
  });
  it("detects Reveal deck", async () => {
    const frame = fixture("");
    (frame.contentWindow as any).Reveal = {
      slide() {},
      getHorizontalSlides: () => [1, 2],
    };
    const d = new DeckDriver(frame);
    const p = d.load("/deck", { detectTimeoutMs: 1 });
    frame.dispatchEvent(new Event("load"));
    expect((await p).adapter).toBe("reveal");
  });
  it("detects section.slide deck", async () => {
    const frame = fixture('<section class="slide"></section>');
    const d = new DeckDriver(frame);
    const p = d.load("/deck", { detectTimeoutMs: 1 });
    frame.dispatchEvent(new Event("load"));
    expect((await p).adapter).toBe("sections");
  });
  it("reports none matched", async () => {
    const frame = fixture("");
    const d = new DeckDriver(frame);
    const p = d.load("/deck", { detectTimeoutMs: 1 });
    frame.dispatchEvent(new Event("load"));
    expect((await p).adapter).toBeNull();
  });
});
