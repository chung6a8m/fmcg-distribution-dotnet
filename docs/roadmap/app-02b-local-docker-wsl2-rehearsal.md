# APP-02B LAB-D00–D04 — Windows Docker Desktop / WSL2 rehearsal roadmap

**Status (2026-11-21):** D00 architecture `ROADMAP_PROPOSED`, D01 `SOURCE_MERGED / CI_VERIFIED / HOST_READY`. The Owner requested this roadmap for a Windows laptop with Docker/WSL2 and insufficient capacity for separate Hyper-V APP/DATA/VAULT VMs. The [32-increment Windows VM roadmap](app-02b-local-preprod-acceptance.md) and its separate physical acceptance gates remain in force. The Owner authorized D02 source and CI only on 2026-11-21. The Owner authorized D03 source, CI and merge, then authorized D04 source and CI. Host Apply remains separately gated.

**Decision/ADR:** [Docker/WSL2 proposal](../decisions/proposed-app02b-docker-wsl2-rehearsal-2026-11-21.md) · **Codex handoff:** [LAB-D handoff](app-02b-local-docker-wsl2/codex-handoff.md) · **Existing architecture:** [APP-02B local lab design](app-02b-local-preprod/design.md) · **VM platform contract:** [LAB-I01/I02](app-02b-local-preprod/01-platform-and-recovery.md) · **Physical oracle:** [B.9](app-02b9-independent-preprod-oracle.md).

**Reviewed baseline:** PR [#93](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/93) merged as `4bb30c342b50d73af7f66f509202d275af6d4809` on 2026-11-21. Re-evaluate later changes on `main` before implementation; current I01 source and the original B.9 oracle remain untouched.

## 1. Intent, constraints and non-goals

**Intent:** use the user's real Windows laptop as an operator workstation for *bounded local engineering rehearsal* with Linux containers under Docker Desktop's WSL2 backend. Exercise the source/build path, API/Worker foundation, SQL tenant separation, scoped Vault credentials, network denial and restart/persistence with repeatable evidence. No separate Hyper-V VMs are required for this track.

**Non-goals:** do not establish independent VMs, physical PREPROD, off-host disaster recovery, production-grade security isolation or original B.1/B.9 human approval. Do not open APP-02C/D/E runtime, real money posting, production/Pilot/Go-Live or grant real business command authority. Docker containers on one daemon/WSL2 host share a kernel/privilege/storage/failure domain. `DOCKER_REHEARSAL_READY` is a proposed **new engineering-only milestone**, not `LOCAL_LAB_READY` or `PREPROD_FOUNDATION_VERIFIED`.

Preserve `business/`, `business.lock.json`, `tools/app02b9/expected-v1.json`, the B.9 validator, current source/synthetic guards, RUNTIME denial semantics, PR #93 source/CI history, and the original five gates G-B01..05 with 30 cases plus RUNTIME-01/02. A GitHub issue's closed state cannot substitute for physical acceptance. Record #53 as GitHub CLOSED (PR #74 still OPEN/DRAFT and physical B53 NOT_VERIFIED), #68/#75 as OPEN and #59 by its scoped policy; never infer fixes from issue status.

**Separate evidence axes (never collapse):**

| Axis | Possible evidence | Claim forbidden without separate proof |
|---|---|---|
| Source/CI | Reviewed SHA, tests, CI runs on exact HEAD, image digest | Laptop measurement or deployment |
| Host preflight | Read-only Docker/WSL2 report on the actual laptop | Full Hyper-V I01 readiness |
| Authorized local execution | Enrolled scope, actual Compose/SQL/Vault/network observations | Independent host/role custody or external-origin observation |
| Physical B.1/B.9 | Original approved trust roots, observer/Security/DBA, fault/restore witnesses | Inherited from Docker rehearsal or solo AI review |

### Proposed service topology (not VM trust domains)

```text
Windows operator laptop (approved local evidence folder outside Git)
  Docker Desktop / WSL2 Linux-container engine [one shared host/failure domain]
    edge (loopback HTTPS only when authorized)
      -> API (FOUNDATION_ONLY / NO_GO)       APP service role
      -> Worker (non-dispatching foundation) APP service role
    SQL Server container + separate A/B and Control Plane databases  DATA service role
    persistent Vault server + audit + TLS                      VAULT service role
    operator test/observer process (Windows, or enrolled private test peer)
  separate restricted evidence files + operator-held external copies where provided
```

Use explicit Compose networks and service membership; the existing [B.2 Compose](../../deploy/preprod/compose.yaml) currently leaves SQL/Vault **outside** its APP stack and keeps API/Worker `FOUNDATION_ONLY / NO_GO`. Do not alter its production/PREPROD meaning to absorb this rehearsal. Introduce a dedicated, explicitly named local Compose project and overlays in later increments. No `ports` for SQL/Vault/Worker/API management, no `network_mode: host`, no `privileged`, no Docker socket in containers, no wildcard host bindings; a permitted edge may bind `127.0.0.1` only after a port ownership check. Compose `internal` networks and UFW rules **alone are not verified egress/ingress policy**; test actual connectivity from each claimed vantage.

SQL's Linux container is supported only for matching Linux x86-64 Intel/AMD host/engine architecture; don't assume support on ARM64/QEMU/translation. Pin published image digests and verify the actual Docker context and license before creating data volumes. Plan memory/disk against actual WSL2/Docker limits (not the I01 VM 32/64 GiB planning profile).

## 2. Register and dependencies — one increment / one PR

These five proposed rows are **separate from the existing 32-row LAB-E/I/S/O register**. Creating this roadmap does not increment that register's SOURCE_MERGED count or make its M1 milestone complete.

| ID | Proposed PR scope and dependency | Deliverable / exit gate | Current state |
|---|---|---|---|
| **LAB-D00** | Docs-only architecture proposal, contract boundaries and this roadmap; no source dependency beyond PR #93 baseline | Reviewed ADR + D01–D04 specs + Codex handoff, exact links and acceptance labels | `DOCS_REVIEW / PROPOSED` |
| **LAB-D01** | Read-only Docker/WSL2 host preflight after D00 proposal merge and direct D01 Owner instruction | Versioned redacted report, capability + provenance inventory, safe operator disposition | `SOURCE_MERGED / CI_VERIFIED / HOST_READY` |
| **LAB-D02** | Guarded Compose plan/apply, networks, storage, negative probes; source dependency D01 | Scoped project plan/ownership, idempotent stopped foundation staging, no business admission | `SOURCE_MERGED / CI_VERIFIED / HOST_PLAN_BLOCKED / APPLY_BLOCKED` |
| **LAB-D03** | Trusted TLS, durable Vault Raft, role credential lifecycle; D02 source and enrolled live sandbox | Real Vault 403/audit, persistence, fail-closed trust & token rotation | `SOURCE_MERGED / CI_VERIFIED / HOST_NOT_RUN / APPLY_BLOCKED` |
| **LAB-D04** | Supported SQL instance + A/B tenant identities and permission probes; D02/D03 source, B3–B5 and current authority prerequisites | Same-instance DB layout, migrations/readback, denial witnesses and local reconciliation | `SOURCE_REVIEW / HOST_NOT_RUN / APPLY_BLOCKED` |

Source-only work may be reviewed independently while laptop access/host approval remains blocked; in that event report `SOURCE_REVIEW` and `HOST_NOT_RUN/BLOCKED`, never `DOCKER_REHEARSAL_READY`. D04 may **design** SQL migrations/probes while D03 live Vault is blocked; D04 **Apply** must consume actual enrolled D03 credentials/trust.

```mermaid
flowchart LR
  D00["D00: ADR + contract"] --> D01["D01: Docker/WSL preflight"]
  D01 --> D02["D02: guarded Compose"]
  D02 --> D03["D03: TLS + Vault"]
  D03 --> D04["D04: SQL + tenant rights"]
  D04 --> M["Proposed DOCKER_REHEARSAL_READY"]
  I["LAB-I01/I02 Hyper-V track"] -. "unchanged, separate" .- D01
```

## 3. LAB-D00 — Architecture, authority and roadmap

**PR boundary:** documentation only. Paths: [ADR](../decisions/proposed-app02b-docker-wsl2-rehearsal-2026-11-21.md), this roadmap, [handoff](app-02b-local-docker-wsl2/codex-handoff.md), plus navigation links in README/master roadmap/AGENTS. Do not modify I01/I02 source, status, schema or VM acceptance criteria.

**Contract decisions to record (not silently approve):**
- `DOCKER_DESKTOP_WSL2` as proposed execution topology and one shared failure domain.
- Evidence tier `LOCAL_OPERATOR_LAB` and `SOLO_OWNER_SELF_ATTESTED` only when the actual local run supports those statements; original `originalGateStatus=NOT_VERIFIED`.
- Proposed `DOCKER_REHEARSAL_READY` as a **nonphysical** milestone. Until separately authorized and measured, use `PROPOSED/NOT_VERIFIED`.
- D01 and D02 proposed versioned input/output names, disjoint from `APP02B-LAB-WINDOWS-PLAN-V1`, `APP02B-LAB-WINDOWS-PREFLIGHT-V1` and LAB-E05 protected `APP02B-LAB-ENVIRONMENT-V1`/`APP02B-LAB-ADMISSION-V1`.
- A concrete operator-approved Docker context, host/resources, project nonce/ownership, volumes, images, approved bindings, trust and retention is required before live Apply.

**Review/exit:** independent docs review, relative link/anchor checks, preserve 32-row register and B.9 remaining physical obligations verbatim; final HEAD CI as applicable. Merge only after a separate Owner review/merge instruction. A merged docs PR is not implementation authorization for D01–D04.

## 4. LAB-D01 — Docker/WSL2 preflight and image provenance

**Source review:** `feat/app02b-lab-d01-docker-preflight` starts from D00 merge `be961ba7f71eead5360fe8cc31c22a0a6e989818` via [PR #94](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/94). The Owner's direct LAB-D01 implementation instruction of 2026-11-21 authorizes this source increment. The checked-out source SHA and plan SHA-256 are recorded in each valid attempt.

**Implemented interfaces:**
- `scripts/lab/Test-DockerWslPrerequisites.ps1 -ConfigFile <path> -OutputDirectory <new-protected-directory>` — **read-only**; exit 0 only when required conditions measured PASS, exit 1 input/collection failure, exit 2 required condition BLOCKED/UNKNOWN.
- `tools/app02b_lab/docker_preflight.py` plus `tools/app02b_lab/schemas/lab-docker-host-*-v1.json`. D01 uses the independent `APP02B-LAB-DOCKER-HOST-PLAN-V1` and `APP02B-LAB-DOCKER-HOST-PREFLIGHT-V1` contracts and shared strict parsing and Windows path safeguards.
- [Operator guide](../../deploy/lab/docker-wsl2/README.md), example plan, toolchain floors, and `tests/app02b_lab/test_docker_preflight.py`.
- Outputs `docker-preflight.json`, `docker-preflight.md` and `SHA256SUMS` under operator-protected outside-repo storage.

**Required observations:** exact Windows build/architecture; WSL version and distro mode; running Docker Desktop/Engine, selected `docker context`, daemon endpoint/host identity (not a remote/unexpected engine), Linux container OS/arch, Compose/plugin versions, storage backend and Docker root; resource ceilings/free RAM/disk/WSL2 allocation; existing Compose projects, container networks, volumes, binds and host/loopback port collisions; local DNS/clock; image registry/digest/signature or trusted publisher provenance; CLI availability and redaction.

**Negative tests:** absent daemon, Windows-container backend, WSL1, remote context, ARM/emulation SQL, missing memory/storage, unknown data root, preoccupied ports, unsupported image pin, polluted `DOCKER_HOST`, secret-leaking input, symlink/reparse and Git evidence path. A synthetic CI runner is not the user's laptop. **Exit:** one exact-source redacted report with no required UNKNOWN; host restrictions remain explicit even if Docker works. The D01 entry point is read-only. Exact image cache staging requires separate operator authorization.

**Source and CI:** [PR #95](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/95) merged reviewed source `8e74a3f2a1cd3b9c1b4056008db2538407000ffd` at `9486b30326e16093620ef37de39bedbc90ffd229`. GitHub lists 24/24 successful PR jobs. The exact source passed [LAB-E05 Windows/Linux, attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44901699348/attempts/1) and the [synthetic B.9 dispatch, attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44901696304/attempts/1).

**Operator laptop:** On 2026-11-21 at 12:25:24 UTC, `d01-attempt-01` ran on clean merge `9486b30326e16093620ef37de39bedbc90ffd229` and returned `BLOCKED` (exit 2), with `authorization=NOT_EVALUATED`. The first-run plan used zero identity pins for discovery, publisher metadata receipts for four `linux/amd64` pins, and the example resource floors and reservations. Plan SHA-256 `6886ae9b5c524cfbad76fb7a043c39bf25f93fadaab24741b9f6fb11d2aec791`. The JSON report SHA-256 is `4ab37c85a9eec239c0a11a891e7625131ab8c9aca565b3fdacd482d1e858b509`. The report, Markdown and verified checksums are under protected local custody outside Git.

The collector returned `DAEMON_UNAVAILABLE` before the Docker CLI probe because two distinct executable paths were reported for running Docker Desktop processes. A separate signature-checked read-only probe reached local `desktop-linux`, with an x64 Linux engine, WSL2, 7,796 MiB daemon RAM and 20 visible CPUs. The official report measured 3,677 MiB free Windows RAM against the 4,096 MiB example floor. Two distinct storage setting paths prevented selection of a recognized VHD. A separate cache read found no match for the four exact plan pins. Linux filesystem headroom remains `NOT_MEASURED`.

**D01 collector follow-up and laptop replay:** Local source `25377b90f4decad005fd8991d59cab81ecfd6313` accepts the signed launcher and frontend from one Desktop install and resolves the recognized VHD candidates across configured locations. Local Windows verification passed 84 LAB tests and 12 B.9 validator tests. The managed worktree's injected Git configuration caused the first full LAB run to fail during disposable fixture initialization. Clearing it for the test process allowed the full suite to pass. This local source and host evidence are tracked separately from PR CI.

On 2026-11-21 at 12:34:25 UTC, `d01-attempt-02` returned `BLOCKED` (exit 2) on the clean follow-up source with `authorization=NOT_EVALUATED`. The plan's source SHA was the only input changed. Plan SHA-256 `9adf2cce62b30c8c1d7e5fca0b1ef9b08b9ad97f15636c1a9da0315bc995226b`. Verified JSON SHA-256 `9f9945c75579b26950c0255c5cc5d0cd11a4f3468d75f64ae4c9b9fc49fbde24`. Docker Desktop, the local daemon and WSL2 passed. The plan retains zero identity pins and all four exact image cache checks remain `UNKNOWN`. Free Windows RAM was 3,506 MiB against a 4,096 MiB floor. The supplemental storage probe resolved one candidate and Windows Storage reported `Attached=False` with a nominal capacity of 262,144 MiB. `WINDOWS_BACKING_STORAGE` therefore remains `UNKNOWN`. Linux filesystem headroom remains `NOT_MEASURED`.

**Pinned laptop replay and storage diagnosis:** The operator confirmed the laptop and local daemon from attempt 02. Both identity checks passed on clean source `a9d2aff6a548a17b4b122b11514b7a102c83bca8` with plan SHA-256 `a4cf23bc136b251237fbcd7f31c454bf9c4397be6239fdb3539bcff02f46a022`. At 12:39:40 UTC on 2026-11-21, `d01-attempt-03` returned `BLOCKED` (exit 2), with 4,026 MiB free Windows RAM. Its verified JSON SHA-256 is `c949ffee40fe4e40d87a1670b6e5677c5d33861700c0cb5daa73c34946885405`. At 12:41:17 UTC on the same date, `d01-attempt-04` returned `BLOCKED` (exit 2), with 4,115 MiB free RAM against the unchanged 4,096 MiB floor. Its verified JSON SHA-256 is `4bb267ca25a8d382b0ab58490a2ee7bbe30c06cc77b94fa804f3b7b24a9217c5`. The only non-PASS checks in attempt 04 were `WINDOWS_BACKING_STORAGE` and the four `IMAGE_*` checks, all `UNKNOWN`. No images were pulled.

The Windows Storage provider reported `Attached=False` with no Windows device path. A successful Hyper-V Compute event 2007 names the exact configured VHDX and the currently running WSL VM. The most recent successful change in that SCSI slot was the add, and the guest's read/write ext4 mount matched SCSI LUN 3. A separate read-only guest observation measured 236,903 MiB free on `/mnt/docker-desktop-disk`. These supplemental observations explain the different Windows and guest views. Attempts 03–04 required Windows Storage attachment confirmation. The HCS/SCSI diagnostic is retained outside Git with SHA-256 `c2fd0b1afe6e267177df85454004c2112c9aa775d450a85c869ceda74782142c`.

**Owner-authorized HCS/WSL fallback and exact cache staging:** Local source `c695105121bfa30441b5e4cbdf6067bc329d1759` rechecks the running WSL VM, exact VHDX, guest ext4 mount, unique SCSI LUN, capacity and latest successful HCS change before recording `HCS_WSL`. Windows Storage attachment remains a supported method. 85 LAB tests and 12 B.9 validator tests passed locally. The follow-up has no merge or CI receipt.

The Owner separately approved cache staging for the four existing `linux/amd64` digests on the pinned local daemon. Vault and Caddy were pulled and verified by exact digest. Pulls of both Microsoft Container Registry images returned EOF, including bounded retries. The protected cache-stage log SHA-256 is `268d0d02522e7f15dbd18b2ca1ddc42cdb7d8381afb42b9895d7b1d0ff83ec4c`. Preflight inspected the cache.

On 2026-11-21, `d01-attempt-05` at 12:52:50 UTC and `d01-attempt-06` at 12:54:25 UTC both returned `BLOCKED` (exit 2) on clean follow-up source. Plan SHA-256 `314a70d9ebecd35f24ea5595d725ad6409c2b6ed55fd098298413a23f847c90a`. Verified JSON SHA-256 values are `9945d9badc05ae1bfc668d93710e861b5813a36bf201469caed08a843972a153` and `452fb07ebd5d8c91c83017813a8b87a5d3394345a68838a50a5373430b1cecb2`, respectively. Both reported `WINDOWS_BACKING_STORAGE=PASS` with `HCS_WSL`. The latest attempt had 40,286 MiB free on the Windows backing volume. The remaining non-PASS checks were `HOST_MEMORY=FAIL` (3,976 of 4,096 MiB), `IMAGE_APP=UNKNOWN` and `IMAGE_SQL=UNKNOWN`.

**MCR transport diagnosis:** Windows fetched the two exact MCR manifests over IPv4 and reproduced their plan SHA-256 digests. The corresponding IPv6 requests failed during TLS. The Docker Desktop proxy log showed successful CONNECT setup with direct upstream mode. An observed Docker backend TCP connection to MCR used IPv6 during the APP pull that ended with EOF. Both authorized digest pulls were retried and neither entered the exact image cache. The operator elected to keep Windows configuration unchanged. Diagnostic log SHA-256 `4369a18baa6bb6c3c94d363bca1bfd0ad557e76a781a4609c5ff428c25c59b48`.

On 2026-11-21 at 13:02:15 UTC, `d01-attempt-07` returned `BLOCKED` (exit 2) on clean source `90a809376ccfaeb32d34fd6ab2c7990d8dbcf673`. Plan SHA-256 `46e67def64910531c57f5c495dc2e29961d4b455d91a072bbfe5e33f70200ba1`. Verified report JSON SHA-256 `31b4db10a08c47079bfd9fdb16efcbae583016f0ab6ddcc1fa8c6e5bb3e07e8c`. The backing storage still passed using `HCS_WSL`. Windows free RAM fell from 4,090 MiB before launch to 3,976 MiB during collection against the 4,096 MiB floor. The only other non-PASS checks were `IMAGE_APP` and `IMAGE_SQL`, both `UNKNOWN`.

**Scoped IPv4 cache staging and laptop readiness (2026-11-21):** The Owner approved a temporary Docker Desktop Containers proxy with no restart or Windows settings changes. A loopback CONNECT tunnel forwarded IPv4 traffic only to Microsoft Container Registry and its data CDN with end-to-end TLS. Both ASP.NET and SQL Server pulled successfully and the confirmed local daemon reported the exact plan RepoDigests for `linux/amd64`. The protected pull log SHA-256 is `25de385edc8d43d3c579ad737bc411e16fd865bc3f61bc1acb0bd53c7a524c9d`. The Containers proxy returned to `Same as host proxy`, Docker Desktop remained on `System proxy`, and the tunnel stopped. Docker serialized the restored default fields, so the settings-file hash changed. The temporary endpoint was absent after rollback.

Both replays used clean source `260530120d0c0fa7195d52d608d8010fde8e020a` and plan SHA-256 `862a7fae6ff33cf00a0ed97ce8877807ea1daf60e098f31dff85c277b6067552`. At 13:12:50 UTC, `d01-attempt-08` returned `BLOCKED` (exit 2) with RAM as the only failing check at 4,024 MiB. Its verified JSON SHA-256 is `25d4f9ca127fdbc061f0a01f892c8d011e7735500e92d4bf0a83f712b473a9e7`. At 13:13:40 UTC, `d01-attempt-09` returned `READY` (exit 0) with 29/29 checks passing. Free Windows RAM was 4,129 MiB against the unchanged 4,096 MiB floor. The verified JSON SHA-256 is `9570c772f316b9a4e6293dd61d043bc193b886f8e1b833c40d32cf2f3f483b3f`. Both attempts recorded `WINDOWS_BACKING_STORAGE=PASS` with `HCS_WSL`.

**Follow-up source merge and CI:** [PR #96](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/96) merged reviewed HEAD `a9b1cc5f7b178639dfa8f43742ff1dfc86d7936a` as [`8106a0a8b19f983bc05e5e9845fc7e98f5d81bd7`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/8106a0a8b19f983bc05e5e9845fc7e98f5d81bd7) on 2026-11-21 at 13:33:59 UTC. All [24 PR jobs](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/96/checks) and 13 workflows passed on the reviewed HEAD, including [LAB-E05 Windows/Linux](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908409873/attempts/1), [LAB-I01](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908409858/attempts/1), [APP-00A](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908409885/attempts/1), [dependency/security audit](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908409831/attempts/1), and the separate [synthetic B.9 dispatch](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908405745/attempts/1). The [synthetic B.9 artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908405745/artifacts/14709196616) has GitHub archive SHA-256 `2c219278a1b680a9c47be38b9cf2ce201ee39ab3a70fb64984285a593d398d1e`. The original [Windows LAB-E05 failure](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44908156762/attempts/1) is retained. The reviewed HEAD canonicalizes the disposable Windows fixture path.

**D02 disposition:** D01 follow-up source is merged and CI verified. Attempt 09 records `READY` on its exact laptop source `260530120d0c0fa7195d52d608d8010fde8e020a`. The Owner subsequently authorized D02 source and CI only. Live Apply remains `APPLY_BLOCKED` pending a current matching preflight, a scoped enrolled plan and separate operator authorization. The preflight reports `authorization=NOT_EVALUATED`. The SQL license choice remains a separate operator decision. Original physical acceptance is `NOT_VERIFIED`. APP-02B retains `FOUNDATION_ONLY / NO_GO`.

**Native tool provenance:** Use OS-known Windows roots and verify Authenticode for Docker Desktop, CLI plugins and Windows system tools before invoking those probes. Reject conflicting shell roots and user-controlled plugin search paths. The implementation PR retains the source review and Windows regression receipts.

## 5. LAB-D02 — Guarded Compose plan/apply, networks and storage

**Source scope:** Owner-authorized source and CI on 2026-11-21, branch `codex/lab-d02-compose-boundaries`. Depends on merged D01 [PR #96](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/96) at `8106a0a8b19f983bc05e5e9845fc7e98f5d81bd7` and its closeout on main `80d2166efc9e281ccb26d814cdca88a159a0ef4c`. D01 attempt 09 is a historical laptop capability result. D02 Plan and Apply require D01 `READY` evidence bound to the exact D02 source/config/target and recheck the live context and resource conditions. The laptop has no D02 execution or operator enrollment in this increment.

**Owned paths/interfaces (proposed):** `deploy/lab/docker-wsl2/compose.yaml` (and separately reviewed overlays/templates), `scripts/lab/New-DockerRehearsal.ps1`, `scripts/lab/Test-DockerRehearsalNetwork.ps1`, `deploy/lab/docker-wsl2/network-policy.md`, `tests/app02b_lab/test_docker_compose.py` and focused shell/PowerShell tests.

- `-Plan -ConfigFile ... -OutputDirectory ...` is mutation-free; produces a source/daemon/context/config/image/volume/network/port/nonce-bound exact change set and digest, plus existing-resource inventory.
- `-Apply -EnrollmentFile ...` is opt-in and **fail-closed** on absent/mismatched preflight, target context/daemon, image digests, enrollment, ACL, target ownership, resource budget, port/network collision or changed plan. Do not self-issue approval.
- Enforce project-specific Compose name and nonce/ownership labels. Re-apply converges without replacing or erasing existing volumes; foreign or unlabeled resources are never adopted. Cleanup only exact enrolled resources after separate explicit operation; prohibit `docker compose down -v`, `docker system prune` and shared-volume deletion by default.
- Stage service topology and persistent directory/volumes for APP/DATA/VAULT without pretending SQL/Vault are provisioned in D02. Reuse B.2 API/Worker foundation behavior and `NO_GO`; missing later D03/D04 services remain `BLOCKED`, not test-success stubs.
- Default exposure none; approved edge may bind Windows `127.0.0.1` only after probing. Exclude docker.sock, privileged/host network, plaintext secrets, broad bind mounts and mutable image tags. Network policy for backend/mgmt and actual Docker Desktop/WSL host paths is mandatory.

**Verification matrix:** dry-run no mutation, first apply on nonce-owned fixture, second apply idempotent, foreign project/volume, stale plan/context/host, port overlap, network conflicts, interrupted apply, cleanup denial, and data persistence over container recreation. Network probes use actual `WINDOWS_HOST`, `DOCKER_PRIVATE_PEER` and `OUTSIDE_APP_BRIDGE` vantage labels; no `EXTERNAL_INTERNET` claim without an actual external tester. Check positive permitted edge and negative SQL/Vault/Worker/API direct sockets, forwarded headers, unintended published ports and shared-host bypass. Container network membership is **not** proof of external firewall isolation.

**Implementation and evidence:** See the [D02 operator contract](../../deploy/lab/docker-wsl2/network-policy.md) and [Compose model](../../deploy/lab/docker-wsl2/compose.yaml). The entry points bind the D01 report and plan checksums, exact source, daemon, config, images, resources and nonce. Apply requires the separately supplied enrollment and its operator-approved SHA-256, stages only stopped B.2 workloads and exposes no ports. The network verifier records Windows loopback observations separately from GitHub-hosted synthetic peer checks. Source CI covers interrupted retry, foreign ownership, idempotency and named volume persistence. The laptop state is `HOST_PLAN_BLOCKED / APPLY_BLOCKED`. D03/D04 workloads, physical acceptance and business admission have separate gates.

**Source and CI closeout (2026-11-21):** [PR #97](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/97) merged reviewed HEAD `991a932e3a14f3ef341dcad1157590346d03fc29` into main as [`1b8d6230dd61b2577e2c55b0ddbaab9030891e86`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/1b8d6230dd61b2577e2c55b0ddbaab9030891e86) at 14:03:00 UTC. The final independent source review verified per-write and final-readback freshness, the Windows reservation report behavior and the D02 workflow path filter. GitHub recorded [25/25 PR jobs](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/97/checks) and 14/14 workflows passing on that HEAD. [Tenant B4 attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947919/attempts/1) failed during LocalDB startup before SQL tests. [Attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947919/attempts/2) passed on the same source.

[LAB-D02 synthetic Linux CI](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947993/attempts/1) passed and retained [artifact 14709867023](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947993/artifacts/14709867023), GitHub archive SHA-256 `19a3f34b535f3145b8d41c275abe25a4a58806413569635056088e92daca4624`. Its private APP peer reached the test listener, DATA and VAULT peers were denied, and the shared Linux host reached the bridge directly. [LAB-E05 Windows/Linux](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947952/attempts/1), [LAB-I01](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910948084/attempts/1), [B.2 runtime](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947906/attempts/1), [APP-00A](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910947879/attempts/1), and [B.9 synthetic NO-GO](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910957586/attempts/1) passed on the same HEAD. The [retained B.9 artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44910957586/artifacts/14709279945) has GitHub archive SHA-256 `95985d418503342d5a62b5bb704ac7981cea9c3503cd696d20bdd107b922aa53`.

**Laptop Plan check (2026-11-21).** D01 attempts 10 and 11 ran on clean merge `1b8d6230dd61b2577e2c55b0ddbaab9030891e86` and returned `BLOCKED`. Free Windows RAM was 3,922 MiB and 4,033 MiB respectively against the pinned 4,096 MiB floor. All other D01 checks passed. The attempt 11 report SHA-256 is `d3adcbba60f87fef0bfd080dc2a7b271a0c7a0f4b01b02b1bfac8897f4f59413`. Both evidence manifests were verified, and D02 baseline validation separately returned `D01_BASELINE_MISMATCH` for both reports.

The read-only `desktop-linux` cache check inspected 11 images and found zero with the merge revision. The D02 `-Plan` invocation pointed to attempt 11 and returned exit 2 with `INVALID_WORKLOAD_IMAGES` before baseline validation. It emitted no plan.

The follow-up published B.2 API and Worker for Linux x64 using .NET SDK 10.0.404 and packaged them with the pinned ASP.NET base. A temporary loopback registry registered immutable digests in the local Docker cache and was removed. The API manifest digest is `sha256:e72b37bf64edae7c990e67f5f00bb614bda4ffc70ff81658a838c5b87f5d1d80`. The Worker manifest digest is `sha256:34ee0e13b161850fe48fe6d165fc56e625889b6bfe5d692469edf1597292834b`. Both inspected by digest after the registry stopped and carried the exact merge revision.

D01 attempt 12 still failed the RAM floor at 3,214 MiB. Attempt 13 returned `READY` on the exact merge with 4,225 MiB of free RAM. Its verified report SHA-256 is `067d74a531b1bd2147bd11cc232d991afd21f5be3833bc5300c72b9d4794fbd6`. D02 `-Plan` consumed this baseline and the new image refs, then returned exit 2 with `D02_DOCKER_COMMAND_FAILED`. On this Docker Desktop CLI, the combined image-inspect template fails at `.Config.Cmd`. The inspected configs omit both `Cmd` and `Volumes` when unset. A read-only reproduction using Go template `index` for those fields returned null for both images. D02 emitted no plan. A successful plan and separate operator-approved enrollment are still required for Apply. Apply was not run. LAB-D03 was `NOT_AUTHORIZED` at that attempt. Physical acceptance remains `NOT_VERIFIED`.

**Image inspection follow-up (2026-11-21).** [PR #98](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/98) merged reviewed HEAD `2cf7a6adce410ce3ef8918778cced26a9461b44a` into main as [`391aa5807a24b4dc56c9fcc2dc9739ab1203ce00`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/391aa5807a24b4dc56c9fcc2dc9739ab1203ce00) at 14:35:50 UTC. The patch handles omitted image config fields while retaining rejection of nonempty commands and declared volumes. Independent review found no merge blockers. The new regression failed before the fix and passed after it. The patched projection read both previously cached image IDs on the pinned laptop daemon. The final local LAB suite passed all 103 tests. [Synthetic D02 CI](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44913820038/attempts/1) passed. [Artifact 14709904932](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44913820038/artifacts/14709904932) records the exact PR HEAD and GitHub archive SHA-256 `ab061cf3ef9316c2fdc538046aadcc02524944480f6ad7743ce7fda5daeb3ec6`. All 25 jobs across 13 PR workflows passed on that HEAD, including the regression on Windows and Ubuntu. Three supplemental manual workflow runs on the same SHA also passed. The last laptop Plan ran on the PR #97 merge and remains `HOST_PLAN_BLOCKED`. The PR #98 merge has no laptop Plan result yet. The next Plan needs a fresh D01 READY report and API and Worker images pinned to the new merge revision. Apply remains blocked pending a successful Plan and separate operator enrollment.

## 6. LAB-D03 — Local trusted TLS, persistent Vault and secret lifecycle

**Proposed branch:** `feat/app02b-lab-d03-vault-tls`. **Dependencies:** D02 source; D02 enrolled network/volume boundaries and an approved operator-owned trust/custody arrangement for live testing.

**Source review (2026-11-21).** D03 starts at `cb7e151785473cd71a964230bec2a4554ba71474`, including the D02 staging and image correction in PRs #97 and #98. The Owner authorized source and CI only. The [operator contract](../../deploy/lab/docker-wsl2/vault/README.md) covers guarded stopped staging, private TLS, separate Raft/audit volumes, role/audit checks and delegated rotation. Hosted CI uses the immutable Linux amd64 Vault image and reports synthetic evidence on exact source HEAD. [PR #99](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/99) merged reviewed source `e0abd5b47edc89fb8bf62a59ce30274c8e356577` as `117a9e7f2d1c952f6e3b63f70cfdf5c7e6c8e189`. All 26 PR jobs passed before merge, including [D03 synthetic CI](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44921244674/attempts/1) and the separately dispatched [B.9 synthetic NO-GO](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44921251851/attempts/1). Laptop `HOST_NOT_RUN / APPLY_BLOCKED`, edge `NOT_RUN`, physical `NOT_VERIFIED`.

- Normal Vault server mode, persistent Raft storage and audit sink **outside any SQL/tenant restore path**; no dev/in-memory mode, one-share ephemeral CI unseal or implicit root tokens as lab verification.
- TLS chain and hostname verified for edge/Vault; CA signing keys and unseal/recovery material remain in protected operator custody and never enter APP containers or evidence bundles.
- Separate API/Worker/operator role-scoped revocable tokens. Probe real 200/403 and Vault audit events, wrong role/path, revoked/expired token, untrusted cert, missing audit sink, restart and durable Raft recovery.
- Token issuance/bootstrap and fault windows require actual operator approval. Do not auto-grant access on missing credentials or fall back to HTTP/`-k`/`TrustServerCertificate=true`.
- Record that one Windows/Docker host and one owner do not provide independent secret custody, off-host restore or HA. If audited off-host copy/key custody is absent, keep corresponding recovery/physical claims BLOCKED.

**Exit:** verified local TLS/403/audit/persistence evidence with source/image/volume references; still `FOUNDATION_ONLY / NO_GO` and not original B.2 runtime acceptance.

## 7. LAB-D04 — Real SQL container, A/B databases and privilege probes

**Proposed branch:** `feat/app02b-lab-d04-sql-tenants`. **Dependencies:** D02/D03 sources and live protected trust/credentials; reuse reviewed B3 Identity, B4 Tenant Registry, B5 revocation fence and I05 lifecycle scope where available. Issue #53 is CLOSED on GitHub but PR #74 remains OPEN/DRAFT; current B53 source/integration and physical revocation/PITR authorization remain independent prerequisites for any B53 operation. No cherry-pick or silent grant.

**Source review (2026-11-21).** D04 starts from D03 merge `117a9e7f2d1c952f6e3b63f70cfdf5c7e6c8e189`. Owner scope is source and synthetic CI. The [SQL operator contract](../../deploy/lab/docker-wsl2/sql/README.md) records the immutable image and edition, guarded read-only Plan, Vault-scoped credentials and trusted TLS, same-instance B.3/B.4/B.5 migrations plus A/B marker and privilege tests, and explicit live boundaries. The fixture uses the existing B.5 SQL fence only in disposable CI. Full B.4 activation/finalization, B53, PITR and business admission remain gated. Laptop `HOST_NOT_RUN / APPLY_BLOCKED`; physical `NOT_VERIFIED`.

- Pin an officially supported **Linux x86-64** SQL image by immutable digest, version, edition/CU and license/usage mode; reject ARM emulation, unsupported architecture, unreviewed tags or SQL instance mismatches. Scope to synthetic local data.
- Use persistent enrolled SQL data and distinct backup target; *never* restore/overwrite foreign DB/volume. Require stable DB name allowlist and run nonce/ownership before create/backup/restore.
- Keep B3/B4/B5 and A/B databases needed for atomic cross-DB effects on **one SQL instance**. Current authorization fence is not reset with tenant PITR; prohibit whole-DATA-container/volume rewind as accepted restore.
- TLS encryption with CA/hostname validation, Vault-provided role-specific credentials in memory. No SQL password in argv, `.env`, logs or reports; no `sqlcmd -P`, `TrustServerCertificate`, shared sysadmin/db_owner to satisfy permission checks.
- Exercise approved tenant A/B provisioning/readback, identity and role separation, cross-tenant denied queries, observer cannot DML, API cannot DDL, stale/revoked authority denied, migration idempotency and no peer fingerprint drift. Measure actual SQL effects; SQL row counts and integrity witnesses come from independent bounded read operations, not only API success flags.
- If tenant lifecycle, B53 permit, durable APP02 journal, independent fault/restore or operator admission is not implemented/authorized, keep those tests `NOT_RUN/BLOCKED` rather than inventing PASS.

**Exit:** traceable local SQL permission evidence and source/CI, not completed original `G-B01..05`, RPO/RTO, two-tenant production admission or B.9 physical approval.

## 8. Proposed milestones and acceptance labels

| Milestone | Exact condition | Max permissible claim |
|---|---|---|
| D0 — roadmap review | This docs PR reviewed, accepted and merged | `DOCKER_REHEARSAL_ROADMAP_REVIEWED` (documents only) |
| D1 — measured host | Actual Windows Docker/WSL2 preflight, no required blocker; operator-approved resources | `DOCKER_HOST_PREFLIGHT_OBSERVED` |
| D2 — guarded foundation | D02 source/CI, separately authorized local Apply, idempotency and negative probes | `DOCKER_FOUNDATION_OBSERVED` (not VM readiness) |
| D3 — trust layer | D03 TLS/Vault persistence/denials with protected actual evidence | `DOCKER_VAULT_OBSERVED` |
| D4 — SQL and tenants | D04 real supported SQL, B3/B4/B5 bounded tenancy and rights, negative probes, exact-source CI; no missing required D-track check | Proposed `DOCKER_REHEARSAL_READY` after Owner adopts that nonphysical label |
| Existing VM M1 / physical B.9 | Separate VM/VPS host and original gate evidence from the canonical 32-increment roadmap | **Never inferred from D00–D04** |

`SOURCE_REVIEW` means implemented PR open; `SOURCE_MERGED` requires an actual merge. `HOST_OBSERVED`/`LAB_EXECUTED` need current original evidence. D-track failures remain FAIL/BLOCKED/NOT_RUN with reasons. No source-only PR may update B.9 physical gate to PASS.

## 9. Verification, evidence and closeout protocol

For **every** D increment:

1. Begin on current `main`, identify dependency merge SHAs, frozen source/commit, toolchain lock and exact scope. Separate source development from privileged live execution. Never alter the frozen B.9 expected fixture.
2. Use independent technical review (separate Codex context or reviewer); classify findings, resolve blocking ones and run meaningful negative tests. AI review is not independent human Security/DBA approval.
3. Run focused Python/PowerShell/Compose tests and applicable .NET/APP-00A/B.2/E05/B.9 tests; validate the retained GitHub workflows on the **final PR HEAD**. A `git diff --check` and docs links check are additionally mandatory for D00.
4. Protected evidence lives outside every Git worktree. Record SHA, config/image digests, Docker context/daemon identity, operation nonce, environment class, observed target, UTC, test IDs, failed attempts, exit/status, data redaction and actual vantage. A Docker stdout message alone is not an independent witness. Never upload tokens/private keys/raw host identifiers.
5. Run/attempt history is append-only; a green retry does not erase a prior FAIL. Publish only redacted summaries in PR, with protected raw evidence under operator custody and a separately retained checkpoint if E05's integrity model is used.
6. Keep a canonical section/row per increment and PR metadata for final source H, source checks, later merge commit M, approved operation and actual host results. Follow [existing closeout policy](app-02b-local-preprod/codex-handoff.md#51-increment-closeout); no recursive docs PRs acknowledging earlier docs merges.
7. All source changes require their **own** D01/D02/D03/D04 PR. Report `NOT_AUTHORIZED` if Owner has approved roadmap review only. Do not auto-merge, auto-deploy or close original #53/#56/#75 due to local PASS.

## 10. Risks and handoff

| Risk | Required guard / owning increment |
|---|---|
| Acting on unexpected Docker daemon/context | D01 host+context identity, D02 atomic revalidation before Apply |
| WSL2/host resource contention | D01 measured budget; D02 bounded resource/volume and no global daemon cleanup |
| Wrong port/network/false external origin | D02 loopback default, network/negative probes with actual vantage |
| Shared-host loss of SQL/Vault/audit | D02–D04 persistent distinct storage; explicitly record shared failure domain and missing off-host copy |
| Compromised token/CA or untrusted TLS | D03 protected trust and role-scoped revocation; D04 SQL encrypted verified transport |
| Cross-tenant/rollback permission drift | D04 same-instance current authority, independent SQL readback; #53 remains a separate prerequisite |
| Physical evidence mislabelled as approved | D00 decision boundary, all D PRs and original B.9 unchanged |

**Next executable step:** review the dedicated LAB-D01 source PR and collect the read-only host receipt on the intended laptop with the protected operator plan. D02 implementation and Apply retain their separate authorization gates.
