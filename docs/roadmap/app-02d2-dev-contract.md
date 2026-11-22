# APP-02D.2 — Hợp đồng DEV bán và giao hàng B

**v0.1 · 22/11/2026 (Asia/Bangkok) · `DOCS_ONLY / OWNER_APPROVAL_PENDING / NOT_RUN`**

## Điều kiện bắt đầu

| Điều kiện | Kết quả rà soát |
| --- | --- |
| D.1 đã ký và có trên main | Đạt. Owner ký DEV trên `ff6d2b9`. PR #112 merge tại `d293609`. Hồ sơ đã lên main qua PR #113. Mốc rà soát là `28b83ae6d9d6ac14bd7ac8a527585558b008cea8`. |
| Windows LocalDB và toolchain | Có instance riêng `(localdb)\MSSQLLocalDB` 15.0.4382.1, .NET 10.0.404, Node 26.13.1. Instance đang dừng khi rà soát. Bằng chứng chạy hai tenant gần nhất thuộc D.1. |
| Tương thích mô hình manifest | Có đường triển khai. D.1 nạp B qua manifest từng phần `IsC3=1`. Lệnh B20 hiện hữu chỉ nhận `IsC3=0`. D.2 cần điều kiện chọn nguồn riêng và hồi quy legacy. |
| Chính sách và oracle | Chờ Owner duyệt hợp đồng, [ma trận v0.1](app-02d2-dev-acceptance-matrix.md) và dữ liệu giả lập ghi trong đó. Chốt phiên bản và SHA-256 của fixture và expected-results trước khi chạy nghiệm thu. |
| Phạm vi được phép | Chuẩn bị tài liệu và rà soát chỉ đọc. D.2 source, migration, UI và lượt chạy ma trận chờ Owner duyệt. |

[D.1 đã ký](../closeout/2026-11-22-app-02d1-localdb.md) · [Phạm vi D.0](app-02d0-dev-contract.md) · [Owner decision](../decisions/owner-app02d-dev-2026-11-22.md)

## Phạm vi và thiết kế đề xuất

Một lệnh B SaleDeliveryReceipt online ghi cùng giao dịch: sale/delivery, issue hàng và giá vốn, receipt/allocation khi có, AR theo Customer, sử dụng CustomerCredit đã giữ, B right và B credit grant, cùng các ledger effects có nguồn. Bản DEV dùng một dòng SKU/EA, một dòng manifest B đã nạp, giá fixture do server xác định, đơn vị `SYNTHETIC_UNITLESS`, và `InvoiceEligible=false`.

Đề xuất tái sử dụng phép tính, xác thực thanh toán và lõi posting giao dịch của APP-01C.2, với một điểm vào D.2 riêng có điều kiện chọn nguồn D.1 rõ ràng. Mở rộng trực tiếp predicate của B20 sẽ đưa manifest D.1 vào đường legacy đang được bảo vệ. Tạo một sổ hoặc bộ ghi giao dịch riêng sẽ tăng rủi ro lệch logic đối soát. D.2 duy trì một tập bảng nguồn, stock right, physical balance và ledger hiện hữu. Nếu cần migration, dùng thay đổi tiến nhất quán, giữ khóa liên kết và dữ liệu lịch sử.

- **Chọn nguồn.** Đối chiếu tenant/trip/SKU, BQuota, right B, manifest line `IsC3=1` của lô D.1 và dòng nguồn nạp cùng dấu vết OUT/IN. Lượng bán phải đồng thời hợp lệ theo phần đã nạp còn lại của dòng manifest, right B còn lại và hàng thực trên xe. Tính giá vốn bình quân của location theo quy tắc hiện có, đối chiếu lượng/giá trị vật lý với nguồn trước khi ghi. Right A và số lượng, giá trị MAIN giữ nguyên.
- **Khách hàng và actor.** Customer đang hoạt động là bên nợ. Outlet tùy chọn và phải thuộc Customer nếu được cung cấp. ShipTo snapshot bất biến phải thuộc Customer và giữ đúng bản đã chốt lúc xác nhận. Ghi liên kết bất biến từ nguồn D.2 tới `C1ShipToSelection` cho snapshot đã chọn, để SQL truy ra version và Outlet tùy chọn; Outlet gửi kèm phải khớp selection, kể cả khi cùng Customer. Người bán giữ quyền Sales hiện hành và dùng chung writer giao hàng `C4DeliveryWriter` theo tenant/trip với luồng A. `C3TripWriter` vẫn kiểm soát thao tác nạp của Warehouse. Tenant và actor lấy từ danh tính được server xác thực. Kiểm quyền hiện hành trước ghi và khi tra cứu hoặc replay. Reviewer cash fixture khác Sales; Finance/maker/checker theo D.1.
- **Tính tiền.** `P = qty × giá đã chốt`, `R = min(P, tiền xác minh áp vào sale)`, `C = CustomerCredit đã giữ hợp lệ`, với `0 ≤ R+C ≤ P`. `E = P−R−C` bằng AR ròng và lượng B grant tiêu thụ. Grant còn lại giảm đúng `E`, phần đã dùng và đã release được đối chiếu sổ nguồn. Với `E>0` yêu cầu grant đúng tenant/trip/Customer/ShipTo. Tại lúc ghi, theo đồng hồ DEV phải có đúng một quyết định hiệu lực cho scope credit tenant/trip/Customer/pool/currency, với ID và version khớp chứng cứ cấp grant D.1 đã lưu. `E=0` không tiêu thụ hoặc giải phóng grant. Fixture không đặt kỳ hạn B, thuế hay chiết khấu mới. Quy tắc kỳ hạn A hiện hành tiếp tục áp dụng cho A.
- **Bằng chứng thanh toán.** Tiền mặt cần chứng cứ đã xác minh, lưu sẵn trong fixture có phạm vi và reviewer riêng, đúng số tiền, chưa dùng. Bank pending chỉ lưu tham chiếu chưa xác minh, `R=0`, không tạo confirmed receipt, phân bổ hay custody. CustomerCredit sử dụng reservation duy nhất có nguồn receipt dư đã xác minh, đúng phạm vi và còn đủ số dư. Với receipt dư, tăng custody theo tiền mặt thực nhận, áp tối đa `P` và ghi phần dư thành CustomerCredit có nguồn. Quỹ, tạm ứng và số dư AR cũ giữ nguyên.
- **Giao dịch và phục hồi.** Khóa command/trip và các dòng cạnh tranh bằng khóa thuộc transaction theo thứ tự chung với writers hiện hành. Hoàn tất command, nguồn, stock/COGS, AR, các chứng từ thanh toán/credit và ledger trước một commit. Mất ACK sau commit trả kết quả/nguồn cũ qua quyền hiện hành. Cùng command với payload khác conflict; command mới dùng lại nguồn hoặc chứng cứ một lần không phát sinh ghi lặp. Khi rollback kỹ thuật, cùng command còn dùng được. Từ chối nghiệp vụ có thể lưu trạng thái terminal hoặc audit được chỉ định, các bảng nghiệp vụ bảo toàn.
- **Kênh hóa đơn.** Chỉ mở trong IntegrationTest synthetic đã xác minh hai DB, nonce và clock; profile giả lập do server cấp, không có cờ request để vượt kiểm tra. Các sale hợp lệ ở DEV có `InvoiceEligible=false` và không có yêu cầu phát hành. Đường giao thật thiếu kênh hợp lệ bị `B_NO_CHANNEL`; chứng cứ ngân hàng pending không tự nâng thành verified. Sự kiện giao thực ngoài luồng và bên ngoài phạm vi DEV đi theo quy trình case hiện hành.
- **UI tối thiểu.** Dùng workspace Sales và controls đã duyệt. Hiển thị Customer/Outlet/ShipTo, dòng B khả dụng, payable, nguồn thanh toán, phần AR và trạng thái command. Dùng preview từ server, vết nguồn sau commit và khả năng truy hồi cùng command. UI offline khóa ghi, không có hàng đợi ghi nghiệp vụ. Với test mất ACK, giữ command identity và tra cứu/submit lại online theo quyền hiện hành.

[BR-11/12/13/15/16/18](../../business/business-rules.md) · [SOP-03](../../business/workflows/module-b-online.md) · [AC-09](../../business/acceptance.md) · [B20 legacy](../../src/FmcgDms.Api/Persistence/App01c2BPostingService.cs) · [Thanh toán local](../../src/FmcgDms.Api/Persistence/App01c2LocalPayments.cs) · [UI baseline](../ui-ux/README.md)

## Nội dung cần Owner duyệt

1. Phạm vi nguồn D.1, tái dùng lõi B20 với đường ghi D.2 riêng và giữ nguyên legacy; giao dịch DEV một dòng cùng Customer, Outlet tùy chọn và ShipTo bất biến.
2. Các bằng chứng thanh toán do fixture cung cấp, quyền writer/Finance, grant scope và thời điểm hiệu lực, giá synthetic và quy tắc không phát hành hóa đơn.
3. Fixture, 18 ca và 36 lượt ca–tenant đề xuất, đồng hồ đã khóa, kỳ vọng SQL, cách thu hồi test DB và phạm vi kiểm tra hồi quy.

D.2 hoàn thành nghiệm thu DEV sau khi đủ ca có đối soát độc lập không sai lệch, CI trên mã nguồn cuối và Owner ký kết quả. D.3 giữ phạm vi giao nhận tiền với thủ quỹ, trả hàng trên xe về kho và luồng Mixed hoàn chỉnh. D.4 giữ mô phỏng tích hợp hóa đơn/kế toán. EV-06/08 thực, vận hành B_ONLINE/Mixed, EV/P0, Pilot và Go-Live giữ các điều kiện hiện hành. C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`, PREPROD `FOUNDATION_ONLY / NO_GO`, B.1/B.9 và RUNTIME-01/02 giữ nguyên.
