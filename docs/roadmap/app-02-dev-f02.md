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

## Chạy tương tác và tái tạo

Cài dependency `npm --prefix web ci`. Chạy `./scripts/Run-App02C1.ps1 -Increment DevF02`, mở đường dẫn `/sales/advances` in trên terminal. Terminal đổi mã DEV trong clipboard với 1 (Finance), 2 (nhân viên) và 3 (Cashier). Trong trang, bấm Đổi phiên DEV rồi kết nối bằng mã mới. Ctrl+C dọn DB scratch. Dùng `-Verify` để chạy Chromium và SQL oracle. Thu artifact ở `TestResults/dev-f02/<run-id>` và `web/test-results-dev-f02/<run-id>`.

## Biên bản 23/11/2026

| Ca | Expected và actual | Trạng thái |
| --- | --- | --- |
| F02-01 · AC-20 | API/Sales đọc 500000 sau phát và sau đề nghị chi, 200000 sau duyệt và sau offer, 100000 và 0 sau hai lần nhận. SQL hai tenant giữ nguyên bốn bảng sổ khách hàng. | PASS |
| F02-02 · AP-06/07 | Từ chối chéo tenant, nhân viên ngang vai, thiếu lý do, actor sai vai, tự duyệt/tự nhận và grant thu hồi. Cashier có claim Finance cũ vẫn phục hồi bằng quyền hiện hành của chính lệnh. | PASS |
| F02-03 · BR-13 | Replay giữ source ID, payload/evidence trùng sai ngữ cảnh bị chặn. Hai mã tranh chấp phần dư có một 200 và một 409. | PASS |
| F02-04 · AC-20 | Chromium 1/1 qua ba vai, online guard, ACK mất và tra cứu sau reload. SQL count tenant A/B 1/0, năm dòng journal theo đúng actor, approval/accept có lý do và nguồn, 8/8 row image sổ khách hàng giữ nguyên. Diff số dư bằng 0 ở cả năm mốc oracle. Fixture F02 dùng sổ khách hàng ban đầu trống. | PASS |

Browser/SQL run `9979b098631d41138909ff55287a46f6` trên source `f6b462503b75fbe693de4fcc8886157b2b46e19e`, tree `8049dec533d51d9ddcbddcad2c74ecbcb0a870c4`. Oracle golden SHA-256 LF `8273CE3114EBFF437A616B3E77022707CD3629EE743870AA745ACCE75E1562CF`. Fixture browser v1 SHA-256 bytes `DB8FE98BFB5A70D56E306B8344A270B9BBCB045DFCB35ED09846CD7B32C0227D`, API fixture `CAAB68498FD93E775C02E6233DB590DB8C2021238B4982E6E98E49D5F61DD96D`. SQL receipt SHA-256 `2A7377024B774A8EEDC2243D0295C1C9A8F64959FA1F9B826AF8A8C8E31F9840`. Lượt browser có một log SQL hủy GET khi điều hướng, case và oracle hoàn tất. Workflow APP-00A tái chạy và lưu artifact.

Hồi quy `dotnet test --solution FmcgDms.slnx -c Release -- --report-trx` trên source đó đạt 347/347, 0 lỗi và 0 skip. Red case ban đầu nhận 404 trước khi có route. Review bổ sung lý do AP-06/07, kiểm same-tenant peer và xác thực vai đang hiệu lực khi phục hồi. Kết quả EV-06 thực vẫn theo hồ sơ đã ký.

### Hồi quy form sau CI

Runner GitHub run 45097743285 dừng tại bước F02-04. DOM ghi hai trường bị xóa khi GET khoản tạm ứng đầu tiên trả về và form đổi khóa. Lượt LocalDB `5a360dc14a684658b0568e50a5118d27` tái hiện với GET bị giữ. Sau khi vô hiệu hóa trường nhập đến khi dữ liệu nguồn sẵn sàng, lượt `cdb9b92c4d774869a9e89487e62817f9` đạt 1/1 Chromium, đủ năm mốc AC-20 và 8/8 ảnh dòng sổ khách hàng giữ nguyên. Bản cập nhật spec giữ hành vi chậm tải trong CI. [Run hosted](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45097743285)
