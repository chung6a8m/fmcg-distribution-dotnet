#!/usr/bin/env bash
# GitHub-hosted CI container-level RUNTIME-01 negatives. Never actual PREPROD proof.
set -euo pipefail
: "${PREPROD_FQDN:=preprod.example.invalid}" "${PREPROD_BIND_IP:=127.0.0.1}"
: "${VAULT_ADDR:=https://vault.private.example.invalid:8200}"
: "${TLS_CERT_FILE:=/tmp/ci-unmounted.crt}" "${TLS_KEY_FILE:=/tmp/ci-unmounted.key}"
: "${VAULT_API_TOKEN_FILE:=/tmp/ci-unmounted-api.token}"
: "${VAULT_WORKER_TOKEN_FILE:=/tmp/ci-unmounted-worker.token}"
api="$(docker image inspect fmcg-preprod-b2-api --format '{{.Id}}' 2>/dev/null || true)"
worker="$(docker image inspect fmcg-preprod-b2-worker --format '{{.Id}}' 2>/dev/null || true)"
[[ -n "$api" && -n "$worker" ]] || { echo "RUNTIME_CI_FAIL missing image"; exit 1; }
deny() {
  local role="$1" assignment="$2" expected="$3" image="$api"
  [[ "$role" == worker ]] && image="$worker"
  local logs status
  set +e
  logs="$(timeout 15s docker run --rm --network none \
    -e DOTNET_ENVIRONMENT=Preprod \
    -e ASPNETCORE_ENVIRONMENT=Preprod \
    -e Runtime__Environment=Preprod \
    -e Runtime__Role="$role" \
    -e Runtime__Mode=FOUNDATION_ONLY \
    -e Runtime__Admission=NO_GO \
    -e Runtime__TrustedProxyIp=172.30.50.10 \
    -e Runtime__PublicHost=preprod.example.invalid \
    -e Vault__Address=https://vault.private.example.invalid:8200 \
    -e Vault__ProbePath="preprod/data/runtime/$role" \
    -e Vault__TokenFile="/run/secrets/vault_${role}_token" \
    -e "$assignment" "$image" 2>&1)"
  status=$?
  set -e
  [[ "$status" -ne 0 && "$status" -ne 124 ]] || {
    echo "RUNTIME_CI_FAIL $role $expected unexpectedly started or timed out"; exit 1; }
  printf '%s' "$logs" | grep -Fq "RUNTIME_START_DENIED code=$expected" || {
    echo "RUNTIME_CI_FAIL $role expected static code $expected missing"; exit 1; }
  echo "RUNTIME_CI_NEGATIVE_PASS role=$role code=$expected"
}
deny api SyntheticAuth__Enabled=true SYNTHETIC_OR_DIRECT_DB_FORBIDDEN
deny worker App02Worker__Enabled=true SYNTHETIC_OR_DIRECT_DB_FORBIDDEN
deny api SyntheticAuth__SigningKey=DEV-KEY-DENY SYNTHETIC_OR_DIRECT_DB_FORBIDDEN
deny api Runtime__Mode=Production MODE_NOT_AUTHORIZED
deny api Vault__Address=http://vault.private:8200 VAULT_TLS_REQUIRED
deny api ASPNETCORE_ENVIRONMENT=Development ENVIRONMENT_MISMATCH
echo "RUNTIME_CI_BLACKBOX_PASS: 6/6 process-level startup denials (CI only)"
