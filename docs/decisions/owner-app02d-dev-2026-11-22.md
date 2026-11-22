# Owner decision for APP-02D DEV and D.0

**Decision date:** 22/11/2026 (Asia/Bangkok)
**Status:** `DEV_ENVIRONMENT_APPROVED / D0_CONTRACT_APPROVED / D1_DEV_ACCEPTANCE_VERIFIED`

The Owner approved Windows LocalDB with two synthetic tenants for APP-02D DEV and directed D.0 to prepare the roadmap, contract and acceptance matrix for review before D.1.

> Tôi duyệt phạm vi APP-02D DEV trên Windows LocalDB với hai tenant giả lập. Hãy triển khai D.0, cập nhật roadmap và chuẩn bị hợp đồng cùng ma trận nghiệm thu để tôi duyệt trước D.1. Giữ nguyên các cổng PREPROD và nghiệm thu vật lý.

| Ref | Decision |
|---|---|
| **OWN-APP02D-DEV-01** | Use Windows and two dedicated, nonce-owned synthetic LocalDB databases. Use the existing React application and server-validated synthetic identity path when implementation is authorized. Keep the ownership and tenant-binding guards for open, migration, reset and DROP. |
| **OWN-APP02D-DEV-02** | D.0 may document the [DEV roadmap and draft contract](../roadmap/app-02d0-dev-contract.md) and the [draft D.1 matrix](../roadmap/app-02d1-dev-acceptance-matrix.md). Owner approval of the contract and matrix is the entry decision for D.1 source, migrations, UI and integrated DEV execution. D.2–D.4 are proposed increments with their own contract and evidence. |
| **OWN-APP02D-DEV-03** | Keep the locked business baseline, online-only writes, `SYNTHETIC_UNITLESS`, `invoiceEligible=false` and `B_NO_CHANNEL BLOCKED`. Synthetic fixture limits and approval identities apply only in the owned DEV environment. |
| **OWN-APP02D-DEV-04** | Preserve C-PREPROD `BLOCKED / NOT_VERIFIED`, physical C-E2E `NOT_RUN`, the existing B.1/B.9 and RUNTIME-01/02 requirements, and PREPROD `FOUNDATION_ONLY / NO_GO`. Operational B_ONLINE/Mixed admission, production, EV/P0, Pilot and Go-Live retain their existing conditions and authority. |

The signed [C.6 DEV closeout](../closeout/2026-11-22-app-02c6-localdb.md) is the predecessor evidence. D.0 records design and scope.

## D.1 contract and matrix approval

On 22/11/2026 (Asia/Bangkok) the Owner approved D.0 and matrix D.1 v0.1, authorized D.1 source, migrations, workspace and execution of all 14 cases on Windows LocalDB with two synthetic tenants, and reserved signoff until after SQL review. The approved documents are preserved as submitted. SHA-256 after normalizing line endings to LF is `0AAAAE73A161C0C3ECF17F34D7D747F84AF3031CA6430CBB57298832EC73F30B` for the contract and `E776C40ADEA1262F2C1B0052B32549747195C128EF2A9560EACC18E521EB9BCF` for the matrix. The historical draft markers identify the texts submitted for approval.

## D.1 DEV signoff

On 22/11/2026 (Asia/Bangkok) the Owner signed APP-02D.1 DEV acceptance on `ff6d2b9d9cc4620774ab47817971ffbae695d62a` and authorized merging [PR #112](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/112). The signed scope is matrix D.1 v0.1 on Windows LocalDB with two synthetic tenants. The [D.1 closeout](../closeout/2026-11-22-app-02d1-localdb.md) records the SQL evidence, hosted CI and implementation merge. Decision OWN-APP02D-DEV-04 continues to apply.

## D.2 contract, fixture and matrix approval

On 22/11/2026 (Asia/Bangkok) the Owner approved D.2 contract, fixture and matrix v0.1 and authorized implementation and all 18 cases on Windows LocalDB with two synthetic tenants. The Owner reserved DEV signoff pending SQL review. The [D.2 contract](../roadmap/app-02d2-dev-contract.md) and [D.2 matrix](../roadmap/app-02d2-dev-acceptance-matrix.md) are the approved texts. Their LF-normalized SHA-256 values are `71E6D77200289D6FE15425586AB6B8B90630EA2CAC62B8C0C331E4FE5CC05699` and `210757E3799F300737F6D6BE63196A3934C1C89B0F3E8388D7699AA717344165`, respectively. Decision OWN-APP02D-DEV-04 continues to apply.

## D.2 DEV signoff

On 22/11/2026 (Asia/Bangkok) the Owner signed APP-02D.2 DEV acceptance on `9953935224e938b4167917b48275146b72286ca1` and authorized merging [PR #114](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/114). The signed scope is D.2 matrix v0.1 on Windows LocalDB with two synthetic tenants. The [D.2 closeout](../closeout/2026-11-22-app-02d2-localdb.md) records the local and hosted SQL evidence, hosted CI and implementation merge. Decision OWN-APP02D-DEV-04 continues to apply.
