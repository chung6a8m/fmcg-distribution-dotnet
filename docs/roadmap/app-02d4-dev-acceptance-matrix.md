# APP-02D.4 — Ma trận nghiệm thu DEV Windows LocalDB

**v0.1 · 23/11/2026 (Asia/Bangkok) · `OWNER_APPROVAL_PENDING / NOT_RUN`**

Áp dụng [hợp đồng D.4](app-02d4-dev-contract.md) với **18 ca trên mỗi tenant**, tổng cộng **36 lượt ca–tenant**. Mỗi kết quả có mã ca, tenant, phiên fixture, source commit và bằng chứng API/UI/SQL tương ứng. Mỗi ca cần kết quả của chính tenant đang kiểm, kể cả ca đối chiếu chéo. Các kịch bản dương, lỗi và sửa sai dùng nhánh fixture cô lập hoặc reset có guard để kết quả một ca không ảnh hưởng ca khác.

## Fixture đề nghị đóng băng

Hai tenant giả lập `D4-T1` và `D4-T2` có DB scratch khác nhau và marker/nonce được kiểm tra. Lặp lại mã nghiệp vụ, mã seller và profile giữa hai tenant để phát hiện lỗi phân vùng. Định danh kỹ thuật, phiên ứng dụng và định tuyến tenant do server xác thực. Mỗi tenant có seller đủ profile, seller B thiếu kênh, người gửi hóa đơn, kế toán tiếp nhận/post, người đối soát, Maker và Checker khác nhau, cùng tài khoản SQL oracle chỉ đọc. Hai đối tác mô phỏng chạy loopback với kết nối và giao dịch SQL riêng. Oracle đọc trực tiếp từ phiên chỉ đọc. Giới hạn ghi giữa các vùng được thử bằng lệnh SQL với user hiệu lực.

| Nguồn đã commit trên từng tenant | Thời điểm UTC | Lượng | Tiền hàng | AR | Giá vốn |
| --- | --- | ---: | ---: | ---: | ---: |
| A10, đợt giao riêng của đơn A | 2026-11-23 03:10 | 10 | 150 | 150 | 100 |
| A15, đợt giao riêng của cùng đơn A | 2026-11-23 03:20 | 15 | 225 | 225 | 150 |
| B20, bán–giao có profile mô phỏng | 2026-11-23 03:30 | 20 | 300 | 100 | 200 |
| Tổng kỳ vọng | | 45 | 675 | 475 | 450 |

A10/A15 qua C.4, B20 qua luồng DEV D.2/D.3 với 200 tiền thu và 100 AR. Ba delivery và ba source line được truy lại từ SQL theo định danh commit, với `InvoiceEligible=false`. Các số theo đơn vị quy ước `SYNTHETIC_UNITLESS`. Fixture âm dùng source draft/không thuộc loại giao, profile bị thu hồi hoặc seller thiếu kênh. Fact B đã xảy ra là quan sát giả lập độc lập và có ID riêng, với occurred-at 03:35Z và received-at 03:45Z; không tạo delivery hay bút toán.

Đồng hồ kiểm thử cố định bằng clock có guard, chỉ bơm trong IntegrationTest. Các mốc UTC thuộc ngày 23/11/2026, Asia/Bangkok là UTC+07. Tạo ba source theo thời gian trong bảng. Cửa sổ đầy đủ `[03:10Z, 04:00Z)`, snapshot đầu `asOf=04:00Z`; cửa sổ cắt `[03:10Z, 03:30Z)` giữ A10 tại cận dưới và loại B20 tại cận trên. Lỗi B được tạo bên phía stub bằng posting `20 / 310 / 110 / 200`. Oracle kỳ vọng tổng thực tế `45 / 685 / 485 / 450`, chênh `0 / +10 / +10 / 0`. Correction kỳ mở 11/2026 tại 04:05Z là `0 / -10 / -10 / 0`; chạy lại cùng cửa sổ nguồn với `asOf=04:10Z` phải trả tổng kỳ vọng, đồng thời snapshot 04:00Z vẫn giữ DIFF.

## Các ca bắt buộc

| Mã | Chạy và quan sát qua UI/API | Điều kiện PASS từ SQL độc lập |
| --- | --- | --- |
| D4-01 | Tạo mới và nâng cấp bản DEV có dữ liệu D.3. Kiểm tra từ chối sai instance, tên DB, marker, nonce hoặc danh tính. Kiểm tra cấu hình loopback. | Hai DB đúng owner/profile, user hiệu lực có đúng quyền. Principal từng stub bị từ chối khi ghi vùng app hoặc stub còn lại hay đọc DB tenant kia; oracle bị từ chối ghi. Đếm và fingerprint nguồn D.3 trước/sau khớp. Các phép thử sai guard không ghi hay di trú vào DB đó. Log cấu hình/kết nối xác nhận các đối tác chỉ bind loopback. |
| D4-02 | Từ UI tạo intent cho hai đợt giao của cùng đơn A. | Hai intent khác ID, mỗi intent có đúng một delivery/source link, seller/profile và hash. Hai delivery vẫn invoice-ineligible. |
| D4-03 | Tạo intent B với profile đúng. Thử profile thiếu/ngừng hiệu lực, seller sai và tenant sai. | Chỉ B20 có intent hợp lệ, có liên kết payload gốc. Nhánh bị từ chối tạo 0 intent/fact; B20 vẫn invoice-ineligible. |
| D4-04 | Thử từ draft, tải/trả xe, receipt, handover, source chưa có snapshot và payload bị sửa. | Không tạo intent, hóa đơn mô phỏng, receipt kế toán hoặc posting từ các đầu vào không hợp lệ. Source và ledger ứng dụng khớp fingerprint trước/sau. |
| D4-05 | Gửi bình thường và đọc lại trạng thái từ UI. | App chuyển `PREPARED → MOCK_RECORDED`. Một fact đối tác có đúng ID, profile, hash, thời điểm commit và audit ACK. Trạng thái invoice eligibility nguồn giữ false. |
| D4-06 | Gây timeout trước commit đối tác; tra cứu ABSENT, sau đó chạy lại. | Dấu vết có `UNKNOWN`, ABSENT đúng ID/profile và hết xử lý dang dở, gửi lại cùng ID; cuối cùng đúng một fact. |
| D4-07 | Mất ACK sau commit, dừng thành phần gửi trước khi ghi kết quả, khởi động lại và tra cứu qua UI. | `UNKNOWN → MOCK_RECORDED` sau FOUND khớp hash; số lần gửi giữ nguyên sau lookup và số fact là 1. |
| D4-08 | Gửi đồng thời theo cùng khóa với transport ID khác; thử payload, seller hoặc channel xung đột. | Cùng khóa/hash có một intent/fact và trả cùng ID. Nhánh xung đột không tạo hiệu ứng hoặc đổi payload, seller, channel. |
| D4-09 | Tra cứu gián đoạn, PROCESSING, hoặc FOUND sai định danh/hash/profile. | `UNKNOWN` còn hiệu lực, không gửi lại hoặc gắn fact sai. Kết quả sai có một case theo intent và phiên fact, lưu owner/next action; replay trả case cũ. |
| D4-10 | Thử nhánh B thiếu kênh trước giao và nhập lại fact đã xảy ra, dùng hai input độc lập. | Nhánh dự định trả `B_NO_CHANNEL`, không phát sinh post. Fact đã xảy ra sinh một case theo ID fact, lưu hai thời điểm; delivery, AR, stock và intent không đổi. |
| D4-11 | Gửi ba nguồn đã commit tới kế toán giả lập và xem trạng thái UI trước lệnh post. | 3 receipt khớp ID/hash và commit trước ACK; 0 posting và 0 đối soát đạt. Quyền ghi của principal stub chỉ giới hạn vào vùng stub. |
| D4-12 | Lặp và chạy cạnh tranh cả receive lẫn post bằng transport ID khác; thử đổi hash ở mỗi bước và post nguồn chưa nhận. | Đúng 3 receipt và 3 posting bất biến với số đúng fixture và quan hệ 1:1. Bản lặp trả ID gốc, các nhánh sai không thêm receipt/posting. Trạng thái mới nhất là POSTED đến khi chạy đối soát. |
| D4-13 | Đối soát cửa sổ cắt, cửa sổ đầy đủ với B20 lần lượt thiếu receipt, chưa post, đã post thiếu dòng hay sai mapping, và scope chưa xác định được N. | Cửa sổ cắt OK 2/2, B20 tại cutoff nằm ngoài scope. Cửa sổ đầy đủ cho OK 2/3. Thiếu receipt hoặc posting cho B20 pending kèm lý do; posting thiếu dòng hay sai mapping cho B20 DIFF. Cả hai chưa đạt toàn scope và đổi cutoff không tạo receipt/posting. Scope chưa biết N chỉ ghi số quan sát và lý do, không ghi tỷ lệ hoàn thành. |
| D4-14 | Post B20 sai số ở stub và chạy đối soát riêng qua UI. | 3 receipt, 3 posting, 2 OK + 1 DIFF; chênh B20 `0 / +10 / +10 / 0`, case có posting/report, owner và next action. Thời điểm nghiệp vụ và tiếp nhận riêng; POSTED không tự đổi thành RECONCILED_OK. |
| D4-15 | Thử Maker tự duyệt, kỳ khóa và sai scope. Dùng Checker hiện hành duyệt correction đúng; replay, đối soát lại rồi thử đóng case. | Một correction `0 / -10 / -10 / 0` tham chiếu posting sai, report và approval. Trước snapshot mới, DIFF vẫn còn. Sau snapshot mới OK 3/3, tổng `45 / 675 / 475 / 450`, đóng case có bằng chứng riêng. Posting sai và snapshot DIFF còn nguyên. |
| D4-16 | Mất kết nối trước commit và mất ACK sau commit receipt/post, khởi động lại rồi gửi hay tra cứu cùng định danh. | Nhánh trước commit rollback, nhánh sau commit trả lại receipt/posting gốc; đúng một hiệu ứng cuối mỗi nguồn. SQL app và stub lưu đủ khóa, mốc thời gian và audit để phân biệt từng nhánh. |
| D4-17 | Dùng thông tin tenant kia để đọc, gửi, lookup, đối soát hoặc duyệt. Lặp T1→T2 và T2→T1 với cùng mã nghiệp vụ. | Requests bị chặn theo danh tính ở cả hai hướng, không lộ dữ liệu đích; fingerprint nguồn, intent, fact, receipt, posting, report và case ở hai tenant giữ nguyên. Log an ninh được đối soát riêng. |
| D4-18 | Browser kiểm quyền đúng, thu hồi quyền sau khi render, rồi thử offline và kết nối lại. | Server kiểm quyền ở lúc thực thi các thao tác gửi, lookup và duyệt. Sau thu hồi/chế độ offline, 0 hiệu ứng nghiệp vụ và 0 command offline tự gửi khi online. |

## Evidence và điểm ký

- Trước lượt chạy sau phê duyệt, chuẩn bị script fixture phiên bản hóa, bảng expected-results dùng cho SQL oracle, clock có guard, bộ kích lỗi theo phiên và cấu hình hai đối tác. Ghi SHA-256 của hợp đồng, ma trận, fixture và expected-results. Kiểm tra instance, tên DB, marker và nonce trước create/upgrade/reset/cleanup.
- Lưu kiểm đếm **36 lượt**, truy vấn và kết quả SQL đọc trực tiếp từ hai DB và cả ba vùng dữ liệu mỗi DB. Bằng chứng gồm fingerprint source trước/sau, ID/hash/attempt của hóa đơn, receipt/posting, snapshot cutoff/asOf và sai lệch, correction + approval, case, ảnh/traces UI ca tương ứng và kết quả migration. Kết quả đối soát tính trực tiếp từ dữ liệu đã commit theo fixture độc lập.
- DEV chỉ trình ký khi 36/36 PASS có evidence, đủ các nhánh lỗi trong từng ca, có CI trên đúng commit cuối và Owner đã xem đối soát SQL. Baseline hiện tại là `NOT_RUN`. PREPROD tiếp tục `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`; các điều kiện B.1/B.9 và RUNTIME-01/02 giữ nguyên.
