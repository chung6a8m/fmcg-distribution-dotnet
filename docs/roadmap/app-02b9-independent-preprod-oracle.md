# APP-02B.9 — Independent Physical PREPROD Oracle & Closeout (B.9)

**Scope:** OWN-APP02B-10, frozen APP-02B.1 thirty-case matrix, **PREPROD/NONPRODUCTION only**. **Implementation status: ORACLE_SOURCE_IN_PR / PHYSICAL_PREPROD_NOT_VERIFIED / G-B01..G-B05 NOT_RUN / NO_GO**. This increment introduces a fail-closed evidence validator and negative-only source CI, **not** an actual physical PREPROD experiment or a final foundation signoff.

Canonical authority: [APP-02B.1 acceptance cases](../security/app-02b1-acceptance-evidence.md) · [APP-02B program](app-02b-preprod-foundation.md) · [Owner decisions](../decisions/owner-app02b-2026-11-17.md) · [B.9-specific bounded source-only Owner decision](../decisions/owner-app02b9-bounded-source-only-merge-2026-11-18.md) · [Issue #56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56).

## 1. Acceptance model — strict separation

1. **Frozen expected values:** [expected-v1.json](../../tools/app02b9/expected-v1.json) contains all 30 exact B01-01..06, B02-01..06, B03-01..06, B04-01..06 and B05-01..06 outcome identities, negative invariants and required distinct physical witness kinds. The human Fixture Owner signs these bytes *before execution* and the externally approved trust roots pin the SHA-256. The operator-provided fixture must be **byte-identical to the frozen file in the checked-out reviewed source**, not merely a different re-signed/re-pinned 30-case table. Editing expected data, changing HEAD or re-signing after measurements invalidates earlier evidence.
2. **Independent actual:** An external observer (with a nonempty identity distinct from the implementer) signs a full raw artifact manifest, including each actual/expected/signed difference, match, negative fault ID, UTC and full peer SQL fingerprint. Each case requires independently observed positive **and** negative witness files, content-hashed, with all frozen required evidence types (HTTP, SQL, Registry, Vault, policy, recovery, OTel, alert, retention, etc.). An API 200 alone cannot satisfy a case. The signed manifest binds the 30 records, TestRunId, environment/host, immutable build image digests, schema-migration SHA and exact source commit.
3. **Separate reviews:** An externally trusted **Security** key and a different externally trusted **DBA** key sign their own reviews for the same exact manifest SHA and final source HEAD. Both reviewers are distinct from the implementer and observer, have reviewed exactly all 30 named cases, and report zero blocking findings. B.4's historical solo-Owner staffing exception does not silently replace this B.9 original independent-acceptance gate.
4. **Proof of environment:** A trust configuration from an **approved independent operator outside the repository, workflow workspace and bundle** pins a **nonempty approved environment ID**, the host fingerprint, physical environment, two distinct tenant database identifiers under the **exact keys A and B**, source HEAD and four independent role-specific public keys. Signing keys/private secrets/real raw tenant data must **never** be committed. GitHub-hosted LocalDB/ephemeral Vault or self-generated CI keys are not PREPROD physical proof.
5. **All-or-nothing:** All 30 cases PASS, two real RUNTIME-01/02 probes, real off-host-encrypted restore with continuous LSN chain, exact measured RPO <=900 seconds/RTO <=14400 seconds, current revocation fence and external-outcome reconciliation, ten required final-HEAD CI checks **all SUCCESS without required skips**, and both reviewer signatures are mandatory. Any missing or conflicting witness, stale HEAD/fixture, unverifiable key, unresolved blocker or failed APP-00A full npm audit => **NO_GO / NOT_VERIFIED**. No case may be N/A or skipped.
6. **No promotion:** Even a future externally accepted `PREPROD_FOUNDATION_VERIFIED` result authorizes *only the PREPROD foundation*. Real AC-11/EV-08, EV-01..15, production P0 G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV remain NOT_VERIFIED; APP-02C/D/E, production, A/B/Mixed admission, Pilot, Go-Live and real business posting are NOT_AUTHORIZED.

## 2. Physical evidence layout and custody

The approved operator prepares a fresh **nonproduction** test run under an independently chosen TestRunId and collects a protected directory **outside Git**:

```text
<secure-evidence-root>/APP-02B/<TestRunId>/
  manifest.json                 # observer-signed, 30 cases and all hashes
  manifest.sig                  # detached SHA-256/ECDSA signature
  expected.sig                  # signature over the exact committed expected-v1.json
  witness/<CaseId>-*.json       # content hashed independent physical observations
  reviews/security.json        # final-source security reviewer, all 30 cases
  reviews/security.sig
  reviews/dba.json             # separate final-source SQL/restore reviewer
  reviews/dba.sig
```

**The external trust file is not inside that directory or the repo.** Trust file format (placeholders only, not real authorization):

```json
{
  "schema": "APP02B9-TRUST-V1",
  "approvedSourceHeadSha": "<40-hex exact Git HEAD>",
  "approvedEnvironmentId": "<operator-approved physical PREPROD ID>",
  "approvedHostSha256": "<64-hex independent host fingerprint>",
  "expectedFixtureSha256": "<64-hex hash of committed expected-v1.json>",
  "tenantDatabaseIds": { "A": "<real physical SQL DB ID A>", "B": "<different physical SQL DB ID B>" },
  "keys": {
    "fixture": { "publicKeyPath": "/secure/outside/git/fixture.pem", "publicKeySha256": "<sha256 PEM bytes>" },
    "observer": { "publicKeyPath": "/secure/outside/git/observer.pem", "publicKeySha256": "<sha256 PEM bytes>" },
    "security": { "publicKeyPath": "/secure/outside/git/security.pem", "publicKeySha256": "<sha256 PEM bytes>" },
    "dba": { "publicKeyPath": "/secure/outside/git/dba.pem", "publicKeySha256": "<sha256 PEM bytes>" }
  }
}
```

All four public key pins must differ. The validator verifies detached signatures with OpenSSL `dgst -sha256 -verify`. An operator must verify original credential issuance, authority, key custody, host fingerprint and SQL/backup/OTel provenance before signing; this tool checks evidence **consistency, hash chain of custody, signature and required coverage**, not truth of a fabricated physical statement. A trusted reviewer must inspect **raw SQL permission/peer read probes, genuine off-host/key restore, original logs and telemetry receipts**. Subject identifiers and secrets remain redacted; use SHA references to separately retained originals.

**Manifest V1 required sections:** `schema=APP02B9-PHYSICAL-EVIDENCE-V1`, `evidenceClass=PHYSICAL_PREPROD_WITNESSED`, `environmentMode=PREPROD_NONPRODUCTION`, `environmentId`, `hostSha256`, `testRunId`, `sourceHeadSha`, `observerId`, `implementerId`, `fixtureSha256`, `apiImageDigest`, `workerImageDigest`, `schemaMigrationSetSha256`, `tenantDatabases` (A/B physical IDs/name/Registry version), `openBlockingFindings`, `witnesses`, `cases`, `runtimeProbes`, `recovery`, `ciChecks`. Each witness lists unique `ref`, safe relative `path`, `sha256`, `kind`; its file repeats TestRunId, source HEAD, environment, observer, case, peer fingerprint, `peerReadRows=0`, observed outcome or fault/negative invariant. Each case contains gate, status, UTC, both-tenant scope, frozen expected, independent actual, signed difference = Actual-Expected (here 0 for exact symbolic outcomes), match, negative invariant, fault ID, before/after peer SHA-256 and separate positive/negative refs. Real numeric RPO/RTO values are independently recomputed from UTC/monotonic fields, not accepted from API or a planned backup schedule. The `recovery.witnessRefs` list must bind four distinct **B03-02 positive** independently hashed raw witnesses of kinds RESTORE, SQL, BACKUP and OUTCOME, and the RESTORE witness must repeat the exact four recovery UTC/monotonic clock anchors; signed manifest timestamps alone are insufficient.

**Operator invocation from a clone at the final reviewed HEAD:**

```bash
python3 tools/app02b9/oracle.py \
  --bundle /secure/APP-02B/<TestRunId> \
  --fixture tools/app02b9/expected-v1.json \
  --trust-roots /secure/approved-independent-trust.json \
  --expected-head "$(git rev-parse HEAD)" \
  --output /secure/reviews/<TestRunId>-oracle-report.json
# Exit 0 = externally witnessed PREPROD-only gate candidate; exit 2 = NO_GO
```

The output status is **not** its own sign-off: verify both independent reviewer signatures and actual physical evidence, review the final source/CI and record a separate Owner disposition. After any new HEAD, repeat signatures, 30-case experiment as affected, CI and review. Preserve reports/artifact hashes according to approved retention and legal holds, never automate evidence purge.

## 3. CI-B9 source tool coverage (not G-B01..05)

| Check | Deterministic source-only assurance |
|---|---|
| CI-B9-01 | Frozen 30 IDs and expected/negative/kind fixture, external fixture hash pin |
| CI-B9-02 | Detached observer + two distinct reviewer signatures, out-of-repo keys, separate trusted roles |
| CI-B9-03 | Physical environment identity, two distinct operator-approved SQL DB IDs, API/Worker digest |
| CI-B9-04 | Independent actual, expected, signed difference, match, fault and peer no-read/no-write |
| CI-B9-05 | SHA-256 content of each independent artifact, safe paths, positive + negative and required kinds |
| CI-B9-06 | Real RUNTIME-01/02 and measured physical backup/restore/effect/revocation conditions |
| CI-B9-07 | All ten final-HEAD workflow/artifact/attempt statuses; full APP-00A failure never called PASS |
| CI-B9-08 | Eleven independent mutation negatives: expected, actual, signed difference, match, peer, HEAD, missing physical, API self-attestation, skipped CI, open security blockers and frozen fixture |
| CI-B9-09 | Untrusted signatures or missing physical bundle => 30 NOT_RUN, five NOT_VERIFIED, NO_GO |
| CI-B9-10 | Explicit CI-only provenance, artifacts/source SHA and no PREPROD/production promotion |

`.github/workflows/app02b9-independent-oracle.yml` runs the Python unittest signed-**synthetic** fixtures and adversarial mutation suite on GitHub-hosted Ubuntu; it deliberately runs the production entrypoint with **no trusted physical bundle** and verifies NO_GO. These tests do not stand in for the original physical B.1 B01-01..B05-06, nor for RUNTIME-01/02, separate .NET/SQL/Node regression suites, an independent code review or an operational sign-off.

## 4. Present blocker ledger and handoff

As of this PR's preparation, **no real physical PREPROD tenant/Control Plane/Vault/outcome journal, RPO/RTO, off-host audit/WORM, alert receiver, legal approval or independent signed operator bundle is available to this task**. [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53) independently durable revocation fence, [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) full npm audit HIGH, [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60) physical DBA privileges, [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52), [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54), [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55), [#56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56), [#64](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/64), [#65](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/65), [#67](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/67) and [#68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68) must be dispositioned using actual operator/reviewer evidence; no previous source-only waiver transfers to B.9.

**Current closeout decision: NOT_VERIFIED / NO_GO / 0 physical B.1 cases executed.** Leave issue #56 OPEN until independent actual PREPROD evidence meets the original all-30/all-five gate. A code-only B.9 PR may be reviewed independently as an inert validation tool, but **shall not be merged as B.9 verified** or cause production/pilot authorization. The Owner has now explicitly accepted **PR #72 source-only integration**, recorded in [OWN-APP02B9-SOURCE-01](../decisions/owner-app02b9-bounded-source-only-merge-2026-11-18.md); this allows only a bounded merge-timing exception after final-HEAD independent review/CI, with APP-00A audit still visibly FAILED under #59. **Physical B.9 admission remains NO_GO**, and this is not a waiver for 30/30, G-B01..05 or full-audit physical acceptance.
