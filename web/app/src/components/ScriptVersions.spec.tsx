import { fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { usePresenterStore } from "../store/presenterStore";

const { get, post } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock("@presenter/shared/api", () => ({ default: { GET: get, POST: post } }));
import { ScriptVersions } from "./ScriptVersions";

const listResponse = (overrides?: Partial<{ items: unknown[] }>) => ({
  data: {
    items: [
      {
        number: 2,
        source: "live_edit",
        createdAt: "2026-01-02T10:00:00Z",
        summary: "Shortened slide 3",
        baseVersion: 1,
        revertedFrom: null,
        changedSlides: [2],
        isCurrent: true,
      },
      {
        number: 1,
        source: "import",
        createdAt: "2026-01-01T10:00:00Z",
        summary: "Imported from deck.md",
        baseVersion: null,
        revertedFrom: null,
        changedSlides: [],
        isCurrent: false,
      },
    ],
    page: 1,
    pageSize: 25,
    total: 2,
    ...overrides,
  },
});

const detailResponse = (overrides?: Record<string, unknown>) => ({
  data: {
    number: 1,
    source: "import",
    createdAt: "2026-01-01T10:00:00Z",
    summary: "Imported from deck.md",
    baseVersion: null,
    revertedFrom: null,
    changedSlides: [],
    isCurrent: false,
    slides: [],
    changes: [
      { slideIndex: 2, title: "Pricing", before: "Old narration text.", after: "New narration text." },
    ],
    ...overrides,
  },
});

function isRevisionsList(path: string): boolean {
  return path === "/v1/presentations/{id}/revisions";
}

describe("ScriptVersions", () => {
  beforeEach(() => {
    get.mockReset();
    post.mockReset();
    usePresenterStore.setState({ scriptVersion: null, edits: {}, editOrder: [], currentEditId: null });
  });

  afterEach(() => {
    usePresenterStore.setState({ scriptVersion: null, edits: {}, editOrder: [], currentEditId: null });
  });

  it("lists every version with source and summary", async () => {
    get.mockImplementation((path: string) =>
      isRevisionsList(path) ? Promise.resolve(listResponse()) : Promise.resolve(detailResponse()),
    );
    render(<ScriptVersions presentationId="demo" />);

    expect(await screen.findByText("v2 (current)")).toBeTruthy();
    expect(screen.getByText("live_edit")).toBeTruthy();
    expect(screen.getByText("Shortened slide 3")).toBeTruthy();
    expect(screen.getByText("v1")).toBeTruthy();
    expect(screen.getByText("import")).toBeTruthy();
    expect(screen.getByText("Imported from deck.md")).toBeTruthy();
  });

  it("shows before and after for changed slides", async () => {
    get.mockImplementation((path: string) =>
      isRevisionsList(path) ? Promise.resolve(listResponse()) : Promise.resolve(detailResponse()),
    );
    render(<ScriptVersions presentationId="demo" />);
    const row = await screen.findByText("v1");
    fireEvent.click(row);

    expect(await screen.findByText("Slide 3: Pricing")).toBeTruthy();
    expect(screen.getByText("Before: Old narration text.")).toBeTruthy();
    expect(screen.getByText("After: New narration text.")).toBeTruthy();
  });

  it("revert posts and refreshes", async () => {
    let listCalls = 0;
    get.mockImplementation((path: string) => {
      if (isRevisionsList(path)) {
        listCalls++;
        return Promise.resolve(listResponse());
      }
      return Promise.resolve(detailResponse());
    });
    post.mockResolvedValue({
      data: {
        revision: {
          number: 3,
          source: "revert",
          createdAt: "2026-01-03T10:00:00Z",
          summary: "Reverted to v1",
          baseVersion: 2,
          revertedFrom: 1,
          changedSlides: [],
          isCurrent: true,
        },
        pendingEdits: [],
      },
    });
    render(<ScriptVersions presentationId="demo" />);
    const row = await screen.findByText("v1");
    fireEvent.click(row);
    const revertButton = await screen.findByRole("button", { name: "Revert to this version" });
    const callsBeforeRevert = listCalls;
    fireEvent.click(revertButton);

    expect(post).toHaveBeenCalledWith(
      "/v1/presentations/{id}/revisions/{number}/revert",
      { params: { path: { id: "demo", number: 1 } } },
    );
    await vi.waitFor(() => expect(listCalls).toBeGreaterThan(callsBeforeRevert));
  });

  it("revert shows the pending edits that will follow", async () => {
    get.mockImplementation((path: string) =>
      isRevisionsList(path) ? Promise.resolve(listResponse()) : Promise.resolve(detailResponse()),
    );
    post.mockResolvedValue({
      data: {
        revision: {
          number: 3,
          source: "revert",
          createdAt: "2026-01-03T10:00:00Z",
          summary: "Reverted to v1",
          baseVersion: 2,
          revertedFrom: 1,
          changedSlides: [],
          isCurrent: true,
        },
        pendingEdits: [
          { id: "edit_4", slideIndexes: [2], status: "queued" },
          { id: "edit_5", slideIndexes: [3], status: "processing" },
        ],
      },
    });
    render(<ScriptVersions presentationId="demo" />);
    const row = await screen.findByText("v1");
    fireEvent.click(row);
    const revertButton = await screen.findByRole("button", { name: "Revert to this version" });
    fireEvent.click(revertButton);

    const notice = await screen.findByRole("status");
    expect(within(notice).getByText(/2 pending edits will apply after this revert/)).toBeTruthy();
    expect(within(notice).getByText(/edit_4 \(slide 3, queued\)/)).toBeTruthy();
    expect(within(notice).getByText(/edit_5 \(slide 4, processing\)/)).toBeTruthy();
  });

  it("shows revision.not_found message", async () => {
    get.mockImplementation((path: string) => {
      if (isRevisionsList(path)) return Promise.resolve(listResponse());
      return Promise.resolve({
        error: { code: "revision.not_found", title: "Not Found", detail: "hidden detail" },
      });
    });
    render(<ScriptVersions presentationId="demo" />);
    const row = await screen.findByText("v1");
    fireEvent.click(row);

    expect(await screen.findByText("That script version was not found.")).toBeTruthy();
    expect(screen.queryByText("hidden detail")).toBeNull();
  });

  it("shows 409 conflict message", async () => {
    get.mockImplementation((path: string) =>
      isRevisionsList(path) ? Promise.resolve(listResponse()) : Promise.resolve(detailResponse()),
    );
    post.mockResolvedValue({
      error: { code: "concurrency.conflict", title: "Conflict", detail: "hidden detail" },
    });
    render(<ScriptVersions presentationId="demo" />);
    const row = await screen.findByText("v1");
    fireEvent.click(row);
    const revertButton = await screen.findByRole("button", { name: "Revert to this version" });
    fireEvent.click(revertButton);

    expect(await screen.findByText("This item changed. Refresh and try again.")).toBeTruthy();
    expect(screen.queryByText("hidden detail")).toBeNull();
  });
});
