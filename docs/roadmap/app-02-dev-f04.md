# DEV-F04 — Giá, chiết khấu và thuế

AC-19 phần tính giá, BR-08 và POL giá/thuế. Tái sử dụng giá/thuế và điều khoản được duyệt C.2 theo Customer/SKU, thêm điều khoản chiết khấu cấp chứng từ và chế độ giá gồm/chưa VAT trong profile DEV. Đóng băng thông số và line amounts khi Confirm. Khi giao từng phần, nguồn Delivery nhận chênh lũy kế của net và tax nguồn Confirm, làm tròn các phần lũy kế theo đúng phân số base quantity. Gross của phần giao bằng net cộng tax. Phiên quy tắc VND_NET_TAX_CUMULATIVE_V1 lưu trong snapshot. Chỉ các quote ID được liên kết với plan VND có hiệu lực. F08 dùng snapshot Delivery cho credit/refund. Hóa đơn giả lập giữ invoiceEligible=false.

## Fixture và kỳ vọng trước thực thi · v1

Hai tenant LocalDB, A EXCLUSIVE và B INCLUSIVE. Chuẩn tiền VND nguyên và làm tròn 0 chữ số, half-away-from-zero. Giả định P đặt 3 EA, Q 1 EA. Giá P/Q lần lượt 66666.6667/100000 (chưa VAT) hoặc 72000/110000 (đã VAT). Làm tròn giá dòng trước chiết khấu. Cả hai xuất phát từ base ghi trong golden AC-19. Thuế synthetic 8%/10%; chiết khấu chứng từ 10001, phân bổ theo largest remainder với tie-break theo SKU nguồn. Scope này áp dụng chiết khấu cố định cấp đơn. Không chồng chiết khấu đơn vị.

- F04-01/02 · AC-19: mô hình độc lập đọc locked golden. EXCLUSIVE net/tax/gross 289999/25134/315133. INCLUSIVE 290797/25202/315999. Kiểm chi tiết P/Q và bảo toàn chiết khấu 10001.
- F04-03 · BR-08: Confirm và mọi đọc sau đó giữ hợp đồng đã chốt khi có phiên giá hoặc discount mới. Delivery giữ snapshot nguồn. Kiểm lũy kế theo lượng ở P, đặc biệt 64444/5156/69600 ở lần đầu của EXCLUSIVE. Nhánh hàng trả và chi refund ở F08.
- F04-04: từ chối kế hoạch thiếu/không hiệu lực, tự duyệt hoặc discount vượt tổng, tenant/actor không đúng. Giữ idempotency của C.2/C.4, không trộn currency trong profile cùng lượt.
- UI Sales A hiển thị giá, kiện tính thuế, discount, net/tax/gross theo snapshot. Dùng OrderWorkspace/DeliveryWorkspace và API/SQL hiện hành trong scratch. Fixture pin ngày nghiệp vụ DEV 23/11/2026 và hai policy độc lập.
