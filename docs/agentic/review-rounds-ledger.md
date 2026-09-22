# Review rounds ledger

One row per external review round (agent-research skill). A = class fix missed sites · B = oracle too narrow · C = claim unsupported by code · D = genuine defect/gap.

| Date | Branch / ticket | Round | A | B | C | D | Note |
|---|---|---|---|---|---|---|---|
| 2026-09-21 | plan 002 (phase 0a port) — plan review, pre-implementation | 1 | 1 | 1 | 4 | 3 | protocol field name, HTTP shapes and parallel-path claims were unsupported by code; pump oracle too narrow; concurrency model unspecified — all folded in before approval |
| 2026-09-21 | feature/002-dotnet-core-port — impl review T2–T6/T9 (`326cab7..2f22c58`) | 1 | 0 | 7 | 0 | 2 | secrets-guard class too narrow (whitespace/case bypass) and traceparent not guaranteed on one producer; seven ported tests weaker than their Node/spec oracles — all folded in, see docs/review/002 |
| 2026-09-21 | feature/002-dotnet-core-port — impl review T7/T8/T10/T12 + review-1 fixes (`2f22c58..906af83`) | 2 | 0 | 2 | 0 | 3 | round-1 classes held at every site; new D: no live session is ever disposed (class: `ILiveSession` not disposable), fire-and-forget `EndAsync` unobserved, guard boundary vs `\u`-escaped JSON names (documented as out of class); B: stop-after-slide oracle (Node parity — claim corrected in place), backpressure test drove the seam not the channel — see docs/review/003 |
