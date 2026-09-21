# 002 — Implementation review round 1: plan 002 (T2–T6, T9)

**Reviewer:** `pi --model openai-codex/gpt-5.6-terra:medium`, review-only (no edits, no tests, no server, no `.env`), 2026-09-21.
**Scope:** commits `326cab7..2f22c58` on `feature/002-dotnet-core-port` (scaffold, docker/CI, scripts, prompt, audio level, upstream options) against plan 002 §§3–7, conventions doc §§3/5/6/12 and the Node parity sources.
**Classification:** F1 D (blocker), F2 D (major), F3–F9 B (minor). No A, no C. Ledger: `docs/agentic/review-rounds-ledger.md`.

## Disposition (orchestrator)

| Finding | Class | Fix applied |
|---|---|---|
| F1 secrets-guard bypass | D | Class widened: any `*key`/`*secret`/`*token`/`*password` setting, JSON/YAML/env/CLI spelling, any whitespace, any case; boundary stated in the script header; `scripts/secrets-guard.selftest.sh` pins 11 must-fail files (incl. the reviewer's `"Key" : "…"`) and a must-pass repo; the old guard fails the self-test (4/11); CI runs the self-test before the guard. |
| F2 `traceparent` not guaranteed | D | `Api/Errors/ProblemTrace.cs` is the single trace source for both producers (`DomainExceptionHandler`, `Problems.Create`): W3C id from the request activity or a generated one, always written to the `traceparent` header. `ProblemDetailsTests` assert header = body `traceId` via the pipeline and via both producers with no activity (the reviewer's counterexample); mutation (header only when an activity exists) fails both no-activity tests. T10's hand-built `/ws` 400 was the third producer — routed through `Problems.Create` in the T10 continuation. |
| F3 fallback custom endpoint | B | Node case `config.test.js:110-124` ported with URL, order, model and headers asserted (R1FIX). |
| F4 catalogue oracle | B | Independent expected code→status map from conventions §6 asserted against `ErrorCodes.Catalogue` and the OpenAPI extension (R1FIX, after T10). |
| F5 endpoint document oracle | B | Required paths + HTTP operations (`/health` → `get`, 200 schema) asserted independently (R1FIX, after T10). |
| F6 prompt golden | B | Node-generated goldens for no-context, untitled slides and truncation-with-warning; exact strings for every builder (R1FIX). |
| F7 RMS boundary | B | Mixed-amplitude frames with RMS strictly between 119 and 120 (and just above 120) (R1FIX). |
| F8 startup exit path | B | Subprocess test: non-Testing environment, empty key → exit 1, `Missing required setting: Upstream:Key` on stderr, no stack trace (R1FIX). |
| F9 script writer | B | Record-level round trips with quotes, colons, unicode, multi-line notes, null optionals, `---` in context (R1FIX). |

---

## Summary
- Found one release-blocking secret-scanning bypass: a valid JSON key property with whitespace before its colon is not inspected by CI.
- The script, prompt, RMS, URL, and authentication implementations match the reviewed Node behavior for their exercised paths; the error catalogue is complete (including the documented 500 decision for `generation.job_failed`).
- Problem Details do not reliably emit the required `traceparent` response header.
- Several named ported tests are materially weaker than their Node/spec oracle claims; in particular fallback custom URL, OpenAPI contract, prompt snapshot branches, RMS fractional boundary, and command-line startup exit are not proved.

## Findings
### F1 — Secrets guard misses valid JSON key assignments  [D]  severity: blocker
- Where: `scripts/secrets-guard.sh:37-39`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:500-502` (T1 requires a tracked non-empty `"Key": "…"` to fail); `.github/workflows/ci.yml:12-13` runs this guard as the CI security gate.
- What: The `git grep` pattern only recognizes the literal spelling `"Key":` (no whitespace before `:`). Valid JSON such as `"Key" : "<a real key>"` is neither selected by `git grep` nor inspected, so it exits clean. Configuration binding is case-insensitive too, so lower-case `"key": "…"` is another bypass.
- Why it matters: A real upstream credential can be committed in `appsettings.json` while the required `secrets-guard` CI step reports success.
- Suggested fix: Parse tracked JSON configuration (or use a whitespace/case-tolerant detector) and scan all configuration key spellings; retain placeholder exemptions only after extracting the complete value.

### F2 — Problem Details does not guarantee `traceparent`  [D]  severity: major
- Where: `src/PresenterAi.Api/Errors/DomainExceptionHandler.cs:25-28,40-55`; `src/PresenterAi.Api/Errors/Problems.cs:16-28`; `docs/reference/001-api-and-code-conventions.md:58-65,164-165`.
- What: The exception handler writes `traceparent` only when `Activity.Current?.Id` is non-null. Its fallback `HttpContext.TraceIdentifier` is still put in the body without a corresponding header. The parallel `Problems.Create` helper writes no `traceparent` header at all.
- Why it matters: The conventions require both a W3C `traceId` in every Problem Details response and the same value in a `traceparent` response header. Clients/support cannot rely on the required correlation header, especially on paths using `Problems.Create`.
- Suggested fix: Centralize Problem Details creation, ensure a W3C activity/trace context exists, and always set `Response.Headers["traceparent"]` to its ID before writing either exception-handler or helper responses. Add assertions for header/body equality.

### F3 — Fallback-route test omits the Node custom-endpoint scenario  [B]  severity: minor
- Where: `tests/PresenterAi.Infrastructure.Tests/Live/UpstreamRoutesTests.cs:58-80`; Node oracle `test/config.test.js:110-124`.
- What: `Fallback_key_adds_second_route_after_primary` supplies only a fallback key/model and therefore checks the default OpenAI URL. A wrong route builder that always uses `https://api.openai.com` and ignores `Upstream:Fallback:Endpoint` would pass, whereas the Node oracle verifies `FALLBACK_OPENAI_ENDPOINT=https://gw.example.com` resolves to that gateway.
- Why it matters: A configured fallback gateway would silently connect to OpenAI instead of the configured provider.
- Suggested fix: Port the Node test's custom fallback endpoint case and assert the resolved URL, order, model, and headers.

### F4 — Error-catalogue oracle can approve a renamed or incomplete catalogue  [B]  severity: minor
- Where: `tests/PresenterAi.Api.Tests/OpenApiTests.cs:37-55`; `src/PresenterAi.Contracts/ErrorCodes.cs:8-92`; `docs/reference/001-api-and-code-conventions.md:72-118`.
- What: `Every_error_code_has_title_and_status` compares the OpenAPI extension to the same C# `ErrorCodes.Catalogue` that generates it, and only requires non-empty fields plus a 400–599 status. Renaming/removing `session.slots_busy` in both the constants and dictionary, or assigning `presentation.not_found` status 500, still passes.
- Why it matters: The test does not enforce the published §6 catalogue, its fixed spellings, or its specified statuses—the compatibility property it is intended to guard.
- Suggested fix: Assert an independently declared expected code/status map derived from §6 (with the documented `generation.job_failed` 500 exception), then separately assert OpenAPI equals that map.

### F5 — Endpoint-document test checks paths, not the required operations or shape  [B]  severity: minor
- Where: `tests/PresenterAi.Api.Tests/OpenApiTests.cs:13-34`; `src/PresenterAi.Api/Endpoints/HealthEndpoints.cs:7-9`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:544-549` (T2 requires `GET /health` and OpenAPI at `/openapi/v1.json`).
- What: `Document_lists_all_mapped_endpoints` only compares route-template strings from the running application's own `EndpointDataSource` with OpenAPI path keys. An implementation that changes health to `POST /health`, or documents the path with an incorrect response/body, still passes.
- Why it matters: It cannot protect the promised HTTP method and wire contract, despite its name and its role as the T2 mapping oracle.
- Suggested fix: Assert the independently required paths and HTTP operations (`/health` → `get`), then validate the expected 200 response/schema as well.

### F6 — The prompt golden snapshot covers one input only  [B]  severity: minor
- Where: `tests/PresenterAi.Application.Tests/Presenting/PromptBuilderTests.cs:94-103`; `tests/PresenterAi.Application.Tests/Golden/system-instructions.sample.txt:1-28`; Node oracle `test/prompt.test.js:16-37`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:596-598`.
- What: `System_instructions_match_node_snapshot` checks exactly the sample title/slides/context. A builder that special-cases that sample but changes truncation, an empty title/slide title, or all other system-instruction output passes; the other tests use loose substring checks rather than an oracle string.
- Why it matters: The plan calls for copied-verbatim, byte-identical prompt text. Prompt drift changes model behavior but is not caught for most presentations.
- Suggested fix: Add independent Node-produced golden cases for no context, untitled slides, and truncation (including warning text), and exact expected strings for each remaining builder.

### F7 — RMS boundary test does not test fractional RMS values  [B]  severity: minor
- Where: `tests/PresenterAi.Application.Tests/Presenting/AudioLevelTests.cs:36-40`; `src/PresenterAi.Application/Presenting/AudioLevel.cs:29-30`; Node oracle `src/server/audio-util.js:21-24`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:610-614`.
- What: `Threshold_boundary_119_vs_120` uses constant frames, whose RMS is exactly 119 or 120. `IsVoiced` implemented as `Rms(...) >= 119.5` would pass every boundary assertion here (and the supplied tone/silence checks) but incorrectly classify a frame with RMS 119.75 as voiced; Node requires `>= 120`.
- Why it matters: Quiet output close to the threshold can incorrectly hold slide advancement.
- Suggested fix: Add constructed mixed-amplitude frames whose RMS lies strictly between 119 and 120, and assert they are unvoiced; retain the exact-120 test.

### F8 — Startup test does not prove the production exit path  [B]  severity: minor
- Where: `tests/PresenterAi.Api.Tests/StartupTests.cs:16-31`; `src/PresenterAi.Api/Program.cs:44-67`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:661-665`.
- What: `Missing_upstream_key_fails_startup` only observes that a Testing `WebApplicationFactory` throws. Removing the non-Testing `OptionsValidationException` filter entirely (thereby producing an unhandled exception/stack trace instead of the promised clean stderr text and exit code 1) still passes this test.
- Why it matters: The operational contract is specifically `dotnet run` without the key → clean `Missing required setting: Upstream:Key` and exit 1; the test proves neither condition.
- Suggested fix: Launch the API in a subprocess in a non-Testing environment with an empty key and assert exit code 1, stderr contains the exact setting name, and no stack trace/secret appears.

### F9 — Script writer round trips only two fixture shapes  [B]  severity: minor
- Where: `tests/PresenterAi.Application.Tests/Scripts/ScriptWriterTests.cs:8-29`; `src/PresenterAi.Application/Scripts/ScriptWriter.cs:14-45`; `docs/plan/002-phase0-dotnet-core-port-api-web.md:586-589`.
- What: `Round_trips_ricoh` (and sample) only feeds existing fixtures. A writer that handles the two fixture metadata combinations but serializes an arbitrary title/note containing YAML-sensitive text incorrectly would pass. Neither fixture exercises quotes/newlines in title metadata or a null optional value followed by a non-empty field as an explicit writer case.
- Why it matters: `ScriptWriter.Format` is introduced as the inverse needed by later phases; malformed YAML or changed metadata will surface only when users edit/save a non-fixture script.
- Suggested fix: Add record-level round trips with quote, colon, newline, Unicode, null optionals, and multi-line notes; assert the parsed result, not merely fixture preservation.

## Parity checklist

| Node test file / oracle | C# test(s) | Verdict | Reason |
|---|---|---|---|
| `test/script-parser.test.js` | `Scripts/ScriptParserTests.cs`, `TextChunkerTests.cs` | same | All nine Node parser/chunker scenarios are represented, including Ricoh and long-word chunking. `ScriptWriterTests.cs` is additional coverage, but is weaker for arbitrary writer input (F9). |
| `test/prompt.test.js` | `Presenting/PromptBuilderTests.cs` | weaker | The seven scenarios are ported, but assertions are mostly substring-based; one exact system snapshot covers only sample input (F6). |
| `test/audio-util.test.js` | `Presenting/AudioLevelTests.cs` | weaker | Silence/tone behavior is covered and extra threshold/odd-byte/stride tests exist, but the threshold oracle misses fractional RMS values (F7). |
| `test/config.test.js` | `Live/LiveUrlResolverTests.cs`, `UpstreamAuthTests.cs`, `UpstreamRoutesTests.cs`, API `StartupTests.cs` | weaker | Resolver/auth/default/order coverage is present, but the custom fallback endpoint scenario is absent (F3) and startup test does not exercise the production exit behavior (F8). |

## Files examined
- Specifications: `docs/plan/002-phase0-dotnet-core-port-api-web.md` (§§3–7, T2/T3/T4/T5/T6/T9), `docs/reference/001-api-and-code-conventions.md` (§§3, 5, 6, 12).
- Node parity sources/tests: `src/server/{script-parser,prompt,audio-util,config}.js`, `test/{script-parser,prompt,audio-util,config}.test.js`.
- C# implementation/tests: all scoped `Application/Scripts`, `Application/Presenting`, `Infrastructure/Live`, API `Program`, `Errors`, `Endpoints`, contracts/domain errors, and their scoped test projects/fixtures/golden file.
- Delivery/configuration: `docker-compose.yml`, `src/PresenterAi.Api/Dockerfile`, `.dockerignore`, `.github/workflows/ci.yml`, `README.md`, `.gitignore`, `scripts/secrets-guard.sh`, appsettings and project files.
- History: commits `413b817`, `3c32aa6`, `01c6f55`, `c2dd9b6`, `6a452c9`, `7c676fd`, and `2f22c58` via read-only `git log`/`git show`.

## IS THIS BRANCH READY TO MERGE?
Blockers: F1 — CI can allow a committed real upstream key.

Improvements: Fix F2 and strengthen the weak port/contract oracles in F3–F9 before relying on this phase as the parity gate. No missing/renamed §6 catalogue code was found; `generation.job_failed` at 500 is treated as the documented decision.
