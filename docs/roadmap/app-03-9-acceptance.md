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
| Dừng và mở lại | Thủ quỹ nhận 20.000 VND, browser bỏ ACK sau khi API đã commit. Trước restart, SQL có một lần nhận và một lần kiểm đếm. Playwright giữ nguyên tab Thủ quỹ khi API và Vite dừng và khởi động lại trên cùng origin. Sau khi tải lại trang và đăng nhập bằng mã mới, tab hiện đúng command pending do ứng dụng đã lưu. Bộ kiểm chứng tra cứu và replay cùng ID, sau đó đối chiếu ngay các dòng lệnh, nguồn và sổ tiền của cả hai tenant trước khi chạy lệnh nghiệp vụ tiếp theo. Tenant B dùng quyền thủ quỹ tương ứng và bị chặn đọc/ghi/replay hồ sơ A. Kho nạp chính phần giữ chờ của chuyến B. |
| Trả hàng → QA → Finance → Chủ NPP | Trả 1 EA từ delivery A. Kho nhận, Finance duyệt, QA giải phóng, ghi credit 10.000 VND và thực chi 4.000 VND. SQL đối chiếu các ID và phân nhiệm. Chủ NPP mở dòng nguồn credit 6.000 VND. Tổng MAIN chỉ đếm mỗi vị trí/SKU một lần. |
| Ngày hai | Chuẩn bị ngày mới hai lần chỉ sinh một chuyến mỗi tenant. So sánh số chốt và mở ngày theo EA, VND và số dòng. So sánh riêng digest của opening vật lý, opening theo lô, quy đổi, nội dung ShipTo, phân bổ lô giữ/nạp/xuất và snapshot giá, điều khoản và lần giao. Sales Confirm đơn 1 EA trên chuyến ngày hai với giá 12.000 VND. |

Phục hồi này được kiểm chứng khi tab còn mở qua restart của host. Hành trình đóng rồi mở lại trình duyệt nằm ngoài phạm vi của lượt này.

Mỗi run lưu JUnit, log trình duyệt, các ID nguồn, ảnh màn hình, kết quả Owner và `sql-review.json` dưới `TestResults/app03-9/<run-id>/`. SQL ghi từng expected, actual và diff kèm tenant, pha và đơn vị. `run.json` gắn source, tree, trạng thái lượt trực tiếp và SHA-256 của artifact. Hồ sơ F09 nằm trong `TestResults/dev-f09/<run-id>/run.json`. Phạm vi synthetic DEV và các mốc nghiệm thu vận hành tuân theo [roadmap APP-03](app-03.md).

## Kết quả local trước review ngày 24/11/2026

Môi trường Windows với LocalDB `MSSQLLocalDB` 2019 `15.0.4382.1`, .NET SDK `10.0.404`, Node `26.13.1` và Chromium của Playwright. Source kiểm chứng `c96e7ea473aa2da02361e002d1a62e40ff1dddeb`, tree `9735d64ffff22c548dea8887ab2e8cb0e0ee9ffd`. Bản ghi kết quả này chỉ bổ sung tài liệu.

- `VerifyAcceptance` run `42f549bff4724bd790874ea83de9093c` đạt 3/3 bài trình duyệt và 279/279 đối chiếu SQL. Trước và sau phát ứng, AR A và custody đều giữ 20.000 VND trong khi dư tạm ứng chuyển từ 0 lên 500.000 VND. Sau khi nhận tiền và hoàn tất trả hàng, MAIN chốt 27 EA và 62.001 VND, TRIP còn 2 EA và 6.000 VND. Dữ liệu mở ngày hai khớp số chốt. Đơn mới xác nhận 12.000 VND với chính sách v2 trong khi digest ngày một giữ nguyên.
- Run lưu ba JUnit và log trình duyệt, ảnh, ID nguồn và báo cáo SQL dưới `TestResults/app03-9/42f549bff4724bd790874ea83de9093c/`. SHA-256 của `run.json` là `D34BFF2EDAF4459CB66491CD96B03A291F5B3F65D87567F2AC37C24C307F880D`. SHA-256 của `sql-review.json` là `3A8269030CF1792895DE9349CDAF24B14E4E19F1FC6EE97A816C225C86197E04`. Hash từng JUnit và log có trong `run.json`.
- `Run-DevFunctionalRegression.ps1` run `93cff458ed0f42539563e1e6237fee89` đạt E-SIM-01, .NET 355/355, nền A.4/C.6/D.1–D.4 175/175, Chromium 29/29 và 8/8 profile F01–F08. Danh mục LocalDB trước/sau khớp. SHA-256 của `run.json` là `67B60583C7F51551D325E04236BF3CB7CD6B58074CEFDE4E3721FC15FB4C6874`, của TRX là `0D5D36C158D64CDB3F70EDA7EEC3F2874AAD7B6ABCE0A3457B18503A6A5A6E84`. Lượt trước vướng báo cáo A.4 Debug cũ. Đã lưu riêng các báo cáo đó trước khi chạy lại từ checkout sạch.
- `VerifyFinance` run `c9599d89082b435b954082149c00b741` đạt 6/6 bài Chromium và đối chiếu SQL. SHA-256 của `sql-review.json` là `F17627B2E08946E8CADD58D6E398CEC8753A31E6DFD7D03C577D72C522035244`. Kiểm nghiệp vụ khóa, dependency, theme, token và Tailwind đạt. Hai lượt audit npm qua HTTPS ghi nhận 0 lỗ hổng. `npm --prefix web run test:e2e` đạt 58 bài và bỏ qua một ca PWA standalone yêu cầu profile CI dùng một lần. SHA-256 của JUnit web là `6FAA8590AF0A66D48B26391EEE4374836DB5DE1B8157159A3C4CF1CC6AEF85EA`.

Lượt thao tác trực tiếp dừng ở màn kết nối trên Edge khi công cụ điều khiển Windows không xác minh được URL để tiếp tục dán mã DEV. Chưa có bản ghi visit trực tiếp. Lượt tự động cuối ghi `directBrowser: NOT_RUN`. Tiêu chí thao tác trực tiếp của APP-03.9 vẫn mở.

## Kiểm chứng sau review PR #143

Source chạy kiểm chứng `d48d17dbca3b338aaf7ef09922b842a57eaacc4b`, tree `975bfc38f560fcf416cd0fc0f7d4762fccaa3612`. Môi trường Windows LocalDB 2019 `MSSQLLocalDB`, .NET SDK `10.0.404`, Node `26.13.1` và Chromium của Playwright. Commit biên nhận sau đó chỉ thêm tài liệu và artifact.

- `VerifyAcceptance` run `2a67975b56174d92a2f7e6951e987e50` đạt **2/2 Chromium và 414/414 đối chiếu SQL**. Một bài Chromium giữ cùng tab Thủ quỹ qua restart, tải lại và đăng nhập lại. Bài còn lại xác nhận đơn ngày hai. Trước lần restart và ngay sau tra cứu/replay, SQL ghi cùng command, source, line và ID của hai dòng sổ, với custody −20.000 VND và fund +20.000 VND. Ảnh dòng của hai tenant khớp trước khi Kho ghi lệnh tiếp theo. Các digest opening theo lô, phân bổ lô, ShipTo và quy đổi ngày một khớp ở đầu ngày hai và sau Confirm ngày hai.
- `Run-DevFunctionalRegression.ps1` run `15039bd6e91f4feeb268928c00209a5c` đạt E-SIM-01, **.NET 355/355, đối chiếu nền 175/175, Chromium 29/29 và profile F 8/8**. Danh mục LocalDB trước/sau khớp. `Verify-Business.ps1` và kiểm dependency cũng đạt.
- [Gói bằng chứng sau review](app-03-9-evidence/pr143-review-d48d17db.zip) chứa hai manifest run, đối chiếu SQL, JUnit, TRX, log và `SHA256SUMS` cho 90 artifact. SHA-256 của ZIP là `52C96772B31892A3824D55EFC3210FBCA25E92C49B82508E47DA89CF6F2E0D2C`. SHA-256 của `sql-review.json` APP-03.9 là `FBAD654288ED3E690BAE2C12824B043B651D079EE1BE1C14BDD8515CA2935E23`. SHA-256 của manifest F09 là `3146C1DFA693B12AB4AD2E3E1D2D700A0F2B0F8D37CB56905FF4407767F3BD33`.
- Lượt thao tác trực tiếp của operator vẫn `NOT_RUN`.

## Nghiệm thu cuối PR #143

Phạm vi synthetic DEV trên laptop Windows, SQL Server LocalDB 2019 `MSSQLLocalDB`, SDK .NET `10.0.404`, Node `26.13.1`, npm `12.2.3` và Playwright Chromium `1.65.1`. Lượt trực tiếp và hồi quy chạy trên source `d349acaa994df3e6039fc6d5b869310549ee6899`, tree `0ea8176d46d7747890b3ab9cb56b31e7864357a7`. Source thực thi khớp `d48d17dbca3b338aaf7ef09922b842a57eaacc4b` trong review vòng 2. [Review độc lập xác nhận 3/3 findings đã xử lý](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/143#pullrequestreview-6038514791).

- `VerifyAcceptance` với `APP03_ACCEPTANCE_DIRECT=1`, run `69abd36c5064476387f4284eff20f7a6`. Hai bài Chromium đạt. Owner ghi một viếng thăm **Không có đơn** qua giao diện sau khi Confirm đơn ngày hai. SQL ghi ID `c39cb899-15a8-417f-ac46-95e6eb3a4054`, command `4fd2a1e7-1937-4bc6-bbdf-a36c7ffb39c6` và actor `c1-dev-sales`. Manifest ghi `automated: PASS`, `directBrowser: PASS`. SQL đạt **450/450**. So với lượt tự động sau review, lượt Owner thêm 36 phép đối chiếu về visit, số dư và snapshot.
- Runner thu hồi cả hai database theo ownership/nonce và dừng toàn bộ API/Vite của phiên. Danh mục LocalDB trước và sau cùng có 34 database và khớp tên. Manifest phiên đã được thu hồi.
- `Run-DevFunctionalRegression.ps1` chạy sau lượt Owner. Run `316f5eddfd9c41ee806ddce908f7368a` đạt E-SIM-01, .NET 355/355, nền SQL 175/175, Chromium 29/29 và profile F 8/8. Danh mục LocalDB trước/sau khớp. `Verify-Business.ps1`, `npm --prefix web run test:dependencies` và hai lượt audit npm qua HTTPS đạt.
- [Gói nghiệm thu bất biến](app-03-9-evidence/pr143-closeout-d349acaa.zip) chứa 98 artifact đã đối chiếu SHA-256, gồm biên nhận app/F09, đối chiếu SQL, `direct-visit.json`, log, JUnit, TRX, ảnh Chromium tự động và biên nhận thu hồi. Runner kết thúc ngay khi xác nhận bản ghi nên Owner chưa kịp chụp ảnh riêng của thao tác trực tiếp. Bằng chứng của thao tác này là biên nhận SQL gắn actor, command ID và lý do riêng của run.
- SHA-256 ZIP `7B49E845C16A1F3CFD6ECD2D12C9BC8E4241959DC5EF9944F7A962CF7320A47C`. SHA-256 `sql-review.json` `1B1EADEEC29B4A1B10883940F93615ABE59C4334FF7B52FD4AE4333E4DDD3F9D`. SHA-256 manifest F09 `05B2B635FF8D2BE95D0C8BB9798CA51C21ECE7398D3719337BECC20DD87DA222`.
- Trên HEAD review `d349acaa994df3e6039fc6d5b869310549ee6899`, 10/10 workflow đạt. APP-00A foundation run `45385147365`, attempt 2, job `validate` đạt sau khi lượt đầu vướng khởi tạo LocalDB. [Biên nhận APP-00A foundation](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45385147365/attempts/2).
