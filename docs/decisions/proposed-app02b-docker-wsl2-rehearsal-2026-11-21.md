# APP-02B Docker Desktop / WSL2 rehearsal — proposed architecture disposition

**Status:** `PROPOSED_FOR_REVIEW / DOCS_ONLY / NO_PHYSICAL_ACCEPTANCE_AMENDMENT` (2026-11-21). The Owner requested a roadmap suitable for a Windows laptop with Docker and WSL2 but without the capacity to operate separate Hyper-V APP/DATA/VAULT VMs. This request authorizes **preparation of a proposal**, not installation, automatic host mutation, an enrollment permit, or a substitute physical acceptance contract.

**Roadmap:** [LAB-D00–D04](../roadmap/app-02b-local-docker-wsl2-rehearsal.md) · **Existing 32-increment VM roadmap:** [Windows local PREPROD](../roadmap/app-02b-local-preprod-acceptance.md) · [existing architecture alternatives](../roadmap/app-02b-local-preprod/design.md#2-architecture-decision-and-alternatives) · [I01/I02 VM contract](../roadmap/app-02b-local-preprod/01-platform-and-recovery.md).

## Context and decision proposed

The source-merged LAB-I01 contract checks a proposed full Hyper-V, three-guest architecture, and can legitimately return `BLOCKED` (exit 2) when that topology is unavailable. LAB-I02 proposes a Generation-2 Hyper-V VM lifecycle; neither Docker Desktop nor WSL2 by themselves satisfy those VM conditions. PR [#93](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/93) merged source/status reconciliation with no amendment to physical admission.

**Propose an additive, nonproduction Docker/WSL2 engineering rehearsal track** instead of weakening LAB-I01, renaming LAB-I02, or pretending containers are independent Linux VMs. Retain APP/DATA/VAULT as *service roles* in Docker Compose, not as separate host/VM trust domains. The Docker daemon, WSL2 backend, operator account and laptop storage are shared privilege/failure domains.

| Topic | Proposed disposition | Explicit non-equivalence |
|---|---|---|
| Operator workstation | Windows with running Docker Desktop WSL2 Linux-container backend; actual capabilities measured in LAB-D01 | WSL2 may use a lightweight VM; no three independent Hyper-V guest identities |
| APP / DATA / VAULT | Dedicated Compose services/networks, controlled volumes, scoped principals and source-pinned images | Compose network separation is not host isolation or an off-host recovery boundary |
| Application authority | Keep `FOUNDATION_ONLY / NO_GO` and existing guards in [B.2 Compose](../../deploy/preprod/compose.yaml); add no business routing/posting merely to make rehearsal green | Health/readiness does not grant business admission |
| Data | A single supported x86-64 Linux SQL Server container for synthetic A/B and relevant Control Plane DBs, subject to actual image/platform/license and D04 gates | A/B databases are not physically independent; LocalDB results do not substitute for this instance |
| Vault | Real server mode with persistent Raft, TLS, audit and role separation in D03 | Same-host custody does not prove independent trust, HA or recovery from loss of the laptop |
| Observation | Host/container/private-peer probes each identified with real vantage and raw redacted evidence | Host loopback is not a true external internet witness |
| Result label | Proposed `DOCKER_REHEARSAL_READY` **only after** D01–D04 scoped live checks and separately authorized enrollment | Not `LOCAL_LAB_READY`, `PREPROD_FOUNDATION_VERIFIED`, `B9 PASS` or production authorization |

The original [APP-02B Owner decisions](owner-app02b-2026-11-17.md), [B.1 thirty-case obligations](../security/app-02b1-acceptance-evidence.md), [B.9 fail-closed oracle](../roadmap/app-02b9-independent-preprod-oracle.md), and [proposed Windows operator scope](proposed-app02b-local-lab-scope.md) remain unchanged. `DOCKER_REHEARSAL_READY` is a **new proposed engineering-only label** and is not authorized for use as a physical gate outcome by publishing this document.

## Authority and execution gates

1. **Documentation preparation (this PR):** create D00 proposal, roadmap and Codex handoff only. Do not change the 32-row VM register, I01 exit semantics, B.9 schema, source guards, locked business data or source application code.
2. **Before D01–D04 source implementation:** obtain an explicit Owner increment instruction/adoption for the proposed track; source-only work can then proceed in one independent PR per increment. No earlier merge on the VM track grants implicit approval to mutate a laptop.
3. **Before any host-impacting Apply:** the operator reviews an exact-source/config/image/target/nonce-bound plan, preflight evidence and clean ownership inventory, then authorizes an explicitly bounded operation. A script may validate an external approval; it cannot manufacture one. Do not alter shared Docker Desktop/WSL settings, prune resources, reboot, delete volumes, change firewall rules or publish ports as a side effect of read-only checks.
4. **Before local business experiments:** separate reviewed B3/B4/B5 tenant membership/authority, current #53 status and scoped lab-only activation/command contract still apply. D04 may inspect and set up approved synthetic databases, but does not grant business posting.
5. **Before any physical PREPROD claim:** use the original independently approved VM/VPS environment and unchanged B.1/B.9 evidence, external witnesses and human custody. The Docker track cannot silently satisfy or waive these.

## Decisions still required before live Apply

| Decision to record | Why the operator must supply it |
|---|---|
| Real Docker daemon/context and WSL distro/backend/CPU architecture | Avoid acting on a remote/unintended engine or unsupported SQL Linux platform |
| Memory, disk and Docker data root | Determine achievable workload; do not import the VM 32/64 GiB thresholds as Docker requirements |
| Project namespace/nonce, volumes, backup destination and cleanup policy | Avoid collisions and deletion of pre-existing operator data |
| Allowed loopback/LAN binding, Docker networks and port inventory | No accidental SQL/Vault/API/management exposure |
| Exact publisher-verified OCI digests and SQL edition/license terms | No mutable tags or fabricated source-provenance claims |
| TLS issuer/trust, Vault enrollment, secret references and key custody | Prevent root/unseal credentials or CA keys entering Git or APP mounts |
| SQL A/B tenant/identity scope, fault windows, retention and external copies | Distinguish bounded synthetic rehearsal from production, PITR and off-host custody |
| Actual human reviewer roles and trust roots when physical acceptance is pursued | Solo/AI review is not independent human Security/DBA approval |

**Stop conditions:** absent/foreign Docker context, unsupported architecture, unsafe host binding, insufficient verified resources, missing image provenance, unowned volumes or changed approval scope yield `BLOCKED` with reason/evidence. A user-provided statement that Docker and WSL2 exist is a planning input, not a measured host PASS.

## Accepted limitations and migration path

Development on one laptop may demonstrate source correctness, authenticated endpoints, basic container isolation/deny behavior, real SQL permission denial, Vault 403/audit and bounded restart persistence. It cannot demonstrate failure-domain independence, off-host recoverability, genuine external-origin reachability or independent human custody solely by adding containers. A crash/restart of Docker Desktop or WSL2 affects several service roles at once; report the shared boundary explicitly.

Preserve separate VM/VPS deployment contracts and route to LAB-I01–I05 or an approved remote PREPROD host when these obligations must be met. Reuse reviewed image/build artifacts, SQL migrations, application source and evidence schemas only when identity and trust checks actually match; never relabel Docker evidence as evidence from a different physical source/host.

## Official technical references

- [Docker Desktop WSL2 backend](https://docs.docker.com/desktop/features/wsl/)
- [Docker Desktop Windows installation and backend requirements](https://docs.docker.com/desktop/setup/install/windows-install/)
- [Docker Compose networking and internal networks](https://docs.docker.com/compose/how-tos/networking/)
- [Microsoft SQL Server Linux container platform/persistence guidance](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver17)
