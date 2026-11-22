# Owner decision — APP-02C.6 Windows + LocalDB acceptance

**Decision date:** 2026-11-22 (Asia/Bangkok). **Status:** `DEV_SCOPE_APPROVED / MATRIX_V0.1_APPROVED / EXECUTION_BLOCKED`.

The Owner approved the Windows + LocalDB alternative with two synthetic tenants and requested the decision, roadmap and acceptance matrix for approval before execution:

> Tôi đồng ý phương án nghiệm thu APP-02C.6 trên Windows + LocalDB với hai tenant giả lập. Hãy cập nhật quyết định và roadmap, chuẩn bị ma trận nghiệm thu để tôi duyệt trước khi chạy. Giữ nguyên các cổng PREPROD và nghiệm thu vật lý.

| Decision | Disposition |
|---|---|
| **OWN-APP02C6-01 — Scope** | Accept a separate DEV acceptance path for integrated A_ONLINE business behavior using the existing React workspace, real DEV API and two nonce-owned synthetic LocalDB databases. The [C.1–C.5 DEV authority](owner-app02c-dev-first-2026-11-22.md) continues to govern those increments. |
| **OWN-APP02C6-02 — Execution boundary** | The initial instruction approved the environment choice and documentation preparation. The subsequent approval below covers matrix v0.1, its fixture and execution on Windows LocalDB. Record the execution scope before a qualifying run. Pin the host, LocalDB instance, operator, run window, and allowed create/migrate/seed/fault/restart/guarded cleanup actions. Use the existing server authority and instance/name/owner/nonce/tenant-binding checks. |
| **OWN-APP02C6-03 — Review and closeout** | Record the approved reviewer and evidence method with the matrix. The proposed solo-Owner method uses Owner observation and signoff, technical review, and a read-only SQL comparison against expectations frozen before execution. After every required case passes on the reviewed build with required CI and zero blocking findings, the proposed label is `A_ONLINE_DEV_ACCEPTANCE_VERIFIED`, subject to Owner signoff. |
| **OWN-APP02C6-04 — Operational gates** | `C-PREPROD` remains `BLOCKED / NOT_VERIFIED` and physical `C-E2E` remains `NOT_RUN`. The original B.1/B.9, RUNTIME-01/02, recovery, approved commercial policy and independent witness requirements apply at physical admission. Operational PREPROD configuration stays `FOUNDATION_ONLY / NO_GO`. Real data, production, Pilot, Go-Live and EV/P0 keep their separate authority and evidence gates. |

## Matrix v0.1 approval

On 2026-11-22 the Owner approved the [matrix v0.1 and fixture](../roadmap/app-02c6-localdb-acceptance-matrix.md), requested all 11 cases on Windows LocalDB with two synthetic tenants, and reserved acceptance signoff until after reviewing SQL reconciliation. The approved text SHA-256 is `04743B66AF77F02D3DA86EED229498D6AC947C201B5C189A3BF79BB96DFA6290`. Its draft marker records the version submitted for approval.

The [C roadmap](../roadmap/app-02c-a-online-e2e.md) records both paths. The [DEV execution report](../closeout/2026-11-22-app-02c6-localdb-attempt.md) records the baseline run, the blockers and the outstanding 11-case acceptance. Owner signoff is pending.
