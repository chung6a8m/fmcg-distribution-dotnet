#!/usr/bin/env python3
"""Independent Vault audit witness: read audit JSONL on stdin and emit counts only.

Never write raw audit to a public artifact; Vault audit can include sensitive metadata.
HTTP status/real Vault ACL negatives are checked separately by the CI runner.
"""
import json
import sys

records = []
for raw in sys.stdin:
    raw = raw.strip()
    if not raw:
        continue
    try:
        records.append(json.loads(raw))
    except (ValueError, TypeError) as exc:
        raise SystemExit("CI_VAULT_AUDIT_FAIL malformed audit record") from exc

responses = [r for r in records if r.get("type") == "response"]

def denied_for(path: str) -> bool:
    for record in responses:
        if record.get("request", {}).get("path", "").lstrip("/") != path:
            continue
        error = str(record.get("error", "")).lower()
        status = record.get("response", {}).get("status_code")
        if status == 403 or "permission denied" in error:
            return True
    return False

witnesses = {
    "api_cannot_read_worker": denied_for("preprod/data/runtime/worker"),
    "worker_cannot_read_api": denied_for("preprod/data/runtime/api"),
    "workload_cannot_list_vault_admin": denied_for("sys/policies/acl"),
}
if len(records) < 5 or len(responses) < 3 or not all(witnesses.values()):
    raise SystemExit("CI_VAULT_AUDIT_FAIL missing independently witnessed 403 response")

print(json.dumps({
    "AuditStorage": "actual Vault file audit, raw events discarded after parsing",
    "TotalEvents": len(records),
    "ResponseEvents": len(responses),
    "DeniedCrossIdentityAndAdmin": witnesses,
    "IndependentlyObserved": "CI_VAULT_AUDIT_403_PASS",
}, sort_keys=True))
