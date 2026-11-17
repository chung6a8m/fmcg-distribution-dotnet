# APP-02B.2 — PREPROD runtime (FOUNDATION_ONLY / NO_GO)

This Compose bundle runs a **separate health-only .NET API** and a **non-dispatching Worker** on a Linux VPS/VM, behind Caddy TLS. It **does not run** the old synthetic API, LocalDB, a business tenant, a real order/cash command, or a Control Plane. B.3–B.5 must implement and independently prove Identity, tenant registry and revocation before any business endpoint or Worker claim is designed. The image builds and GitHub-hosted CI **are not actual PREPROD evidence**.

## Required private infrastructure (not provided or bootstrapped by this PR)

1. An operator-controlled Linux PREPROD VPS/VM with Docker/Compose, an explicit public binding/443 policy, TLS cert/key for the selected DNS name, private routing and egress firewall allowing only the approved private Vault address. Inspect DNS, host firewall, cloud security group, Docker daemon TCP exposure, no host network, and public port scan from an **independent external observer**. Deny public 8080, SQL 1433, Vault 8200, Worker, Docker API and future Control Plane. The Compose bridge's outbound network **does not itself enforce egress ACL**. Verify the subnet 172.30.50.0/28 doesn't overlap an existing network.
2. A dedicated, privately reachable, **HTTPS and system-trusted TLS** Vault server, provisioned by an independent Vault operator (not this compose stack). Enable protected Vault audit logging. Create a KV-v2 mount named \`preprod\`, with two versioned entries \`runtime/api\` and \`runtime/worker\`, each containing only \`principal=api|worker\` and \`admission=FOUNDATION_ONLY\`. Assign separate narrow policies from \`vault-policy-api.hcl\` / \`vault-policy-worker.hcl\`; issue separate **time-bound, revocable, renewable workload tokens**, never root tokens. Use a dedicated, audited out-of-band issuance/renewal path; app never bootstraps a token or unseals Vault. Do not log/commit token values, keys, secret response bodies or backup credentials.
3. TLS key and workload token files provisioned out-of-band, owned by the appropriate container UID (1654 for API/Worker), with minimum file and directory permissions. Caddy TLS key must be accessible to Caddy's process and not exposed to API/Worker. Tokens are read fresh on every Vault probe, so an operator can atomically replace mounted **file contents** for rotation. Check Docker bind-mount semantics if replacing the inode: in-place updates or container recreation may be required. A revoked/deleted/stale token must fail startup/readiness; there is **no plaintext fallback**.
4. No tenant SQL binding, registry, signing key or revocation authority is configured in B.2. In particular, a successful readiness response says \`admission=NO_GO\`, **not** safe production or even PREPROD business admission. SQL Server must remain outside Compose on a private network, with DB-per-tenant and login permissions to be verified in B.4.

## Operator deployment (only after change approval)

Copy \`.env.example\` to untracked \`.env\` on the approved PREPROD host and replace ALL example DNS, Vault and absolute certificate/token paths. Never put credentials in \`.env\` or a GitHub environment variable; these entries are only addresses/paths. Ensure each token file contains a valid **distinct** Vault token, is host-readable by container UID 1654, and is not world-readable. Test that VAULT_ADDR is HTTPS and the host trusts the Vault certificate chain; no \`curl -k\`, insecure-skip-verify or non-TLS bootstrap.

From \`deploy/preprod\` on the approved host:

\`\`\`sh
docker compose config --quiet
docker compose build --pull api worker
docker compose up -d --remove-orphans
docker compose ps
curl --fail-with-body --silent --show-error "https://YOUR_PREPROD_FQDN/health/ready"
\`\`\`

Expected readiness payload is \`{"state":"foundation_ready","admission":"NO_GO"}\`, never \`GO\`. Only \`/health/live\` and \`/health/ready\` are exposed by Caddy. No application routes to \`/api/synthetic/*\`, \`/api/diagnostics/*\`, \`/api/admin/*\`, or \`/api/business/*\` exist. API rejects direct socket access, including forged X-Forwarded-Proto/For; Caddy always overwrites untrusted forwarded values and no api/worker port is published. Rotate host certificates and Vault tokens through approved operator procedures with a bounded outage; B.2 does not invent a fixed cadence.

## RUNTIME-01 and RUNTIME-02: mandatory *actual PREPROD* witnesses

**RUNTIME-01**: Record a signed change/host ID, source HEAD, image digests, public/host firewall scan, compose network inspect, Caddy HTTPS certificate verification, live+ready outcomes, and negative admission probes: \`SyntheticAuth__Enabled=true\`, \`App02Worker__Enabled=true\`, dev signing key, mode mismatch, missing/untrusted Vault and other public ports. Each must stop/deny and show a stable redacted \`RUNTIME_*_DENIED\` reason; never log token values.

**RUNTIME-02**: From independent ingress vantage point and a private nonproxy peer, check direct API bypass with forged \`X-Forwarded-*\`, management route denial, unexpected public Control Plane/SQL/Vault/Worker sockets, and an API token presented to Worker Vault path (and vice versa) denied by real Vault **403 + Vault audit record**. Then revoke the API token and check \`/health/ready\` becomes HTTP 503; Worker revocation should terminate its process. Restore tokens only under audited change. Record TLS chain, policy IDs, Vault audit event IDs, UTC times and peer/role without dumping secrets.

Run the *operator-owned* \`scripts/Collect-App02B2PreprodEvidence.sh\` for bounded mechanical probes, then attach the independent firewall/network/Vault-audit witnesses to a **nonpublic**, redacted evidence store with immutable SHA-256, environment/host ID, actual run/test IDs, final source/image digests, operator and independent reviewer names. Never check private operational evidence into this public repo.

If actual deployment, external observer, policy-403 or independent review is missing, mark \`PREPROD_RUNTIME_NOT_VERIFIED\` and the corresponding gate **BLOCKED/NOT_RUN**, never \`PREPROD_RUNTIME_VERIFIED\`. GitHub-hosted CI proves only a synthetic engineering exercise, not a physical PREPROD deployment.
