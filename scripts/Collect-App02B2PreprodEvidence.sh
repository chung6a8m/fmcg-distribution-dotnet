#!/usr/bin/env bash
# APP-02B.2 operator-only PREPROD probe, never a GitHub-hosted CI job.
# Collects mechanical negatives only. Independent network/Vault audit still required.
set -euo pipefail
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$repo/deploy/preprod"
[[ -f .env ]] || { echo "PREPROD_DENY host .env missing"; exit 2; }
# Source only the repository-owned loader, never execute host .env as shell code.
source "$repo/scripts/Load-App02B2Env.sh" .env
: "$PREPROD_FQDN" "$VAULT_API_TOKEN_FILE" "$VAULT_WORKER_TOKEN_FILE"
[[ "$PREPROD_FQDN" != *.invalid && "$PREPROD_FQDN" != localhost ]] || {
  echo "PREPROD_DENY placeholder DNS"; exit 2; }
repo="$(git rev-parse --show-toplevel)"
head="$(git -C "$repo" rev-parse HEAD)"
test_run_id="app02b2-preprod-$(date -u +%Y%m%dT%H%M%SZ)-$(openssl rand -hex 4)"
out_dir="$APP02B2_EVIDENCE_DIR"
[[ -n "$out_dir" && "$out_dir" != "$repo"* ]] || {
  echo "PREPROD_DENY evidence must be outside repo"; exit 2; }
mkdir -p "$out_dir"; chmod 0700 "$out_dir"
docker compose config --quiet
python3 "$repo/tools/verify_preprod_runtime.py"
for service in api worker edge; do
  docker compose ps --status running --services | grep -qx "$service" || {
    echo "PREPROD_FAIL $service not running"; exit 1; }
done
ready="$(curl --fail-with-body --silent --show-error --max-time 10 \
  "https://$PREPROD_FQDN/health/ready")"
printf '%s' "$ready" | python3 -c '
import json,sys
assert json.load(sys.stdin)=={"state":"foundation_ready","admission":"NO_GO"}
'
for path in /api/synthetic/unknown /api/diagnostics/tenant /api/admin/unknown /api/business/unknown; do
  status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 "https://$PREPROD_FQDN$path")"
  [[ "$status" == 404 ]] || { echo "PREPROD_FAIL route $path status=$status"; exit 1; }
done
spoofed="$(curl -fsS --max-time 10 -H 'X-Forwarded-Proto: http' \
  -H 'X-Forwarded-For: 127.0.0.1' "https://$PREPROD_FQDN/health/ready")"
[[ "$ready" == "$spoofed" ]] || { echo "PREPROD_FAIL proxy header override"; exit 1; }

deny_case() {
  local label="$1" reason="$2"; shift 2
  local output status
  set +e
  output="$(timeout 30s "$@" 2>&1)"
  status=$?
  set -e
  [[ "$status" -ne 0 && "$status" -ne 124 ]] || {
    echo "PREPROD_FAIL $label returned success or timeout"; exit 1; }
  printf '%s' "$output" | grep -Fq "RUNTIME_START_DENIED code=$reason" || {
    echo "PREPROD_FAIL $label no static denial"; exit 1; }
  # Never store raw subprocess logs, token strings or environment dumps.
  echo "PREPROD_NEGATIVE_PASS $label"
}
deny_case synthetic_auth SYNTHETIC_OR_DIRECT_DB_FORBIDDEN docker compose run --rm --no-deps -e SyntheticAuth__Enabled=true api
deny_case synthetic_worker SYNTHETIC_OR_DIRECT_DB_FORBIDDEN docker compose run --rm --no-deps -e App02Worker__Enabled=true worker
deny_case dev_key SYNTHETIC_OR_DIRECT_DB_FORBIDDEN docker compose run --rm --no-deps -e SyntheticAuth__SigningKey=DEV_KEY_FORBIDDEN api
deny_case mode_mismatch MODE_NOT_AUTHORIZED docker compose run --rm --no-deps -e Runtime__Mode=Production api
deny_case wrong_vault_role VAULT_ACCESS_DENIED env "VAULT_API_TOKEN_FILE=$VAULT_WORKER_TOKEN_FILE" \
  docker compose run --rm --no-deps api

peer_code="$(docker run --rm --network fmcg-preprod-b2_private \
  curlimages/curl:8.12.1 -sS -o /dev/null -w '%{http_code}' \
  -H 'X-Forwarded-Proto: https' -H "Host: $PREPROD_FQDN" \
  'http://api:8080/health/live')"
[[ "$peer_code" == 403 ]] || { echo "PREPROD_FAIL direct API forgery"; exit 1; }

api_image="$(docker compose images -q api)"
worker_image="$(docker compose images -q worker)"
[[ -n "$api_image" && -n "$worker_image" ]] || {
  echo "PREPROD_FAIL missing deployed image ids"; exit 1; }
export B2_RUN_ID="$test_run_id" B2_SHA="$head" B2_API="$api_image" B2_WORKER="$worker_image"
export B2_MANIFEST="$out_dir/$test_run_id.json"
python3 - <<'PY'
import datetime,json,os,pathlib,socket
data = {
 "TestRunId":os.environ["B2_RUN_ID"],"SourceHeadSha":os.environ["B2_SHA"],
 "UtcCollected":datetime.datetime.now(datetime.timezone.utc).isoformat(),
 "HostId":socket.gethostname(),"ApiImageId":os.environ["B2_API"],
 "WorkerImageId":os.environ["B2_WORKER"],
 "RUNTIME01_Mechanical":"PASS","RUNTIME02_Mechanical":"PASS",
 "ExternalIndependentPublicScan":"NOT_ATTACHED",
 "Vault403AuditAndRevocation":"NOT_ATTACHED",
 "IndependentReviewer":"UNSET",
 "Outcome":"PREPROD_RUNTIME_NOT_VERIFIED"
}
p=pathlib.Path(os.environ["B2_MANIFEST"])
p.write_text(json.dumps(data,indent=2)+"\n");p.chmod(0o600)
PY
sha256sum "$B2_MANIFEST" > "$B2_MANIFEST.sha256"
chmod 0600 "$B2_MANIFEST.sha256"
echo "PREPROD mechanical probes passed; external scan/Vault audit/reviewer still REQUIRED"
echo "Private redacted manifest: $B2_MANIFEST (NOT final acceptance)"
