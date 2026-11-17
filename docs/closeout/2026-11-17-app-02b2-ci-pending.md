# APP-02B.2 — CI-only source merge closeout candidate (2026-11-17)

**State: PROVISIONAL / NOT_MERGED / INDEPENDENT_AI_REVIEW_PENDING.** This file prepares a bounded CI-only closeout for [PR #50](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/50). It is **not** a completed acceptance, independent reviewer signature, actual operator-owned PREPROD deployment, or production/Pilot authority.

## Authority and preserved acceptance obligations

- [OWN-APP02B2-01..03](../decisions/owner-app02b2-2026-11-17.md) choose *GitHub-hosted ephemeral Compose only*, independent AI technical security review in an **actually separate context**, and code merge only when **CI-B2-01..08** [versioned acceptance](../security/app-02b2-ci-acceptance-addendum.md) all PASS on exact final source HEAD. These are supplementary to 10 APP-02B Owner decisions.
- A successful **source merge** permits only `MERGED / EPHEMERAL_CI_RUNTIME_TESTED`. Original real-host `PREPROD_RUNTIME_VERIFIED` remains NOT_VERIFIED; frozen B.1 30 G-B01..05 cases remain NOT_RUN. This does not accept two tenant SQL, IAM/Registry/revocation, PITR, OTel/retention or real financial effects.
- Existing APP-01E.5 and APP-02A.4 synthetic regressions, no modifications to `business/` or `business.lock.json`, `FOUNDATION_ONLY / NO_GO`, `B_NO_CHANNEL BLOCKED` and production/Pilot/Go-Live `NOT_AUTHORIZED` remain invariant.

## Technical review and CI-only lineage

| Item | Current disposition / canonical evidence |
|---|---|
| 3 B.2 Owner decisions | **OWNER_APPROVED** — file linked above; not an implementation sign-off |
| F-01 root/unseal/token argv | **CORRECTED** in implementation review; runtime negative regressions tracked on PR |
| F-02 Docker live exposed ports | **CORRECTED**; four-service inspect and two mutation witnesses in CI artifact |
| F-03 operator `.env` shell execution | **FIX COMMITTED**: trusted literal whitelist loader and injection/secret/unknown-key negative test, must PASS CI on final HEAD |
| Linux Docker/Vault CI | Previously PASS on source `27cf6a4` [#44103548009](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44103548009), `app02b2-ci-only` artifact #14336519455, TestRunId `app02b2-ci-docker-44103548009-1`, redacted manifest SHA256 `b1bfc4aa687be1da189a05c2b8d179c2ed52edd3e41b18c8ee9fc544eacd9364` **HISTORICAL; superseded once HEAD changes** |
| Windows APP-01E.5/APP-02A.4 regressions | Previously PASS on source `27cf6a4` [#44103548008](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44103548008): 296/296 .NET, 182/182 + 518/518, 24/24 A11 groups, 60/60 checkpoints, 1440/1440 SQL cells, 5/5 mutations, 5/5 Playwright — **HISTORICAL** |
| Exact final-HEAD CI and artifact hashes | **PENDING**. Mutable run/attempt/source/manifest digest facts must be bound to the latest final HEAD in the PR discussion/body after the last source commit, avoiding a self-referential SHA commit cycle |
| `INDEPENDENT_AI_TECHNICAL_REVIEW` | **NOT_COMPLETED**. A reviewer in this same implementing conversation or historical implementer account comments **cannot** satisfy the Owner-selected fresh-context separation |
| CI-B2-01..08 complete and code merge | **PENDING**. PR remains Draft/unmerged while any gate is unproven |

The reviewer must fetch the latest PR source HEAD, actual full diff and both latest CI run artifacts, **not accept this candidate closeout's historical descriptions as evidence**. Reviewers must record exact SHA, independent-context identity, findings and dispositions in a separate report. For the final review/merge, the PR body or separate reviewed GitHub discussion comment is the mutable *final* evidence index; this committed document is the stable contract and historical provenance pointer. A changed HEAD invalidates earlier review/CI sign-offs.

## Post-merge status template (NOT YET APPLIED)

Only after the exact-final-HEAD independent AI report with zero blocking findings, both CI workflows PASS and the CI-only manifest source/image/port/Vault audit SHA evidence are verified may the PR be marked ready and merged. Then this candidate's documented **permitted status** is `MERGED / EPHEMERAL_CI_RUNTIME_TESTED`, with separate original `PREPROD_RUNTIME_NOT_VERIFIED` and `G-B01..05 NOT_RUN`.

**Current outcome remains `DRAFT / CI_ONLY_MERGE_PENDING / NOT_MERGED`.**
