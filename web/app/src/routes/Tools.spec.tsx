import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const api = vi.hoisted(() => ({
  GET: vi.fn(),
  POST: vi.fn(),
  PUT: vi.fn(),
  PATCH: vi.fn(),
  DELETE: vi.fn(),
}));
vi.mock("@presenter/shared/api", () => ({ default: api }));
import { Tools } from "./Tools";

const alpha = {
  id: "a",
  name: "Alpha",
  url: "https://alpha.example/mcp",
  authKind: "header",
  status: "connected",
  lastErrorCode: null,
  alwaysAsk: false,
  hasCredential: true,
  lastConnectedAt: null,
};
const beta = {
  ...alpha,
  id: "b",
  name: "Beta",
  status: "needs_reconnect",
  hasCredential: false,
};
const toolList = [
  { name: "lookup", title: "Lookup", description: null, readOnly: true, alwaysAsk: false },
  { name: "write", title: "Write", description: null, readOnly: false, alwaysAsk: false },
];

function setup() {
  api.GET.mockImplementation((path: string) =>
    Promise.resolve(
      path === "/v1/tools/servers"
        ? { data: [alpha, beta] }
        : path === "/v1/tools/settings"
          ? { data: { webSearchEnabled: false } }
          : { data: toolList },
    ),
  );
  api.POST.mockResolvedValue({ data: { ok: true, toolCount: 2 } });
  api.PUT.mockResolvedValue({ data: alpha });
  api.PATCH.mockResolvedValue({ data: alpha });
  api.DELETE.mockResolvedValue({});
  return render(<Tools />, { wrapper: MemoryRouter });
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset();
});

describe("Tools", () => {
  it("lists servers with status, and reconnect state", async () => {
    setup();
    expect(await screen.findByText("Alpha")).toBeTruthy();
    expect(screen.getByText("Connected")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
  });

  it("adds a server and displays Problem Details errors", async () => {
    setup();
    await screen.findByText("Alpha");
    api.POST.mockResolvedValueOnce({
      error: { code: "tools_url_blocked", title: "blocked" },
    });
    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "New" },
    });
    fireEvent.change(screen.getByLabelText("URL"), {
      target: { value: "https://new.example/mcp" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add server" }));
    expect((await screen.findByRole("alert")).textContent).toContain(
      "This server URL is not allowed.",
    );
  });

  it(
    "saves a header from a blank password field, clears it and shows saved without revealing credentials",
    async () => {
    setup();
    await screen.findByText("Alpha");
    const secret = screen.getAllByLabelText("Key / header value")[0] as HTMLInputElement;
    expect(secret.type).toBe("password");
    expect(secret.value).toBe("");
    fireEvent.change(secret, { target: { value: "sensitive-value" } });
    fireEvent.click(screen.getAllByRole("button", { name: "Save key" })[0]);
    await waitFor(() => expect(secret.value).toBe(""));
    expect(api.PUT).toHaveBeenCalledWith(
      "/v1/tools/servers/{id}/credential",
      expect.objectContaining({
        body: { headerName: "Authorization", headerValue: "sensitive-value" },
      }),
    );
    expect(screen.getByText("saved")).toBeTruthy();
    expect(screen.queryByDisplayValue("sensitive-value")).toBeNull();
    expect(screen.queryByText("credential-value")).toBeNull();
  });

  it("tests server and shows test result", async () => {
    setup();
    await screen.findByText("Alpha");
    fireEvent.click(screen.getAllByRole("button", { name: "Test" })[0]);
    expect((await screen.findByRole("status")).textContent).toContain(
      "Connection test passed (2 tools).",
    );
  });

  it("shows tool badges and keeps always ask locked on for non-read-only tools", async () => {
    setup();
    await screen.findByText("Alpha");
    fireEvent.click(screen.getAllByRole("button", { name: "Tools" })[0]);
    expect(await screen.findByText("read-only")).toBeTruthy();
    expect(screen.getByText("asks first")).toBeTruthy();
    const always = screen.getByRole("checkbox", {
      name: "Always ask for write",
    }) as HTMLInputElement;
    expect(always.checked).toBe(true);
    expect(always.disabled).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: "Always ask for lookup" }));
    expect(api.PUT).toHaveBeenCalledWith(
      "/v1/tools/servers/{id}/tools/{toolName}",
      expect.objectContaining({
        params: { path: { id: "a", toolName: "lookup" } },
        body: { alwaysAsk: true },
      }),
    );
  });

  it("updates server-level always ask and web search with billing caption", async () => {
    setup();
    await screen.findByText("Alpha");
    expect(screen.getByText("Searches are billed per call.")).toBeTruthy();
    fireEvent.click(
      screen.getAllByRole("checkbox", { name: "Always ask for this server" })[0],
    );
    expect(api.PATCH).toHaveBeenCalledWith(
      "/v1/tools/servers/{id}",
      expect.objectContaining({ body: { alwaysAsk: true } }),
    );
    fireEvent.click(screen.getByRole("checkbox", { name: "Web search" }));
    expect(api.PUT).toHaveBeenCalledWith("/v1/tools/settings", {
      body: { webSearchEnabled: true },
    });
  });

  it("disconnects and removes a server", async () => {
    setup();
    await screen.findByText("Alpha");
    fireEvent.click(screen.getAllByRole("button", { name: "Disconnect" })[0]);
    expect(api.DELETE).toHaveBeenCalledWith(
      "/v1/tools/servers/{id}/credential",
      { params: { path: { id: "a" } } },
    );
    fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[0]);
    expect(api.DELETE).toHaveBeenCalledWith("/v1/tools/servers/{id}", {
      params: { path: { id: "a" } },
    });
  });

  it("opens advanced OAuth credentials on client-required and retries with values then clears secret", async () => {
    setup();
    await screen.findByText("Alpha");
    api.POST.mockResolvedValueOnce({
      error: { code: "tools_oauth_client_required", title: "required" },
    });
    fireEvent.click(screen.getAllByRole("button", { name: "Connect" })[0]);
    expect(await screen.findByText("Advanced: client ID and secret")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Client ID"), {
      target: { value: "client-id" },
    });
    const secret = screen.getByLabelText("Client secret") as HTMLInputElement;
    fireEvent.change(secret, { target: { value: "private-secret" } });
    api.POST.mockResolvedValueOnce({
      data: { authorizationUrl: "https://auth.example/" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Retry connect" }));
    await waitFor(() =>
      expect(api.POST).toHaveBeenLastCalledWith(
        "/v1/tools/servers/{id}/oauth/start",
        expect.objectContaining({
          body: { clientId: "client-id", clientSecret: "private-secret" },
        }),
      ),
    );
    expect(secret.value).toBe("");
  });
});
