# APP-02B.2 — Versioned CI-only Acceptance and Merge Addendum v1 (2026-11-17)

**Status: OWNER_APPROVED LIMITED ACCEPTANCE POLICY / IMPLEMENTATION ACCEPTANCE PENDING.** Authorized only by [OWN-APP02B2-01..03](../decisions/owner-app02b2-2026-11-17.md), for [PR #50](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/50). This is an explicit B.2-only *alternative merge disposition*; it does not alter the requirements or results of the frozen [B.1 30-case contract](app-02b1-acceptance-evidence.md), [threat model](app-02b1-threat-model.md), or APP-02B.9.

## 1. Evidence classes must never be conflated

| Evidence class | May substantiate | Must NOT substantiate |
|---|---|---|
| `CI_EPHEMERAL_DOCKER` | Concrete GitHub-hosted Ubuntu container start/stop, Caddy HTTPS, separate API/Worker processes, real Vault server-mode TLS + ACL + audit, fault/revocation probes, observed Docker runtime ports and negative mutations | Operator-owned VPS/VM, Internet-origin port scan, independent infrastructure trust, long-lived Vault governance, actual two-tenant PREPROD data, recovery RPO/RTO or production |
| `CI_SYNTHETIC_SQL_REGRESSION` | Existing APP-01E.5 and APP-02A.4 test evidence on LocalDB in CI | Real PREPROD tenant identity/authorization, G-B01/02/03/04/05 PASS, real AC-11 / EV-08 |
| `INDEPENDENT_AI_TECHNICAL_REVIEW` | Independent fresh-context analysis of actual code/negative evidence and zero open blocking technical findings on an exact HEAD | Human Security Approval, operator/second-person attestation, an authenticated independent security principal |
| `ACTUAL_PREPROD_EVIDENCE` | Real operator-owned host/network, independent external probes, durable Vault/operator audit and physical tenant/restore observations **if and only if actually collected** | May not be assumed from any CI-runner artifact; currently NOT_VERIFIED |

## 2. CI-only PR #50 acceptance matrix

Every required item needs an explicit `PASS` witness on the same exact source HEAD. A `PENDING` or `FAIL` result forbids the CI-only merge.

| ID | Minimum mandatory witness | Rejection/negative |
|---|---|---|
| **CI-B2-01** | Ubuntu GitHub-hosted runner builds distinct API/Worker OCI images; `FOUNDATION_ONLY / NO_GO` health only | SyntheticAuth, App02Worker, development signing key, wrong environment/mode fail startup; no real business API, tenant SQL routing or Worker claims |
| **CI-B2-02** | Caddy edge TLS verified with ephemeral CA; live `/health/live` and `/health/ready` HTTPS 200 with admission `NO_GO` | Wrong CA rejected, forbidden/admin/business/diagnostic routes 404; forged headers and direct API bypass denied |
| **CI-B2-03** | Docker inspect all FOUR live services; edge only `127.0.0.1:443/tcp`; API/Worker/Vault have zero published host ports; correct network attachments | Injected public API port and edge `0.0.0.0:443` each independently fails the inspector |
| **CI-B2-04** | Vault server-mode (NOT dev) initializes/unseals with runner-local ephemeral secrets; HTTPS KV-v2 with separate API/Worker token-file grants, Vault audit witness | API→Worker, Worker→API, API→Vault-admin operations return actual 403; no token values in repo, images, artifacts, CLI arguments or Docker exec metadata |
| **CI-B2-05** | Token rotation preserves valid API readiness; revoked current token makes readiness 503; Worker revocation yields nonzero stop; Vault outage yields 503 | No plaintext fallback, stale/revoked token is never authorization; redacted audit corroborates denied operations |
| **CI-B2-06** | Full Windows regression and Linux runtime CI SUCCESS, required tests all PASS with zero failures/skips, APP-01E.5 / APP-02A.4 physical LocalDB source reconciliation and PWA/Playwright retained | No skipped or weakened test, altered baseline, superseded HEAD, missing artifact digest, or synthetic-to-PREPROD status promotion |
| **CI-B2-07** | Independent AI technical reviewer in a **separate, fresh context** reads actual final diff, contracts and artifacts and records findings/dispositions against the exact HEAD; unresolved blocking findings = 0 | No same-context self-review accepted as independent; no AI report called a human approval; changing HEAD resets review |
| **CI-B2-08** | Final PR body/README/roadmap/closeout document exact commit, TestRunId, Linux/Windows workflow URLs + attempt, artifact names and digest, exception Owner decisions, remaining actual PREPROD and G-B gates | Do not merge if any dependency/production claims or evidence provenance are false, stale or incomplete |

At authoring, all eight rows are **REQUIRED / FINAL-HEAD ACCEPTANCE NOT_YET_ASSERTED**. Historical run #44099147282 (Linux) and #44099147291 (Windows) passed on source `99fa6fbdecd3e458b068f52518638cf60a04a1b1` **before this documentation change**; those passes are historical references only and cannot satisfy the subsequent HEAD.

**Provisional documentation and dynamic lineage:** [B.2 closeout candidate](../closeout/2026-11-17-app-02b2-ci-pending.md) links historical evidence but explicitly remains PENDING. The final PR discussion/body must carry the exact latest source/CI run attempts and immutable artifact content hash after final workflow completion; a static commit cannot embed its own future CI SHA without invalidating itself.

## 3. Independent AI review handoff

**Separate-context reviewer instruction** (for an independently opened review session, not to be self-satisfied by this implementing session):

> Review PR #50 in `chung6a8m/fmcg-distribution-dotnet` independently. Fetch current PR HEAD and the full diff; independently read APP-02B.1 architecture, TM-01/02/10/11, 30-case contract, `owner-app02b2-2026-11-17.md` and this addendum. Review real code in `src/FmcgDms.Preprod.*`, the Caddy/Compose/Vault config, scripts, CI runtime outputs, redacted Vault audit and Docker live-port evidence, negative mutations and scope/no-production boundaries. Disregard the implementer's prior review disposition as proof. Report concrete findings with file/line/severity, separate observed facts from assumptions, check that GitHub-hosted CI is never mislabeled real PREPROD, and record reviewed HEAD and both exact-head CI runs. Request fixes for blocking findings; re-review any changed final HEAD. Output `INDEPENDENT_AI_TECHNICAL_REVIEW`, never Human Security Approval or `PREPROD_RUNTIME_VERIFIED`.

The review record must contain `ReviewerContextId/DateUtc/SourceHeadSha/ReviewedPaths/Findings/OpenBlockers/EvidenceRefs/Disposition`. These are review-provenance fields, **not** proof a separate human reviewed. An AI report may be appended to PR discussion; do not manufacture a distinct GitHub reviewer identity or automatically mark `APPROVED` for a person.

## 4. Merge / dependency state machine

| Preconditions | Allowed PR #50 state | Actual PREPROD state |
|---|---|---|
| Owner Decisions approved, CI implementation present, independent fresh-context AI report or final CI absent | `DRAFT / CI_SCOPE_ACCEPTANCE_PENDING` | `PREPROD_RUNTIME_NOT_VERIFIED` |
| All CI-B2-01..08 PASS on same final source HEAD, no blocking findings, independent AI technical report archived, technical scope remains `NO_GO` | **`MERGED / EPHEMERAL_CI_RUNTIME_TESTED`** permitted, not automatic | `PREPROD_RUNTIME_NOT_VERIFIED` |
| Later operator-owned PREPROD has independently verified original B.1 RUNTIME-01/02 requirements | `PREPROD_RUNTIME_VERIFIED` may be assessed only under its separate real-host evidence/approval | **NOT EARNED by this PR** |
| Any B.9 or production mode gates | `NOT_RUN / NOT_VERIFIED / NO_GO` | No waiver by CI-only merge |

**Non-negotiable:** preserve all G-B01..05/30 B.9 cases, original production P0 G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV, actual EV-08/real AC-11, B_NO_CHANNEL blocked, no business posting, no Pilot/Go-Live/APP-02C/D/E automatic authorization. CI-only release is a source-code integration milestone, **not a security certification or deployment decision**.
