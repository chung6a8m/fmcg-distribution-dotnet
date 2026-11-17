#!/usr/bin/env bash
# APP-02B.2: real Docker + HTTPS Vault ACL on a DISPOSABLE GitHub-hosted Ubuntu runner.
# NEVER a persistent/private PREPROD host or an independent external security witness.
set -euo pipefail
umask 077
repo="$(git rev-parse --show-toplevel)"
cd "$repo"
ci_root="$RUNNER_TEMP/app02b2-ci-preprod"
tls="$ci_root/tls"
tokens="$ci_root/tokens"
mkdir -p "$tls" "$tokens" "$repo/TestResults"
chmod 0755 "$ci_root" "$tls" "$tokens"

export PREPROD_FQDN=preprod.example.invalid
export PREPROD_BIND_IP=127.0.0.1
export VAULT_ADDR=https://vault.private.example.invalid:8200
export TLS_CERT_FILE="$tls/edge.crt"
export TLS_KEY_FILE="$tls/edge.key"
export VAULT_API_TOKEN_FILE="$tokens/api.token"
export VAULT_WORKER_TOKEN_FILE="$tokens/worker.token"
export CI_VAULT_CERT_FILE="$tls/vault.crt"
export CI_VAULT_KEY_FILE="$tls/vault.key"
export CI_ROOT_CA_FILE="$tls/ca.crt"

dc() { docker compose -f deploy/preprod/compose.yaml -f deploy/preprod/compose.ci.yaml "$@"; }
deny() { echo "CI_PREPROD_DENIED $1"; exit 1; }
expect() {
  local actual="$1" expected="$2" case_name="$3"
  [[ "$actual" == "$expected" ]] || deny "$case_name expected=$expected actual=$actual"
  echo "CI_PREPROD_CASE_PASS $case_name expected=$expected"
}

# Ephemeral short-lived CA; never use -k, insecure certificate callbacks or
# a root token in Docker Compose environment, source, artifacts or images.
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 \
  -subj '/CN=FMCG CI disposable root' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$tls/ca.key" -out "$tls/ca.crt" >/dev/null 2>&1
issue_cert() {
  local name="$1" dns="$2" ip_extra="$3"
  openssl req -newkey rsa:2048 -nodes -sha256 \
    -subj "/CN=$dns" -keyout "$tls/$name.key" \
    -out "$tls/$name.csr" >/dev/null 2>&1
  {
    echo 'basicConstraints=CA:FALSE'
    echo 'keyUsage=critical,digitalSignature,keyEncipherment'
    echo 'extendedKeyUsage=serverAuth'
    echo "subjectAltName=DNS:$dns$ip_extra"
  } > "$tls/$name.ext"
  openssl x509 -req -in "$tls/$name.csr" -CA "$tls/ca.crt" \
    -CAkey "$tls/ca.key" -CAcreateserial -days 1 -sha256 \
    -extfile "$tls/$name.ext" -out "$tls/$name.crt" >/dev/null 2>&1
}
issue_cert edge "$PREPROD_FQDN" ""
issue_cert vault vault.private.example.invalid ",IP:127.0.0.1"
chmod 0644 "$tls/ca.crt" "$tls/edge.crt" "$tls/vault.crt"
chmod 0600 "$tls/ca.key" "$tls/edge.key"
# Vault image uses a non-root vault user (group 1000). CI-only leaf key:
sudo chgrp 1000 "$tls/vault.key"
chmod 0640 "$tls/vault.key"

# Exercise ordinary Vault server mode: actual init/unseal, KV-v2, ACL, audit.
# inmem storage and 1/1 unseal are CI-only fixtures, never a hosting pattern.
dc config --quiet
dc pull vault >/dev/null
dc up -d vault >/dev/null
init_json=""
for attempt in $(seq 1 30); do
  if init_json="$(dc exec -T vault vault operator init \
    -key-shares=1 -key-threshold=1 -format=json 2>/dev/null)"; then break; fi
  sleep 1
done
[[ -n "$init_json" ]] || deny vault_init_unavailable
root_token="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["root_token"])' <<< "$init_json")"
unseal_key="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["unseal_keys_b64"][0])' <<< "$init_json")"
unset init_json
dc exec -T vault vault operator unseal "$unseal_key" >/dev/null
unset unseal_key
admin() { dc exec -T -e "VAULT_TOKEN=$root_token" vault vault "$@"; }
admin audit enable file file_path=/vault/logs/ci-audit.jsonl >/dev/null
admin secrets enable -path=preprod kv-v2 >/dev/null
admin policy write ci-app02b2-api /vault/policies/api.hcl >/dev/null
admin policy write ci-app02b2-worker /vault/policies/worker.hcl >/dev/null
admin kv put preprod/runtime/api principal=api admission=FOUNDATION_ONLY >/dev/null
admin kv put preprod/runtime/worker principal=worker admission=FOUNDATION_ONLY >/dev/null
new_token() {
  local policy="$1" json
  json="$(admin token create -orphan -policy="$policy" -ttl=30m -format=json)"
  python3 -c 'import json,sys; print(json.load(sys.stdin)["auth"]["client_token"])' <<< "$json"
}
api_token="$(new_token ci-app02b2-api)"
worker_token="$(new_token ci-app02b2-worker)"
[[ "$api_token" != "$worker_token" ]] || deny duplicate_workload_token
printf '%s\n' "$api_token" > "$VAULT_API_TOKEN_FILE"
printf '%s\n' "$worker_token" > "$VAULT_WORKER_TOKEN_FILE"
sudo chgrp 1654 "$VAULT_API_TOKEN_FILE" "$VAULT_WORKER_TOKEN_FILE"
chmod 0640 "$VAULT_API_TOKEN_FILE" "$VAULT_WORKER_TOKEN_FILE"

# Browserless independent curl container uses a real HTTPS Vault endpoint
# and the actual mounted workload token; never echoes the token or raw JSON.
docker pull curlimages/curl:8.12.1 >/dev/null
vault_http() {
  local identity="$1" path="$2"
  docker run --rm --network fmcg-preprod-b2_outbound \
    -v "$tls/ca.crt:/run/ca.crt:ro" \
    -v "$tokens/$identity.token:/run/token:ro" \
    --entrypoint sh curlimages/curl:8.12.1 -c \
    'token=$(cat /run/token); curl --noproxy "*" -fsS --cacert /run/ca.crt \
       -H "X-Vault-Token: $token" -o /dev/null -w "%{http_code}" \
       "https://vault.private.example.invalid:8200/v1/$1" || true' \
    -- "$path"
}
# No -f for 403 checks; a 403 is an EXPECTED negative HTTP result.
vault_http_status() {
  local identity="$1" path="$2"
  docker run --rm --network fmcg-preprod-b2_outbound \
    -v "$tls/ca.crt:/run/ca.crt:ro" \
    -v "$tokens/$identity.token:/run/token:ro" \
    --entrypoint sh curlimages/curl:8.12.1 -c \
    'token=$(cat /run/token); curl --noproxy "*" -sS --cacert /run/ca.crt \
       -H "X-Vault-Token: $token" -o /dev/null -w "%{http_code}" \
       "https://vault.private.example.invalid:8200/v1/$1"' \
    -- "$path"
}
expect "$(vault_http_status api preprod/data/runtime/api)" 200 VAULT_API_ALLOW
expect "$(vault_http_status worker preprod/data/runtime/worker)" 200 VAULT_WORKER_ALLOW
expect "$(vault_http_status api preprod/data/runtime/worker)" 403 VAULT_API_PEER_DENY
expect "$(vault_http_status worker preprod/data/runtime/api)" 403 VAULT_WORKER_PEER_DENY
expect "$(vault_http_status api sys/policies/acl)" 403 VAULT_API_ADMIN_DENY

# Compose base images were previously built; overlay mounts ephemeral trust root.
dc up -d --no-build api worker edge >/dev/null
https_status() {
  local path="$1"
  curl --noproxy "*" --silent --show-error --max-time 8 \
    --cacert "$tls/ca.crt" \
    --resolve "$PREPROD_FQDN:443:127.0.0.1" \
    -o "$ci_root/http-response.json" -w '%{http_code}' \
    "https://$PREPROD_FQDN$path"
}
code=""
for attempt in $(seq 1 30); do
  code="$(https_status /health/ready 2>/dev/null || true)"
  [[ "$code" == 200 ]] && break
  sleep 2
done
expect "$code" 200 HTTPS_READY
python3 - "$ci_root/http-response.json" <<'PY'
import json,sys
assert json.load(open(sys.argv[1])) == {"state":"foundation_ready","admission":"NO_GO"}
PY
echo "CI_PREPROD_CASE_PASS BUSINESS_ADMISSION_NO_GO"
expect "$(https_status /health/live)" 200 HTTPS_LIVE
for path in /api/synthetic/app02a/events /api/diagnostics/tenant /api/admin /api/business; do
  expect "$(https_status "$path")" 404 "BLOCKED_ROUTE_$path"
done
code="$(curl --noproxy "*" -sS --cacert "$tls/ca.crt" \
  --resolve "$PREPROD_FQDN:443:127.0.0.1" \
  -H 'X-Forwarded-Proto: http' -H 'X-Forwarded-For: 127.0.0.1' \
  -o /dev/null -w '%{http_code}' "https://$PREPROD_FQDN/health/ready")"
expect "$code" 200 FORGED_HEADERS_IGNORED
code="$(docker run --rm --network fmcg-preprod-b2_private \
  curlimages/curl:8.12.1 --noproxy "*" -sS \
  -H 'X-Forwarded-Proto: https' -H "Host: $PREPROD_FQDN" \
  -o /dev/null -w '%{http_code}' http://api:8080/health/live)"
expect "$code" 403 NONPROXY_DIRECT_API_DENY
# Real TLS validation rejects a different test CA without insecure overrides.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj '/CN=Untrusted CI CA' -keyout "$tls/other-ca.key" \
  -out "$tls/other-ca.crt" >/dev/null 2>&1
if curl --noproxy "*" --silent --show-error --max-time 8 \
  --cacert "$tls/other-ca.crt" \
  --resolve "$PREPROD_FQDN:443:127.0.0.1" \
  -o /dev/null "https://$PREPROD_FQDN/health/live" 2>/dev/null; then
  deny INVALID_TLS_CA_ACCEPTED
fi
echo "CI_PREPROD_CASE_PASS INVALID_TLS_CA_DENY"

# Runtime token rotation without restarting API; existing bind inode retains content.
api_token2="$(new_token ci-app02b2-api)"
printf '%s\n' "$api_token2" > "$VAULT_API_TOKEN_FILE"
expect "$(https_status /health/ready)" 200 API_TOKEN_ROTATION_HEALTHY
admin token revoke "$api_token" >/dev/null
expect "$(https_status /health/ready)" 200 OLD_API_TOKEN_REVOKED_NEW_OK
admin token revoke "$api_token2" >/dev/null
expect "$(https_status /health/ready)" 503 REVOKED_API_HEALTH_DENY

# Disable restart to observe Worker fail-closed stop instead of restart loops.
worker_id="$(dc ps -q worker)"
[[ -n "$worker_id" ]] || deny missing_worker_container
docker update --restart=no "$worker_id" >/dev/null
admin token revoke "$worker_token" >/dev/null
stopped=false
for attempt in $(seq 1 45); do
  if [[ "$(docker inspect "$worker_id" --format '{{.State.Running}}')" == false ]]; then
    stopped=true; break
  fi
  sleep 2
done
[[ "$stopped" == true ]] || deny REVOKED_WORKER_FAILED_TO_STOP
exit_code="$(docker inspect "$worker_id" --format '{{.State.ExitCode}}')"
[[ "$exit_code" -ne 0 ]] || deny REVOKED_WORKER_EXITED_SUCCESS
echo "CI_PREPROD_CASE_PASS REVOKED_WORKER_STOP exit=nonzero"

# Use independent Vault audit bytes, never upload raw audit lines or token material.
dc exec -T vault cat /vault/logs/ci-audit.jsonl |
  python3 scripts/Check-App02B2CiVaultAudit.py > "$ci_root/vault-audit-summary.json"

# Recover API availability with a fresh policy-scoped token, then inject Vault outage.
api_token3="$(new_token ci-app02b2-api)"
printf '%s\n' "$api_token3" > "$VAULT_API_TOKEN_FILE"
expect "$(https_status /health/ready)" 200 API_RECOVERED_WITH_NEW_SCOPED_TOKEN
dc stop vault >/dev/null
expect "$(https_status /health/ready)" 503 VAULT_OUTAGE_HEALTH_DENY

export B2_AUDIT_SUMMARY="$ci_root/vault-audit-summary.json"
export B2_REPORT="$repo/TestResults/APP-02B2-CI-EPHEMERAL-PREPROD.json"
export B2_API_IMAGE="$(docker image inspect fmcg-preprod-b2-api --format '{{.Id}}')"
export B2_WORKER_IMAGE="$(docker image inspect fmcg-preprod-b2-worker --format '{{.Id}}')"
export B2_VAULT_IMAGE="$(docker image inspect hashicorp/vault:1.21.4 --format '{{.Id}}')"
export B2_EDGE_CERT_FINGERPRINT="$(openssl x509 -in "$tls/edge.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
python3 - <<'PY'
import datetime,hashlib,json,os,pathlib
e=json.loads(pathlib.Path(os.environ["B2_AUDIT_SUMMARY"]).read_text())
report={
 "TestRunId":"app02b2-ci-docker-"+os.environ["GITHUB_RUN_ID"]+"-"+os.environ["GITHUB_RUN_ATTEMPT"],
 "Environment":"EPHEMERAL_GITHUB_HOSTED_UBUNTU_DOCKER_NOT_REAL_PREPROD",
 "SourceHeadSha":os.environ["B2_SOURCE_HEAD_SHA"],
 "WorkflowSha":os.environ["GITHUB_SHA"],
 "WorkflowRunUrl":"https://github.com/"+os.environ["GITHUB_REPOSITORY"]+
                  "/actions/runs/"+os.environ["GITHUB_RUN_ID"],
 "UtcCompleted":datetime.datetime.now(datetime.timezone.utc).isoformat(),
 "ImageIds":{"api":os.environ["B2_API_IMAGE"],"worker":os.environ["B2_WORKER_IMAGE"],
             "vault":os.environ["B2_VAULT_IMAGE"]},
 "CaddyLeafCertificateSHA256":os.environ["B2_EDGE_CERT_FINGERPRINT"],
 "RUNTIME01_CI":"PASS_REAL_CONTAINER_TLS_STARTUP_PORTS_AND_FAIL_CLOSED",
 "RUNTIME02_CI":"PASS_REAL_VAULT_ACL_403_AUDIT_ROTATION_REVOKE_AND_PROXY",
 "DenialAudit":e,
 "RealPREPROD_R01_R02":"NOT_VERIFIED",
 "ExternalObserver":"NOT_RUN",
 "IndependentSecurityReviewer":"NOT_ASSIGNED",
 "G_B01_to_G_B05":"NOT_RUN",
 "ProductionAdmission":"NO_GO"
}
path=pathlib.Path(os.environ["B2_REPORT"])
path.write_text(json.dumps(report,sort_keys=True,indent=2)+"\n",encoding="utf-8")
path.with_suffix(".json.sha256").write_text(
 hashlib.sha256(path.read_bytes()).hexdigest()+"  "+path.name+"\n",encoding="utf-8")
PY
echo "CI_EPHEMERAL_PREPROD_PASS RUNTIME01_CI RUNTIME02_CI real_preprod=NOT_VERIFIED"
# No root/unseal/workload tokens or raw Vault audit stream in artifacts.
unset root_token api_token api_token2 api_token3 worker_token
