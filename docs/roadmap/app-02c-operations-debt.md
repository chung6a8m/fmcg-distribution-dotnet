# APP-02C — Deferred operations and development boundaries

**Disposition:** Owner-approved prioritization on 2026-11-22 under [OWN-APP02C-DEV-01..06](../decisions/owner-app02c-dev-first-2026-11-22.md). C.1–C.5 development proceeds in the permitted DEV environment. This register is not an operations PASS, an issue-closure record or a blanket security waiver.

## 1. Decision rule

If an unresolved item can cause the feature being built to record incorrect stock/COGS/AR, process a command twice, mutate historical terms, or access the wrong tenant/actor scope, handle it in that feature before accepting the affected behavior. If it concerns deployment topology, physical evidence, operations staffing, full monitoring or recovery measurement, apply its trigger below. A concrete dependency must be named to block a feature; “B.9 is not done” is not sufficient for C.1–C.5 DEV.

**Owner for the register:** project Owner, Phạm Chung. The implementing developer maintains affected entries in the substantive feature/operations PR; the reviewer checks classification and evidence. Do not create a separate sequence of acknowledgement-only PRs or label an unrun item complete.

## 2. Deferred operations register

| ID / item | Current disposition and effect | Re-entry trigger | Closure evidence and responsibility |
|---|---|---|---|
| **OPS-C01 — Full LAB topology** | `DEFERRED_FOR_DEV`. Unfinished Hyper-V and Docker/WSL2 work does not block C features. Preserve the LAB registers and failed/capacity observations; do not call the original topology ready. | Select/revise an operational test or deployment topology. | Owner chooses the applicable topology; operator records resource measurements and actual host/network checks. Original LAB evidence remains traceable. |
| **OPS-C02 — Secret service / Vault** | Vault is `NOT_REQUIRED_FOR_DEV`. Implement a bounded local secret provider outside source in the consuming increment. The missing production secret-management capability is deferred; restoring the Vault product is not mandatory. | Shared/remote environment, real credentials, rotation/custody requirements or deployment. | Owner/operator select the provider; verify access, non-logging, replacement/rotation and recovery appropriate to that environment. Local DEV configuration checks are part of the feature, not deferred. |
| **OPS-C03 — Physical recovery and revocation after restore** | Physical PITR/off-host recovery and measured RPO/RTO are `NOT_VERIFIED / DEFERRED_FOR_DEV`. Current runtime actor/revocation checks remain required in DEV; no claim that restart/row-image tests prove physical PITR. | C-PREPROD/C.6, restore of an operational environment, or admission of real data. | Operator and applicable reviewer execute the recovery/authority checks required by the selected acceptance contract, including no revival of revoked access. Owner decides any replacement targets; the prior targets are not silently deleted. |
| **OPS-C04 — Full operational telemetry** | Full alerting/retention/external audit anchoring can wait. Feature-level error reporting, command/source correlation and business audit needed to explain a write remain in scope. | Sustained shared usage or operational acceptance. | Operator verifies incident detection and useful diagnostics/retention; business audit and no-secret logging are checked by the feature reviewer now. |
| **OPS-C05 — Physical reviewer independence and custody** | C0-R05 is deferred to the physical gate. It does not block code, a feature review or a synthetic DEV run. | A claim of B.9 physical PASS or C-PREPROD/C.6 admission. | Owner arranges the required actual reviewers/custody or records an explicit reviewed replacement. Never manufacture multiple human attestations. |
| **OPS-C06 — Physical integrated acceptance** | Physical B.1/B.9 cases, RUNTIME-01/02 and C.6 remain unverified/not run as recorded in their source evidence. Feature expected results and DEV integration testing continue separately. | Request to admit the operational PREPROD runtime or claim physical C E2E. | Approved applicable matrix, actual expected/SQL-observed differences, source/artifact/environment lineage and required reviewers. A closed GitHub issue or a source merge alone supplies no physical result. |
| **OPS-C07 — VPS capacity and deployment automation** | Approximately 2 GB RAM is available for everything **except the already-running SQL Server**. No fit/load result is claimed. Capacity tuning and full deploy/rollback automation are deferred. | Choose the VPS deployment profile after a usable business slice exists. | Operator measures process memory, workload and restart/rollback behavior; Owner accepts the resulting deployment limits. Do not run the full LAB solely to pass a DEV feature gate. |

## 3. Items that remain with application development

| Item | Required treatment |
|---|---|
| C0-R01 Confirm → Reserve semantics | C.2 defines and tests confirmed-unreserved/partial/full states and compatibility with historical APP-01 behavior. |
| C0-R02 ShipTo validation/amendment | C.1 records the data contract and version/snapshot boundaries before implementing dependent behavior. Independent Customer work can proceed while a material address choice is resolved. |
| C0-R03 policy selection and business date | C.2 owns policy selection/snapshot semantics; C.4/C.5 own due-date/report effects. Explicit synthetic fixtures do not approve live commercial values. |
| C0-R04 partial logistics and conservation | The increment touching reservation counters, Load or Delivery owns its reviewed quantity/source equations, concurrency and no-double-consumption checks. |
| C0-R06 feature expectations | Each increment records reviewed/versioned expected results and negative cases before acceptance execution; the physical witness/custody part belongs to OPS-C05/06. |
| Identity, tenant, permissions, replay, transactions | Use and test the actual server/SQL path for the feature. Missing wiring is a feature dependency, not permission for an allow-all adapter. |
| Dependency/security findings and CI failures | Assess their actual affected code/environment. Retain all original risk-policy scopes/expiry/publication limits; neither “operations debt” nor this new DEV decision automatically accepts a finding or a failed required check. |

## 4. Lightweight maintenance and release distinction

For an affected entry, update its disposition, next trigger and evidence link in the same substantive PR. Add a new entry only when it records a distinct capability or risk. Keep implementation progress in the owning C increment, not in a second competing status register.

Before operational admission, revisit this register against the actual target and complete or explicitly revise the applicable obligations. Until then, use separate labels: **C DEV authorized**, **feature implemented/reviewed**, **DEV execution verified or not run**, **physical acceptance not verified**. No status automatically promotes another.

References: [C roadmap](app-02c-a-online-e2e.md) · [C contract](app-02c0-a-online-e2e-contract.md) · [DEV handoff](app-02c-dev-handoff.md) · [B foundation](app-02b-preprod-foundation.md) · [B.9 source evidence](../closeout/2026-11-18-app-02b9-source-only.md) · [LAB roadmap and register](app-02b-local-preprod-acceptance.md).
