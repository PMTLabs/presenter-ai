# 003 — External tools

Connect user-owned MCP servers and optionally enable hosted web search for a managed-mode talk. External tools are
available only when the selected upstream route has a delegation model; the CLI does not register this feature.
See the [audience questions guide](002-audience-questions.md) for the question hold and voice-command rules.

## Connect a server

In the signed-in presenter app, open **Tools**, add a name and an absolute HTTPS server URL, then choose an
authentication method. URLs resolving to non-public addresses are refused. Connections and credentials belong to
your account; a server owned by someone else is not visible to you. You can list/test tools, set **Always ask**,
disconnect (which deletes credentials), or remove the server. A maximum of **10 servers per user** is allowed.

A pasted header is stored encrypted and never returned to the page. Header names must be HTTP tokens; `Host`,
`Content-Length`, `Transfer-Encoding`, `Connection`, `Cookie`, and `Mcp-*` are rejected. Values are limited to
4 KiB and cannot contain CR/LF. Keep server-provided tool descriptions and results in mind as untrusted external
data.

### OAuth registration choices

Choose **Connect** for OAuth. The server's authorization metadata determines which registration option applies;
priority is:

1. **Pre-registered client ID (and optional secret):** use when the authorization server has given you an app
   registration. Enter the ID and, if required, its secret in the advanced fields. It is saved encrypted for reuse.
2. **Client ID metadata document:** used when the authorization server advertises support and the app has a public
   HTTPS `OAuth:ApiBaseUrl`. The server fetches the anonymous metadata at
   `/v1/tools/oauth/client-metadata.json`. This is preferred over dynamic registration. It is unavailable for a
   local HTTP app URL.
3. **Dynamic registration:** used if metadata-document registration is unavailable and the authorization server
   exposes registration. It remains supported for older servers although the MCP authorization specification
   deprecates it.
4. If none can be used, the app requests a pre-registered client ID (`tools_oauth_client_required`).

OAuth uses authorization code with PKCE S256. The callback is tied to the signed-in owner; short-lived state is
one-use. Access and refresh tokens are encrypted. If OAuth probing says the endpoint does not require auth, the
server is marked connected without OAuth. Never paste a real client secret into documentation or support logs.

## Permissions and use in a talk

An MCP tool marked `readOnlyHint: true` may run immediately. All other tools, including tools with no hint, require a
spoken confirmation. You can force confirmation for a whole server or an individual tool using **Always ask**.
When confirmation is required, the presenter asks exactly **“Shall I use {title} on {server}?”**; only an eligible
spoken yes while the model is silent runs the captured action. No, timeout, or another action cancels it. The model
cannot approve an action itself. Confirmation requests expire after 10 seconds; the question has up to 8 seconds to
be voiced. Tool calls time out after 10 seconds by default.

Tools are loaded at talk start in parallel. Each server's discovery has a 3-second budget
(`Tools:Mcp:StartBudgetMs`, starting after repository reads); the presenter applies a separate 4-second hard cap
(`StartBudgetMs` plus 1 second) to the whole talk-start load. A server may contribute at most 64 tools. A schema
larger than 4 KiB is skipped. The inline catalogue defaults to 16 tools; larger catalogues use discovery
(`find_tools` / `call_tool`) subject to its payload budget. A failed or slow server is skipped, logged, and does not
stop the talk. Tools are available only in managed delegation mode, not client/deck-only mode.

**Web search** is a separate switch, off by default. When enabled, the hosted `web_search` tool is offered in
managed mode. Searches are billed per call by the upstream provider; disable it if you do not want those calls.

## Configuration

Set options for the API and restart it. Local runs use API user-secrets or environment variables; Docker maps
configuration using `__` instead of `:`. Generate your own 32-byte key; do not copy a real key into files or docs.

| .NET key | Default | Purpose / validation |
|---|---|---|
| `Tools:CredentialKey` | unset (credential operations requiring storage return 503; unauthenticated servers still work) | Base64 encoding of exactly 32 bytes, AES-256-GCM key. Validated on API start when set. Keep as a secret outside the database. |
| `Tools:OAuthRedirectUri` | unset; derived from `OAuth:ApiBaseUrl` plus `/tools/oauth/callback` | Optional absolute HTTP or HTTPS callback URI. |
| `Tools:Mcp:StartBudgetMs` | `3000` | Server discovery budget at talk start; must be positive. |
| `Tools:Mcp:CallTimeoutSeconds` | `10` | MCP call/test timeout; must be positive. |
| `Tools:MaxInlineTools` | `16` | Inline tool catalogue threshold; valid range 0–128. |
| `OAuth:ApiBaseUrl` | empty | Public app/API base URL; client-ID-metadata registration requires HTTPS. OAuth settings validate this as absolute HTTP(S) when an existing SSO provider is enabled. |

The options readers, defaults and validators are in `ExternalToolsOptions.cs`, `ToolsOptions.cs`,
`DependencyInjection.cs`, `OAuthSettings.cs`, and `ToolOAuthMetadataEndpoint.cs` (source paths under
`src/PresenterAi.Infrastructure/` or `src/PresenterAi.Api/`). The tool-server count and web-search default are
persistence rules, not configuration values.

## Status and troubleshooting

Server status is one of `not_connected`, `connected`, `needs_reconnect`, or `error`. The server view exposes only
`lastErrorCode`, never credential material. Reconnect after an expired/invalid token, a changed credential key,
or an unreadable encrypted credential. Disconnect removes the saved credential; add it again to reconnect with a
header, or run OAuth again.

| Symptom / code | Meaning and action |
|---|---|
| `tools_url_invalid` | URL must be a valid absolute HTTPS URL without user-info or fragment. |
| `tools_url_blocked` | Host resolved to a blocked address. Use a genuinely public MCP endpoint; internal and metadata targets are intentionally unsupported. |
| `tools_server_limit` | Remove a server; the per-user maximum is 10. |
| `tools_credentials_unavailable` | `Tools:CredentialKey` is unset. Configure it before saving credentials or completing OAuth. |
| `tools_oauth_client_required` | Register an OAuth client and enter its client ID (and secret if required). |
| `tools_oauth_unsupported` | Server metadata/registration does not support the required PKCE or accepted client configuration. Ask the server administrator. |
| `tools_auth`, `tools_oauth_invalid_grant`, `credential_key_changed`, `credential_unreadable` | Authentication or stored credential cannot be used. Reconnect. |
| `tools_unreachable`, `timeout` | Server did not respond within its budget. Check availability and URL, then use **Test** or retry the talk. |
| `tools_redirect_refused` | A credential-bearing or protocol request attempted a redirect; redirects are refused for security. Configure the canonical endpoint. |
| `tools_response_too_large` | Upstream response exceeded its size cap. Ask the server operator to reduce the response. |
| `tools_oauth_state_invalid`, `tools_oauth_failed` | OAuth callback state/code was invalid, expired, already consumed, or exchange failed; restart Connect. |

The API's `/v1/tools` surface applies the `tools` rate-limit policy (30 requests per minute per user) to create,
credential, OAuth, test and list-tools operations. A rate limit is temporary; honor `Retry-After`. CRUD settings,
server listing/update/delete, disconnect and per-tool override routes do not attach this policy. Status and
Problem Details codes are safe diagnostics; never include credentials, arguments, or tool results in logs.

Outbound requests use HTTPS and a strict public-address guard, rechecking DNS at connect time and refusing blocked
addresses (including localhost, private, link-local, cloud metadata, redirects for authenticated requests, and
proxy bypass). This protects the app and is not configurable off.
