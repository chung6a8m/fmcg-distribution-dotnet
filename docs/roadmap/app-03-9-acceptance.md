# APP-03.9 — Nghiệm thu DEV tích hợp

Kịch bản dùng profile `APP-03.7-v1` với một run và hai database LocalDB synthetic. Sales, Kho, Hiện trường, Thủ quỹ, Finance, QA và Chủ NPP thao tác bằng danh tính theo vai. Chứng từ phát sinh qua các màn hiện có và command tương ứng. Bộ đối chiếu đọc SQL sau khi xác minh nonce và binding ở từng tenant.

## Chạy kiểm chứng

Từ checkout sạch trên Windows với LocalDB, .NET SDK 10.0.404, Node 26 và Chromium của Playwright:

```powershell
npm --prefix web ci
npm --prefix web exec -- playwright install chromium
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyAcceptance
pwsh -NoProfile -File ./scripts/Run-DevFunctionalRegression.ps1
pwsh -NoProfile -File ./scripts/Verify-Business.ps1
npm --prefix web run test:dependencies
```

Các launcher sử dụng chung cổng API 5116. Chạy tuần tự với các launcher DEV khác. `VerifyAcceptance` dừng host và thu hồi hai database thuộc run khi kết thúc. Log lỗi của từng pha được giữ trong thư mục kết quả.

Để ghi thêm một lượt thao tác trực tiếp, đặt `$env:APP03_ACCEPTANCE_DIRECT = '1'` trước khi chạy `VerifyAcceptance`. Sau pha tự động ngày hai, launcher in URL và lý do viếng thăm gắn với run, đồng thời sao chép mã DEV của Sales. Mở URL trong trình duyệt, kết nối, chọn khách hàng DEV và ghi viếng thăm **Không có đơn** bằng đúng lý do được in. Kết quả SQL cần có đúng actor, command ID và một visit mới. Chạy hồi quy F09 sau khi launcher kết thúc.

## Ma trận bằng chứng

| Điểm ghép | Expected |
| --- | --- |
| Sales → Kho → Hiện trường | Đơn trên hai chuyến dùng cùng SKU có lô. Kho nạp 2 EA cho chuyến A. Giữ 5 EA cho chuyến B bị từ chối do còn 4 EA, sau đó giữ thành công 2 EA. Hiện trường giao A 2 EA, ghi một visit và bán B 2 EA. |
| Tiền khách và tạm ứng | B ghi 20.000 VND custody và tất toán AR B. A còn 20.000 VND AR trước khi đối soát tiền vào F08. Phát ứng 500.000 VND giữ nguyên AR và custody khách. |
| Dừng và mở lại | Thủ quỹ nhận 20.000 VND, browser bỏ ACK sau khi API đã commit. Trước restart, SQL có một lần nhận và một lần kiểm đếm. Sau khi API và Vite dừng, Playwright mở lại cùng origin, khôi phục đúng mục pending đã lưu từ tab trước, xác thực bằng mã mới, tra và replay cùng command/source ID. Tenant B dùng quyền thủ quỹ tương ứng và bị chặn đọc/ghi/replay hồ sơ A. Kho nạp chính phần giữ chờ của chuyến B. |
| Trả hàng → QA → Finance → Chủ NPP | Trả 1 EA từ delivery A. Kho nhận, Finance duyệt, QA giải phóng, ghi credit 10.000 VND và thực chi 4.000 VND. SQL đối chiếu các ID và phân nhiệm. Chủ NPP mở dòng nguồn credit 6.000 VND. Tổng MAIN chỉ đếm mỗi vị trí/SKU một lần. |
| Ngày hai | Chuẩn bị ngày mới hai lần chỉ sinh một chuyến mỗi tenant. So sánh số chốt và mở ngày theo EA, VND và số dòng. Opening và snapshot của ngày một giữ nguyên. Sales Confirm đơn 1 EA trên chuyến ngày hai với giá 12.000 VND. |

Mỗi run lưu JUnit, log trình duyệt, các ID nguồn, ảnh màn hình, kết quả Owner và `sql-review.json` dưới `TestResults/app03-9/<run-id>/`. SQL ghi từng expected, actual và diff kèm tenant, pha và đơn vị. `run.json` gắn source, tree, trạng thái lượt trực tiếp và SHA-256 của artifact. Hồ sơ F09 nằm trong `TestResults/dev-f09/<run-id>/run.json`. Phạm vi synthetic DEV và các mốc nghiệm thu vận hành tuân theo [roadmap APP-03](app-03.md).
