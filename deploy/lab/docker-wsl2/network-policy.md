# LAB-D02 operator contract

LAB-D02 stages three internal bridges, two unused persistent named volumes
and stopped B.2 API and Worker containers. Use reviewed B.2 build artifacts
with immutable registry digests and the exact D02 checkout SHA in the OCI
revision label. D01 separately verifies publisher receipts and cached image
inputs. Staging performs no builds or pulls. The runtime stays FOUNDATION_ONLY /
NO_GO. Edge TLS, Vault and SQL have separate D03 and D04 gates.

## Protected inputs and plan

Run D01 separately on the exact clean D02 checkout and intended Windows
daemon with pinned identities. A historical READY report establishes laptop
capability at that time. D02 requires READY for this checkout, config and
target. It verifies the checksum manifest and checks current conditions
again. Retain both D01 reports and SHA256SUMS together.

Create a private config outside Git and outside the D01 evidence root:

~~~json
{
  "schema": "APP02B-LAB-DOCKER-REHEARSAL-CONFIG-V1",
  "nonce": "<nonzero 32-character lowercase hexadecimal string>",
  "baselinePlan": "C:\\PrivateLabPlan\\docker-host.json",
  "baselineReport": "C:\\LabEvidence\\Docker\\d01-attempt-01\\docker-preflight.json",
  "images": {
    "api": "registry.example.invalid/dms/api@sha256:<published digest>",
    "worker": "registry.example.invalid/dms/worker@sha256:<published digest>"
  }
}
~~~

Use D01 network names ending in _app, _data and _vault, and volume names
ending in _sql and _vault, all prefixed with the exact project. Port
reservations stay unbound. Keep a private custody directory for config and
approval material on fixed NTFS or ReFS. D02 checks owner and write
permissions against the current operator, Administrators and SYSTEM, and
rejects reparse points. Use a fresh child under the D01 evidence root for
each output.

~~~powershell
.\scripts\lab\New-DockerRehearsal.ps1 -Plan -ConfigFile C:\PrivateLabPlan\d02-config.json -OutputDirectory C:\LabEvidence\Docker\d02-plan-01
~~~

Plan is read-only and publishes docker-rehearsal-plan.json and SHA256SUMS.
It binds source, local daemon, config, D01 inputs, image content IDs, nonce,
names, subnets and reservations. It records the exact Compose model and the
scoped inventory without reporting native secrets or foreign inventory.

For a partial stage with only the APP bridge, use the [D01 resume plan](README.md#resume-a-partially-staged-d02-app-bridge) and the same D02 nonce. D02 rechecks the exact pinned bridge in both live inventories before Plan and keeps the ID pinned through staging. A missing or replaced bridge blocks the operation.

## Separate operator authorization

After reviewing the plan and target, the operator or custodian supplies a
protected enrollment independently. Pass its SHA-256 from the operator's
approval channel. D02 never creates an enrollment. The fingerprint binds
approved file bytes under operator custody. Source and CI authorization
does not enroll a laptop operation.

~~~json
{
  "schema": "APP02B-LAB-DOCKER-ENROLLMENT-V1",
  "operation": "CREATE_STOPPED_FOUNDATION",
  "planSha256": "<exact SHA-256 of docker-rehearsal-plan.json>",
  "nonce": "<same nonce as the config>",
  "expiresUtc": "<UTC timestamp such as 2026-11-22T00:00:00Z>"
}
~~~

~~~powershell
.\scripts\lab\New-DockerRehearsal.ps1 -Apply -ConfigFile C:\PrivateLabPlan\d02-config.json -PlanFile C:\LabEvidence\Docker\d02-plan-01\docker-rehearsal-plan.json -EnrollmentFile C:\PrivateLabPlan\d02-enrollment.json -EnrollmentSha256 $ReviewedEnrollmentSha256 -OutputDirectory C:\LabEvidence\Docker\d02-apply-01
~~~

Apply checks the enrollment, exact plan, current identity, budget, ports,
subnet conflicts, image provenance checks and resource ownership. It never
replaces an existing container or deletes a volume. An interrupted
operation can leave exact nonce-owned resources for review and retry
under the still-valid approval. Apply refuses to recreate a volume that
was present during plan review and later disappeared. Cleanup is outside
the D02 command. D03/D04 operation contracts govern their data and lifecycle.

Docker administrators share privilege with D02. Keep other orchestration
out of the namespace during Plan and Apply. The mutex serializes
cooperating Apply invocations for the same daemon and project. Labels and
readback detect configuration drift and foreign ownership.

## Network observations

~~~powershell
.\scripts\lab\Test-DockerRehearsalNetwork.ps1 -ConfigFile C:\PrivateLabPlan\d02-config.json -PlanFile C:\LabEvidence\Docker\d02-plan-01\docker-rehearsal-plan.json -OutputDirectory C:\LabEvidence\Docker\d02-network-01
~~~

The verifier checks the staged model and Windows IPv4/IPv6 loopback TCP port
reservations from WINDOWS_HOST. A connection refusal means DENIED. Other
socket errors remain UNKNOWN. D01 fresh inventory also checks TCP/UDP port
reservations. The D02 verifier does not launch peer containers.

Synthetic Linux GitHub-hosted CI uses temporary, named, owned peers to
calibrate an APP TCP success from DOCKER_PRIVATE_PEER and check cross-bridge
denial from OUTSIDE_APP_BRIDGE. It observes Linux host-to-bridge access
separately. Docker bridge isolation does not establish a Windows/WSL host
firewall or an external network witness. APP containers attach only to the
private APP bridge and have no published ports.

Forwarded headers require an enrolled edge and listener. D02 records NO_EDGE.
D03 must verify forwarding if it exposes an authorized loopback HTTPS edge.
D02 does not infer Vault or SQL socket policy from the services being absent.
It grants no business or tenant authority and earns no physical acceptance.

Exit 0 reports Plan publication, stopped staging, or an observed network
snapshot. Exit 2 reports a contract or host blocker. Exit 1 reports an
invocation or collection failure.
