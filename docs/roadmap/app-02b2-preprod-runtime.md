# APP-02B.2 — PREPROD Runtime & Secrets Foundation

**Status: IMPLEMENTED_IN_PR / PREPROD_RUNTIME_NOT_VERIFIED pending actual PREPROD witnesses and independent final-HEAD review.** Owner authorization: [OWN-APP02B-01/02/07/10](../decisions/owner-app02b-2026-11-17.md). Parent contract: [B.1 architecture](app-02b1-architecture-security-contract.md), [TM-01/TM-02/TM-10/TM-11](../security/app-02b1-threat-model.md), [30-case frozen matrix](../security/app-02b1-acceptance-evidence.md).

## Deliverables and exact security boundary

- Separate \`FmcgDms.Preprod.Api\`, \`FmcgDms.Preprod.Worker\` and shared \`FmcgDms.Preprod.Runtime\` projects targeting SDK 10.0.404; **no reference** to legacy \`FmcgDms.Api\` (SyntheticAuth, LocalDB and App02a3 synthetic worker are excluded by construction).
- Linux Docker Compose with Caddy HTTPS/443 as the only host-published port, fixed private trusted proxy address, internal private bridge for Caddy↔API and isolated outbound for API/Worker only. Nonroot API/Worker, read-only root, dropped capabilities, never a public Worker/Control Plane/SQL/Vault socket. Host/cloud firewall and independent external scan remain mandatory.
- Dedicated remote HTTPS Vault KV-v2. Two independent token-file mounts and narrowly read-only policies. Startup, API readiness and Worker periodic control all require valid role-scoped marker and version. Missing token/ACL/TLS/Vault/marker fails closed without logging secrets. Exact Vault operator, unseal/recovery, token TTL and rotation schedule are deployment/security approvals **not supplied by Owner**.
- \`FOUNDATION_ONLY / NO_GO\` is compulsory. No tenant registry or independent revocation fence exists in B.2; therefore no JWT business authority, tenant binding, claim/lease/dispatch, production admission, external provider, or posting endpoint exists. API health shows \`admission=NO_GO\` even when Vault is ready. B.3/B.4/B.5 must build and separately verify these controls.
- TLS public host and exact proxy-peer trust are checked before forwarded header use; Caddy strips hostile forward headers. Control Plane does not exist in this bundle; \`/api/admin/*\` is 404, not an implementation of B.4.

## Scoped negative matrix (NOT a B.9 G-B01..05 PASS)

| Case | Required PREPROD fault/negative | Expected / independent witness | Current |
|---|---|---|---|
| RUNTIME-01.A | Wrong or conflicting DOTNET/ASPNETCORE environment/mode/role; dev signing key; synthetic auth or synthetic worker enabled | Startup nonzero, static redacted reason, no public synthetic endpoint | PREPROD NOT_RUN |
| RUNTIME-01.B | Non-TLS/invalid Vault endpoint or Vault down; token missing/revoked | Startup nonzero or readiness 503, Worker ceases any processing | PREPROD NOT_RUN |
| RUNTIME-01.C | Published API/Worker/SQL/Vault port, invalid TLS, missing proxy, host socket bypass | Public scan/config rejects; only HTTPS/443, no management routes | PREPROD NOT_RUN |
| RUNTIME-02.A | Forged forwarded headers, wrong public Host or direct private API | 403/404; external and nonproxy private observer, no privilege | PREPROD NOT_RUN |
| RUNTIME-02.B | API token→Worker path, Worker token→API path, revoked identity | Vault HTTP 403, Vault audit trace, no fallback; 503/Worker stop | PREPROD NOT_RUN |
| RUNTIME-02.C | Attempt Control Plane or synthetic business/diagnostics access | 404, no SQL effect or stateful business endpoint | PREPROD NOT_RUN |

**CI is a different evidence class**: \`tests/FmcgDms.Preprod.Runtime.Tests\` uses fake Vault handler and synthetic guard inputs; \`tools/verify_preprod_runtime.py\` parses Compose network/secret policies; GitHub Actions builds both OCI images. This is \`CI_ENGINEERING_ONLY\` and **cannot** assert any live PREPROD RUNTIME case PASS or verify a tenant DB.

## Actual PREPROD evidence required before exit

| Field | Independent source / rule |
|---|---|
| TestRunId, actual UTC, Host/VM identifier, public IP (redacted in public) | Operator-owned PREPROD run; not GitHub-hosted runner |
| Final source SHA + image digest(s) | Signed/immutable reviewed HEAD, hash of actually deployed images |
| DNS, TLS certificate fingerprint/expiry/CA; firewall and external scan | Two independent viewpoints; no \`-k\` insecure test |
| Compose/network/port fingerprint | Runtime container inspection, not YAML-only |
| Vault issuer/role, path, KV version, access 200 + denied 403 + audit ID | Independent Vault audit and restricted test identity; never token content |
| Fault matrix expected/actual/diff, denial logs and reason codes | Signed negative execution; peer-tenant data not applicable until B.4 |
| Operator, independent Reviewer, change/incident reference, evidence archive SHA | Distinct reviewer; any missing witness means NOT_VERIFIED |

B.2 exit \`PREPROD_RUNTIME_VERIFIED\` requires **actual PREPROD** RUNTIME-01 and RUNTIME-02, independent review with no blocking findings, final-HEAD CI PASS and immutable evidence; otherwise \`PREPROD_RUNTIME_NOT_VERIFIED\`. G-B01..05's frozen thirty cases still remain \`NOT_RUN\` until B.9's independent physical two-tenant matrix, regardless of B.2 status.

**Forbidden promotion**: No real AC-11/EV-08; original production G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV NOT_VERIFIED; B_NO_CHANNEL BLOCKED; APP-02C/D/E, production deployment, Pilot and Go-Live NOT_AUTHORIZED. Preserve historical APP-01/02A regression artifacts and immutable \`business/\` + \`business.lock.json\`. 
