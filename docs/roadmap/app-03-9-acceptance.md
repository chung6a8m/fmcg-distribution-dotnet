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

## Kết quả local ngày 24/11/2026

Môi trường Windows với LocalDB `MSSQLLocalDB` 2019 `15.0.4382.1`, .NET SDK `10.0.404`, Node `26.13.1` và Chromium của Playwright. Source kiểm chứng `c96e7ea473aa2da02361e002d1a62e40ff1dddeb`, tree `9735d64ffff22c548dea8887ab2e8cb0e0ee9ffd`. Bản ghi kết quả này chỉ bổ sung tài liệu.

- `VerifyAcceptance` run `42f549bff4724bd790874ea83de9093c` đạt 3/3 bài trình duyệt và 279/279 đối chiếu SQL. Trước và sau phát ứng, AR A và custody đều giữ 20.000 VND trong khi dư tạm ứng chuyển từ 0 lên 500.000 VND. Sau khi nhận tiền và hoàn tất trả hàng, MAIN chốt 27 EA và 62.001 VND, TRIP còn 2 EA và 6.000 VND. Dữ liệu mở ngày hai khớp số chốt. Đơn mới xác nhận 12.000 VND với chính sách v2 trong khi digest ngày một giữ nguyên.
- Run lưu ba JUnit và log trình duyệt, ảnh, ID nguồn và báo cáo SQL dưới `TestResults/app03-9/42f549bff4724bd790874ea83de9093c/`. SHA-256 của `run.json` là `D34BFF2EDAF4459CB66491CD96B03A291F5B3F65D87567F2AC37C24C307F880D`. SHA-256 của `sql-review.json` là `3A8269030CF1792895DE9349CDAF24B14E4E19F1FC6EE97A816C225C86197E04`. Hash từng JUnit và log có trong `run.json`.
- `Run-DevFunctionalRegression.ps1` run `93cff458ed0f42539563e1e6237fee89` đạt E-SIM-01, .NET 355/355, nền A.4/C.6/D.1–D.4 175/175, Chromium 29/29 và 8/8 profile F01–F08. Danh mục LocalDB trước/sau khớp. SHA-256 của `run.json` là `67B60583C7F51551D325E04236BF3CB7CD6B58074CEFDE4E3721FC15FB4C6874`, của TRX là `0D5D36C158D64CDB3F70EDA7EEC3F2874AAD7B6ABCE0A3457B18503A6A5A6E84`. Lượt trước vướng báo cáo A.4 Debug cũ. Đã lưu riêng các báo cáo đó trước khi chạy lại từ checkout sạch.
- `VerifyFinance` run `c9599d89082b435b954082149c00b741` đạt 6/6 bài Chromium và đối chiếu SQL. SHA-256 của `sql-review.json` là `F17627B2E08946E8CADD58D6E398CEC8753A31E6DFD7D03C577D72C522035244`. Kiểm nghiệp vụ khóa, dependency, theme, token và Tailwind đạt. Hai lượt audit npm qua HTTPS ghi nhận 0 lỗ hổng. `npm --prefix web run test:e2e` đạt 58 bài và bỏ qua một ca PWA standalone yêu cầu profile CI dùng một lần. SHA-256 của JUnit web là `6FAA8590AF0A66D48B26391EEE4374836DB5DE1B8157159A3C4CF1CC6AEF85EA`.

Lượt thao tác trực tiếp dừng ở màn kết nối trên Edge khi công cụ điều khiển Windows không xác minh được URL để tiếp tục dán mã DEV. Chưa có bản ghi visit trực tiếp. Lượt tự động cuối ghi `directBrowser: NOT_RUN`. Tiêu chí thao tác trực tiếp của APP-03.9 vẫn mở.
