# 010 — Plan 008 external plan review, round 1

**Date:** 2026-09-23. **Reviewer:** `pi` `openai-codex/gpt-5.6-sol:medium`, read-only, request `p008-plan-rv-01`.
**Scope:** `docs/plan/008-mcp-external-tools.md` (draft) against `develop` @ `0a30aa3`, plan 007 (approved, not built),
and research 005/007. **Verdict given:** not ready (8 blockers).

Every claim was checked before it was accepted:
- the take-over rule (`PresenterBridge.cs:86,106-107`);
- the anonymous SSO token endpoint (`AuthEndpoints.cs:83-111`);
- the MCP specification 2026-07-28 (Context7): dynamic client registration is deprecated in favour of client ID
  metadata documents, and the preferred order is pre-registered, then metadata document, then dynamic registration,
  then asking the user.

All 15 findings were accepted and folded into plan revision 1. The raw report stays outside the repository.

The reviewer confirmed the rest of §2 against the code. It marked the MCP SDK 2.2.0 API surface as unverified, since
the SDK is not in the repository. Task 4 compiles against it.

## Findings and outcome

| # | Class | Severity | Finding | Outcome |
|---|---|---|---|---|
| D-001 | D | blocker | Auto-redirects could forward a fixed header or bearer token, or replay a registration or token body, to another public origin | **Folded:** auto-redirect off on every MCP/OAuth client; MCP, registration and token requests never follow a 3xx; metadata GETs follow ≤ 3 re-checked hops; tests with a second origin that must receive nothing |
| D-002 | D | blocker | "A 401 means nothing ran" is not guaranteed; retrying could repeat a write | **Folded:** proactive refresh; retry after 401/404 only for tools that need no confirmation; a confirm-needed call returns `auth` and needs a new "yes"; counter test proves one effect |
| D-003 | D | blocker | Dynamic registration only narrowed the confirmed "MCP OAuth"; registration response not validated | **Folded:** spec 2026-07-28 order: pre-registered (pasted client ID) → client ID metadata document (served by the API) → dynamic registration → ask for a client ID; response validation and token auth methods |
| D-004 | D | blocker | A losing refresh on another instance could mark a healthy connection for reconnect | **Folded:** Redis refresh lease; one compare-and-swap for success and failure; re-read before `needs_reconnect`; two-instance test |
| D-005 | D | blocker | The Start hard cap could orphan a late loader and its clients | **Folded:** linked cancellation at the cap, the loader always observed, a late set disposed once, partial clients disposed in `finally`; tests |
| D-006 | D | blocker | An approved run's result could be announced after navigation | **Folded:** captures 007's run generation; any navigation, restart, End, take-over or session change → not announced; one test per path |
| D-007 | D | blocker | Secret redaction asserted but not designed end to end | **Folded:** a sanitising boundary to stable codes; SDK gets `NullLoggerFactory`; `RemoveAllLoggers()` on the named clients; `SecretHygieneTests` with per-field sentinels across every flow and output surface |
| C-001 | C | blocker | "Take-over by a second user" contradicts the same-user-only bridge rule | **Folded:** same-user take-over test and a different-user `busy` test with no loader call |
| C-002 | C | nit | SSO was cited as precedent for binding OAuth completion to a signed-in user, but its token endpoint is anonymous | **Folded:** §2 and §3.6 now name the owner check as new |
| D-008 | D | should-fix | Only the well-known NAT64 prefix was covered | **Folded:** the guard also resolves and checks the `A` records, which catches DNS64 under any prefix; custom-prefix tests |
| B-001 | B | should-fix | No oracle proves the connected address is the validated one | **Folded:** `ISocketConnector` seam asserts the exact address, port and token; mutation "connect by host name" |
| B-002 | B | should-fix | OAuth oracle too loose (verifier, redirect URI, registration) | **Folded:** strict fake authorization server; mutations for an unrelated verifier, altered redirect URI and incompatible registration |
| B-003 | B | should-fix | One secret and one token scanned; other credential fields could leak | **Folded:** per-field sentinels and property allowlists for every response type |
| B-004 | B | should-fix | The owner hand-off test could bypass the bridge | **Folded:** bridge-level test with owner-like fields in the `start` frame; mutation "owner from the frame" |
| D-009 | D | should-fix | Confirmation outputs were not routed through the exactly-once tracker | **Folded:** every branch completes through the tracker's single completion API; duplicate and interleaving tests per branch |

## topicsWithNoFindings (reviewer)

- current state, apart from the SSO precedent;
- the credential cipher and associated data;
- owner-scoped repository queries;
- the confirmation gate for direct calls versus `call_tool`;
- the model cannot approve;
- the carry-on suspension and subject phases;
- non-blocking invocation on the presenter loop;
- no external tools in the CLI;
- acceptance-criteria mapping;
- a verify step for every task;
- web search in managed mode only;
- response size caps.
