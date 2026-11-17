#!/usr/bin/env bash
set -euo pipefail
repo="$(git rev-parse --show-toplevel)"
loader="$repo/scripts/Load-App02B2Env.sh"
folder="$(mktemp -d)"
trap 'rm -rf "$folder"' EXIT
bash -c 'set -eu; source "$1" "$2"; test "$PREPROD_FQDN" = preprod.example.invalid; test "$PREPROD_BIND_IP" = 127.0.0.1' \
  bash "$loader" "$repo/deploy/preprod/.env.example"
printf 'PREPROD_FQDN=$(touch %s)\n' "$folder/pwned" > "$folder/injection.env"
if bash -c 'set -e; source "$1" "$2"' bash "$loader" "$folder/injection.env" 2>/dev/null; then
  echo "RUNTIME_ENV_FAIL: shell substitution accepted" >&2; exit 1
fi
if [[ -e "$folder/pwned" ]]; then
  echo "RUNTIME_ENV_FAIL: host .env code executed" >&2; exit 1
fi
printf 'VAULT_TOKEN=should-not-be-an-environment-secret\n' > "$folder/secret.env"
if bash -c 'set -e; source "$1" "$2"' bash "$loader" "$folder/secret.env" 2>/dev/null; then
  echo "RUNTIME_ENV_FAIL: secret variable accepted" >&2; exit 1
fi
printf 'PREPROD_FQDN=preprod.example.invalid\nprintf "hijacked"\n' > "$folder/commands.env"
if bash -c 'set -e; source "$1" "$2"' bash "$loader" "$folder/commands.env" 2>/dev/null; then
  echo "RUNTIME_ENV_FAIL: command line accepted" >&2; exit 1
fi
echo "RUNTIME_ENV_PARSER_PASS: literal whitelisted values, no shell evaluation, secret/unknown key rejected"
