# 001 — API and code conventions (frontend ↔ backend contract)

**Status:** adopted 2026-09-21 (decisions confirmed with the owner; see §11). Applies to every endpoint written
from plan 002 onward. The three Node-parity endpoints under `/api` were frozen exceptions until plan 004 removed
them; §10 keeps the record.
**Enforced by:** `PresenterAi.Contracts` (DTOs, `ErrorCodes`), the API's exception handler, `OpenApiTests`,
the generated TypeScript client in `web/shared`, and code review.

## 1. Principles

1. **One contract, generated twice.** The OpenAPI document at `/openapi/v1.json` is produced from the C# types;
   the TypeScript client and the `ErrorCode` union are generated from it and checked in. Hand-written client
   types are a bug.
2. **HTTP status carries the class of outcome; `code` carries the reason.** The frontend switches on `code`,
   never on `detail` text.
3. **Bare resources, standard errors.** Success bodies are the resource itself (no envelope); every error is
   RFC 9457 Problem Details with a stable `code`.
4. **Frozen wire formats are tested, not remembered.** Golden JSON tests pin the presentation payloads and the
   WebSocket frames; a changed field is a red test.

## 2. URLs and versioning

- Base path `/v1`. Breaking changes → `/v2` alongside, never in place. Since plan 004 there is no unversioned
  API surface (§10); only static assets (`/decks/**`), `/health` and `/openapi/v1.json` sit outside `/v1`.
- Resources are plural nouns in kebab-case: `/v1/presentations`, `/v1/decks`, `/v1/admin/provider-models`.
  Sub-resources nest one level: `/v1/presentations/{id}/versions`. Actions that are not CRUD are verbs as a
  final POST segment: `/v1/presentations/{id}/generate`, `/v1/admin/providers/{id}/test-connection`,
  `/v1/admin/providers/{id}/rotate-key`.
- Admin surface under `/v1/admin/...`; auth under `/v1/auth/...`; everything else is the user's own data and is
  scoped to the caller — there is no `/v1/users/{id}/...` for non-admins.
- Endpoint groups: one `Map<Area>Endpoints(this IEndpointRouteBuilder)` per area under `Api/Endpoints/`
  (`Endpoints/Admin/` for admin), wired in `Program.cs` — InkSpoke's shape.
- Query strings for filtering/paging only; ids and required parameters in the path or body. Never put secrets
  or personal data in a query string.

## 3. JSON

- `camelCase` properties; enums serialised as `camelCase` strings (`JsonStringEnumConverter`); no nulls for
  absent optional fields on output unless the null is meaningful (`start_ms: null` in the frozen frames is one).
- Timestamps: ISO 8601 UTC with `Z` (`2026-09-21T15:04:05.123Z`), never local time, never epoch numbers.
  Durations: integer milliseconds with an `…Ms` suffix (`advanceSilenceMs`), or seconds with `…Seconds`
  when the upstream uses seconds (`usageSeconds`). Money: decimal string with currency (`"0.05"`, `"USD"`).
- Ids: opaque, prefixed, URL-safe strings — `usr_`, `dck_`, `prs_` (presentation), `prv_` (provider),
  `mdl_` (model), `bnd_` (binding), `doc_`, `ses_`, `job_`, `qa_` — `prefix_` + 16 base32/hex chars, generated
  server-side (InkSpoke pattern). Internal database keys never appear in responses (`OpaqueIdLeakTests`).
- Request bodies are objects, never bare arrays; empty request body → no `Content-Type`.
- Unknown properties in requests are ignored; unknown enum strings are a `validation.failed` error.
- Binary payloads (audio, uploads) are never base64 in JSON; use multipart (uploads) or WebSocket binary frames.

## 4. Responses

| Case | Status | Body |
|---|---|---|
| Read one | 200 | the resource |
| Create | 201 + `Location` | the created resource |
| Update (full/partial) | 200 | the updated resource |
| Delete | 204 | empty |
| Action accepted, runs async | 202 | `{ jobId, status: "queued", statusUrl }` |
| Action completed synchronously | 200 | action result object |
| List | 200 | `{ items: T[], page, pageSize, total }` |
| Streaming progress | 200 `text/event-stream` | SSE events, §7 |

Lists: `?page=1&pageSize=25` (default 1/25, max `pageSize` 100), `?sort=field` / `?sort=-field` (leading `-`
= descending; only whitelisted fields), `?q=` free text where supported, other filters as named parameters
(`?status=active`). `total` is the unfiltered-by-page count. Out-of-range `page` returns an empty `items`.

Partial update uses `PATCH` with a JSON object containing only the fields to change (`null` clears); `PUT`
replaces. Optimistic concurrency where it matters (scripts, prep): `ETag` on read, `If-Match` on write,
`412` + `concurrency.conflict` on mismatch.

## 5. Errors — RFC 9457 Problem Details + `code`

Every non-2xx response is `application/problem+json`:

```json
{
  "type": "https://presenter-ai.dev/errors/presentation.not_found",
  "title": "Presentation not found",
  "status": 404,
  "detail": "No presentation with id prs_8f3a2c1d9e7b4a60 belongs to the caller.",
  "instance": "/v1/presentations/prs_8f3a2c1d9e7b4a60",
  "code": "presentation.not_found",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

- `code` — required, stable, from the catalogue (§6). `type` is derived from it (`…/errors/<code>`); the
  frontend never parses `type`.
- `title` — short, constant per code. `detail` — human sentence, may vary, may be shown to the user as-is,
  never contains secrets, keys or stack traces.
- `traceId` — the W3C trace id of the request, also in the `traceparent` response header; support asks for it.
- Validation errors add `errors`: `{ "errors": { "title": ["must not be empty"], "slides[3].narration": ["exceeds 1400 characters"] } }`
  with property paths in the request's camelCase.
- Rate limits add `retryAfterSeconds` and the `Retry-After` header. Quota errors add `quota: { kind, limit, used, resetsAt }`.
- Unexpected exceptions → 500 `internal.error` with a generic `detail`; the real exception goes to the log with
  the same `traceId`. Never leak `ex.Message` to the client.
- Implementation: `AddProblemDetails()` + one `IExceptionHandler` mapping a `DomainException(code, status, detail,
  extensions)` hierarchy; endpoints throw or return `Results.Problem(...)` via a helper `Problems.NotFound(code, …)`;
  no ad-hoc `new { error = … }` bodies (that is the InkSpoke drift this standard exists to prevent).

## 6. Error code catalogue

Format `area.reason`, lower snake_case, one C# source of truth (`Contracts/ErrorCodes.cs` as `static class`
constants with `[Description]` titles), exported into the OpenAPI document as the `x-error-codes` extension and
generated into `web/shared/src/api/errorCodes.ts` as a string-literal union. Adding a code = adding a constant;
the generator and `OpenApiTests.Every_error_code_has_title_and_status` keep both sides honest.

| Area | Code | HTTP | When |
|---|---|---|---|
| `validation` | `validation.failed` | 400 | body/query fails validation (`errors` present) |
| `validation` | `validation.unsupported_media_type` | 415 | wrong `Content-Type` |
| `validation` | `validation.payload_too_large` | 413 | body or upload exceeds the limit |
| `auth` | `auth.required` | 401 | no/invalid/expired token (`WWW-Authenticate` set) |
| `auth` | `auth.forbidden` | 403 | authenticated, not allowed (role, ownership) |
| `auth` | `auth.sso_provider_disabled` | 400 | provider not enabled |
| `auth` | `auth.sso_state_invalid` | 400 | state/PKCE check failed |
| `auth` | `auth.sso_code_used` | 400 | one-time code already claimed |
| `auth` | `auth.signup_not_allowed` | 403 | e-mail domain not in policy / not invited |
| `auth` | `auth.account_disabled` | 403 | user disabled by admin |
| `concurrency` | `concurrency.conflict` | 412 / 409 | `If-Match` mismatch / state changed |
| `presentation` | `presentation.not_found` | 404 | |
| `presentation` | `presentation.invalid_script` | 400 | parser rejected the Markdown (`errors` lists the reasons) |
| `presentation` | `presentation.slide_count_mismatch` | 409 | script vs deck count differ (when enforced) |
| `deck` | `deck.not_found` | 404 | |
| `deck` | `deck.unsupported_format` | 415 | not HTML/PDF/PPTX |
| `deck` | `deck.no_driver` | 422 | no adapter matched and none configured |
| `session` | `session.slots_busy` | 429 | no live slot on the upstream (`retryAfterSeconds`) |
| `session` | `session.already_running` | 409 | caller already has a live session |
| `session` | `session.quota_exceeded` | 429 | per-user minutes exhausted (`quota`) |
| `session` | `session.ticket_invalid` | 401 | WebSocket ticket missing/expired/used |
| `upstream` | `upstream.unavailable` | 503 | all bindings failed to start (`detail` names the last reason) |
| `upstream` | `upstream.rate_limited` | 429 | provider 429 surfaced (`retryAfterSeconds`) |
| `upstream` | `upstream.rejected` | 502 | provider returned an error event (safe message only) |
| `generation` | `generation.quota_exceeded` | 429 | tokens budget (`quota`) |
| `generation` | `generation.job_not_found` | 404 | |
| `generation` | `generation.job_failed` | 200 on the job resource | job `status: "failed"`, `error.code` inside the job |
| `generation` | `generation.busy` | 409 | another job in flight for this presentation |
| `provider` | `provider.not_found` / `provider.disabled` / `provider.test_failed` | 404 / 409 / 502 | admin catalogue |
| `model` | `model.not_found` / `model.retired` / `model.not_allowed` | 404 / 410 / 403 | `model.retired` includes `replacementModelId` |
| `document` | `document.not_found` / `document.not_ready` / `document.too_large` | 404 / 409 / 413 | knowledge base |
| `rate_limit` | `rate_limit.exceeded` | 429 | generic per-user/IP limit (`retryAfterSeconds`) |
| `internal` | `internal.error` | 500 | unexpected; `traceId` only |
| `internal` | `internal.not_implemented` | 501 | stubbed endpoint |

Codes are never removed; a retired code stays in the catalogue marked deprecated so old clients still compile.

## 7. Long-running work: jobs and SSE

- Start: `POST …/generate` → 202 `{ jobId, status, statusUrl }`. Poll: `GET /v1/jobs/{jobId}` →
  `{ id, kind, status: "queued"|"running"|"succeeded"|"failed"|"cancelled", progress: { current, total, message }, result?, error?: ProblemDetails, createdAt, startedAt?, finishedAt? }`.
  Cancel: `POST /v1/jobs/{jobId}/cancel` → 202.
- Stream: `GET /v1/jobs/{jobId}/events` (`text/event-stream`), events `progress`, `slide` (data = the slide
  produced), `done`, `error`; `id:` = monotonically increasing sequence so `Last-Event-ID` resumes; heartbeat
  comment every 15 s. Same JSON rules as §3 inside `data:`.
- Job records are durable (Postgres from plan 003); a restart yields `failed` with `error.code = "internal.error"`
  and `retryable: true`, never a vanished job.

## 8. WebSocket `/ws` (live presenter bridge)

- Auth: `POST /v1/sessions/ticket` → `{ ticket, expiresInSeconds: 30 }`; the client's **first** text frame is
  `{"type":"auth","ticket":"…"}`; anything else before it, or an invalid ticket, closes `4401` with reason
  `session.ticket_invalid`. **Plan 002 transition:** with the Dev scheme the `auth` frame is *optional* (the
  Node parity page never sends it; the React client sends it from day one with a dummy ticket); plan 003 makes
  it mandatory when real tickets exist.
- Text frames are JSON `{type, …}` both ways; binary frames are PCM16 mono 24 kHz, 20 ms = 960 bytes.
  Auth and text-command frames are limited to 4 KiB and binary audio frames to 4 KiB while fragments accumulate;
  an authenticated oversized frame closes 1009 (Message Too Big). The frozen command/message set is in plan 002
  §4.3; new message types are added, never renamed.
- Error frames: `{"type":"error","code":"<catalogue code>","message":"…"}` — the same `code` values as HTTP.
  Close codes: `1000` normal, `1013` busy (second client), `1011` server cannot keep up, `4401` auth,
  `4409` `session.already_running`, `4429` `session.slots_busy`.
- Ping/pong: client `{"type":"ping"}` → `{"type":"pong"}` every 15 s; server closes after 45 s of silence.

## 9. Security and headers

- `Authorization: Bearer <jwt>` for users; `X-Api-Key: <key>` for CLI/API keys (never both). Tokens are never
  in query strings or cookies for the API; the SPA keeps the access token in memory and the refresh token via
  `POST /v1/auth/refresh` (httpOnly cookie, `SameSite=Strict`).
- Responses: `traceparent`, `X-Request-Id` (echoed if well-formed, otherwise generated), and `Cache-Control:
  no-store` on everything under `/v1` except explicitly cacheable static assets. When rate limiting is enabled,
  successful rate-limited routes emit `RateLimit-Limit` only. A 429 additionally emits
  `RateLimit-Remaining: 0`, `RateLimit-Reset`, and an equal whole-second `Retry-After`, derived from the limiter
  lease (or the configured window when metadata is unavailable). They are absent when rate limiting is disabled.
  `ETag` appears where §4 says.
- CORS: only the SPA origins from configuration; credentialed CORS is restricted to the cookie-authenticated
  refresh and logout endpoints.
- Uploads: multipart, field name `file`, size limit per route (decks 100 MB, documents 50 MB), MIME sniffed
  server-side, filename sanitised, stored under an opaque key — the original name is metadata only.
- Never log request bodies of uploads, scripts, prompts, transcripts or keys; log ids and sizes.

## 10. Removed parity endpoints (history)

Plan 002 kept three Node-parity endpoints frozen under `/api` so the old page could be the .NET API's first
client. **Plan 004 deleted them.** Their replacements follow §2–§9: they are owner-scoped, use the list envelope,
and return Problem Details errors.

| Removed | Replaced by |
|---|---|
| `GET /api/presentations` | `GET /v1/presentations` (paged `{items,page,pageSize,total}`) |
| `GET /api/presentations/{id}` | `GET /v1/presentations/{id}` (404 `presentation.not_found` for a missing id or another user's id) |
| `GET /api/config` | `GET /v1/config` |

`GET /decks/**` (static deck assets, anonymous, 404 as plain text) was not part of the trio. Plan 004 left it
unchanged.

## 11. Decisions and rationale

| Decision | Chosen | Alternatives rejected |
|---|---|---|
| Error format | RFC 9457 Problem Details + `code` | InkSpoke `{error, message}` (inconsistent in practice); `{success,data,error}` envelope (non-standard, hides status) |
| Success/list | bare resource; `{items,page,pageSize,total}` | cursor pagination (diverges from InkSpoke admin pages to be copied); `{data,meta}` envelope |
| Codes | `area.reason` snake_case, generated TS union | flat strings; numeric codes |
| Parity trio | frozen as-is until 004, then deleted (§10) | migrate now (breaks AC4); serve both (extra surface) |

## 12. Code conventions (the parts that touch the contract)

- **C#:** DTOs are `sealed record`s in `PresenterAi.Contracts`, one file per resource, suffix `Request` /
  `Response` only when the shape differs from the resource; validation with `FluentValidation` or DataAnnotations
  surfaced as `validation.failed`; endpoints are thin (bind → handler → map result); handlers in
  `PresenterAi.Application` return results, never `IResult`. `TreatWarningsAsErrors`; nullable enabled.
- **TypeScript:** never hand-write API types — import from `@presenter/shared/api`; every call goes through the
  generated client; errors are narrowed with `isProblem(err) && err.code === "session.slots_busy"`; UI copy for
  each code lives in one map (`errorMessages.ts`) so `detail` is a fallback, not the primary text.
- **Tests:** `OpenApiTests.Document_lists_all_mapped_endpoints`, `OpenApiTests.Every_error_code_has_title_and_status`,
  golden JSON for §10, `ProblemDetailsTests.Unhandled_exception_is_500_with_traceId_and_no_message`,
  CI step `openapi-drift` (regenerate the client and fail on diff).
- **Changing this document** is a plan-level decision: note it in the relevant plan's approval log, bump the
  section, and regenerate the client in the same PR.
