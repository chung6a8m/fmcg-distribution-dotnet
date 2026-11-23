# APP-02E EV/P0 — Bảng người nhận và kênh riêng

**v0.2 · 23/11/2026 (Asia/Bangkok) · Đã mô phỏng 30 mục**

B-RO-01 vẫn `PENDING` theo xác nhận của Owner và nằm ngoài bảng gửi này. Danh sách gồm **30 mã cần gửi**, theo thứ tự đã duyệt **6 mục ưu tiên 1, 19 mục ưu tiên 2 và 5 mục ưu tiên 3**. Nội dung yêu cầu và mẫu bàn giao chỉ đọc nằm trong gói đã duyệt. [Gói EV/P0 v0.1](ev-p0-read-only-evidence-request-v0.1.md)

**Owner / đầu mối điều phối** · Owner dự án

**Thông tin liên hệ Owner** · Lưu trong bản làm việc nội bộ.

## Bảng định tuyến theo vai trò

- Mỗi mã có một đầu mối nhận chính trong mô phỏng. Vai trò phối hợp và đối chiếu dùng để xác định phạm vi. Các vai độc lập B.9 theo hợp đồng gốc.
- Tên, hộp thư và kho của các vai trò trong bảng là dữ liệu giả. Bản này công khai thông tin giả lập theo vai trò.

| Vai trò | Gửi chính | Phối hợp hoặc đối chiếu | Tên người / nhóm nhận | Email hoặc kênh riêng và cách gửi | Kho bàn giao chỉ đọc / người được xem |
| --- | --- | --- | --- | --- | --- |
| Tenant Owner | O-RO-01, SPK-RO-01 | SPK-RO-08, SPK-RO-02 | Mô phỏng — Tenant Owner | r01@example.invalid · SIM-EVP0/R01 | SIM-VAULT-EVP0/R01 · Owner và vai trò theo từng mã |
| Product Owner | SPK-RO-08 | — | Mô phỏng — Product Owner | r02@example.invalid · SIM-EVP0/R02 | SIM-VAULT-EVP0/R02 · Owner và vai trò theo từng mã |
| IT dữ liệu | — | O-RO-01, O-RO-06, O-RO-07 | Mô phỏng — IT dữ liệu | r03@example.invalid · SIM-EVP0/R03 | SIM-VAULT-EVP0/R03 · Owner và vai trò theo từng mã |
| Operator PREPROD | B-RO-02, B-RO-07 | — | Mô phỏng — Operator PREPROD | r04@example.invalid · SIM-EVP0/R04 | SIM-VAULT-EVP0/R04 · Owner và vai trò theo từng mã |
| Implementer | B-RO-03, B-RO-08 | — | Mô phỏng — Implementer | r05@example.invalid · SIM-EVP0/R05 | SIM-VAULT-EVP0/R05 · Owner và vai trò theo từng mã |
| Security | B-RO-04 | B-RO-02, B-RO-06, B-RO-07, W-03, W-05, W-08, SPK-RO-03, SPK-RO-08 | Mô phỏng — Security | r06@example.invalid · SIM-EVP0/R06 | SIM-VAULT-EVP0/R06 · Owner và vai trò theo từng mã |
| DBA | B-RO-06, W-05, W-08 | B-RO-02, B-RO-04 | Mô phỏng — DBA | r07@example.invalid · SIM-EVP0/R07 | SIM-VAULT-EVP0/R07 · Owner và vai trò theo từng mã |
| Vận hành PREPROD | — | B-RO-04, B-RO-07 | Mô phỏng — Vận hành PREPROD | r08@example.invalid · SIM-EVP0/R08 | SIM-VAULT-EVP0/R08 · Owner và vai trò theo từng mã |
| Operator độc lập | B-RO-05 | — | Mô phỏng — Operator độc lập | r09@example.invalid · SIM-EVP0/R09 | SIM-VAULT-EVP0/R09 · Owner và vai trò theo từng mã |
| Fixture Owner | — | B-RO-05 | Mô phỏng — Fixture Owner | r10@example.invalid · SIM-EVP0/R10 | SIM-VAULT-EVP0/R10 · Owner và vai trò theo từng mã |
| Observer B.9 | — | B-RO-03, B-RO-05, B-RO-06, B-RO-07 | Mô phỏng — Observer B.9 | r11@example.invalid · SIM-EVP0/R11 | SIM-VAULT-EVP0/R11 · Owner và vai trò theo từng mã |
| Reviewer Security độc lập | — | B-RO-05, B-RO-08 | Mô phỏng — Reviewer Security độc lập | r12@example.invalid · SIM-EVP0/R12 | SIM-VAULT-EVP0/R12 · Owner và vai trò theo từng mã |
| Reviewer DBA độc lập | — | B-RO-05, B-RO-08 | Mô phỏng — Reviewer DBA độc lập | r13@example.invalid · SIM-EVP0/R13 | SIM-VAULT-EVP0/R13 · Owner và vai trò theo từng mã |
| Quản lý Kho-01 | W-01, O-RO-02 | W-02, W-06, O-RO-01, O-RO-03, O-RO-06, O-RO-07 | Mô phỏng — Quản lý Kho-01 | r14@example.invalid · SIM-EVP0/R14 | SIM-VAULT-EVP0/R14 · Owner và vai trò theo từng mã |
| Quản trị hệ cũ | — | W-01, W-05 | Mô phỏng — Quản trị hệ cũ | r15@example.invalid · SIM-EVP0/R15 | SIM-VAULT-EVP0/R15 · Owner và vai trò theo từng mã |
| Điều vận | W-02, O-RO-03 | — | Mô phỏng — Điều vận | r16@example.invalid · SIM-EVP0/R16 | SIM-VAULT-EVP0/R16 · Owner và vai trò theo từng mã |
| Bán hàng | — | W-02, O-RO-03 | Mô phỏng — Bán hàng | r17@example.invalid · SIM-EVP0/R17 | SIM-VAULT-EVP0/R17 · Owner và vai trò theo từng mã |
| IT ứng dụng | — | W-01, W-02 | Mô phỏng — IT ứng dụng | r18@example.invalid · SIM-EVP0/R18 | SIM-VAULT-EVP0/R18 · Owner và vai trò theo từng mã |
| IT thiết bị | W-06 | — | Mô phỏng — IT thiết bị | r19@example.invalid · SIM-EVP0/R19 | SIM-VAULT-EVP0/R19 · Owner và vai trò theo từng mã |
| Tổ giao nhận | — | W-06 | Mô phỏng — Tổ giao nhận | r20@example.invalid · SIM-EVP0/R20 | SIM-VAULT-EVP0/R20 · Owner và vai trò theo từng mã |
| IT tích hợp | W-03 | W-04, W-07, SPK-RO-04, SPK-RO-05 | Mô phỏng — IT tích hợp | r21@example.invalid · SIM-EVP0/R21 | SIM-VAULT-EVP0/R21 · Owner và vai trò theo từng mã |
| Chủ hệ đối tác | — | W-03, W-08 | Mô phỏng — Chủ hệ đối tác | r22@example.invalid · SIM-EVP0/R22 | SIM-VAULT-EVP0/R22 · Owner và vai trò theo từng mã |
| Vận hành ứng dụng | W-04 | W-08 | Mô phỏng — Vận hành ứng dụng | r23@example.invalid · SIM-EVP0/R23 | SIM-VAULT-EVP0/R23 · Owner và vai trò theo từng mã |
| Chủ ETL | — | W-04 | Mô phỏng — Chủ ETL | r24@example.invalid · SIM-EVP0/R24 | SIM-VAULT-EVP0/R24 · Owner và vai trò theo từng mã |
| Legal | SPK-RO-02 | SPK-RO-01, SPK-RO-03 | Mô phỏng — Legal | r25@example.invalid · SIM-EVP0/R25 | SIM-VAULT-EVP0/R25 · Owner và vai trò theo từng mã |
| Finance / thuế | — | W-07, O-RO-03, O-RO-04, O-RO-05, SPK-RO-01, SPK-RO-02, SPK-RO-03, SPK-RO-04, SPK-RO-07 | Mô phỏng — Finance / thuế | r26@example.invalid · SIM-EVP0/R26 | SIM-VAULT-EVP0/R26 · Owner và vai trò theo từng mã |
| Thủ quỹ | W-07, O-RO-05 | W-06, O-RO-07 | Mô phỏng — Thủ quỹ | r27@example.invalid · SIM-EVP0/R27 | SIM-VAULT-EVP0/R27 · Owner và vai trò theo từng mã |
| Kế toán tồn kho | — | O-RO-02 | Mô phỏng — Kế toán tồn kho | r28@example.invalid · SIM-EVP0/R28 | SIM-VAULT-EVP0/R28 · Owner và vai trò theo từng mã |
| Kế toán công nợ | O-RO-04 | — | Mô phỏng — Kế toán công nợ | r29@example.invalid · SIM-EVP0/R29 | SIM-VAULT-EVP0/R29 · Owner và vai trò theo từng mã |
| Kế toán phụ trách opening | O-RO-06, O-RO-07 | O-RO-01, O-RO-05, W-07 | Mô phỏng — Kế toán phụ trách opening | r30@example.invalid · SIM-EVP0/R30 | SIM-VAULT-EVP0/R30 · Owner và vai trò theo từng mã |
| Kế toán gửi | SPK-RO-05 | SPK-RO-06, SPK-RO-07, SPK-RO-08 | Mô phỏng — Kế toán gửi | r31@example.invalid · SIM-EVP0/R31 | SIM-VAULT-EVP0/R31 · Owner và vai trò theo từng mã |
| Kế toán nhận | SPK-RO-06, SPK-RO-07 | SPK-RO-05, SPK-RO-08 | Mô phỏng — Kế toán nhận | r32@example.invalid · SIM-EVP0/R32 | SIM-VAULT-EVP0/R32 · Owner và vai trò theo từng mã |
| Provider hóa đơn | SPK-RO-03, SPK-RO-04 | O-RO-06, SPK-RO-08 | Mô phỏng — Provider hóa đơn | r33@example.invalid · SIM-EVP0/R33 | SIM-VAULT-EVP0/R33 · Owner và vai trò theo từng mã |

**Kênh gửi riêng dùng chung nếu có** · SIM-EVP0 · hàng đợi cục bộ trong mô phỏng

**Kho bàn giao chỉ đọc dùng chung nếu có** · SIM-VAULT-EVP0 · tham chiếu giả, phạm vi theo vai trò

**Ngoại lệ định tuyến nếu có** · B-RO-01 giữ PENDING và ngoài hàng đợi.

Địa chỉ gửi và kho bàn giao có thể khác nhau. Yêu cầu gửi gồm các mục thuộc phạm vi người nhận và mẫu bàn giao chung. Chứng từ gốc nằm tại kho bằng chứng ngoài Git đã duyệt.

## Biên nhận mô phỏng ngày 23/11/2026

Đã xử lý 30 thông điệp theo ưu tiên 1 → 2 → 3. Mỗi thông điệp chứa mã, nội dung yêu cầu và toàn bộ mẫu bàn giao chỉ đọc từ gói đã duyệt. Biên nhận chi tiết có mã băm nằm trong bản làm việc nội bộ. Các phản hồi là dữ liệu kịch bản. Số gửi thật, phản hồi thật và bằng chứng thực nhận đều bằng 0.

| Ưu tiên | Yêu cầu mô phỏng | Xác nhận đã nhận | Cần làm rõ | Chờ phản hồi |
| --- | ---: | ---: | ---: | ---: |
| 1 | 6 | 6 | 0 | 0 |
| 2 | 19 | 12 | 7 | 0 |
| 3 | 5 | 0 | 0 | 5 |
| **Tổng** | **30** | **18** | **7** | **5** |

| Lượt | Ưu tiên | Mã | Kênh mô phỏng | Phản hồi mô phỏng |
| --- | --- | --- | --- | --- |
| 01 | 1 | O-RO-01 | SIM-EVP0/R01 | Xác nhận đã nhận |
| 02 | 1 | SPK-RO-01 | SIM-EVP0/R01 | Xác nhận đã nhận |
| 03 | 1 | B-RO-02 | SIM-EVP0/R04 | Xác nhận đã nhận |
| 04 | 1 | B-RO-03 | SIM-EVP0/R05 | Xác nhận đã nhận |
| 05 | 1 | B-RO-05 | SIM-EVP0/R09 | Xác nhận đã nhận |
| 06 | 1 | SPK-RO-05 | SIM-EVP0/R31 | Xác nhận đã nhận |
| 07 | 2 | B-RO-04 | SIM-EVP0/R06 | Cần làm rõ · Cần mã môi trường và phạm vi IAM. |
| 08 | 2 | B-RO-06 | SIM-EVP0/R07 | Cần làm rõ · Cần tham chiếu catalog backup và vị trí lưu. |
| 09 | 2 | B-RO-07 | SIM-EVP0/R04 | Cần làm rõ · Cần tham chiếu runtime và cửa sổ sự kiện. |
| 10 | 2 | B-RO-08 | SIM-EVP0/R05 | Xác nhận đã nhận |
| 11 | 2 | W-05 | SIM-EVP0/R07 | Cần làm rõ · Cần xác định DB legacy và phạm vi xuất metadata. |
| 12 | 2 | W-08 | SIM-EVP0/R07 | Xác nhận đã nhận |
| 13 | 2 | W-01 | SIM-EVP0/R14 | Xác nhận đã nhận |
| 14 | 2 | W-02 | SIM-EVP0/R16 | Xác nhận đã nhận |
| 15 | 2 | W-06 | SIM-EVP0/R19 | Xác nhận đã nhận |
| 16 | 2 | O-RO-02 | SIM-EVP0/R14 | Cần làm rõ · Cần cutoff và danh sách vị trí. |
| 17 | 2 | O-RO-03 | SIM-EVP0/R16 | Xác nhận đã nhận |
| 18 | 2 | W-03 | SIM-EVP0/R21 | Cần làm rõ · Cần danh sách hệ tích hợp thuộc Kho-01. |
| 19 | 2 | W-04 | SIM-EVP0/R23 | Xác nhận đã nhận |
| 20 | 2 | SPK-RO-02 | SIM-EVP0/R25 | Xác nhận đã nhận |
| 21 | 2 | W-07 | SIM-EVP0/R27 | Xác nhận đã nhận |
| 22 | 2 | O-RO-04 | SIM-EVP0/R29 | Xác nhận đã nhận |
| 23 | 2 | O-RO-05 | SIM-EVP0/R27 | Xác nhận đã nhận |
| 24 | 2 | O-RO-06 | SIM-EVP0/R30 | Xác nhận đã nhận |
| 25 | 2 | SPK-RO-03 | SIM-EVP0/R33 | Cần làm rõ · Cần định danh seller và môi trường provider. |
| 26 | 3 | SPK-RO-08 | SIM-EVP0/R02 | Chờ phản hồi |
| 27 | 3 | O-RO-07 | SIM-EVP0/R30 | Chờ phản hồi |
| 28 | 3 | SPK-RO-06 | SIM-EVP0/R32 | Chờ phản hồi |
| 29 | 3 | SPK-RO-07 | SIM-EVP0/R32 | Chờ phản hồi |
| 30 | 3 | SPK-RO-04 | SIM-EVP0/R33 | Chờ phản hồi |

PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, B.1/B.9 vật lý `NOT_RUN / NOT_VERIFIED` và C-E2E vật lý `NOT_RUN`.
