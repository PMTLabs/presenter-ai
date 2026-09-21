# 002 — Work log: phase 0a .NET core port, API bridge, React web app (plan 002)

Plan: `docs/plan/002-phase0-dotnet-core-port-api-web.md` (approved 2026-09-21). Conventions:
`docs/reference/001-api-and-code-conventions.md`. Implementation mode: coding delegated to external `pi`
agents (plan §8); Claude orchestrates and does T1, T11, T15, T16.

## 2026-09-21

### T1 — Git init, ignore rules, initial commit, push — done

- Stopped the stray Node presenter server that still held port 47913 (pid 103372) and removed its pid file.
- `git init -b master`; `.gitignore` (`.env`/`.env.*` except `.env.example`, `appsettings.Local.json`, `data/`,
  build/IDE/node output, `*.pid`, `*.log`, `.claude/`; `!web/bun.lock` re-included for the future workspace),
  `.gitattributes` (LF in repo, `*.sh` LF, `*.ps1|cmd|bat` CRLF, binaries), `.editorconfig`,
  `scripts/secrets-guard.sh` (fails on tracked `.env`/`.env.*`/`appsettings.Local.json`/`*.key`/`*.pem` and on
  real-looking `UPSTREAM_KEY=` / `FALLBACK_OPENAI_KEY=` / `"Key": "…"` values; placeholders allowed).
- Verified: guard exits 0 on the staged tree; on a scratch copy with `.env` force-added it exits 1 reporting
  both the forbidden path and two real-looking values (values not printed). `git ls-files | grep '^\.env'` →
  only `.env.example`.
- Commit `326cab7 chore: initial commit of the Node MVP and docs` pushed to `origin/master`
  (`https://github.com/PMTLabs/presenter-ai.git`, default branch `master`); `develop` created and pushed;
  working branch `feature/002-dotnet-core-port`.
- Note: `decks/ricoh/index.html` (535 KB copy of the Ricoh delivery-overview deck) is tracked because AC4/AC5
  present its slides; the repository is private.

### T2 — Solution scaffold — in progress (pi gpt-5.6-terra:medium, terminal `tm-21a50155a`)
