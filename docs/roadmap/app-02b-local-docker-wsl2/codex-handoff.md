# LAB-D00–D04 — Codex handoff for Docker Desktop / WSL2 rehearsal

**State:** `PROPOSED / DOCS_ONLY`. This file is a handoff for future **separately authorized** increments, not an instruction to mutate the current Windows laptop. First read [LAB-D roadmap](../app-02b-local-docker-wsl2-rehearsal.md), [proposed ADR](../../decisions/proposed-app02b-docker-wsl2-rehearsal-2026-11-21.md), [APP-02B VM design](../app-02b-local-preprod/design.md), [VM I01/I02](../app-02b-local-preprod/01-platform-and-recovery.md), [original B.9](../app-02b9-independent-preprod-oracle.md) and [existing Codex closeout protocol](../app-02b-local-preprod/codex-handoff.md#51-increment-closeout).

## 1. Universal guardrails

- Repository: `chung6a8m/fmcg-distribution-dotnet`, base `main`. Confirm current tip and dependency PR merges at task start; PR #93 merge `4bb30c342b50d73af7f66f509202d275af6d4809` is a historical reference, not a permanent HEAD.
- One D increment = one implementation PR: `feat/app02b-lab-d01-docker-preflight`, `feat/app02b-lab-d02-compose-boundaries`, `feat/app02b-lab-d03-vault-tls`, `feat/app02b-lab-d04-sql-tenants`. D00 is this docs-only proposal PR.
- The user reports that the laptop has Docker and WSL2 but cannot host the planned three Hyper-V VMs. Verify the actual Docker backend/context and host before calling any result PASS. Never interpret an I01 exit 2 as a Docker failure or relax the Hyper-V contract.
- Do not create Docker infrastructure or change host settings without an independently operator-provided, exact-source/config/target/nonce-bound authorization. A prose roadmap, `-Plan`, a GitHub CI token, or the model's own judgment cannot create an operator authorization.
- Never mutate existing Docker Desktop contexts, WSL distributions, images, volumes, networks, firewall/NAT, host directories or live DBs outside the enrolled project. `docker system prune`, `compose down -v`, wildcard `rm` and unbounded cleanup are forbidden in default scripts.
- Keep all immutable business/oracle files and source guards unchanged. `LOCAL_OPERATOR_LAB / SOLO_OWNER_SELF_ATTESTED` is not independent human physical review. Original B.9 30 cases and RUNTIME-01/02 remain `NOT_VERIFIED` until separately satisfied.

## 2. Preliminary read-only baseline (the only immediate host work)

On the **intended Windows laptop** after separately confirming the repo checkout is clean, read or collect the following (these commands do not install/reconfigure anything):

```powershell
git status --short
git rev-parse HEAD
git merge-base --is-ancestor 4bb30c342b50d73af7f66f509202d275af6d4809 HEAD
pwsh --version
wsl.exe --status
wsl.exe --version
wsl.exe --list --verbose
docker version
docker context show
docker context ls
docker info
docker compose version
```

**Redaction warning:** full `docker info` / `docker context ls` can disclose local context names/paths/endpoints. Keep original stdout in operator-protected storage and publish only sanitized facts (OS/arch, backend, resource summary, daemon reference hash, version); do not paste raw host identity, context URL, private addresses or personal paths into a public PR.

If Codex runs in cloud/Linux or GitHub-hosted CI, it may write/test the source implementation but **must not** claim those probes describe the owner's Windows laptop. Mark `HOST_NOT_RUN` until an on-device run is available.

## 3. First implementation prompt (LAB-D01)

> Implement **LAB-D01 only**, after the Owner explicitly authorizes D01 source implementation. Read `AGENTS.md`, the D-track roadmap/ADR/handoff, LAB-E05 contract, the Windows LAB-I01 source to reuse safe patterns, and the current Docker Desktop/WSL official references.
>
> Start from current clean `main` with the approved D00 docs branch merged; record base SHA and project source boundaries. Create an independent feature branch and a source-only PR.
>
> Build a **strictly read-only**, schema-versioned Docker Desktop/WSL2 host preflight for the actual Windows operator environment: supported Linux Docker engine/context, WSL2 backend, CPU arch (explicit SQL x86-64 compatibility), toolchain, RAM/disk/data-root limits, networks, ports, Compose projects, volumes and image signatures/digests. Accept only a validated nonsecret outside-repo plan; emit redacted JSON/Markdown with exact source, engine context and operator-held hashes. Exit 0 only when all required checks truly pass, 1 on invalid input/collection error, 2 on required FAIL/UNKNOWN.
>
> Test Docker daemon absent, WSL1, Windows-container mode, wrong/remote context, ARM translation, insufficient resources, conflicting ports/projects/volumes, unpinned/unknown images, unsafe output paths, secret-bearing input, missing privileges and malformed JSON. No Docker pull/up/create/stop, installs or WSL changes from preflight.
>
> Reuse LAB-E05 evidence rules only through explicit adapters. Do not change LAB-I01 schema or status and do not forge approval. A host report is not LAB-E05 protected admission. Document command examples, toolchain constraints, redaction and operator custody.
>
> Independently review the diff, fix findings, run Python/PowerShell focused tests plus relevant retained CI on exact final HEAD. Open a reviewable PR with true source/CI and host evidence tiers. **Do not merge or proceed to D02 Apply automatically.**

## 4. Subsequent increments: entry and stop contracts

| Increment | Source PR may start when | Live Apply must refuse unless |
|---|---|---|
| LAB-D02 | LAB-D01 source/contract merged | Valid laptop D01 report, same Docker daemon/context, enrolled plan digest, project ownership, image trust, safe bounded network/port/volume, explicit operator change authorization |
| LAB-D03 | LAB-D02 source/contract merged | D02 enrolled sandbox and protected TLS issuer/key custody, Vault audit/storage/role plan and operation approval |
| LAB-D04 | LAB-D02 + D03 source contracts merged; B3/B4/B5 dependencies mapped | Real live D03 trust, supported x86-64 SQL image/license, enrolled exact instance/DB allowlist, operator credentials/approval, safe versioned migration and no foreign data |

A D01 host PASS does not prove D02 network security; D02 storage/network PASS does not prove Vault/TLS; D03 Vault PASS does not grant tenant rights or B53 permission; D04 test PASS does not satisfy physical B.9 or replace a missing independent human reviewer.

## 5. Verification and evidence tiers

**Existing source checks to consider** from repo root (inspect current workflow command owners and add project-specific test commands as needed):

```powershell
python -m unittest discover -s tests/app02b_lab -p "test_*.py" -v
python -m unittest discover -s tests/app02b9 -p "test_*.py" -v
dotnet test --project tests/FmcgDms.Preprod.Runtime.Tests/FmcgDms.Preprod.Runtime.Tests.csproj -c Release -- --report-trx
dotnet test --solution FmcgDms.slnx -c Release --minimum-expected-tests 296 -- --report-trx
```

These are **references for later implementation**, not a declaration that they ran for D00. The solution alone does not cover all APP-02B projects. CI must preserve security npm audits, APP-00A, B.2 runtime, LAB-E05, B.9 negative oracle and applicable new D-track checks on the final **source** HEAD; path-filtered or missing required workflow is not a PASS. No physical claim from a GitHub runner.

Recommended protected records (outside every worktree): `docker-preflight.json`, `docker-preflight.md`, plan/enrollment digests, exact source, context/daemon SHA-based opaque reference, image digests, UTC, attempt IDs, test result and redacted witness list. Keep raw evidence + signatures under approved operator custody; reuse E05 append-only journal/checkpoint only after explicit source-bound compatibility review. Never rewrite an earlier failed run after a retry.

For D02/D03/D04, record actual vantage (`WINDOWS_HOST`, `DOCKER_PRIVATE_PEER`, `OUTSIDE_APP_BRIDGE`) and positive/negative socket observations. A probe from inside the same bridge is not an outside observer, and a `127.0.0.1` port denial does not prove a LAN or internet boundary. Health `FOUNDATION_ONLY / NO_GO` is the expected safe result until separately authorized business runtime exists.

## 6. Definition of Done for each PR

- Diff strictly scoped to the assigned increment. Include threat/ownership analysis, non-secret config examples, exact interfaces/schema, positive and meaningful negative tests, and safe rollback/no-delete defaults.
- All findings resolved or explicitly documented with decision and scope; source-only vs live evidence labeled truthfully. Run `git diff --check`, verify links and actual retained CI on final source HEAD.
- Update the increment's **existing** D-track roadmap row/section, PR problem/change/verification/evidence/remaining-gates and dependency SHA. Before GitHub merge: `SOURCE_REVIEW`. Only after merge confirmation: `SOURCE_MERGED`. Don't create recursive merge-acknowledgement PRs.
- If no laptop access or missing approval: finish only independently reviewable source work, mark `HOST_NOT_RUN` / `APPLY_BLOCKED` and provide a specific operator handoff; do not fabricate host measurements.
- Do **not** auto-merge, close #53/#56/#75, set `LOCAL_LAB_READY`, claim production/B.9 acceptance, or implement D02–D04 in the D01 PR.

## 7. Handoff checklist to the Owner

Report: current source SHA; link to independent PR; tests and CI run/attempts; exact Docker/WSL2 host preflight status (or `NOT_RUN`); actionable reason-coded blockers; proposed operator approval/change plan; protected evidence hashes and location references (redacted); next increment and its separately required authorization. State `DOCKER_REHEARSAL_READY` only under a later explicitly approved definition and **actual** D01–D04 live evidence, never from docs or CI alone.
