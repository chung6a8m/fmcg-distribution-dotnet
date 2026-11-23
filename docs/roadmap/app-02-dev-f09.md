# DEV-F09 — Hồi quy hai tenant

Một candidate Git tích hợp DEV-F01–08 và E-SIM-01 để chạy lại ma trận A.4, C.6, D.1–D.4. Các lượt tính theo fixture gốc của từng suite, mỗi fixture dùng scratch riêng. Truy vết kiểm theo run ID, SQL report, JUnit và SHA-256 của nội dung, với source commit và tree chung. [APP-02E đã ký](../closeout/2026-11-23-app-02e-localdb.md) vẫn dùng source và mẫu số lịch sử. [Truy vết AC–EV–P0 đã đóng băng](app-02e-ac-ev-p0-traceability.md) cung cấp phạm vi nền.

| Hành trình | Nguồn và oracle | Expected |
| --- | --- | --- |
| Visit · AC-17 | [F01](app-02-dev-f01.md) | Lưu visit qua browser, giữ order, AR và tồn, kiểm quyền và retry trong API/.NET |
| Tạm ứng · AC-20 | [F02](app-02-dev-f02.md) | 500000 → 200000 → 200000 → 100000 → 0; giữ AR/custody khách |
| Lô và FEFO · AC-05 | [F03](app-02-dev-f03.md) | 2 BOX → 48 EA, lô/quarantine và số dư theo nguồn |
| Giá · AC-19 | [F04](app-02-dev-f04.md) | EXCLUSIVE/INCLUSIVE, discount 10001, snapshot và giao P theo tỷ lệ |
| Trả và QA · AC-05/06 | [F05](app-02-dev-f05.md) | Nhận QA, giá vốn về 3334 + 3333; AR giữ nguyên |
| Kỳ khóa · AC-06 | [F06](app-02-dev-f06.md) | 3334 + 50 + 3384 = 6768, kỳ khóa giữ nguyên |
| Quà · AC-07 | [F07](app-02-dev-f07.md) | Nhánh trả quà được cấp 2, waiver được cấp 0 sau mua lại |
| Credit và refund · AC-19 | [F08](app-02-dev-f08.md) | Ba phần net/tax/gross/discount; 69600 → 49600 sau ghi chi 20000 |
| Nền A.4, C.6, D.1–D.4 | Ma trận SQL và Playwright của từng suite | 24 + 15 + 28 + 36 + 36 + 36 = 175 lượt đối chiếu, tất cả PASS; D.2 chặn B thiếu kênh |
| E-SIM-01 | [Biên nhận mô hình](app-02e-ev-p0/e-sim-01.md) | 31 ánh xạ, nội dung khóa không thay đổi, 10 đột biến bị từ chối |

Chạy `./scripts/Run-DevFunctionalRegression.ps1` từ candidate checkout sạch trên Windows với LocalDB, Chromium, .NET/Node được pin, PowerShell 7 và sqlcmd. `-Suite Functional` chạy F01–08, `-Suite Foundation` chạy nền, `-ManifestOnly` chỉ kiểm mô hình. Candidate kiểm đủ commit của PR #124 và nhánh F02 theo SHA trước khi chạy. Runner chỉ đọc danh mục LocalDB trước/sau, và gọi các launcher đã xác minh quyền nonce. Mỗi launcher có run độc lập và trả về lỗi nếu evidence thiếu hoặc sai.

Kết quả nằm ở `TestResults/dev-f09/<run>/run.json`; từng bước ghi lệnh, exit, đường dẫn và SHA-256 của báo cáo, log và JUnit/TRX theo phạm vi. Lỗi giữ trạng thái FAIL và phần cần kiểm lại; chạy ngoài phạm vi đã ghi là NOT_RUN. PREPROD, EV/P0, thiết bị thật, Pilot và Go-Live theo các gate đang có.
