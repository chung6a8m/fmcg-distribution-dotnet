# DEV-F01 — Visit online

AC-17, BR-22, POL-22 và SOP-12. Trong Sales A DEV, actor có Sales grant hiện hành chọn Customer đang hoạt động và Outlet tùy chọn thuộc Customer. Ghi outcome, lý do và quan sát tùy chọn. Mỗi lệnh hoàn tất một visit với actor và giờ UTC từ server. Danh sách và hoàn tất theo Customer được phân công cho actor. Gán Customer bằng fixture SQL trong DEV. Outlet tùy chọn thuộc phạm vi Customer đã gán. Giả định DEV v1 gồm NO_ORDER và NOT_MET, lý do 1–160 ký tự, quan sát tối đa 500 ký tự. Return request theo SOP-12 thuộc luồng trả hàng DEV-F05.

Giữ UI Sales A tại `/sales`, dữ liệu ở `dbo.F01Visit`, C.1 GET/POST `/visits`, GET `/visits/customers` và tra cứu kết quả gốc qua `/visits/commands/{id}`. SQL lưu visit và outcome cùng transaction. Retry cùng actor/id/body trả ID đã lưu. Payload khác cùng ID trả 409. Token hợp lệ thiếu quyền hoặc quyền đã thu hồi trả 403. Customer/Outlet không khớp trả 404. Thiếu lý do hoặc outcome không hợp lệ trả 400.

## Kỳ vọng trước thực thi · v1

- F01-01 Browser: Customer C2-FIXTURE, thêm Outlet Visit DEV, hoàn tất NO_ORDER với lý do Hẹn lần sau và quan sát Có ghi chú. Mất ACK sau 200. Tra cứu cùng lệnh hoặc gửi lại trả cùng visit. Reload và kết nối lại đọc visit. SQL tenant-a, actor c1-dev-sales, Customer và Outlet đúng, số visit tăng đúng 1.
- F01-02 API/SQL: Customer không Outlet hoàn tất NOT_MET với lý do Closed, quan sát null. Cạnh tranh cùng ID/payload lưu một dòng. Thử sai lý do, sai outcome và Outlet không khớp. Tenant B đọc danh sách riêng rỗng và ghi vào Customer A bị từ chối. Actor warehouse và actor Sales bị thu hồi không được đọc hoặc ghi. Gỡ phân công với Sales grant vẫn còn thì danh sách rỗng, recovery/replay của lệnh đã hoàn tất bị từ chối.
- F01-03 Browser offline: nút hoàn tất tắt, số POST không tăng, bật lại không tự gửi. Không phát sinh order/sale/AR/stock. So sánh row image SQL trước và sau trên AOrder, SaleDelivery, ReceivableOpenItem, PhysicalStockMovement, PhysicalStockBalance và LedgerEffect.
- Fixture v1 được ghim bằng Playwright spec và ca tích hợp SQL. Dữ liệu visit không tiền, không lượng, mốc UTC trước/sau theo run ID. Bộ expected nằm ở đặc tả này và các assertion trong test. So sánh chênh lệch số dòng và row image trên scratch giữa hai mốc, lấy actor qua token do server xác thực.

## Chạy tương tác và tái tạo

Cài dependency bằng `npm --prefix web ci`. Chạy `./scripts/Run-App02C1.ps1 -Increment DevF01` rồi mở URL `/sales` được in, dán mã DEV từ clipboard và chọn **Viếng thăm**. Fixture tạo Customer C2-FIXTURE và phân công cho actor DEV. Có thể thêm Outlet thử ở mục Khách hàng hoặc để trống. Ctrl+C để thu hồi DB do lượt chạy sở hữu.

Kiểm chứng với `./scripts/Run-App02C1.ps1 -Increment DevF01 -Verify` và `dotnet test --solution FmcgDms.slnx -c Release -- --filter-class '*App02DevF01Tests' --report-trx`. Browser plugin có skill riêng không có trong phiên này. Dùng Chromium Playwright đã ghim của repo.

## Biên bản DEV

- F01-01, F01-03 · AC-17 · PASS. Windows LocalDB và Chromium. Run `0eca9c10168d4a96ac0b830b0a05e8c8` từ commit `36411b18c073d1ea7d1c97c6921f30884a271af3`, tree `6b354a668f551bacaf428f8557faefee7cefe962`. V1 Playwright SHA-256 `FA932C327318EF028FF0C5F4059ABB76C1BD5F79E306F27A383E8DB3741F6C66`. Mất ACK sau POST 200, sau reload tra cứu bằng mã cũ và hiển thị lại cùng visit. Offline không có POST. Một lượt browser đạt gồm desktop và kiểm tra bố cục 390 px.
- SQL trước/sau cùng run. Expected 1/0 visit ở A/B và 12 row image giữ nguyên. Actual 1/0, tenant-a / c1-dev-sales / C2-FIXTURE / Outlet Visit DEV, lý do và quan sát đúng. Diff 0 ở sáu bảng mỗi tenant. Biên nhận chứa VisitId, CommandId, UTC, số dòng và hash ở hai mốc.
- Artifact tái tạo bằng lệnh Verify. SQL ở `TestResults/dev-f01/<run-id>/sql-result.json`, JUnit và hai ảnh ở `web/test-results-app02c5/<run-id>/`. SHA-256 SQL `C684F35552E5B4621070638400B15E33D63A0A9878AACEBE60A04D98560D8B0C`. DLL SHA-256 `D0CF1DA8E16549A5637F6A91842DF1CC63CACBD4E18930E6CA65E7064A88ACA7`. Workflow APP-00A giữ cả hai nhóm artifact.
- Ca red ban đầu trả 404 vì thiếu API và browser thiếu nút Viếng thăm. Review phát hiện rủi ro mốc migration D.3/D.4 và phạm vi phân công. Bản triển khai dùng migration profile riêng, quyền Sales và liên kết phân công hiện hành. Hoàn tất visit là bất biến. Bằng chứng EV-13 thực tế và ca thiết bị vật lý vẫn chờ theo hồ sơ APP-02E.
- F01-02 · AC-17 · PASS trong hồi quy `dotnet test --solution FmcgDms.slnx -c Release -- --report-trx` trên source `36411b18c073d1ea7d1c97c6921f30884a271af3`. Expected và actual khớp cho visit không Outlet, một dòng qua hai POST đồng thời, 400/404/409, từ chối chéo tenant, sai vai, gỡ phân công và thu hồi grant. Hai tenant giữ nguyên SQL trước/sau. Tổng 346/346 ca, 0 lỗi và 0 skip. Fixture API SHA-256 (byte checkout) `36095AF9512C91881464FF32F35BC6323AAAC038FA586296E969BB77489ED964`. TRX tái tạo tại `TestResults/FmcgDms.Api.Tests_net10.0_x64.trx`, SHA-256 lượt local `9AFE0CDA21527EB44DD9A129BBE011A53EFD3AEADF2D7019DF591E8F8DFDCB66`.
