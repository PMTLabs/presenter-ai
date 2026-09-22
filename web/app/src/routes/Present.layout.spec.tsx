import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { get, load } = vi.hoisted(() => ({
  get: vi.fn(),
  load: vi.fn().mockResolvedValue({ adapter: "sections", count: 1 }),
}));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get } }));
vi.mock("../deck/deckDriver", () => ({
  DeckDriver: class {
    load = load;
    dispose() {}
  },
}));
vi.mock("../audio/capture", () => ({ startAudio: vi.fn() }));
vi.mock("../ws/bridgeClient", () => ({
  BridgeClient: class {
    snapshot = { state: "idle" };
    on() {}
    connect() {}
    disconnect() {}
  },
}));
import { Present } from "./Present";

function renderPresent() {
  return render(
    <MemoryRouter initialEntries={["/present/demo"]}>
      <Routes><Route path="/present/:id" element={<Present />} /></Routes>
    </MemoryRouter>,
  );
}

describe("Present layout", () => {
  beforeEach(() => {
    get.mockReturnValue(new Promise(() => {}));
    load.mockClear();
    window.localStorage.clear();
  });

  it("renders two panels with an accessible separator", () => {
    renderPresent();

    const group = document.querySelector("[data-group]");
    expect(group?.id).toBe("presenter-split");
    expect(group?.querySelectorAll("[data-panel]")).toHaveLength(2);
    expect(group?.querySelector('[role="separator"]')).not.toBeNull();
  });

  it("keeps the deck in the first panel and transcript in the second", () => {
    renderPresent();

    const deckPanel = document.querySelector('[data-panel][id="deck"]');
    const sidePanel = document.querySelector('[data-panel][id="side"]');
    expect(deckPanel?.contains(screen.getByTitle("Presentation deck"))).toBe(true);
    expect(sidePanel?.contains(screen.getByRole("heading", { name: "Transcript" }))).toBe(true);
  });

  it("reads the presenter split layout from localStorage", () => {
    const getItem = vi.spyOn(Storage.prototype, "getItem");
    renderPresent();

    expect(getItem).toHaveBeenCalledWith("presenter-split");
    getItem.mockRestore();
  });
});
