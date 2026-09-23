import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, useAuthStore } from "@presenter/shared";

const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get } }));
import { Library } from "./Library";

const user = { id: "usr_test", email: "test@example.invalid", displayName: null, role: "user" };

function signIn() {
  useAuthStore.setState({ ready: true, user });
}

describe("Library", () => {
  beforeEach(() => {
    get.mockReset();
    clearAuthSession();
    useAuthStore.setState({ ready: false });
  });

  afterEach(() => clearAuthSession());

  it("loads presentations when a user appears", async () => {
    useAuthStore.setState({ ready: true, user: null });
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
    useAuthStore.setState({ user });
    expect(await screen.findByText("First presentation")).toBeTruthy();
    expect(screen.getByText("Second presentation")).toBeTruthy();
  });

  it("uses stable mapped copy for Problem Details", async () => {
    signIn();
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

  it("does not request presentations before readiness or when signed out", async () => {
    render(<Library />, { wrapper: MemoryRouter });
    useAuthStore.setState({ user });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(get).not.toHaveBeenCalled();

    useAuthStore.setState({ ready: true, user: null });

    expect(await screen.findByText("Please sign in to continue.")).toBeTruthy();
    expect(get).not.toHaveBeenCalled();
  });

  it("clears its list when the user signs out", async () => {
    signIn();
    get.mockResolvedValue({
      data: { items: [{ id: "one", title: "First presentation", slideCount: 2 }], page: 1, pageSize: 25, total: 1 },
    });
    render(<Library />, { wrapper: MemoryRouter });
    expect(await screen.findByText("First presentation")).toBeTruthy();

    useAuthStore.setState({ user: null });

    expect(await screen.findByText("Please sign in to continue.")).toBeTruthy();
    expect(screen.queryByText("First presentation")).toBeNull();
  });
});
