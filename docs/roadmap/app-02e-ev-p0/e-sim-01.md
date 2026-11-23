# E-SIM-01 — Hồ sơ giả định và kiểm chứng mô hình

Phạm vi kiểm tra là dữ liệu mô hình APP-02E. Bộ v1 kiểm liên kết readiness, writer/opening và seller/provider/kế toán. Ba bản v0.2 mô tả nguồn giả định. Bộ ba JSON chứa manifest, bản trích và kỳ vọng nhập riêng. Lấy tập khóa mở đầu và tổng đối chiếu từ expected trước khi chạy validator.

Kho-01 trong mô hình gồm MAIN, xe, transit, QA và ngoài wave. Tồn SKU-SIM là EA nguyên, giá vốn giả định 10 đơn vị thử mỗi EA, mốc cutoff UTC chốt trong manifest. Tiền dùng SYNTHETIC_UNITLESS số nguyên. Credit 0 là giả định tường minh, bank pending chưa biết. Hai tenant và DB trong manifest là định danh mẫu. Profile ghi nhận trạng thái tra cứu mô phỏng; Delivery giữ invoiceEligible=false và B_NO_CHANNEL giữ chặn.

Chạy `py tools/validate_e_sim_01.py` từ root. Validator đọc dữ liệu, đối chiếu tập khóa và số lượng/tiền, kiểm provenance, rồi đột biến bản trích trong bộ nhớ để thử phát hiện sai lệch.

## Phân công 31 yêu cầu hiện có

MODEL là mô hình giả định. DEV dẫn kết quả phạm vi đã chạy trong APP-02E. DEV-F dẫn increment còn cần triển khai. OPS là bằng chứng hệ vận hành. EV/P0 và B-RO-01 thực giữ trạng thái lịch sử. [Danh sách gốc](ev-p0-read-only-evidence-request-v0.1.md) · [Định tuyến mô phỏng](ev-p0-private-routing-v0.1.md) · [Biên bản APP-02E](../../closeout/2026-11-23-app-02e-localdb.md)

| Mã | Phạm vi | Bằng chứng hoặc công việc |
| --- | --- | --- |
| B-RO-01 | OPS | Quyết định môi trường thật |
| B-RO-02 | MODEL | Binding giả lập |
| B-RO-03 | DEV | Nguồn và artifact APP-02E đã ký |
| B-RO-04 | OPS | IAM và restore thực |
| B-RO-05 | OPS | Phân nhiệm B.9 |
| B-RO-06 | OPS | Backup/restore vật lý |
| B-RO-07 | OPS | Runtime vật lý |
| B-RO-08 | OPS | Độc lập và manifest vật lý |
| W-01 | MODEL | UI kho |
| W-02 | MODEL | Điều vận |
| W-03 | MODEL | Connector |
| W-04 | MODEL | ETL |
| W-05 | MODEL | SQL job |
| W-06 | MODEL | Thiết bị |
| W-07 | MODEL | Tiền ngoài wave |
| W-08 | MODEL | Recovery ngoài wave |
| O-RO-01 | MODEL | Tập khóa Kho-01 |
| O-RO-02 | DEV-F03/F05 | Lot/FEFO và QA qua SQL |
| O-RO-03 | MODEL | Khóa phạm vi opening mẫu |
| O-RO-04 | DEV | AR trong D.3 |
| O-RO-05 | DEV-F02 | Tạm ứng nhân viên |
| O-RO-06 | MODEL | Chuỗi nguồn mẫu |
| O-RO-07 | MODEL | Tổng opening mẫu |
| SPK-RO-01 | MODEL | Seller/mode mẫu |
| SPK-RO-02 | OPS | Căn cứ pháp lý thực |
| SPK-RO-03 | MODEL | Profile mẫu |
| SPK-RO-04 | DEV | Tra cứu loopback và thiếu kênh trong D.4 |
| SPK-RO-05 | MODEL | Mapping-v1 mẫu |
| SPK-RO-06 | DEV | Receipt/posting đối tác loopback D.4 |
| SPK-RO-07 | DEV | Correction và snapshot D.4 |
| SPK-RO-08 | OPS | Phạm vi vận hành thực |

## Biên bản DEV ngày 23/11/2026

| Ca | Lệnh và nguồn | Expected | Actual / diff | Trạng thái |
| --- | --- | --- | --- | --- |
| SIM-01 · AC-04/16 | `py tools/validate_e_sim_01.py`, bộ v1 bên dưới | 31 mã, 8 writer, 11 opening, 100 EA / giá trị 1.000 | 31/31, 8/8, 11/11, 100/1.000. AR 375, credit 0 tường minh, custody 200, quỹ 600, tạm ứng 500.000; bank pending vẫn null. Diff số dư đã biết bằng 0. | PASS mô hình |
| SIM-02 · AC-14/15 | Cùng validator | Source → receipt → posting đúng kỳ, AR 75 và COGS 50 | AR 75 và COGS 50, diff 0 ở cả hai dòng. Tra cứu provider nằm trong mô hình. | PASS mô hình |
| SIM-03 | Cùng validator, mười đột biến tại chỗ | Từ chối 10/10 | 10/10 gồm thiếu/trùng khóa, tenant sai, ép unknown thành 0, sai dòng/tổng tồn, đứt receipt, sai kỳ và chênh posting. | PASS mô hình |
| SIM-04 · AC-14/15 | `npm --prefix web ci`, `./scripts/Run-App02D4.ps1` trên Windows LocalDB, hai tenant, loopback | Ma trận D.4: 36 lượt và 2 Chromium | Lượt sau đạt 36/36 và 2/2, diff 0 theo oracle D.4. D.4 dùng fixture riêng của D.4. | PASS DEV D.4 |

SHA-256 của JSON v1 (UTF-8, LF):

- Manifest `2d1b177721c320dbc4bab502d65203fdfe9385994ea2f3805e91b799112c22aa`
- Bản trích `119e0244da93dd7cd9aa79d2207faadf0b5428b77a9926db25cb35fcf17b03bf`
- Expected `05bfee9c14fcc929df9cec9b86c52f33311b7d682e19f1e2dc1ffc7be32131b0`

SIM-04 chạy mã ứng dụng trên HEAD `5203ffc10e5b4c19ac215a940b9bb3cb066b23bb`, tree `5ef2a83a80df4c0aecd9a914690fc0e37360a5c7`, có chỉnh sửa mô hình chưa commit. DLL SHA-256 `898AFEF9D8A09D653195DD4A0C3C80AA9AF02BCDFC40216230BFC4F1F8A747DA`; fixture D.4 `BF3DAFBB0C65397155FD625916FE30B9AEEA22E18F279F61E70131367765B987`. Thu kết quả bằng lệnh trên tại `TestResults/app02d4/<run-id>/sql-matrix.json` và `browser-sql-receipt.json`. Lượt đạt `ec9704a13fa245abbd2f804daa3ad854`, SHA-256 ma trận `D4C681026DBCC76949BD0D9649C439F54836814EADDE2FA2363099C1C01B14A3`. Lần khởi động đầu thiếu dependency web và dừng trước browser. Sau khi cài, lượt `5499d71a51984bc5a3e1767dea8e79fe` đạt 35/36 và 2/2 Chromium. D4-12 tenant-b mong HTTP 200, nhận 503 ACCOUNTING_ACK_UNKNOWN. Lỗi không lặp ở lượt kế tiếp. Chưa xác định nguyên nhân. SHA-256 ma trận 35/36 là `75E682FBD691F32526B3F8F81258FEC0A9FEBA9E0355A55CB18167A28D1F3528`.

Biên bản [APP-02E đã ký](../../closeout/2026-11-23-app-02e-localdb.md) giữ 6 suite, 91 mã và 175 lượt nguồn đã ký. Opening, writer và provider mẫu chưa thực thi trên ứng dụng dưới dataset E-SIM-01. Visit, tạm ứng, FEFO, giá/thuế, hàng trả, điều chỉnh kỳ khóa, quà/waiver và credit/refund lần lượt thuộc DEV-F01–08. DEV-F09 tổng hợp hồi quy. G-INV, G-IDEMP, G-LEDGER, G-CUT và G-STOCK tiếp tục theo bằng chứng vận hành thực tế.
