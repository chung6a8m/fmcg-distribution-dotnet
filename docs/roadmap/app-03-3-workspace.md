# APP-03.3 — Phiên người dùng và khung ứng dụng

## Mở phiên làm việc

Từ thư mục gốc trên Windows, dùng .NET 10.0.404, Node 26 LTS, LocalDB `MSSQLLocalDB` và cài dependency web bằng `npm --prefix web ci`.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1
```

Lưu `RunId`. Mở URL gốc mà launcher in ra và dán mã DEV từ clipboard một lần trong tab. Sau khi đổi danh tính hoặc khi mã hết hạn, lấy mã mới từ terminal rồi kết nối lại. Các mã có hạn 15 phút.

| Lệnh trong terminal | Phạm vi |
| --- | --- |
| `source` | Tenant A, actor nguồn đã được cấp Sales và Warehouse trong fixture A/F. |
| `receiver` | Tenant A, actor kho nhận hàng trả và phụ trách nạp cho các chuyến mới từ [APP-03.4](app-03-4-handoff.md). |
| `finance` | Tenant A, actor duyệt trả hàng. |
| `qa` | Tenant A, actor kiểm tra và giải phóng hàng. |
| `peer` | Actor nguồn của tenant B đối chứng. |

Sidebar hiển thị các màn theo grant hiện hành và profile A/F01/F03/F04/F05. Các danh sách và lệnh tiếp tục kiểm theo phân công của từng nghiệp vụ. Chọn chuyến trên màn đơn hoặc nạp để mang sang màn nạp và giao. Từ đơn đã xác nhận, mở báo cáo AR theo đơn, rồi mở lần giao có lô để đề nghị trả. Báo cáo và JSON xuất trong profile này dùng VND. Công cụ và UI Lab có trang riêng tại `/dev`.

Mã truy cập chỉ nằm trong tab. Sau **Đổi danh tính**, shell bỏ mã cũ và xóa ngữ cảnh trên URL. Các lựa chọn và lệnh chưa rõ kết quả được giữ theo run, tenant và actor. Khi quay lại đúng actor, API kiểm quyền hiện hành trước khi hiển thị nguồn hay cho tra cứu hoặc gửi lại lệnh cũ. Browser tải lại màn khi khôi phục trang từ bộ nhớ back/forward. Không có lệnh ghi offline.

Sau `Stop` hoặc `NextDay`, dùng `Resume` với cùng `RunId`, tải lại URL và kết nối bằng mã mới. Lịch sử và lệnh chưa rõ kết quả tiếp tục dùng nguồn cũ. Xem [vòng đời phiên](app-03-1b-dev-session.md) và [chuẩn bị ngày mới](app-03-2-day-bootstrap.md).

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyWorkspace
```

Runner mở một cặp LocalDB synthetic có dấu sở hữu theo run. Chromium dùng shell để tạo đơn 2 EA, Confirm/Reserve, nạp và giao trên chuyến B của ngày 23/11, mở AR theo đơn rồi lập đề nghị trả 1 EA từ lần giao đó. Playwright bỏ ACK sau khi máy chủ commit. Finance và tenant B không dùng được lệnh chờ của Sales A. Quay lại Sales A tra được kết quả gốc. Sau khi gỡ grant Sales, API phải trả 403 cho đọc công nợ trên chuyến, tra cứu và gửi lại lệnh trả. JWT hết hạn phải nhận 401 tại phiên và endpoint nghiệp vụ. Đối chiếu SQL độc lập phải có đúng một đơn, một lần giao và một hồ sơ trả chưa qua bước xử lý tiếp ở tenant A, cùng số dòng bằng 0 ở tenant B.

Artifact nằm tại `TestResults/app03-3/<RunId>/`, gồm kết quả JUnit, đối chiếu SQL và ảnh Chromium desktop/320 CSS px. Phạm vi kiểm chứng là synthetic DEV, với thiết bị thật, zoom vật lý 125%, PREPROD và production theo cổng nghiệm thu riêng.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** trên source `27a50bd4fcd54b72cbb7bdadae8080ec01529997`, tree `e8dd8e5806b01580a09c2c0cf0cec8eda4aa9dfd`. Windows LocalDB, .NET 10.0.404, Node 26.13.1.

- **APP-03.3** · run `07dc1367e32648e08a0c256495482c27` · Chromium 1/1. Reload giữ chuyến, AR dùng VND và liên kết đúng lần giao. Mất ACK, cách ly actor/tenant, JWT hết hạn và thu hồi Sales khớp kỳ vọng. SQL xác nhận tenant A có một đơn, một lần giao, một hồ sơ trả và chưa có bước xử lý trả. Các số đếm ở tenant B đều bằng 0.
- **APP-03.1a** · run `45609e13f21e444b911b123b0e685843` · Chromium 1/1, giữ được đơn qua nâng cấp schema và kiểm lại các cấu hình khởi động bị chặn.
- **APP-03.2/.1b** · run `34cc5e5d76564fd69252e4bbd0c25ed0` · bootstrap, ngày kế tiếp, restart, tra cứu ACK, replay và thu hồi phiên có kiểm tra ownership đều qua.
- **.NET** · `dotnet test --solution FmcgDms.slnx -- --report-trx` · 352/352, không có lỗi hoặc ca bỏ qua.
- **Web** · build và các bài unit/dependency/PWA 23/23. Bộ Playwright web 51 qua, một bài cài PWA standalone bỏ qua theo cấu hình local của repo.

| Artifact local | SHA-256 |
| --- | --- |
| `TestResults/app03-3/07dc1367e32648e08a0c256495482c27/sql-review.json` | `8C99BAE93A8D8A6EFB54B9CC44B41EBC97F033397CF0F037B1F3E3F27EFFD95C` |
| `TestResults/app03-3/07dc1367e32648e08a0c256495482c27/junit.xml` | `F7633BEA8DCB6D88EA51541429E276B3B617F65C8FCBEFC42FD411D1ED86BF34` |

Workflow Windows có bước chạy `VerifyWorkspace` và tải artifact. CI từ xa và merge còn chờ xác nhận.

### Kiểm chứng sau review PR #137

Source `16ffae101fb99cc0e7375c6019f2dfaa3141a5a9`, tree `6f74fb33dd3bd58c6808f6e5f9eadea29be01f01`.

- Lịch sử trình duyệt giữ phạm vi theo run, tenant và actor, thêm vai cho màn trả hàng. Khi Back/Forward đi vào mục lịch sử của danh tính khác, shell xóa ngữ cảnh cũ trước khi mở màn. AR chỉ khôi phục mã đơn sau khi đối chiếu danh sách đơn và phân công chuyến từ API.
- Nạp và Giao đồng bộ chuyến thực tế với URL, sidebar và lựa chọn đã lưu. Thứ tự chọn là chuyến của lệnh chờ còn được phân công, chuyến trong ngữ cảnh còn được phân công, rồi chuyến mặc định. Tra cứu vẫn dùng mã lệnh gốc.
- Playwright web 53 qua, một bài standalone chỉ dành cho CI được bỏ qua ở local. Hai regression mới tái hiện lỗi trước sửa rồi qua sau sửa. Web build và unit/dependency/PWA 23/23.
- APP-03.3 Chromium → API → LocalDB 1/1, run `a74645969550491b957ea972089d7897`. SQL giữ đúng một đơn, một lần giao, một hồ sơ trả ở tenant A và không phát sinh các dòng này ở tenant B. APP-03.1a Chromium → API → LocalDB 1/1, run `641c057ed3b14faaabd38caea1269c8a`.

| Artifact local | SHA-256 |
| --- | --- |
| `TestResults/app03-3/a74645969550491b957ea972089d7897/sql-review.json` | `49C97BF4774B517F3B7E07560DEF4B2734A5BE6DBE0B4B9C0124E3450031E5A0` |
| `TestResults/app03-3/a74645969550491b957ea972089d7897/junit.xml` | `E34A99510E802A3C71DDBA6AC895BB26453C088D813396B18A6CCA294D0F20A4` |
