# LAB-D03 Vault rehearsal

The Windows initializer plans or stages one stopped Vault service on the D02
internal VAULT bridge. It reuses the unused D02 Vault volume for Raft and
creates a distinct audit volume. Both volumes stay on the same laptop fault
domain. The pinned Vault image uses server mode, a read-only root filesystem,
no extra capabilities and no published ports. Vault uses TLS 1.2+ and a
server leaf certificate with the DNS SAN `vault`. The leaf key and chain
are the only TLS material mounted. The issuer key and unseal shares stay
with the operator. Raft's recommended disabled mlock setting requires the
operator to assess host paging and custody before live execution.

## Review and stage

On a clean D03 checkout, provide the independently verified D01 plan used by
D02 (with this exact Vault publisher manifest in its inventory), D02 plan and
successful stopped Apply receipts with their original checksum manifests, and
the approved leaf certificate, key and CA files. All are protected files
outside Git. Restrict the leaf key directory and file to the current Windows
operator, SYSTEM and Administrators. Confirm the Windows bind can be read by
the container Vault UID before a separately approved start. Reject exposure
of the leaf key through a shared path. The Vault container uses no APP
bridge, consumes no SQL volume and makes no change to existing B.2 services.

~~~json
{
  "schema": "APP02B-LAB-DOCKER-VAULT-CONFIG-V1",
  "d01Plan": "C:\\LabPrivate\\d01.json",
  "d02Plan": "C:\\LabEvidence\\d02-plan\\docker-rehearsal-plan.json",
  "d02Apply": "C:\\LabEvidence\\d02-apply\\docker-rehearsal-apply.json",
  "certificate": "C:\\LabPrivate\\tls\\leaf.pem",
  "leafKey": "C:\\LabPrivate\\tls\\leaf.key",
  "caCertificate": "C:\\LabPrivate\\tls\\ca.pem",
  "evidenceRoot": "C:\\LabEvidence"
}
~~~

~~~powershell
.\scripts\lab\Initialize-DockerLabVault.ps1 -Plan -ConfigFile C:\LabPrivate\d03.json -OutputDirectory C:\LabEvidence\d03-plan-01
~~~

The read-only plan records exact source, D02 receipt hashes, daemon, project,
nonce, config, TLS input and model hashes and cached image content ID. The
operator supplies an independent enrollment and passes its SHA-256 from the
approval channel:

~~~json
{
  "schema": "APP02B-LAB-DOCKER-VAULT-ENROLLMENT-V1",
  "operation": "CREATE_STOPPED_VAULT",
  "nonce": "<D02 project nonce>",
  "planSha256": "<SHA-256 of docker-vault-plan.json>",
  "expiresUtc": "<UTC timestamp ending in Z>"
}
~~~

Only under that separate consent use `Initialize-DockerLabVault.ps1 -Apply`
with the same config, the approved `-PlanFile`, `-EnrollmentFile`,
`-EnrollmentSha256` and a fresh `-OutputDirectory`. Staging never pulls,
starts, replaces, prunes or deletes. An interrupted stage is retained for
operator reconciliation. A fresh invocation refuses any existing resources
in the D03 namespace. A staged receipt alone proves no Vault health.

## Separately enrolled live verification

Start, initialize, unseal, enroll roles and run faults only under separate
operator-approved scopes. Use a private enrolled Linux peer that resolves
`vault` on the VAULT network. Do not publish Vault or connect the B.2
workloads. Edge TLS remains reserved until an edge is separately enrolled.
Use the native Vault operator workflow to verify an uninitialized target before
initialization, encrypt shares and the initial root token to custodian PGP
keys, load them under protected custody, and maintain a multi-share
threshold. Never persist raw initialization JSON or use a dev/root token
for role probes. Retain independently captured, protected Vault audit records.

The source CI bootstrap is an executable fixture for the expected mount and
roles. The operator names the KV v2 mount `preprod`, registers the file
audit device `lab-file` at `/vault/logs/lab.jsonl` with default sensitive
field hashing, and logs the nonsecret `X-Lab-D03-Probe` header without HMAC
for correlation. Combine the unchanged B.2 API and Worker policies and
`operator-probe.hcl` individually with `token-self-revoke.hcl` to make
`lab-d03-api`, `lab-d03-worker` and `lab-d03-operator` respectively.
Configure the same-named non-renewable, orphan token roles with only their
matching policy, no default policy and an explicit maximum TTL of 5 minutes.
The delegated issuer uses only `token-issuer.hcl`. The three approved
synthetic probe keys at `preprod/runtime/{api,worker,operator}` carry
`{"fixture":"LAB_D03_SYNTHETIC"}`. They carry no application credentials.

On the enrolled Linux peer, keep the stage receipt, scope, CA, current
audit-file readback and output in an owner-only directory (0700), with input
files mode 0600. The protected scope has schema
`APP02B-LAB-DOCKER-VAULT-PEER-V1` and fields `sourceHead`, `nonce`,
`clusterId`, `caSha256` and `stageReceiptSha256`. Record the cluster ID
from the enrolled Vault and verify the copied receipt hash against the
operator-held original. Use a new path for each report.

~~~powershell
./scripts/lab/Test-DockerLabVault.ps1 -ConfigFile /private/peer.json -CaCertificate /private/ca.pem -StageReceipt /private/docker-vault-stage.json -AuditFile /private/audit.jsonl -OutputFile /private/roles-attempt-01.json
~~~

Feed the audit reader a current protected readback while the probe runs. It
requires the independently collected request/response pair for each 200
or 403 and stops if that witness is missing. Stdout shows only disposition.
The role test proves only those scoped HTTP/audit observations.

For separately approved rotation, provide a new enrollment with operation
`ROTATE_API`, `ROTATE_WORKER` or `ROTATE_OPERATOR`. Its `planSha256`
is the exact private peer scope file hash. Add the SHA-256 of the exact
current role token and issuer token bytes (no newline) as
`previousTokenSha256` and `issuerTokenSha256`, and the absolute
`replacementPath`. Compute token hashes under protected custody without
passing a credential on the command line. The stage receipt also pins the
leaf certificate on every connection. Run the peer command with
`--role`, `--enrollment`, `--enrollment-sha256` and an unused
`--replacement` path instead of the output option. Supply the audit reader
for both observations and rotation. The command asks
for the current role and delegated issuer credentials without echo in an
interactive terminal, creates a short-lived replacement, checks role
boundaries and audit, stores it in restricted custody, then revokes the old
token and checks denial and audit. An exclusive attempt marker prevents
replaying the same enrollment. If interrupted after issuance, inspect audit and credential
state before retrying. A replacement that was issued but never delivered
expires within five minutes. Bring the replacement into the operator's
approved custody flow before it expires.

~~~bash
python3 -I -S -B tools/app02b_lab/docker_d03_peer.py --config /private/peer.json --ca /private/ca.pem --stage-receipt /private/docker-vault-stage.json --audit /private/audit.jsonl --role api --enrollment /private/rotation.json --enrollment-sha256 "$APPROVED_D03_ROTATION_SHA256" --replacement /private/api-replacement
~~~

## Evidence limits

GitHub-hosted CI uses real TLS, Vault, Raft and file audit with temporary CA
and in-memory synthetic bootstrap credentials. The runner reaches the
private bridge IP with verified DNS SNI and reads the log via Docker exec.
It also exercises restart/re-unseal and replacement against retained Raft/audit
volumes, CA and hostname rejection, role
boundaries, expiry, rotation and missing audit readback. The archive contains
only redacted check results. It establishes no Windows-host, edge,
off-host custody, physical or business admission status. Live restart and
failure windows require separate consent and protected original evidence.

See the [HashiCorp Linux/amd64 manifest](https://hub.docker.com/layers/hashicorp/vault/1.21.4/images/sha256-6c77f568e6b6310d5bc68befb5711b9215c574de7da489e7c24332581176888b),
[Vault initialization API](https://developer.hashicorp.com/vault/api-docs/system/init)
and [Vault audit documentation](https://developer.hashicorp.com/vault/docs/audit).
