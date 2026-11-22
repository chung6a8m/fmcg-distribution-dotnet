# APP-02D.3 — Hợp đồng DEV đối soát chuyến Mixed

**v0.1 · 22/11/2026 (Asia/Bangkok) · `DOCS_ONLY / OWNER_APPROVAL_PENDING / NOT_RUN`**

## Điều kiện bắt đầu

| Điều kiện | Kết quả rà soát |
| --- | --- |
| Tiền nhiệm | D.1 và D.2 đã được ký nghiệm thu DEV, có trên `main`. D.2 được ký trên `9953935`, merge PR #114 tại `facc68a`. Mốc rà soát `0c0e2514d440d1dee472b599fc9c6a558c5d6709` gồm hồ sơ đã ký. C.1–C.6 cung cấp luồng A đã kiểm chứng DEV. |
| Môi trường | Máy có Windows LocalDB `(localdb)\MSSQLLocalDB` 15.0.4382.1, .NET 10.0.404 và Node 26.13.1. Instance đang dừng tại thời điểm rà soát. Hai tenant và runner có bằng chứng DEV từ D.2. |
| Đầu vào kỹ thuật | APP-01D/01E.5 cung cấp mô hình handover, unload và oracle độc lập. Unload legacy chọn một manifest `IsC3=0`; phạm vi D.3 cần chọn toàn bộ manifest hiện đại của trip và bảo toàn các dòng lịch sử. D.2 hiện dùng giá fixture 10, trong khi AC-03 yêu cầu 15. |
| Quyền thực hiện | D.3 chờ Owner duyệt hợp đồng và [ma trận, fixture v0.1](app-02d3-dev-acceptance-matrix.md). Sau phê duyệt, đóng băng fixture và expected-results bằng SHA-256 trước lượt chạy nghiệm thu. |

[D.2 đã ký](../closeout/2026-11-22-app-02d2-localdb.md) · [C.6 đã ký](../closeout/2026-11-22-app-02c6-localdb.md) · [D.0 roadmap](app-02d0-dev-contract.md) · [APP-01D](app-01d-cashier-unload.md) · [APP-01E.5](app-01e5-reconciliation-closeout.md)

## Phạm vi đề xuất

Chạy một trip Mixed qua C.2/C.3/C.4 và D.1/D.2, thêm đề nghị nộp tiền, thủ quỹ xác nhận độc lập và trả tồn xe về MAIN. D.3 dừng ở `RECONCILING` và cung cấp kết quả đối soát theo nguồn. Phạm vi gồm AC-03 T0–T6 và phần custody/quỹ của AC-10/21. Khi đối soát, đọc AR, CustomerCredit, tạm ứng, invoice và các mục còn chờ từ trạng thái phù hợp, giữ riêng tác động từng sổ.

Đề xuất dùng chung các giao dịch nguồn và ledger của APP-01D, với đường ghi D.3 có điều kiện chọn manifest hiện đại và quyền từng thao tác. Mở rộng predicate của unload legacy vẫn để lại giả định một manifest và một dòng cho mỗi right. Sao chép ledger hoặc tạo sổ tiền riêng tăng nguy cơ chênh giữa A, B và kho. D.3 tái dùng mô hình nguồn/ledger chung, thêm migration tiến cho các liên kết và ràng buộc thực sự cần thiết.

## Quy tắc triển khai để duyệt

### 1. Trip Mixed và oracle AC-03

- Một MAIN, một xe, một SKU/EA, hai quyền A40 và B30 trên cùng stock pool. A có Customer/ShipTo/giá/terms snapshot và chỉ tiêu thụ right A; B dùng Customer/ShipTo và chỉ tiêu thụ right B có nguồn D.1. Nạp A40 một lô, B10 và B20 hai lô trên cùng right B. Ghi riêng nguồn nạp, issue, receipt, AR, COGS và grant consumption.
- Giữ giá **15** cho A và B theo oracle AC-03. Với B, server chọn phiên bản giá fixture đã duyệt và ràng buộc riêng theo tenant/trip/SKU trong profile D.3 DEV. Đóng băng giá và provenance tại sale. Thiếu/sai phiên bản hoặc sai phạm vi bị từ chối trong profile D.3. Payload chỉ định giá hoặc chế độ phát hành không thay được quyết định của server. Luồng D.2 hiện hành tiếp tục được hồi quy với giá fixture **10**.
- Dùng một Sales writer theo `C4DeliveryWriter` cho A và B trên trip. Warehouse writer theo `C3TripWriter` cho nạp/trả xe. Cashier có role hiện hành và phạm vi trip riêng do server cấp. Cashier xác nhận khác actor gửi offer; Sales gửi offer là writer/custodian của trip trong fixture. Kiểm quyền hiện hành khi ghi, đọc và replay. Tenant lấy từ danh tính đã xác minh và DB binding. B luôn `SYNTHETIC_UNITLESS`, `InvoiceEligible=false`, thiếu kênh thật vẫn `B_NO_CHANNEL`.

### 2. Nộp và nhận tiền

| Hành vi | Điều kiện và hiệu ứng |
| --- | --- |
| OFFERED | Sales writer đưa số tiền dương, không vượt custody đã đối soát từ receipt cash confirmed và các lần nhận trước. Mỗi trip có tối đa một offer chưa được nhận trong DEV. Ghi offer và nguồn bất biến. Custody, quỹ, AR, allocation, CustomerCredit, advance và ledger tài chính giữ nguyên. |
| CASHIER_ACCEPTED | Cashier được gán trip, khác Sales, dùng chứng cứ kiểm đếm lưu sẵn theo tenant/trip, đúng thủ quỹ, số tiền, chưa dùng và thời điểm kiểm đếm không muộn hơn đồng hồ DEV. Bằng chứng pending chưa có bản ghi trong kho chứng cứ VERIFIED. So khớp và nhận nguyên số offer. Một giao dịch ghi acceptance và đúng hai bút toán `CUSTODY-OUT −x`, `FUND-IN +x`. Không áp lại tiền vào AR, không động tới credit/receipt/advance. |
| Sai chứng cứ hoặc xung đột | Chứng cứ thiếu, pending, ở tương lai, sai tiền, khác trip, tự xác nhận, thiếu quyền, dùng lại proof hoặc không đủ custody đều dẫn tới từ chối. Offer hợp lệ giữ nguyên để nhận lại bằng chứng cứ đúng. Giao dịch nhận thiếu, hủy và xử lý chênh lệch thực tế thuộc quy trình vận hành riêng. |

### 3. Trả xe và kết thúc phạm vi DEV

- Một command theo trip khóa cùng phạm vi với A/B posting, nạp, offer và accept. Rà tất cả manifest hiện đại thuộc đúng trip/xe/MAIN, kể cả dòng đã bán hết và phần B chưa từng nạp. Đối chiếu từng dòng nạp, issue và quyền theo nguồn. So sánh tồn lượng/giá trị tổng hợp với snapshot SQL, giữ thứ tự khóa xác định. Thiếu nguồn, sai vehicle/MAIN, drift, custody còn dương, offer còn chờ hoặc C.4 có bằng chứng giao đã nhận bền với command `RECEIVED` làm lệnh trả xe bị chặn. Trong scope D.3, khóa tiếp nhận bằng chứng C.4 mới cùng với khóa trả xe và kiểm trạng thái trip trước khi ghi bằng chứng; kết quả lịch sử vẫn truy hồi được theo quyền hiện hành.
- Tính phần dư từng dòng từ lượng đã nạp trừ issue. Đưa phần dư vật lý TRIP→MAIN bằng cặp bút toán lượng và giá trị, phân bổ giá trị còn lại theo lượng mỗi SKU/location, bốn chữ số thập phân và dòng cuối theo thứ tự ổn định nhận residual. Trả A vào MAIN và giữ phần A chưa giao ở `HELD`. Tăng `StockPool.OnHand` theo lượng trả, cộng allocated cho A trả đang giữ, giảm allocated theo **phần B còn giữ ở MAIN chưa từng nạp**. B hủy quyền bán dư đúng một lần theo mỗi right, kể cả right có nhiều lô hoặc chỉ còn lượng chưa nạp. Release tách nguồn theo right/quota gốc và liên kết đúng một phiên trả xe. Dòng trả vật lý giữ khóa tới manifest line và nguồn nạp. Với quyền B chưa từng nạp, chỉ tạo chứng cứ release theo right/quota, với lượng/giá trị vật lý 0, và không tạo manifest giả. Giữ quan hệ manifest của bản ghi unload legacy. Phần B tài chính chưa sử dụng không tự release khi trả xe.
- Kết thúc giao dịch hợp lệ ở `RECONCILING`, TRIP lượng/giá trị 0, A hold được bảo toàn và B quota hết khả dụng. Chặn ghi bán, giao, nạp và lặp trả bằng ID mới sau mốc này. Đọc lại báo cáo theo trip/nguồn vẫn theo quyền hiện hành. `CLOSED` và `CLOSED_WITH_OPEN_CASES` tuân theo quy trình đối soát vận hành riêng, gồm case owner, asOf và next action nếu áp dụng. Hàng khách trả, quarantine/QA, hoàn tiền và hoàn giá vốn thuộc phạm vi riêng.
- Dùng command/source identity hiện hành, khóa transaction và một commit. Replay cùng payload trả đúng outcome/source ban đầu, payload đổi bị conflict, source/witness cũ không ghi lại. Sau lỗi trước commit, cùng command có thể thử lại. Mất ACK được kiểm thử bằng đường loopback, restart và tra cứu theo quyền hiện hành. UI tái dùng workspace và controls đã duyệt, có ba vai trò Sales, Cashier, Warehouse. Mất mạng khóa ghi và giữ định danh phục hồi của command đã gửi.

[AC-03/10/21](../../business/acceptance.md) · [SOP-04 Mixed](../../business/workflows/mixed.md) · [SOP-08 custody](../../business/workflows/operations.md) · [UI baseline](../ui-ux/README.md)

## Owner cần duyệt

1. Profile giá B15 ràng buộc server dành riêng cho AC-03, giá A15 và terms fixture, cùng phân công Sales/Cashier/Warehouse trên trip.
2. Chính sách một offer đang chờ mỗi trip, cashier độc lập nhận nguyên số theo chứng cứ, và điều kiện custody bằng 0 trước trả xe.
3. Quy tắc trả nhiều lô, bảo toàn A hold, giải phóng B quota đúng một lần kể cả phần chưa nạp, giữ B grant tài chính theo nguồn và dừng ở `RECONCILING`.
4. Fixture, đồng hồ, 18 ca và 36 lượt ca–tenant đề xuất. Duyệt phạm vi AC-10/21 từ checkpoint đã phân bổ, kiểm tra nộp quỹ giữ nguyên AR, CustomerCredit và advance.

Nghiệm thu DEV yêu cầu đủ ca, đối soát SQL độc lập, CI trên nguồn cuối và chữ ký Owner cho kết quả. D.4 giữ phạm vi mô phỏng tích hợp hóa đơn/kế toán. C-PREPROD giữ `BLOCKED / NOT_VERIFIED`, C-E2E vật lý giữ `NOT_RUN`, PREPROD giữ `FOUNDATION_ONLY / NO_GO`. B.1/B.9, RUNTIME-01/02, EV/P0, Pilot, Go-Live và các cổng vận hành B_ONLINE/Mixed tiếp tục áp dụng.
