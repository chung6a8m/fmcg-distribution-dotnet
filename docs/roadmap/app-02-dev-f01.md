# DEV-F01 — Visit online

AC-17, BR-22, POL-22 và SOP-12. Trong Sales A DEV, actor có Sales grant hiện hành chọn Customer đang hoạt động và Outlet tùy chọn thuộc Customer. Ghi outcome, lý do và quan sát tùy chọn. Mỗi lệnh hoàn tất một visit với actor và giờ UTC từ server. Danh sách và hoàn tất theo Customer được phân công cho actor. Gán Customer bằng fixture SQL trong DEV. Outlet tùy chọn thuộc phạm vi Customer đã gán. Giả định DEV v1 gồm NO_ORDER và NOT_MET, lý do 1–160 ký tự, quan sát tối đa 500 ký tự. Return request theo SOP-12 thuộc luồng trả hàng DEV-F05.

Giữ UI Sales A tại `/sales`, dữ liệu ở `dbo.F01Visit`, C.1 GET/POST `/visits`, GET `/visits/customers` và tra cứu kết quả gốc qua `/visits/commands/{id}`. SQL lưu visit và outcome cùng transaction. Retry cùng actor/id/body trả ID đã lưu. Payload khác cùng ID trả 409. Token hợp lệ thiếu quyền hoặc quyền đã thu hồi trả 403. Customer/Outlet không khớp trả 404. Thiếu lý do hoặc outcome không hợp lệ trả 400.

## Kỳ vọng trước thực thi · v1

- F01-01 Browser: Customer C2-FIXTURE, thêm Outlet Visit DEV, hoàn tất NO_ORDER với lý do Hẹn lần sau và quan sát Có ghi chú. Mất ACK sau 200. Tra cứu cùng lệnh hoặc gửi lại trả cùng visit. Reload và kết nối lại đọc visit. SQL tenant-a, actor c1-dev-sales, Customer và Outlet đúng, số visit tăng đúng 1.
- F01-02 API/SQL: Customer không Outlet hoàn tất NOT_MET với lý do Closed, quan sát null. Cạnh tranh cùng ID/payload lưu một dòng. Thử sai lý do, sai outcome và Outlet không khớp. Tenant B đọc danh sách riêng rỗng và ghi vào Customer A bị từ chối. Actor warehouse và actor Sales bị thu hồi không được đọc hoặc ghi. Gỡ phân công với Sales grant vẫn còn thì danh sách rỗng, recovery/replay của lệnh đã hoàn tất bị từ chối.
- F01-03 Browser offline: nút hoàn tất tắt, số POST không tăng, bật lại không tự gửi. Không phát sinh order/sale/AR/stock. So sánh row image SQL trước và sau trên AOrder, SaleDelivery, ReceivableOpenItem, PhysicalStockMovement, PhysicalStockBalance và LedgerEffect.
- Fixture v1 được ghim bằng Playwright spec và ca tích hợp SQL. Dữ liệu visit không tiền, không lượng, mốc UTC trước/sau theo run ID. Bộ expected nằm ở đặc tả này và các assertion trong test. So sánh chênh lệch số dòng và row image trên scratch giữa hai mốc, lấy actor qua token do server xác thực.
