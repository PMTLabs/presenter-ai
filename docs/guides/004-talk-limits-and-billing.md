# 004 — Talk limits and billing

The server bounds how long a GPT-Live upstream can remain connected. Limits apply to web and CLI talks; no setting
turns off the server ceiling.

## The guards

- **Maximum length:** the wall-clock limit starts when Start is accepted, so presentation loading and connection time
  count. It continues through pauses, disconnects for pause grace, and reconnects. The talk ends at the limit from
  any state.
- **Effective cap:** `min(script maxMinutes or configured default, web override when lower, configured ceiling)`.
  For example, a script limit of 90 minutes, web selection of 45, and a 120-minute ceiling gives 45 minutes. A
  script or override above the ceiling is clamped; the server logs a warning. A web override can only lower the cap.
- **Script limit:** add a positive whole number to presentation frontmatter:

  ```yaml
  ---
  title: Example talk
  maxMinutes: 45
  ---
  ```

  A zero, negative, fractional, or non-integer value fails import with
  `maxMinutes must be a positive whole number of minutes`. A value above the ceiling imports and is clamped when the
  talk starts.
- **Web Length picker:** while idle, choose Default or 5, 10, 15, 20, 30, 45, 60, or 90 minutes. Default uses the
  script limit when present, otherwise the server default. The choice is per talk and is not saved to the
  presentation.
- **Pause grace:** pausing starts a grace period (120 seconds by default). Resuming within it keeps the same upstream.
  After it expires, the server closes and disposes the upstream but leaves the talk paused. Resume reconnects (usually
  1–3 seconds), sends the current slide context again, and narrates that slide from its beginning because the new
  upstream has no memory. This avoids continued upstream billing while paused.
- **Idle:** while presenting, five minutes without activity ends the talk. Activity is voiced model output, a
  non-empty user transcript, slide presentation, any command other than microphone audio, or a tool call/completion.
  Microphone frames and the silence pump do not count. The idle deadline does not run while paused; pause grace
  applies instead.
- **Warning:** 60 seconds before the maximum-length or idle cutoff, the app displays a countdown and the presenter
  says a short warning once when presenting with an open upstream. Activity clears an idle warning; it can be raised
  again if the talk becomes idle. A paused talk gets the banner, not spoken output.
- **Heartbeat:** the server sends an application-level `ping` at the configured interval; the client answers `pong`.
  Any inbound frame, including audio, also counts as alive. No inbound frame before the timeout aborts the socket
  and ends the talk. A background tab that continues responding to pings remains connected; the idle guard still
  bounds a silent talk. A frozen tab that cannot answer or send frames is disconnected by the heartbeat.
- **CLI:** the first Ctrl+C requests graceful End and waits for close; disposal is a backstop. `--max-seconds` remains
  an additional client-side stop and is clamped to the configured ceiling (with a warning) if it exceeds that
  ceiling. The presenter-level cap applies independently.

## End reasons

The `end_reason` vocabulary describes why the talk ended; it is distinct from the provider's `close_reason`.

| Value | Plain meaning |
|---|---|
| `user` | The user ended the talk (including voice, tool, or End control). |
| `completed` | The presentation's wrap-up completed. |
| `max_length` | The effective wall-clock talk cap elapsed. |
| `idle` | The presenter was inactive until its idle deadline. |
| `heartbeat` | The browser connection missed its heartbeat deadline. |
| `writer_failed` | The server could not write to the browser socket. |
| `backpressure` | The browser could not keep up with outgoing data. |
| `disconnect` | The browser disconnected without a more specific abort reason. |
| `takeover` | The same user took over the active session. |
| `shutdown` | The application or presenter is shutting down. |
| `error` | An unexpected presenter failure triggered fail-safe close. |
| `upstream_lost` | The upstream connection closed unexpectedly. |
| `reconnect_failed` | No upstream route could be connected after pause-close. |
| `cli_cancelled` | CLI Ctrl+C cancelled the talk. |
| `cli_max_seconds` | The CLI `--max-seconds` stop elapsed. |
| `stop_after_slide` | The CLI stop-after-slide option ended the run. |

A provider `close_reason` can additionally say how its socket closed (for example, `close_timeout`); it does not
replace `end_reason`.

## Recorded usage

The `sessions` row records the talk's local start/end times and billing information:

| Column | How to read it |
|---|---|
| `started_at`, `ended_at` | Talk connection start and end, using the presenter's clock when available. |
| `usage_seconds` | Confirmed upstream usage when `usage_confirmed` is true; otherwise the estimated duration. |
| `usage_confirmed` | `true` means provider usage was confirmed; `false` means duration is estimated. `null` identifies
  a row recorded before this change. |
| `estimated_seconds` | Local elapsed estimate, retained whether or not final provider usage was confirmed. |
| `end_reason` | Why the talk ended, from the vocabulary above. |
| `close_reason` | Provider/diagnostic close detail; separate from `end_reason`. |
| `upstream`, `upstream_session_id` | Upstream route and its session identifier, when available. |

When a close times out or final usage is unavailable, `usage_seconds` uses the elapsed estimate instead of reporting
zero for a talk that ran. A provider-confirmed `usage_seconds` is the best available billing duration; it is not a
provider invoice reconciliation.

## Configuration

Set these .NET configuration keys for the API and restart it. Environment-variable names use double underscores in
place of colons. All values are validated at startup. Ranges are inclusive; the heartbeat timeout must also be at
least twice the heartbeat interval. There is no off switch: the ceiling always applies.

| .NET key | Default | Allowed range | Environment variable |
|---|---:|---:|---|
| `Presenter:MaxTalkMinutes` | 60 | 5 to ceiling | `Presenter__MaxTalkMinutes` |
| `Presenter:MaxTalkCeilingMinutes` | 120 | 5–240 | `Presenter__MaxTalkCeilingMinutes` |
| `Presenter:PauseGraceSeconds` | 120 | 30–900 | `Presenter__PauseGraceSeconds` |
| `Presenter:IdleTimeoutSeconds` | 300 | 120–1800 | `Presenter__IdleTimeoutSeconds` |
| `Session:HeartbeatIntervalSeconds` | 15 | 5–60 | `Session__HeartbeatIntervalSeconds` |
| `Session:HeartbeatTimeoutSeconds` | 45 | at least 2 × interval, up to 300 | `Session__HeartbeatTimeoutSeconds` |

`MaxTalkMinutes` cannot exceed `MaxTalkCeilingMinutes`; that ceiling itself cannot exceed 240 minutes. The warning
lead is fixed at 60 seconds, not configurable. The default ceiling is 120 minutes, so an ordinary default talk is
capped at 60 minutes and a script or web selection cannot raise it past the ceiling.
