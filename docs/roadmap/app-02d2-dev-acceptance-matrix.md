# APP-02D.2 — Ma trận nghiệm thu DEV

**v0.1 · 22/11/2026 (Asia/Bangkok) · `OWNER_APPROVAL_PENDING / NOT_RUN`**

Theo [hợp đồng DEV D.2](app-02d2-dev-contract.md). Toàn bộ 18 ca chạy ở cả hai tenant giả lập, tổng 36 lượt ca–tenant. D2-16 chạy theo hai chiều A→B và B→A. Các chữ A/B trong cột quyền hàng chỉ loại quyền bán.

## Fixture đề xuất

- Windows `(localdb)\MSSQLLocalDB`, hai DB mới `Fmcg_App00_<nonce>_[AB]`, bảo vệ theo instance, tên, marker, nonce và persisted tenant binding. API/web loopback, routing từ token ký và quyền hiện hành của server. Dùng các ID/trip riêng giữa tenant và giữa nhánh. Lưu số mở đầu SQL trước từng ca; biến thể âm và các lần can thiệp fault chạy trên scope riêng.
- Tạo mốc chuẩn qua nguồn C.2/D.1: MAIN `30 EA / 300.0000`, TRIP `70 EA / 700.0000`, right A đã nạp 40, right B đã nạp 30 trên hai manifest line B10 và B20 của cùng quota. Grant B `100.0000`, đã dùng 0 và release 0. Giá fixture `10.0000/EA`; Customer B có ShipTo bất biến và một Outlet hợp lệ. AR B, COGS, CustomerCredit, custody, fund và advance ban đầu bằng 0, trừ ca có số mở đầu ghi riêng.
- Đồng hồ cố định `2026-11-22T04:00:00Z`. Grant approval v1 có hiệu lực `[03:00Z, 05:00Z)`; chứng cứ cash hợp lệ có thời điểm `03:55Z`. Riêng biến thể biên D2-10 dùng approval đã được duyệt lúc `02:55Z` và witness cash riêng lúc `02:56Z`, để cả hai hợp lệ tại `03:00Z`. Chỉ thay đổi giờ khi chạy biến thể biên, rồi khôi phục mốc chuẩn. Lưu actor biệt lập theo tenant: Sales writer được gán trong `C4DeliveryWriter`, Sales không được gán, reviewer cash, Warehouse, Finance, maker/checker và SQL reader. Các witness, bank pending và credit reservation được tạo của riêng fixture, giữ đính kèm tham chiếu nguồn và số tiền.
- Mỗi ca dương chuẩn dùng `10 EA` từ B20, `P=100`. D2-05 dùng B10 để tạo nguồn CustomerCredit 20 trước khi xác lập mốc so sánh, rồi bán từ B20. D2-11 dùng hai lệnh `6 EA` vào cùng B10, D2-12 dùng hai lệnh `8 EA` vào cùng B20 và chứng cứ bank pending riêng. Mỗi lệnh cạnh tranh có command và source riêng.

## Giá trị kỳ vọng cho P100

Các số tiền dùng đơn vị synthetic. Cash receipt là số thực nhận đã xác minh. AR gross tăng 100 ở mỗi sale hợp lệ và AR ròng tăng theo cột bên dưới. CustomerCredit là **delta** so với số mở đầu.

| Ca | Cash receipt | Cash áp AR / CustomerCredit áp AR | AR ròng tăng | Grant dùng / còn | Custody tăng | CustomerCredit delta |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| D2-02 cash 60 | 60 | 60 / 0 | 40 | 40 / 60 | 60 | 0 |
| D2-03 cash 100 | 100 | 100 / 0 | 0 | 0 / 100 | 100 | 0 |
| D2-04 bank pending 60 | 0 | 0 / 0 | 100 | 100 / 0 | 0 | 0 |
| D2-05 cash 60 và credit đã giữ 20 | 60 | 60 / 20 | 20 | 20 / 80 | 60 | −20 |
| D2-06 cash 120 | 120 | 100 / 0 | 0 | 0 / 100 | 120 | +20 |

Trong D2-02/03/04/06, trạng thái sau sale là MAIN `30/300`, TRIP `60/600`, B còn 20 và A còn 40; dòng B20 còn 10. Sau sale gốc từ B10 trong D2-05, TRIP mở là `60/600`, B còn 20, custody 120, CustomerCredit đã giữ 20 và grant còn 100; sau sale từ B20, TRIP `50/500`, B còn 10 và custody 180, A và MAIN giữ nguyên. Mỗi sale ghi issue TRIP `−10/−100`, COGS `+100` và right B `−10`; đối chiếu các ledger effect có dấu theo source line và chứng từ thanh toán phát sinh. Trong cả năm nhánh, quỹ, advance, khoản AR cũ và invoice/outbound giữ nguyên.

## Ca bắt buộc

| ID | Thao tác | Kỳ vọng độc lập |
| --- | --- | --- |
| D2-01 | Tạo mới và nâng cấp từ schema D.1 có fixture legacy B20, C.3 A-only và D.1 có lô B đã nạp. Reapply nếu có migration, kiểm cấu hình đích sai và rào chắn route. | Dữ liệu lịch sử, liên kết nguồn và fingerprint giữ nguyên. Migration tiến áp dụng một lần nếu có. Từ chối sai instance/tên/marker/nonce/tenant binding. D.2 không nhận legacy và B20 legacy không nhận manifest D.1. |
| D2-02 | UI cash 60, Customer và ShipTo hợp lệ, không chọn Outlet. | Theo bảng P100. Customer là debtor, ShipTo đúng version, Outlet rỗng. Một sale, một issue, một AR open item, một cash receipt/allocation, một grant consumption và đúng ledger theo nguồn. |
| D2-03 | UI cash 100 với Outlet hợp lệ. | Theo bảng P100. Truy được Outlet và version từ C1 selection liên kết với nguồn D.2, Customer giữ vai trò debtor. Không có dòng grant consumption, grant không release. |
| D2-04 | UI với bằng chứng bank pending 60, rồi mở lại sale. | Theo bảng P100. Trạng thái pending và bằng chứng giữ nguyên, có AR100. Không có confirmed receipt, cash allocation hoặc custody. |
| D2-05 | Bán gốc cash120 từ B10, giữ riêng credit20 cùng scope, rồi UI cash60 + reservation20 từ B20. Thử lại reservation với witness cash mới. | Sale mới theo bảng P100. Một liên kết bất biến tới reservation và receipt gốc. Credit đã giữ từ chính receipt đó giảm20 đúng một lần; sale mới ghi đúng một lượt dùng right B và một dòng grant consumption20. Lần dùng lại không có hiệu ứng. |
| D2-06 | UI cash120 từ B20 với chứng cứ đúng số tiền. | Theo bảng P100. Receipt áp100, số chưa phân bổ20 tạo CustomerCredit duy nhất có nguồn từ chính receipt đó, AR không âm và không ghi nhận quỹ. |
| D2-07 | Thử thiếu/sai số tiền, witness pending/tương lai/cùng Sales/đã dùng. Thử bank pending không hợp lệ hoặc gắn cờ verified từ client. Thử reservation sai scope, không được giữ hoặc vượt phần còn phải trả. | Các biến thể từ chối theo loại và không thay fingerprint nghiệp vụ. Chứng cứ hợp lệ chưa dùng vẫn dùng được sau yêu cầu sai. |
| D2-08 | Sai hoặc inactive Customer, Outlet của Customer khác hoặc không khớp ShipTo đã chọn dù cùng Customer, ShipTo sai Customer/version. Dùng Sales không được gán hoặc Warehouse-only. | Từ chối, không ghi sale/issue/AR. Kiểm cả trạng thái/mã nghiệp vụ trong response nếu HTTP 200. |
| D2-09 | Chọn right A, SKU/trip khác, nguồn không khớp, bán 11 từ B10, và biến thể balance thiếu/lệch giá trị trên scope fault. Thử payload sửa giá. | Từ chối từng biến thể. Right B tổng đủ cũng không cho bán quá 10 từ B10. Không ghi khi balance vật lý không khớp nguồn. Giá client không thay giá fixture. |
| D2-10 | Với cash60/P100, thử grant còn39, grant 0, scope sai, approval chưa tới lúc/đã hết hạn/không hợp lệ, hai approval cùng hiệu lực, ID/version không khớp proof cấp grant hoặc consumption counter lệch nguồn. Dùng grant còn đúng40 tại đầu khoảng hiệu lực, thử mốc cuối độc quyền. | Chỉ biến thể đúng scope, còn đúng40 và tại đầu khoảng mới thành công, AR40 và grant còn0. Các biến thể từ chối không ghi hiệu ứng. |
| D2-11 | Hai lệnh đồng thời mỗi lệnh mua 6 từ B10, cash60 đã xác minh riêng. | Một thành công. TRIP `64/640`, B còn24, B10 còn4, COGS60, custody60, AR0 và grant còn100. Witness của lệnh thua không bị tiêu thụ. |
| D2-12 | Hai lệnh đồng thời P80, mỗi lệnh 8 từ B20 với bank pending riêng và grant100 chung. | Một thành công. TRIP `62/620`, B còn22, dòng B20 còn12, COGS80, AR80, grant còn20, custody0. Lệnh thua thiếu grant và không dùng chứng cứ. |
| D2-13 | Lỗi SQL có chủ đích sau khi ghi stock và tại bước muộn sau ghi receipt/allocation nhưng trước commit, mỗi lỗi chạy scope riêng với cash60/P100. Sau mỗi lỗi, gỡ fault và thử lại cùng command. | SQL xác nhận đúng điểm fault, command chưa commit và toàn bộ hàng bảo vệ khớp ảnh trước lỗi. Thử lại tạo đúng một outcome theo D2-02. |
| D2-14 | Làm mất ACK sau commit qua proxy loopback, restart API, phục hồi cùng command. Thử đổi payload cùng ID, và ID mới với source cũ. | Thu hồi outcome/source gốc, một bộ effects duy nhất theo D2-02. Payload khác conflict; source trùng không tạo effect mới. |
| D2-15 | Thử thiếu/tắt/sai profile synthetic hoặc gọi nhánh thiếu kênh hợp lệ trong kiểm thử tích hợp. Gửi cờ invoice eligibility trái phép. | Đường D.2 chỉ mở trong profile được cấp server-side. Nhánh giao thật thiếu kênh bị chặn `B_NO_CHANNEL`. Đường DEV hợp lệ giữ `InvoiceEligible=false`, không có invoice/outbound; không có kết nối provider. |
| D2-16 | Xác thực tenant hiện tại rồi thay Trip/Customer/ShipTo/manifest/receipt evidence/command reference từ tenant kia, chạy cả hai chiều. | Từ chối hoặc không tìm thấy theo hợp đồng API, không lộ dữ liệu; không đổi fingerprint nghiệp vụ ở cả hai DB. |
| D2-17 | UI đang mở thì ngắt mạng hoặc thu hồi quyền writer trước submit; thử lookup/replay dưới quyền đã thu hồi sau một lần thành công hợp lệ. | Offline không phát request ghi hoặc lưu vào hàng đợi nghiệp vụ. Server từ chối submit/lookup/replay dưới quyền đã thu hồi, không thêm effect, không lộ outcome/source. |
| D2-18 | Chạy cash60/P100 trên scope có AR cũ 70, custody mở 15, quỹ mở 25 và advance 10, tất cả có nguồn fixture. | AR cũ giữ70, AR mới40, tổng110. Custody 75, fund25, advance10. A rights, AR A, MAIN và dòng nạp B10 giữ nguyên. Khoản 60 chỉ áp cho sale mới. |

## Chuẩn bị và điều kiện kết thúc

1. Sau phê duyệt, tạo fixture phiên bản hóa và bảng expected-results độc lập, gồm giá trị/hàng phải giữ nguyên, mã kỳ vọng, số lượng và giá trị có dấu. Ghi SHA-256 của hợp đồng, ma trận, fixture và expected-results trước lượt chạy. Chuẩn bị Playwright Chromium, SQL reader chỉ đọc mỗi tenant, runner và fault chỉ hoạt động trong phiên scratch có nonce. Ghi nguồn SDK/node/LocalDB, clock và test run ID.
2. Chạy nhánh dương D2-02..06 và kiểm tra UI D2-17 qua trình duyệt → API thật → LocalDB. Chạy các ca concurrency, bằng chứng, các biên chính sách, lỗi SQL và mất ACK trên cùng API/SQL thật. Dùng truy vấn độc lập theo tenant/trip/command/source/manifest line/right/receipt để so sánh tồn, COGS, AR gross/applied/net, receipt, CustomerCredit, grant, custody/fund, quyền và invoice/outbound. Thu riêng trạng thái nghiệp vụ và trạng thái HTTP, metadata lỗi và fingerprint hai tenant; không ghi token hay bí mật vào artifact.
3. Yêu cầu đủ 36 lượt có expected/actual/diff, không sai lệch, không bỏ ca, mỗi rollback/retry và replay có bằng chứng cardinality theo nguồn. Lưu SQL report, TRX, báo cáo browser và hash; kiểm cleanup chỉ xóa hai DB thuộc nonce. Chạy hồi quy APP-01 B20/AC-03, C.1–C.6 và D.1, .NET và Playwright/PWA trên source CI cuối. Owner ký DEV sau khi xem kết quả SQL và CI.

Mọi ca trong tài liệu hiện mang trạng thái `NOT_RUN`. Nghiệm thu DEV này chỉ áp dụng cho phạm vi AC-09 đã nêu. C-PREPROD `BLOCKED / NOT_VERIFIED`, nghiệm thu vật lý C-E2E `NOT_RUN` và PREPROD `FOUNDATION_ONLY / NO_GO`.
