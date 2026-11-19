# APP-02B LAB — Exact Case-to-Increment Matrix

**State:** PLANNED, no case has been executed by this roadmap PR. This table is a planning projection of the byte-frozen original [expected-v1.json](../../../tools/app02b9/expected-v1.json); the original fixture is unchanged. Case IDs, expected symbols, negative invariants and required kinds below are copied from that baseline, not redesigned.

**Read with:** [master roadmap](../app-02b-local-preprod-acceptance.md), [driver increments](04-evidence-and-closeout.md), [original B.1](../../security/app-02b1-acceptance-evidence.md).

All cases start `NOT_RUN` in an actual new run. Missing setup/approval/witness becomes `BLOCKED`. Local technical observations always keep `originalGateStatus=NOT_VERIFIED`. B05-02 cannot PASS as independent review when the same human is implementer, operator, observer and reviewer. AI review and multiple SQL accounts/keys do not change that fact.

| Case | Driver PR | Runtime/source owners | Expected symbol | Negative invariant | Required witness kinds |
|---|---|---|---|---|---|
| B01-01 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S04, I05 | `DENY_CROSS_TENANT_DATA_AND_METADATA` | `PEER_READ_WRITE_ZERO` | HTTP, SQL, REGISTRY |
| B01-02 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S02, S03 | `INVALID_JWT_AND_REUSED_REFRESH_DENIED` | `NO_TOKEN_FAMILY_ELEVATION` | HTTP, IDENTITY, SQL |
| B01-03 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S01, S04, S06, S07 | `REVOKED_API_WORKER_CASE_COMMIT_DENIED` | `NO_PARTIAL_PRIVILEGED_EFFECT` | AUTHORIZATION, SQL, WORKER |
| B01-04 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S02, S04, I05 | `SYSTEM_ADMIN_WITHOUT_MEMBERSHIP_DENIED` | `CONTROL_PLANE_NO_BUSINESS_DATA` | HTTP, SQL, IDENTITY |
| B01-05 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S07, S08 | `SELF_OR_STALE_APPROVAL_DENIED` | `MAKER_CHECKER_AND_SOURCE_IMMUTABLE` | POLICY, SQL, AUDIT |
| B01-06 | [LAB-E08](04-evidence-and-closeout.md#lab-e08) | S01, S02, I08 | `RESTORED_STALE_RIGHTS_DENIED` | `REVOKED_RIGHTS_NOT_RESURRECTED_AND_PEER_UNCHANGED` | RESTORE, AUTHORIZATION, REGISTRY, SQL |
| B02-01 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | I03, I04, I05, S08 | `WRONG_WORKLOAD_TENANT_SECRET_DENIED` | `NO_SECRET_LEAK_OR_SQL_PEER_ACCESS` | VAULT, SQL, AUDIT |
| B02-02 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | I03, S02, S04, S06 | `VAULT_MISSING_KEY_FAIL_CLOSED` | `NO_CONFIG_PLAINTEXT_FALLBACK` | VAULT, RUNTIME, SQL |
| B02-03 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | S03, I03 | `VERSIONED_KEY_ROTATION_WITH_REVOKE` | `NO_REVOKED_FAMILY_REUSE` | VAULT, IDENTITY, SQL |
| B02-04 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | S07, S08 | `L0_IMMUTABLE_OVERRIDE_DENIED` | `L0_HISTORY_HASH_UNCHANGED` | POLICY, SQL, AUDIT |
| B02-05 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | S07, I05 | `L1_SCOPE_EFFECTIVE_TIME_ENFORCED` | `TENANT_B_POLICY_UNCHANGED` | POLICY, SQL, REGISTRY |
| B02-06 | [LAB-E09](04-evidence-and-closeout.md#lab-e09) | S07, S08 | `L2_DISTINCT_MAKER_CHECKER_AND_APPROVED_ZERO` | `UNSET_AND_SELF_APPROVAL_DENIED` | POLICY, SQL, AUDIT |
| B03-01 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I06, I03 | `PHYSICAL_ENCRYPTED_FULL_DIFF_LOG_CHAIN_VALID` | `BACKUP_GAP_OR_KEY_MISSING_REJECTED` | BACKUP, SQL, VAULT |
| B03-02 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I08, I07, S06 | `PHYSICAL_TENANT_PITR_RPO_RTO_MET` | `SAFE_REENTRY_NO_BLIND_EXTERNAL_EFFECTS` | RESTORE, SQL, BACKUP, OUTCOME |
| B03-03 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I08, I05 | `WRONG_REGISTRY_DATABASE_BINDING_DENIED` | `PEER_FULL_SQL_FINGERPRINT_UNCHANGED` | RESTORE, REGISTRY, SQL |
| B03-04 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I08, S01, S02, I03 | `STALE_RESTORE_REVOKE_SUSPEND_DENIED` | `RESTORE_DOES_NOT_REISSUE_RIGHTS` | RESTORE, AUTHORIZATION, VAULT |
| B03-05 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I07, I08, S06 | `SAME_MESSAGE_ID_NO_DUPLICATE_EXTERNAL_EFFECT` | `UNKNOWN_OUTCOME_HOLD_CASE` | OUTCOME, SQL, RESTORE |
| B03-06 | [LAB-E10](04-evidence-and-closeout.md#lab-e10) | I08, O01 | `CORRUPT_KEY_MISSING_UNAUTHORIZED_RESTORE_DENIED` | `NO_ACTIVE_OR_DISPATCH_ON_RESTORE_FAILURE` | RESTORE, BACKUP, INCIDENT, REGISTRY |
| B04-01 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | S08 | `AUDIT_TAMPER_DENIED_OR_DETECTED` | `CHAIN_ORIGINAL_PRESERVED` | AUDIT, SQL |
| B04-02 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | S06, S08, O01 | `END_TO_END_CORRELATED_TRACE_AND_DURABLE_ACK` | `NO_SECRET_PII_AND_ACK_OUTCOME_DISTINCT` | TELEMETRY, SQL, AUDIT |
| B04-03 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | O01 | `REAL_ALERT_TRIGGER_DELIVERY_AND_ACK` | `MISSING_ALERT_DELIVERY_VISIBLE` | ALERT, TELEMETRY, INCIDENT |
| B04-04 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | O01 | `INCIDENT_TIMELINE_ESCALATED_AND_REVIEWED` | `NO_UNOWNED_OR_UNREVIEWED_CLOSEOUT` | INCIDENT, AUDIT |
| B04-05 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | O02, I08 | `LEGAL_HOLD_PRESERVED_DENIED_PURGE` | `PROTECTED_OBJECT_HASHES_STABLE` | RETENTION, AUDIT, SQL |
| B04-06 | [LAB-E11](04-evidence-and-closeout.md#lab-e11) | O02 | `APPROVED_ELIGIBLE_ARCHIVE_PROTECTED_DURABLE` | `NO_DURABLE_APP02A_TTL_PURGE` | RETENTION, SQL, AUDIT |
| B05-01 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | I05, E05, E06, E07 | `TWO_PHYSICAL_TENANTS_AND_REPRODUCIBLE_FIXTURE` | `NO_MISSING_CASE_OR_PEER_EVIDENCE` | SQL, REGISTRY, CI |
| B05-02 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | E12; external humans are live acceptance prerequisites | `INDEPENDENT_SECURITY_AND_DBA_FINAL_HEAD_REVIEW` | `NO_SELF_REVIEW_OR_BLOCKING_FINDINGS` | REVIEW, SECURITY, SQL |
| B05-03 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | E07, E03, E04 | `REQUIRED_FULL_REGRESSION_FINAL_HEAD_PASS` | `NO_SKIPPED_REQUIRED_OR_FAILED_AUDIT` | CI, SECURITY |
| B05-04 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | E05, E12, original B9 | `INDEPENDENT_ORACLE_MUTATION_NEGATIVES_REJECTED` | `EXPECTED_ACTUAL_DIFF_HASH_PEER_HEAD_MUTATIONS_FAIL` | ORACLE, MUTATION |
| B05-05 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | E06, I08, O01 | `API_POSITIVE_WITHOUT_PHYSICAL_PROOF_REJECTED` | `NO_SELF_ATTESTED_GATE_PASS` | HTTP, SQL, RESTORE, ALERT |
| B05-06 | [LAB-E12](04-evidence-and-closeout.md#lab-e12) | E07, E12 | `NEW_HEAD_INVALIDATES_PRIOR_SIGNOFF` | `NO_STALE_CI_OR_PRODUCTION_PROMOTION` | CI, REVIEW, SECURITY |


## Runtime probes in addition to the 30 cases

| Probe | Implementation owners | Collector | Required observation |
|---|---|---|---|
| RUNTIME-01 | LAB-I02/I03, LAB-S04/S06 | LAB-E06 | Exact images/config/source; TLS/proxy-only ingress; rejected synthetic/business switches; foundation host retains NO_GO. |
| RUNTIME-02 | LAB-I02/I03/I04, LAB-S02/S04/S06 | LAB-E06 | Actual HTTP/network/Vault audit; wrong-role 403, token revoke/expiry/outage; direct/proxy bypass and unexpected exposed sockets denied. |

RUNTIME-01/02 have separate result rows; they must not inflate 30 into 32 original B.1 cases. The complete local coverage index therefore contains 30 case rows plus 2 runtime rows. LAB-E05 owns coverage validation, LAB-E06 their collection, LAB-E13 reporting.

For B03-02, connect original RESTORE/SQL/BACKUP/OUTCOME raw records to the same positive run. Require actual UTC and monotonic failure/safe-return anchors. SQL ONLINE alone does not stop RTO; API and Worker must return safely. Measured RPO <=900 s and RTO <=14400 s remain the original goals. Lost observer continuity blocks the claim.

For B05-02, local role enforcement and solo self-review may be recorded as subchecks, but the independent-human condition remains BLOCKED. Do not summarize a local run as “30/30 physical PASS”; other cases can also remain BLOCKED for off-host custody, missing policy or unavailable source prerequisites.
