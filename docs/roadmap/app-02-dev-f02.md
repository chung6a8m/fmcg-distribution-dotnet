# DEV-F02 — Tạm ứng và hoàn ứng online

AC-20, BR-13, AP-06, AP-07 và SOP-08. Tạm ứng có chứng từ phát, người nhận là actor Sales trong fixture. Finance phát ứng và duyệt khoản chi do nhân viên đề nghị. Nhân viên đưa đề nghị hoàn. Cashier độc lập xác nhận phần thực nhận với tham chiếu kiểm đếm. Bảo toàn người lập, người duyệt, chứng từ, lý do xác nhận, nguồn, số tiền và UTC. Sổ tạm ứng độc lập với AR, CustomerCredit, custody và sổ quỹ thu hộ của khách hàng.

Giả định DEV là VND nguyên, policy `DEV-F02-v1`, Finance phát theo chứng từ thử, thu ngân ghi nhận mỗi đợt theo chứng từ kiểm đếm khác nhau. Danh sách nhân viên thử chịu quyền hiện hành cùng tenant. Số dư = ứng − chi đã duyệt − hoàn đã nhận. Pending expense và pending return offer độc lập với số dư. Dành phần hoàn đang đề nghị để chặn đề nghị hoặc duyệt chi vượt số dư. Không có tác động offline, ngân hàng thật, tiền thật hoặc sổ kế toán ngoài.

## Kỳ vọng trước thực thi · v1

| Ca | Expected |
| --- | --- |
| F02-01 · AC-20 | Phát 500000, đề nghị chi 300000 (vẫn 500000), duyệt còn 200000, offer 200000 (vẫn 200000), thủ quỹ nhận 100000 hai lần (100000 rồi 0). Số dư chuẩn theo golden-oracles AC-20. |
| F02-02 · quyền và nguồn | Nhân viên đọc khoản của mình. Tenant B và Sales khác không có quyền thấy hay tác động nguồn A. Nhân viên không duyệt chi hay nhận hoàn. Grant phải còn hiệu lực. Từ chối over-accept và chứng từ dùng lại. |
| F02-03 · lặp/cạnh tranh | Retry cùng payload trả cùng nguồn, khác payload 409. Hai lệnh khác mã tranh số dư chỉ một effect nếu tổng vượt phần còn lại. |
| F02-04 · browser/SQL | Thao tác qua `/sales/advances` với mã DEV của từng vai. Đọc số dư theo từng checkpoint. So sánh raw row image trước/sau cho ReceivableOpenItem, CashReceipt, ReceiptAllocation và LedgerEffect. |

API synthetic có session, danh sách, đọc chi tiết, hoàn tất lệnh và lookup outcome. SQL có nguồn phát, journal và durable command. UI dùng token DEV từng actor và giữ mã lệnh khi mất ACK. Fixture và oracle được chốt trước run. Mốc giờ UTC từ server, giới hạn từ trước đến sau test do run kiểm soát.
