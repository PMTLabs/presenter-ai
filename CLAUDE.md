# CLAUDE.md

Read **`AGENTS.md`** first — it is the single source of truth for working in this repository (map, commands,
non-negotiable rules, conventions, how work is organised). Everything there applies to Claude Code as well.

Claude Code specifics:

- Orchestrate; delegate scouting and implementation to external `pi`/`codex` agents through the terminal MCP
  (`agent-research` skill), keep trivial edits direct. Never hand an agent `.env` or secret values.
- Use the `planning` skill (interview → brief → plan → approval) for new requirements and `context-handoff` for
  long sessions; the newest `docs/progress/NNN-handoff-*.md` is where to resume.
- Ask before anything outward-facing: pushing, opening or merging a PR, deploying, deleting.
