import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const post = vi.hoisted(() => vi.fn());
vi.mock("@presenter/shared/api", () => ({ default: { POST: post } }));
import { ToolsOAuthCallback } from "./ToolsOAuthCallback";

beforeEach(() => post.mockReset());

describe("ToolsOAuthCallback", () => {
  it("posts complete and shows success", async () => {
    post.mockResolvedValue({ data: {} });
    render(
      <MemoryRouter
        initialEntries={[
          "/tools/oauth/callback?code=auth-code&state=state-value&iss=https%3A%2F%2Fissuer.example",
        ]}
      >
        <Routes>
          <Route
            path="/tools/oauth/callback"
            element={<ToolsOAuthCallback />}
          />
          <Route path="/tools" element={<p>Tools page</p>} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByText("Tool server connected successfully.")).toBeTruthy();
    expect(post).toHaveBeenCalledWith("/v1/tools/oauth/complete", {
      body: {
        code: "auth-code",
        state: "state-value",
        iss: "https://issuer.example",
      },
    });
  });

  it("shows a Problem Details error", async () => {
    post.mockResolvedValue({
      error: { code: "tools_oauth_state_invalid", title: "invalid" },
    });
    render(
      <MemoryRouter
        initialEntries={["/tools/oauth/callback?code=auth-code&state=state-value"]}
      >
        <ToolsOAuthCallback />
      </MemoryRouter>,
    );
    expect((await screen.findByRole("alert")).textContent).toContain(
      "The authorization session is invalid or expired.",
    );
  });
});
