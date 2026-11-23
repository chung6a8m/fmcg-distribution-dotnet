# DEV-F04 — Giá, chiết khấu và thuế

AC-19 phần tính giá, BR-08 và POL giá/thuế. Tái sử dụng giá/thuế và điều khoản được duyệt C.2 theo Customer/SKU, thêm điều khoản chiết khấu cấp chứng từ và chế độ giá gồm/chưa VAT trong profile DEV. Đóng băng thông số và line amounts khi Confirm. Khi giao từng phần, nguồn Delivery nhận chênh lũy kế của net và tax nguồn Confirm, làm tròn các phần lũy kế theo đúng phân số base quantity. Gross của phần giao bằng net cộng tax. Phiên quy tắc VND_NET_TAX_CUMULATIVE_V1 lưu trong snapshot. Chỉ các quote ID được liên kết với plan VND có hiệu lực. F08 dùng snapshot Delivery cho credit/refund. Hóa đơn giả lập giữ invoiceEligible=false.

## Fixture và kỳ vọng trước thực thi · v1

Hai tenant LocalDB, A EXCLUSIVE và B INCLUSIVE. Chuẩn tiền VND nguyên và làm tròn 0 chữ số, half-away-from-zero. Giả định P đặt 3 EA, Q 1 EA. Giá P/Q lần lượt 66666.6667/100000 (chưa VAT) hoặc 72000/110000 (đã VAT). Làm tròn giá dòng trước chiết khấu. Cả hai xuất phát từ base ghi trong golden AC-19. Thuế synthetic 8%/10%; chiết khấu chứng từ 10001, phân bổ theo largest remainder với tie-break theo SKU nguồn. Scope này áp dụng chiết khấu cố định cấp đơn. Không chồng chiết khấu đơn vị.

- F04-01/02 · AC-19: mô hình độc lập đọc locked golden. EXCLUSIVE net/tax/gross 289999/25134/315133. INCLUSIVE 290797/25202/315999. Kiểm chi tiết P/Q và bảo toàn chiết khấu 10001.
- F04-03 · BR-08: Confirm và mọi đọc sau đó giữ hợp đồng đã chốt khi có phiên giá hoặc discount mới. Delivery giữ snapshot nguồn. Kiểm lũy kế theo lượng ở P, đặc biệt 64444/5156/69600 ở lần đầu của EXCLUSIVE. Nhánh hàng trả và chi refund ở F08.
- F04-04: từ chối kế hoạch thiếu/không hiệu lực, tự duyệt hoặc discount vượt tổng, tenant/actor không đúng. Giữ idempotency của C.2/C.4, không trộn currency trong profile cùng lượt.
- UI Sales A hiển thị giá, kiện tính thuế, discount, net/tax/gross theo snapshot. Dùng OrderWorkspace/DeliveryWorkspace và API/SQL hiện hành trong scratch. Fixture pin ngày nghiệp vụ DEV 23/11/2026 và hai policy độc lập.

## Chạy và biên bản DEV

`npm --prefix web ci` rồi `./scripts/Run-App02C1.ps1 -Increment DevF04`, mở `/sales`, tạo đơn với P và Q. Confirmation hiển thị bảng giá dòng và tổng. Dùng `-Verify` để dựng hai tenant và kích hoạt lượt Chromium/SQL. C.4 hiện VND đối với AR có snapshot giá F04.

| Ca | Kết quả trên Windows LocalDB và Chromium | Trạng thái |
| --- | --- | --- |
| F04-01/02 · AC-19 | A EXCLUSIVE 289999/25134/315133; B INCLUSIVE 290797/25202/315999. Bốn dòng P/Q và 10001 discount mỗi tenant khớp độc lập với golden. | PASS |
| F04-03 · BR-08 | Sau thêm giá và kế hoạch v2, API/SQL giữ các dòng v1. Giao P 1/3 A ghi 64444/5156/69600, AR69600, 1 source. Replay giữ số nguồn. | PASS |
| F04-04 | Vai warehouse bị chặn xác nhận, tenant B không xác nhận được source A, quote C2-SKU ngoài plan bị từ chối với 0 nguồn Confirm mới. Ca riêng về thiếu/hết hạn plan và tự duyệt chưa chạy. | PASS trong ca đã chạy; NOT_RUN cho các tình huống nêu trên |

Run `f0f3bfe3e87347e392f35f72f8f8f3f0`, Chromium 1/1 từ source `997fcccfd1dc2f10c76241ef2bfcad3bb6ee0433`, tree `59a57e521fc423ebe36e09988fc7371d761b7a1c`. Oracle locked byte hash `6850EA6F4C6AF4F4CDD65A4506BA7035D0A7771536FB54FD4F4809C918FB90E7`; browser v1 hash `23C2797DA1442E6460D21EDDD72CC94D6D9D2D3F7A38801671F9312F3E205D4C`; SQL receipt SHA-256 `5D67B4B95DAD480E7203A95C50EDBE906BD960B3F0181F17BE55ED2FD0112DC9`. `TestResults/dev-f04/<run-id>/sql-result.json` giữ expected/actual/diff. Dùng lệnh Verify để tái tạo và workflow APP-00A lưu artifact.

`dotnet test --solution FmcgDms.slnx -c Release -- --report-trx` đạt 351/351, gồm 5 ca số học AC-19. Review phát hiện khả năng diễn giải quote unitless thành VND và sai số chia trước khi nhân ở ranh đúng nửa đồng. Plan liên kết quote ID và hàm lũy kế dùng phép chia có dư nguyên. Lượt đầu vướng CHECK SQL đa cột, lượt kế thấy map bigint của oracle sai kiểu sau khi browser chạy đạt. Biên nhận sau sửa đã đối chiếu khớp. Chiết khấu cho chứng từ cần tạo bút toán bán bằng 0 giữ ngoài phạm vi posting hiện tại. Giữ invoiceEligible=false, VAT chỉ là giả định thử. Các ca hoàn và credit/refund của AC-19 thuộc F08.
