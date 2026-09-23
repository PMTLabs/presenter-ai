import { useCallback, useEffect, useState } from "react";
import { useLocation } from "react-router-dom";
import apiClient, { type components } from "@presenter/shared/api";
import { errorMessages, isProblem } from "@presenter/shared";

type Server = components["schemas"]["ServerView"];
type Tool = components["schemas"]["ToolItemView"];

function problemMessage(value: unknown, fallback: string) {
  return isProblem(value)
    ? (errorMessages[value.code] ?? value.detail ?? value.title ?? fallback)
    : fallback;
}

export function Tools() {
  const location = useLocation();
  const [servers, setServers] = useState<Server[]>([]);
  const [webSearch, setWebSearch] = useState(false);
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [notice, setNotice] = useState(
    () => (location.state as { toolsNotice?: string } | null)?.toolsNotice ?? "",
  );
  const [error, setError] = useState("");
  const [headerForms, setHeaderForms] = useState<
    Record<string, { name: string; value: string; saved: boolean }>
  >({});
  const [advanced, setAdvanced] = useState<Record<string, boolean>>({});
  const [oauth, setOauth] = useState<
    Record<string, { clientId: string; clientSecret: string }>
  >({});
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [tools, setTools] = useState<Record<string, Tool[]>>({});

  const load = useCallback(async () => {
    const [serverResult, settingsResult] = await Promise.all([
      apiClient.GET("/v1/tools/servers"),
      apiClient.GET("/v1/tools/settings"),
    ]);
    if (serverResult.data) setServers(serverResult.data);
    else if (serverResult.error) {
      setError(problemMessage(serverResult.error, "Unable to load tool servers."));
    }
    if (settingsResult.data) setWebSearch(settingsResult.data.webSearchEnabled);
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const add = async (event: React.FormEvent) => {
    event.preventDefault();
    setError("");
    setNotice("");
    const result = await apiClient.POST("/v1/tools/servers", { body: { name, url } });
    if (result.error) {
      setError(problemMessage(result.error, "Unable to add server."));
      return;
    }
    setName("");
    setUrl("");
    setNotice("Server added.");
    await load();
  };

  const updateServer = async (id: string, body: { alwaysAsk?: boolean }) => {
    const result = await apiClient.PATCH("/v1/tools/servers/{id}", {
      params: { path: { id } },
      body,
    });
    if (result.error) {
      setError(problemMessage(result.error, "Unable to update server."));
    } else {
      await load();
      if (body.alwaysAsk !== undefined && expanded[id]) {
        const toolsResult = await apiClient.GET("/v1/tools/servers/{id}/tools", {
          params: { path: { id } },
        });
        if (toolsResult.data) {
          setTools((old) => ({ ...old, [id]: toolsResult.data ?? [] }));
        }
      }
    }
  };

  const connect = async (server: Server) => {
    setError("");
    setNotice("");
    const values = oauth[server.id];
    const result = await apiClient.POST("/v1/tools/servers/{id}/oauth/start", {
      params: { path: { id: server.id } },
      body: values?.clientId || values?.clientSecret ? values : {},
    });
    if (result.error) {
      if (
        isProblem(result.error) &&
        result.error.code === "tools_oauth_client_required"
      ) {
        setAdvanced((old) => ({ ...old, [server.id]: true }));
      }
      setError(problemMessage(result.error, "Unable to connect server."));
      return;
    }
    setOauth((old) => ({
      ...old,
      [server.id]: { clientId: "", clientSecret: "" },
    }));
    if (result.data?.authorizationUrl) {
      window.location.assign(result.data.authorizationUrl);
    } else {
      setNotice("Connected.");
      await load();
    }
  };

  const saveHeader = async (server: Server) => {
    const form = headerForms[server.id] ?? {
      name: "Authorization",
      value: "",
      saved: false,
    };
    const result = await apiClient.PUT("/v1/tools/servers/{id}/credential", {
      params: { path: { id: server.id } },
      body: { headerName: form.name, headerValue: form.value },
    });
    if (result.error) {
      setError(problemMessage(result.error, "Unable to save header."));
      return;
    }
    setHeaderForms((old) => ({
      ...old,
      [server.id]: { name: form.name, value: "", saved: true },
    }));
    setNotice("Credential saved.");
    await load();
  };

  const toggleTools = async (id: string) => {
    if (expanded[id]) {
      setExpanded((old) => ({ ...old, [id]: false }));
      return;
    }
    const result = await apiClient.GET("/v1/tools/servers/{id}/tools", {
      params: { path: { id } },
    });
    if (result.error) {
      setError(problemMessage(result.error, "Unable to load tools."));
    } else {
      setTools((old) => ({ ...old, [id]: result.data ?? [] }));
      setExpanded((old) => ({ ...old, [id]: true }));
    }
  };

  const doTest = async (id: string) => {
    const result = await apiClient.POST("/v1/tools/servers/{id}/test", {
      params: { path: { id } },
    });
    if (result.error) {
      setError(problemMessage(result.error, "Test failed."));
    } else {
      setNotice(
        result.data?.ok
          ? `Connection test passed (${result.data.toolCount} tools).`
          : `Connection test failed: ${result.data?.errorCode ?? "unknown"}.`,
      );
    }
    await load();
  };

  return (
    <section className="mx-auto max-w-5xl space-y-6 p-6">
      <h1 className="text-2xl font-bold">Tools</h1>
      {notice && (
        <p
          role="status"
          className="rounded-lg bg-green-50 p-3 text-green-800 dark:bg-green-950 dark:text-green-200"
        >
          {notice}
        </p>
      )}
      {error && (
        <p
          role="alert"
          className="rounded-lg bg-red-50 p-3 text-red-700 dark:bg-red-950 dark:text-red-200"
        >
          {error}
        </p>
      )}
      <section className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
        <label className="flex items-center gap-3 font-medium">
          <input
            type="checkbox"
            checked={webSearch}
            onChange={async (event) => {
              const enabled = event.target.checked;
              setWebSearch(enabled);
              const result = await apiClient.PUT("/v1/tools/settings", {
                body: { webSearchEnabled: enabled },
              });
              if (result.error) {
                setWebSearch(!enabled);
                setError(
                  problemMessage(result.error, "Unable to update web search."),
                );
              }
            }}
          />
          Web search
        </label>
        <p className="mt-1 text-sm text-gray-500">
          Searches are billed per call.
        </p>
      </section>
      <form
        onSubmit={(event) => void add(event)}
        className="space-y-3 rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900"
      >
        <h2 className="font-semibold">Add server</h2>
        <label className="block text-sm">
          Name
          <input
            className="mt-1 block w-full rounded border p-2 dark:bg-gray-950"
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </label>
        <label className="block text-sm">
          URL
          <input
            type="url"
            className="mt-1 block w-full rounded border p-2 dark:bg-gray-950"
            required
            value={url}
            onChange={(event) => setUrl(event.target.value)}
          />
        </label>
        <button className="rounded bg-gray-900 px-4 py-2 text-white dark:bg-white dark:text-gray-900">
          Add server
        </button>
      </form>
      <div className="space-y-4">
        {servers.map((server) => (
          <article
            key={server.id}
            className="space-y-4 rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900"
          >
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h2 className="font-semibold">{server.name}</h2>
                <p className="text-sm text-gray-500">
                  {new URL(server.url).host} · {server.authKind}
                </p>
                <span className="text-sm">
                  {server.status === "connected"
                    ? "Connected"
                    : server.status === "needs_reconnect"
                      ? "Needs reconnect"
                      : server.status === "error"
                        ? "Error"
                        : "Not connected"}
                </span>
              </div>
              <div className="flex flex-wrap gap-2">
                <button
                  onClick={() => void connect(server)}
                  className="rounded border px-3 py-1"
                >
                  {server.status === "needs_reconnect" ? "Reconnect" : "Connect"}
                </button>
                <button
                  onClick={() => void doTest(server.id)}
                  className="rounded border px-3 py-1"
                >
                  Test
                </button>
                <button
                  onClick={() => void toggleTools(server.id)}
                  className="rounded border px-3 py-1"
                >
                  Tools
                </button>
                <button
                  onClick={async () => {
                    const result = await apiClient.DELETE(
                      "/v1/tools/servers/{id}/credential",
                      { params: { path: { id: server.id } } },
                    );
                    if (result.error) {
                      setError(problemMessage(result.error, "Unable to disconnect."));
                    } else {
                      setNotice("Disconnected.");
                      await load();
                    }
                  }}
                  className="rounded border px-3 py-1"
                >
                  Disconnect
                </button>
                <button
                  onClick={async () => {
                    const result = await apiClient.DELETE(
                      "/v1/tools/servers/{id}",
                      { params: { path: { id: server.id } } },
                    );
                    if (result.error) {
                      setError(problemMessage(result.error, "Unable to remove server."));
                    } else {
                      setNotice("Server removed.");
                      await load();
                    }
                  }}
                  className="rounded border px-3 py-1"
                >
                  Remove
                </button>
              </div>
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={server.alwaysAsk}
                onChange={(event) =>
                  void updateServer(server.id, {
                    alwaysAsk: event.target.checked,
                  })
                }
              />
              Always ask for this server
            </label>
            <form
              onSubmit={(event) => {
                event.preventDefault();
                void saveHeader(server);
              }}
              className="flex flex-wrap items-end gap-2"
            >
              <label className="text-sm">
                Header name
                <input
                  className="mt-1 block rounded border p-2 dark:bg-gray-950"
                  value={headerForms[server.id]?.name ?? "Authorization"}
                  onChange={(event) =>
                    setHeaderForms((old) => ({
                      ...old,
                      [server.id]: {
                        name: event.target.value,
                        value: old[server.id]?.value ?? "",
                        saved: false,
                      },
                    }))
                  }
                />
              </label>
              <label className="text-sm">
                Key / header value
                <input
                  type="password"
                  autoComplete="new-password"
                  className="mt-1 block rounded border p-2 dark:bg-gray-950"
                  value={headerForms[server.id]?.value ?? ""}
                  onChange={(event) =>
                    setHeaderForms((old) => ({
                      ...old,
                      [server.id]: {
                        name: old[server.id]?.name ?? "Authorization",
                        value: event.target.value,
                        saved: false,
                      },
                    }))
                  }
                />
              </label>
              <button className="rounded border px-3 py-2">Save key</button>
              {server.hasCredential && (
                <span className="text-sm text-green-700 dark:text-green-300">
                  saved
                </span>
              )}
            </form>
            {advanced[server.id] && (
              <div className="space-y-2 rounded bg-gray-50 p-3 dark:bg-gray-800">
                <h3>Advanced: client ID and secret</h3>
                <label className="block text-sm">
                  Client ID
                  <input
                    className="mt-1 block w-full rounded border p-2 dark:bg-gray-950"
                    value={oauth[server.id]?.clientId ?? ""}
                    onChange={(event) =>
                      setOauth((old) => ({
                        ...old,
                        [server.id]: {
                          clientId: event.target.value,
                          clientSecret: old[server.id]?.clientSecret ?? "",
                        },
                      }))
                    }
                  />
                </label>
                <label className="block text-sm">
                  Client secret
                  <input
                    type="password"
                    autoComplete="new-password"
                    className="mt-1 block w-full rounded border p-2 dark:bg-gray-950"
                    value={oauth[server.id]?.clientSecret ?? ""}
                    onChange={(event) =>
                      setOauth((old) => ({
                        ...old,
                        [server.id]: {
                          clientId: old[server.id]?.clientId ?? "",
                          clientSecret: event.target.value,
                        },
                      }))
                    }
                  />
                </label>
                <button
                  onClick={() => void connect(server)}
                  className="rounded border px-3 py-1"
                >
                  Retry connect
                </button>
              </div>
            )}
            {expanded[server.id] && (
              <ul className="space-y-2">
                {(tools[server.id] ?? []).map((tool) => (
                  <li
                    key={tool.name}
                    className="flex flex-wrap items-center gap-3 border-t pt-2"
                  >
                    <span>{tool.title ?? tool.name}</span>
                    <span className="rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800">
                      {tool.readOnly && !server.alwaysAsk && !tool.alwaysAsk ? "read-only" : "asks first"}
                    </span>
                    <label className="ml-auto flex items-center gap-2 text-sm">
                      <input
                        type="checkbox"
                        aria-label={`Always ask for ${tool.name}`}
                        checked={!tool.readOnly || server.alwaysAsk || tool.alwaysAsk}
                        disabled={!tool.readOnly || server.alwaysAsk}
                        onChange={async (event) => {
                          const result = await apiClient.PUT(
                            "/v1/tools/servers/{id}/tools/{toolName}",
                            {
                              params: {
                                path: { id: server.id, toolName: tool.name },
                              },
                              body: { alwaysAsk: event.target.checked },
                            },
                          );
                          if (result.error) {
                            setError(
                              problemMessage(result.error, "Unable to update tool."),
                            );
                          } else {
                            setTools((old) => ({
                              ...old,
                              [server.id]: (old[server.id] ?? []).map((item) =>
                                item.name === tool.name
                                  ? { ...item, alwaysAsk: event.target.checked }
                                  : item,
                              ),
                            }));
                          }
                        }}
                      />
                      Always ask
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </article>
        ))}
      </div>
    </section>
  );
}
