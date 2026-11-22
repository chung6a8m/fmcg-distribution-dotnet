# Owner Decision — APP-02C development before physical operations acceptance

**Decision date:** 2026-11-22 (Asia/Bangkok). **Disposition:** `OWNER_APPROVED / C_DEV_AUTHORIZED`. **This change:** documentation only; C.1–C.5 implementation and integrated DEV verification are `NOT_STARTED / NOT_RUN`.

## 1. Authority and intent

The Owner requested that unfinished operations work be treated as technical debt so application/business development can continue. After reviewing the proposal to separate development from operational acceptance, the Owner explicitly instructed: **“Tôi đồng ý với đề xuất trên, bạn hãy điều chỉnh roadmap, tài liệu cần thiết để có thể bắt đầu APP-02C”.**

This is the new timing/scope decision anticipated by section 2 of the [2026-11-19 C.0 decision](owner-app02c-2026-11-19.md). It expressly replaces the blanket requirement for `PREPROD_FOUNDATION_VERIFIED` before C.1+ development. It authorizes the named C.1–C.5 increments in the environment below, including integrated execution; it is broader than an inert source/CI-only exception. It does not assert that the implementation or environment already exists.

The approved business direction remains Customer as debtor, optional Outlet, flexible validated ShipTo, explicit Confirm → Reserve, atomic Load, per-Delivery stock/COGS/AR, frozen commercial terms and source-bound reporting. The canonical `business/` directory and `business.lock.json` remain unchanged.

## 2. Exact scope of the new decision

| ID | Approved disposition |
|---|---|
| **OWN-APP02C-DEV-01** | Develop **C.1–C.5 sequentially**, one independently reviewable PR per increment, without waiting for B.9 physical acceptance, LAB-I/D completion, Vault, physical reviewer staffing or full operational recovery/monitoring evidence. This is standing DEV authorization for these named increments; do not repeatedly request the same blanket C-DEV permission. |
| **OWN-APP02C-DEV-02** | Permit application source, forward migrations for owned DEV databases, meaningful tests, UI changes and actual **UI → API → SQL** execution using synthetic business data. Each increment adds a usable part of the existing app; C.5 completes workspace/reporting integration rather than postponing all UI until the end. This documentation PR does not implement those features. |
| **OWN-APP02C-DEV-03** | Preserve server-validated identity/tenant/permission checks, command identity and replay protection, transaction/concurrency guarantees, immutable snapshots and source/ledger reconciliation. An applicable correctness or security defect is owned by the feature that encounters it, not automatically waived as operations debt. |
| **OWN-APP02C-DEV-04** | Resolve C0-R01–R04 within the increment that needs each decision, before implementing dependent behavior. R05 is a physical acceptance dependency. R06 retains reviewed expected results and negative cases per feature, while physical witness/signature/custody requirements apply at physical acceptance. Unrelated unresolved rows do not stop the whole roadmap. |
| **OWN-APP02C-DEV-05** | Track deferred operations in the [operations debt register](../roadmap/app-02c-operations-debt.md), with an owner, concrete impact, re-entry trigger and closure evidence. Preserve failed/not-run evidence. HashiCorp Vault is **not required for DEV**; returning to Vault is not a mandatory repayment obligation. A suitable secret provider can satisfy the capability. |
| **OWN-APP02C-DEV-06** | Keep development permission, implementation progress, DEV test results, physical acceptance and production readiness as separate states. C-PREPROD/C.6, live commercial data, production, Pilot and Go-Live retain their own admission decisions and evidence. No new claim of B gate PASS, RPO/RTO achievement, EV/P0 or production readiness follows from this decision. |

## 3. Permitted DEV environment and implementation obligations

### 3.1 Starting target

- **Initial target:** the developer's Windows environment and existing CI, using dedicated, synthetic, nonce-owned SQL Server LocalDB databases under the repository's current `Fmcg_App00_<nonce>_A/B` ownership/name/instance protections. Use the actual API and SQL persistence. Two test tenants are required for isolation checks where applicable.
- Existing source at `6041cb38bd11a2aa6e6764c5bebc18b35011f055` is not a ready C runtime: the connection factory/migrator admit only their guarded synthetic target, PREPROD composition contains no C business routes, and current UI browser tests do not prove a live API/SQL business flow. **C.1 must supply and verify the minimum DEV composition and browser/API/SQL harness it needs.**
- Preserve existing target refusal, ownership marker and nonce protections. Do not remove an environment check, accept a request-selected connection string, or enable operational PREPROD routes merely to make DEV work. Any additional DEV composition must be explicit, reviewed and tested to refuse non-owned/wrong-instance/live targets. This decision does not authorize a new Docker SQL, shared VPS or production target.
- DEV data may survive a developer session in the owned DEV target where the reviewed lifecycle allows it; reset/drop remains explicit and guarded. No production/customer database may be selected, cloned or reset under this scope.
- Only synthetic users, tenants, stock and commercial fixtures are admitted. Source-controlled examples must contain no real secrets or customer data. DEV credentials are provisioned outside source; tenant selection and permission decisions remain server-authoritative. Current API/Worker security checks must be wired for the used path, not replaced by a permissive stub.

### 3.2 Lightweight dependencies

The initial flow needs the existing web app, API and SQL; start the Worker only for a feature that actually needs it. DEV secrets may come from a local secret/configuration mechanism kept outside source, with restricted access and no secret logging. Provisioning this mechanism and testing configuration failure belongs to the consuming increment; a running Vault service and signed physical custody evidence are not prerequisites.

The VPS planning constraint is **approximately 2 GB RAM remaining after SQL Server** for all other components. It is a later deployment/capacity constraint, not a measured capacity result or permission to deploy this DEV profile to that VPS.

### 3.3 Commercial policy and review

Use explicit, versioned synthetic fixtures through the relevant policy/approval validation path. Fixture amounts, terms, addresses and timezones are test inputs, not approved live defaults. Missing/expired/unapproved required policy still fails closed. Existing `SYNTHETIC_UNITLESS`, invoice ineligibility, no offline business writes and B_NO_CHANNEL restrictions are retained wherever applicable.

A developer/reviewer may settle routine technical choices consistent with the approved baseline and record the reason. A genuinely unresolved business choice that changes behavior is raised with the Owner for that affected subtask; work on independent parts continues. Do not invent commercial values or mark a C0-R row closed without its contract and evidence.

Independent code/SQL/fixture review remains part of feature verification. It does not require recruiting B.9's physical Security/DBA observers before writing a feature, and must not be described as an independent human physical attestation when it is automated or agent-assisted.

## 4. Development completion and operations acceptance

| Boundary | Required evidence / effect |
|---|---|
| Start a C increment | This standing authorization, completed predecessor dependencies, and the [DEV handoff](../roadmap/app-02c-dev-handoff.md). Start with the applicable contract work; no global PREPROD prerequisite. |
| Implement dependent behavior | Record the relevant C0-R disposition, business interpretation and expected tests before that behavior is accepted. Unresolved material business semantics block that behavior, not unrelated code. |
| Merge a feature PR | Review the final source, pass required CI and relevant feature/regression checks, inspect SQL/browser evidence and report actual failures/skips. A relevant failure cannot be reclassified as operations debt to obtain a green result. |
| Claim integrated DEV verification | Run the actual scoped UI/API/SQL path, including the relevant negative cases, and retain source SHA, environment/data scope, expected/actual results and artifacts in that increment's closeout. A mocked UI or HTTP success alone is insufficient. |
| Enter physical PREPROD / C.6 | Apply the existing C-PREPROD/B physical admission contract, or an explicitly reviewed replacement suited to the deployment. Deferred debt is re-evaluated here. This decision neither supplies physical evidence nor automatically accepts a replacement. |
| Admit real data / Pilot / Go-Live | Separate operational suitability, commercial policy and Owner/Tenant acceptance. DEV verification alone is insufficient. |

Existing security-risk acceptances keep their original subject, environments, expiry and artifact-publication limits. In particular, the earlier Issue #59 build-tool acceptance is not extended, renewed or converted to deployment permission here. Dependency checks and security review remain applicable; any newly affected execution/publication path must be assessed before use. A vulnerability affecting the feature under review is not automatically deferred.

## 5. Precedence, history and immediate handoff

For **C.1–C.5 DEV timing and authorization**, this decision supersedes contradictory blanket `NOT_AUTHORIZED`, “all C0-R blockers first”, “inert source only” and “physical PREPROD before any code/UI/migration” statements in older C/B/UI planning documents. It does not rewrite their historical evidence or expand their original attestations. Physical/environment-specific controls and business invariants continue to apply in their own scopes.

Sales A UI work needed by C.1–C.5 is included in this DEV scope. [Open PR #86](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/86) is a prior UI-04 readiness proposal, not an implemented feature or prerequisite for C.1. Reconcile/rebase it against this decision when that PR is handled; do not silently treat it as merged or approved. UI-05 and adjacent APP-02D/E scope are not added here.

**Next implementation:** C.1 Customer / Outlet / Flexible ShipTo, including its applicable R02 contract work and minimum integrated DEV wiring/UI. Read the [updated roadmap](../roadmap/app-02c-a-online-e2e.md), [contract](../roadmap/app-02c0-a-online-e2e-contract.md), [DEV handoff](../roadmap/app-02c-dev-handoff.md) and [operations debt register](../roadmap/app-02c-operations-debt.md). This docs PR records authorization; it does not certify C.1 completion or run any host Apply operation.
