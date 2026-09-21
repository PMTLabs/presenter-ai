#!/usr/bin/env bash
# secrets-guard.sh — fail when a secret-bearing file or a real credential value is tracked by git.
#
# Checks (plan 002 T1 / AC1):
#   1. No tracked path matches `.env`, `.env.*` (except `.env.example`), `appsettings.Local.json`,
#      `*.key`, `*.pem`.
#   2. No tracked text file assigns a real-looking value to a credential setting.
#
# Class boundary of check 2 (review round 1, F1 — widened from the literal spellings
# `UPSTREAM_KEY=` / `"Key": "`):
#   - the setting NAME is any token ending in `key`, `secret`, `token` or `password`, case-insensitive,
#     optionally quoted (JSON), e.g. `UPSTREAM_KEY`, `"Key"`, `"key"`, `Upstream__Key`, `--Upstream:Key`,
#     `X-Api-Key`, `"ClientSecret"`;
#   - the SEPARATOR is `=` or `:` with any whitespace on either side (`"Key" : "…"` counts);
#   - the VALUE is a quoted literal (`"…"` or `'…'`) anywhere, or an unquoted token when the name is
#     env-style (UPPER_SNAKE, or containing `__`, `:` or `-`); an unquoted value after a code identifier
#     (`const key = env.UPSTREAM_KEY.trim()`) is a reference, not a literal, and is not scanned; a value
#     that is a markdown code span (starts with a backtick) is prose about the setting and is skipped;
#   - a value looks real when it is not a placeholder and is >= 16 characters, or starts with `sk-`
#     and is >= 12 characters. Placeholders start with `your-`, `<`, `${`, `$(`, `{{`, `dev-`/`dev_`,
#     `example`, `dummy`, `xxx`, `***`, `test`/`test-`, `fake`, `sample`, or contain `changeme`/`change_me`,
#     `placeholder`, `redacted`, `not-a-real`;
#   - a bare code identifier name (`ItemKey = "PresenterAi.ProblemTrace"`, `var apiKey = "…"`) is a
#     program constant, not configuration: its quoted value is reported only with the `sk-` prefix.
#   Not covered (outside this guard's class): values split across lines, encoded blobs, secrets stored
#   under names that end in another word, and the guard's own fixture file
#   `scripts/secrets-guard.selftest.sh` (excluded from the scan; it holds the must-fail examples that
#   pin every bullet above).
#
# Usage: scripts/secrets-guard.sh   (run from anywhere inside the repository; exit 0 = clean)
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

status=0

# --- 1. forbidden tracked paths ---------------------------------------------------------------
forbidden='(^|/)\.env$|(^|/)\.env\.[^/]+$|(^|/)appsettings\.[Ll]ocal\.json$|\.key$|\.pem$'
while IFS= read -r path; do
  [[ "$path" =~ (^|/)\.env\.example$ ]] && continue
  echo "secrets-guard: forbidden tracked file: $path" >&2
  status=1
done < <(git ls-files | grep -E "$forbidden" || true)

# --- 2. real-looking credential values in tracked text ----------------------------------------
# `git grep -o` prints only the matching fragment (name, separator, value), so large lines such as
# base64 image data in decks never reach the bash regexes below.
name='"?[A-Za-z0-9_.:-]*(key|secret|token|password)"?'
sep='[[:space:]]*[=:][[:space:]]*'
value="(\"[^\"]*\"|'[^']*'|[^\"'[:space:]#,;)]*)"
pattern="(^|[^A-Za-z0-9_])${name}${sep}${value}"
placeholder='^(your[-_]|<|\$\{|\$\(|\{\{|dev[-_]|example|dummy|xxx|\*+$|test$|test[-_]|fake|sample)|changeme|change[-_]me|placeholder|redacted|not-a-real'

while IFS=: read -r file line fragment; do
  # `read` hands the remainder of the line (which may itself contain `:`) to `fragment`.
  [[ -z "${fragment:-}" ]] && continue
  if ! [[ "$fragment" =~ ^[^A-Za-z0-9_]?\"?([A-Za-z0-9_.:-]*)\"?[[:space:]]*[=:][[:space:]]*(.*)$ ]]; then
    continue
  fi
  setting="${BASH_REMATCH[1]}"
  raw="${BASH_REMATCH[2]}"
  quoted_name=0
  [[ "$fragment" =~ ^[^A-Za-z0-9_]?\" ]] && quoted_name=1
  env_style=0
  [[ "$setting" =~ ^[A-Z0-9_]+$ || "$setting" == *__* || "$setting" == *:* || "$setting" == *-* ]] && env_style=1
  if [[ "$raw" == \"*\" || "$raw" == \'*\' ]]; then
    val="${raw:1:${#raw}-2}"
    # A bare code identifier holding a string constant is only suspicious with a credential prefix.
    if [[ $quoted_name -eq 0 && $env_style -eq 0 && "$val" != sk-* ]]; then continue; fi
  elif [[ $env_style -eq 1 ]]; then
    val="$raw"
  else
    continue
  fi
  [[ -z "$val" ]] && continue
  [[ "$val" == \`* ]] && continue
  shopt -s nocasematch
  if [[ "$val" =~ $placeholder ]]; then shopt -u nocasematch; continue; fi
  shopt -u nocasematch
  if [[ ( "$val" == sk-* && ${#val} -ge 12 ) || ${#val} -ge 16 ]]; then
    echo "secrets-guard: real-looking credential value for '$setting' in $file:$line (value not shown)" >&2
    status=1
  fi
done < <(git grep -n -o -i -E "$pattern" -- ':!*.lock' ':!package-lock.json' ':!scripts/secrets-guard.selftest.sh' || true)

if [[ $status -eq 0 ]]; then
  echo "secrets-guard: clean"
fi
exit $status
