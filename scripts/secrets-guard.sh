#!/usr/bin/env bash
# secrets-guard.sh — fail when a secret-bearing file or a real key value is tracked by git.
#
# Checks (plan 002 T1 / AC1):
#   1. No tracked path matches `.env`, `.env.*` (except `.env.example`), `appsettings.Local.json`,
#      `*.key`, `*.pem`.
#   2. No tracked text file assigns a real-looking value to UPSTREAM_KEY / FALLBACK_OPENAI_KEY /
#      "Key": "...". Placeholders (`your-api-key`, `<key>`, `${VAR}`, `changeme`, short test
#      tokens) are allowed; anything that looks like a credential (>= 16 chars and not a
#      placeholder, or starting with `sk-`) fails.
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

# --- 2. real-looking key values in tracked text -----------------------------------------------
placeholder='^(your[-_]|<|\$\{|\$\(|\{\{|changeme|change-me|placeholder|redacted|example|dummy|xxx|\*+$|test$|fake|sample)'
while IFS=: read -r file line match; do
  # match is the whole line; pull the value after `=` or after `"Key": "`.
  value=""
  if [[ "$match" =~ (UPSTREAM_KEY|FALLBACK_OPENAI_KEY)=[[:space:]]*\"?([^\"[:space:]#]*) ]]; then
    value="${BASH_REMATCH[2]}"
  elif [[ "$match" =~ \"Key\":[[:space:]]*\"([^\"]*)\" ]]; then
    value="${BASH_REMATCH[1]}"
  fi
  [[ -z "$value" ]] && continue
  shopt -s nocasematch
  if [[ "$value" =~ $placeholder ]]; then shopt -u nocasematch; continue; fi
  shopt -u nocasematch
  if [[ "$value" == sk-* || ${#value} -ge 16 ]]; then
    echo "secrets-guard: real-looking key value in $file:$line (value not shown)" >&2
    status=1
  fi
done < <(git grep -n -E '(UPSTREAM_KEY|FALLBACK_OPENAI_KEY)=|"Key": *"' -- ':!*.lock' ':!package-lock.json' || true)

if [[ $status -eq 0 ]]; then
  echo "secrets-guard: clean"
fi
exit $status
