#!/usr/bin/env bash
# secrets-guard.selftest.sh — pins the class boundary documented at the top of secrets-guard.sh.
#
# Builds two throwaway git repositories: one where every tracked file must be reported (one finding
# per file), one where nothing may be reported. Exit 0 = the guard sees exactly what it claims to.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${SECRETS_GUARD:-$here/secrets-guard.sh}"   # override to mutation-test the guard
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

make_repo() {
  local dir="$1"
  mkdir -p "$dir"
  git -C "$dir" init -q
  git -C "$dir" config user.email selftest@presenter-ai.local
  git -C "$dir" config user.name selftest
  git -C "$dir" config core.autocrlf false
}

# --- must be reported: one file per bullet of the class -------------------------------------
bad="$work/bad"
make_repo "$bad"
mkdir -p "$bad/cfg" "$bad/docs" "$bad/src"
printf '{ "Upstream": { "Key" : "sk-abcdefghijklmnopqrstuvwxyz0123" } }\n' > "$bad/cfg/json-space-before-colon.json"
printf '{ "upstream": { "key": "abcdefghijklmnopqrstuvwxyz" } }\n'          > "$bad/cfg/json-lowercase.json"
printf 'UPSTREAM_KEY=abcdefghijklmnopqrstuvwxyz\n'                           > "$bad/cfg/env-style.txt"
printf 'services:\n  api:\n    environment:\n      Upstream__Key: abcdefghijklmnop0123\n' > "$bad/cfg/compose.yml"
printf 'dotnet run -- --Upstream:Key=sk-abcdefghijklmnop\n'                  > "$bad/docs/cli.md"
printf "FALLBACK_OPENAI_KEY = 'sk-0123456789abcdef'\n"                      > "$bad/cfg/spaces-single-quotes.txt"
printf '{ "Google": { "ClientSecret": "abcdefghijklmnopqrstuvwxyz" } }\n'   > "$bad/cfg/secret-suffix.json"
printf 'curl -H "X-Api-Key: abcdefghijklmnopqrstuvwxyz"\n'                   > "$bad/docs/header-style.md"
printf 'var apiKey = "sk-0123456789abcdef";
'                                > "$bad/src/code-sk-prefix.cs"
printf '{ "Upstream": { "Key": "" } }\n'                                     > "$bad/cfg/appsettings.Local.json"
printf 'UPSTREAM_KEY=\n'                                                     > "$bad/.env"
printf 'x\n'                                                                 > "$bad/cfg/server.pem"
expected_bad=$(find "$bad" -type f -not -path '*/.git/*' | wc -l)
git -C "$bad" add -A
findings=$(cd "$bad" && bash "$guard" 2>&1 >/dev/null || true)
count=$(printf '%s\n' "$findings" | grep -c '^secrets-guard: ' || true)
if [[ "$count" -ne "$expected_bad" ]]; then
  echo "selftest: expected $expected_bad findings in the bad repo, got $count:" >&2
  printf '%s\n' "$findings" >&2
  exit 1
fi
for f in json-space-before-colon json-lowercase env-style compose.yml cli.md spaces-single-quotes secret-suffix header-style code-sk-prefix appsettings.Local.json .env server.pem; do
  if ! printf '%s\n' "$findings" | grep -q -- "$f"; then
    echo "selftest: no finding for $f" >&2
    printf '%s\n' "$findings" >&2
    exit 1
  fi
done

# --- must be clean: placeholders, short values, references, prose --------------------------
good="$work/good"
make_repo "$good"
mkdir -p "$good/cfg" "$good/docs" "$good/src"
printf '{ "Upstream": { "Key": "", "Fallback": { "Key": "" } } }\n'           > "$good/cfg/appsettings.json"
printf '{ "Upstream": { "Key" : "your-api-key" } }\n'                          > "$good/cfg/placeholder.json"
printf '{ "Upstream": { "Key": "test" }, "Google": { "ClientSecret": "test-client-secret-value" } }\n' > "$good/cfg/test-values.json"
printf 'UPSTREAM_KEY=your-api-key\nFALLBACK_OPENAI_KEY=\n'                    > "$good/.env.example"
printf 'services:\n  api:\n    environment:\n      Upstream__Key: "${UPSTREAM_KEY:-}"\n      POSTGRES_PASSWORD: dev_password_change_me\n' > "$good/cfg/compose.yml"
printf "test('x', () => loadConfig({ UPSTREAM_KEY: 'k', FALLBACK_OPENAI_KEY: 'sk-fb' }));\n" > "$good/src/config.test.js"
printf 'const key = env.UPSTREAM_KEY.trim();\nconst fallbackKey = options.Key?.Trim();\n' > "$good/src/config.js"
printf 'private const string ItemKey = "PresenterAi.ProblemTrace";
const cacheKey = "presentations:list:v1:all";
' > "$good/src/constants.cs"
printf -- '- uses: actions/cache@v4\n  with:\n    key: ${{ runner.os }}-nuget-abcdefghijklmnop\n' > "$good/cfg/ci.yml"
printf 'Send `X-Api-Key: <key>`; the tracked file is non-secret: `Content:WebRoot=../../web/app/dist`.\n' > "$good/docs/prose.md"
printf 'export UPSTREAM_KEY=your-key\ndotnet user-secrets set Upstream:Key "<paste-your-key>"\n' > "$good/docs/readme.md"
git -C "$good" add -A
if ! output=$(cd "$good" && bash "$guard" 2>&1); then
  echo "selftest: the clean repo was reported:" >&2
  printf '%s\n' "$output" >&2
  exit 1
fi

echo "secrets-guard selftest: ok ($expected_bad reported, clean repo clean)"
