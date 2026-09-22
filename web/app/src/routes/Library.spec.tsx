import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get } }));
import { Library } from "./Library";

describe("Library", () => {
  beforeEach(() => get.mockReset());

  it("renders presentations from the paged success envelope", async () => {
    get.mockResolvedValue({
      data: {
        items: [
          { id: "one", title: "First presentation", slideCount: 2, deck: "sample", driver: "auto" },
          { id: "two", title: "Second presentation", slideCount: 3, deck: "sample", driver: "auto" },
        ],
        page: 1,
        pageSize: 25,
        total: 2,
      },
    });
    render(<Library />, { wrapper: MemoryRouter });
    expect(await screen.findByText("First presentation")).toBeTruthy();
    expect(screen.getByText("Second presentation")).toBeTruthy();
  });

  it("uses stable mapped copy for Problem Details", async () => {
    get.mockResolvedValue({
      error: {
        code: "presentation.not_found",
        detail: "The server detail must not be displayed.",
        title: "Not found",
      },
    });
    render(<Library />, { wrapper: MemoryRouter });
    expect(await screen.findByText("The presentation was not found.")).toBeTruthy();
    expect(screen.queryByText("The server detail must not be displayed.")).toBeNull();
  });
});
