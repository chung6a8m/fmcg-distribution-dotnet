# Hồ sơ sẵn sàng B.1/B.9 vật lý

**v0.1 · 23/11/2026 (Asia/Bangkok) · Chờ Owner duyệt hồ sơ · B.1/B.9 vật lý `NOT_RUN / NOT_VERIFIED`**

Baseline đối chiếu `main` tại `4a6f00375d1bd04334f0c0f954a9469dd8597cb9`; hồ sơ DEV đã ký là tài liệu tham chiếu. HEAD thực thi B.9 và môi trường vật lý chờ operator chỉ định và Owner duyệt riêng. Chuẩn bị theo đúng bộ 30 ca đóng băng và quy trình operator hiện hành. [APP-02E DEV](../../closeout/2026-11-23-app-02e-localdb.md) · [B.1 30 ca](../../security/app-02b1-acceptance-evidence.md) · [Hợp đồng B.9](../app-02b9-independent-preprod-oracle.md) · [Kiểm tra của operator](../../../deploy/preprod/app02b9-operator.md)

## Phiếu sẵn sàng trước khi chạy

Các trường chưa có chứng cứ giữ `PENDING`. Tài liệu này dùng mã tham chiếu. Operator lưu định danh thật, khóa và bằng chứng nhạy cảm tại kho đã duyệt ngoài Git.

| Hạng mục | Trường chờ điền hoặc bằng chứng cần xuất trình | Xác nhận |
| --- | --- | --- |
| Quyết định phạm vi | ID quyết định Owner, PREPROD/nonproduction, environmentId, host fingerprint, test-only tenants, operator, ca thử/fault được phép, cửa sổ UTC, điểm dừng và phương án khôi phục. Ghi riêng mọi thao tác có ghi, backup/restore, triển khai hoặc thay quyền. | Owner / operator |
| Kiến trúc và DB A/B | Hai DB vật lý riêng, binding Registry, instance/name/DB ID làm chứng, principal API/Worker/Control Plane, Registry/epoch store và Vault tách phạm vi. Ghi đường truy cập và vantage ngoài mạng/proxy tương ứng từng probe. | Operator / DBA / Security |
| Build và cấu hình | Exact source HEAD, API/Worker image digests, SHA-256 tập migration, manifest cấu hình đã che bí mật, vị trí raw evidence, `TestRunId`, đồng hồ UTC và bằng chứng đồng bộ giờ. | Implementer / observer |
| Danh tính, quyền và vận hành | IAM/refresh, SQL grants API/Worker, tenant membership, current epoch và restore-safe revoke. Vault roles/keys/rotation, audit bất biến, alert receipt và escalation, retention/legal hold. | Operator / Security / DBA |
| Fixture đóng băng | File `tools/app02b9/expected-v1.json` phải trùng byte với source HEAD được duyệt. SHA-256 tại baseline là `9D7F89E4CCC941BA2A879509F32F7F4E74CA1B4FB5AAD748E9A7E7851E34DF81`. Fixture Owner ký trước chạy, trust file pin đúng hash. | Fixture Owner |
| Người và khóa | Operator độc lập phê duyệt trust file ngoài repo và ngoài bundle, pin environmentId, host SHA, A/B và 4 khóa công khai khác nhau của Fixture Owner, observer, Security và DBA. Observer khác implementer. Security và DBA khác nhau, cả hai khác implementer và observer. Ghi tham chiếu custody và xác minh thẩm quyền. | Operator độc lập |
| Restore và readiness | SQL FULL/DIFF/LOG, chuỗi LSN liên tục, bản mã hóa ngoài host có thể giải mã, test-only witness commits, journal kết quả của đối tác mô phỏng chuyên dùng cho B.9, nơi kiểm epoch bên ngoài bản restore. Ghi failure, latest recovered commit và first safe API/Worker re-entry với UTC/monotonic anchors theo B.9. | DBA / Security / observer |
| CI và trở ngại | Pin đủ 10 check yêu cầu trên final HEAD, link run/attempt/artifact, full APP-00A audit và kết quả blocker review. Trước khi ký cần 0 blocker, đủ check thành công và 0 skip bắt buộc. | Implementer / hai reviewer |

Đối chiếu riêng [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52), [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54), [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55), [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), [#64](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/64), [#65](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/65), [#67](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/67), [#68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68) và [#75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75). Tại ngày rà soát, #53, #56 và #59 đóng trên GitHub; PR #74 vẫn draft. Trạng thái vật lý, revoke sau restore và full audit của chính HEAD thực thi được xác nhận bằng chứng riêng. [PR #74](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/74)

## Danh mục đối chiếu 30 ca

Cột mã kết quả và loại witness lấy từ fixture đóng băng. Observer điền riêng từng ca theo manifest B.9 với scope bắt buộc, witness dương/âm độc lập, fault ID, peer before/after SHA-256, số dòng peer đọc được và expected/actual/signed difference. Giữ invariant âm từ fixture và điều kiện riêng của B.1; bảng này chỉ để rà sẵn sàng.

| Ca | Mã expected gốc | Loại witness bắt buộc | Hiện trạng |
| --- | --- | --- | --- |
| B01-01 | `DENY_CROSS_TENANT_DATA_AND_METADATA` | HTTP, SQL, REGISTRY | NOT_RUN |
| B01-02 | `INVALID_JWT_AND_REUSED_REFRESH_DENIED` | HTTP, IDENTITY, SQL | NOT_RUN |
| B01-03 | `REVOKED_API_WORKER_CASE_COMMIT_DENIED` | AUTHORIZATION, SQL, WORKER | NOT_RUN |
| B01-04 | `SYSTEM_ADMIN_WITHOUT_MEMBERSHIP_DENIED` | HTTP, SQL, IDENTITY | NOT_RUN |
| B01-05 | `SELF_OR_STALE_APPROVAL_DENIED` | POLICY, SQL, AUDIT | NOT_RUN |
| B01-06 | `RESTORED_STALE_RIGHTS_DENIED` | RESTORE, AUTHORIZATION, REGISTRY, SQL | NOT_RUN |
| B02-01 | `WRONG_WORKLOAD_TENANT_SECRET_DENIED` | VAULT, SQL, AUDIT | NOT_RUN |
| B02-02 | `VAULT_MISSING_KEY_FAIL_CLOSED` | VAULT, RUNTIME, SQL | NOT_RUN |
| B02-03 | `VERSIONED_KEY_ROTATION_WITH_REVOKE` | VAULT, IDENTITY, SQL | NOT_RUN |
| B02-04 | `L0_IMMUTABLE_OVERRIDE_DENIED` | POLICY, SQL, AUDIT | NOT_RUN |
| B02-05 | `L1_SCOPE_EFFECTIVE_TIME_ENFORCED` | POLICY, SQL, REGISTRY | NOT_RUN |
| B02-06 | `L2_DISTINCT_MAKER_CHECKER_AND_APPROVED_ZERO` | POLICY, SQL, AUDIT | NOT_RUN |
| B03-01 | `PHYSICAL_ENCRYPTED_FULL_DIFF_LOG_CHAIN_VALID` | BACKUP, SQL, VAULT | NOT_RUN |
| B03-02 | `PHYSICAL_TENANT_PITR_RPO_RTO_MET` | RESTORE, SQL, BACKUP, OUTCOME | NOT_RUN |
| B03-03 | `WRONG_REGISTRY_DATABASE_BINDING_DENIED` | RESTORE, REGISTRY, SQL | NOT_RUN |
| B03-04 | `STALE_RESTORE_REVOKE_SUSPEND_DENIED` | RESTORE, AUTHORIZATION, VAULT | NOT_RUN |
| B03-05 | `SAME_MESSAGE_ID_NO_DUPLICATE_EXTERNAL_EFFECT` | OUTCOME, SQL, RESTORE | NOT_RUN |
| B03-06 | `CORRUPT_KEY_MISSING_UNAUTHORIZED_RESTORE_DENIED` | RESTORE, BACKUP, INCIDENT, REGISTRY | NOT_RUN |
| B04-01 | `AUDIT_TAMPER_DENIED_OR_DETECTED` | AUDIT, SQL | NOT_RUN |
| B04-02 | `END_TO_END_CORRELATED_TRACE_AND_DURABLE_ACK` | TELEMETRY, SQL, AUDIT | NOT_RUN |
| B04-03 | `REAL_ALERT_TRIGGER_DELIVERY_AND_ACK` | ALERT, TELEMETRY, INCIDENT | NOT_RUN |
| B04-04 | `INCIDENT_TIMELINE_ESCALATED_AND_REVIEWED` | INCIDENT, AUDIT | NOT_RUN |
| B04-05 | `LEGAL_HOLD_PRESERVED_DENIED_PURGE` | RETENTION, AUDIT, SQL | NOT_RUN |
| B04-06 | `APPROVED_ELIGIBLE_ARCHIVE_PROTECTED_DURABLE` | RETENTION, SQL, AUDIT | NOT_RUN |
| B05-01 | `TWO_PHYSICAL_TENANTS_AND_REPRODUCIBLE_FIXTURE` | SQL, REGISTRY, CI | NOT_RUN |
| B05-02 | `INDEPENDENT_SECURITY_AND_DBA_FINAL_HEAD_REVIEW` | REVIEW, SECURITY, SQL | NOT_RUN |
| B05-03 | `REQUIRED_FULL_REGRESSION_FINAL_HEAD_PASS` | CI, SECURITY | NOT_RUN |
| B05-04 | `INDEPENDENT_ORACLE_MUTATION_NEGATIVES_REJECTED` | ORACLE, MUTATION | NOT_RUN |
| B05-05 | `API_POSITIVE_WITHOUT_PHYSICAL_PROOF_REJECTED` | HTTP, SQL, RESTORE, ALERT | NOT_RUN |
| B05-06 | `NEW_HEAD_INVALIDATES_PRIOR_SIGNOFF` | CI, REVIEW, SECURITY | NOT_RUN |

- **RUNTIME-01** cần chứng cứ startup guard và fail-closed khi sai mode/role, lỗi Vault/TLS, kiểm tra port và proxy thực. **RUNTIME-02** cần probe từ ngoài và phía private nonproxy, phân tách API/Worker với Vault, bảo vệ control plane và tuyến synthetic. Cả hai đang `NOT_RUN` trên môi trường vật lý. [Điều kiện RUNTIME-01/02](../app-02b2-preprod-runtime.md)
- **B03-02** dùng bốn witness dương riêng `RESTORE`, `SQL`, `BACKUP`, `OUTCOME` với hash và mốc đo trùng số liệu manifest. Mức chấp nhận là RPO ≤900 giây, RTO ≤14.400 giây, RTO kết thúc lúc API và Worker có thể trở lại an toàn. Chỉ gây outage và restore A trong ca này. B tiếp tục chạy, quyền ghi A giữ khóa cho đến khi observer đối chiếu SQL, epoch, kết quả mô phỏng và fingerprint/peer-read B. Journal mô phỏng B.9 dùng riêng cho dữ liệu kiểm thử. Chứng từ provider thật thuộc hồ sơ EV-10. [Hướng dẫn recovery B.1](../../security/app-02b1-acceptance-evidence.md#3-recovery-experiment-protocol)
- **Biên nhận cuối** gồm manifest do observer ký, chữ ký fixture và hai review Security/DBA độc lập trên cùng manifest SHA và final HEAD, đủ 30 ca và không blocker. Chỉ trình Owner khi cả 30 ca, 5 G-B, 2 RUNTIME và CI bắt buộc đáp ứng hợp đồng gốc. Kết quả validator là một phần hồ sơ để reviewers và Owner đối chiếu. [B.9 oracle](../app-02b9-independent-preprod-oracle.md)

**Điểm duyệt:** Owner xác nhận phạm vi, môi trường, operator, nhân sự độc lập, nơi giữ bằng chứng và danh sách phép thử được phép trước khi lên lịch. Windows LocalDB và Docker/WSL2 hiện có là môi trường chuẩn bị hoặc rehearsal theo phê duyệt riêng. B.1/B.9 vật lý giữ `NOT_RUN / NOT_VERIFIED`, PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`.
