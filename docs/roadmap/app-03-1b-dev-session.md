# APP-03.1b — Phiên DEV dùng lại được

## Khởi chạy và quản lý phiên

Chạy từ thư mục gốc trên Windows với .NET 10.0.404, Node 26 LTS, LocalDB `MSSQLLocalDB` và dependency web đã cài bằng `npm --prefix web ci`.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1
```

Lưu `RunId` in ra trong terminal, mở URL `/sales` và dán mã DEV từ clipboard. Nhập `source`, `sales`, `receiver`, `finance` hoặc `qa` trong terminal để sao chép mã phù hợp. Giữ cửa sổ launcher trong lúc thao tác. Trong cửa sổ khác có thể dùng các lệnh sau, với `$runId` là ID đã lưu.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Stop -RunId $runId
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Resume -RunId $runId
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Remove -RunId $runId
```

- **Stop** hoặc Ctrl+C dừng API và Vite, giữ hai database. Trong lúc chạy, `Remove` và `Resume` cùng run ID bị khóa. Windows Job Object thu hồi API và Vite đã được gắn vào job nếu tiến trình launcher bị kết thúc. Kiểm chứng kết thúc đột ngột chạy sau khi cả hai host báo sẵn sàng.
- **Resume** dùng lại URL loopback và clock cũ. Nếu trình duyệt vẫn giữ tab có lệnh chờ, tải lại trang, kết nối bằng mã DEV mới rồi tra cứu lệnh trong workspace. Các mã ký trước khi dừng hết hiệu lực trên host mới. Quyền đọc, ghi và replay vẫn theo grant và phân công hiện hành trong DB.
- **Remove** xóa cặp database và manifest của phiên đã dừng sau khi đối chiếu cả hai database tồn tại. Nếu lần khởi tạo dở dang đã ghi đủ dấu sở hữu, `Remove` có thể dọn phần thuộc phiên. Nếu LocalDB bị gián đoạn ngay sau CREATE DATABASE và chưa ghi xong dấu sở hữu, lệnh từ chối xóa database đó.

Manifest nằm dưới `%LOCALAPPDATA%\FmcgDms\App03Dev\<RunId>\session.json`. Manifest giữ instance, tên và binding hai tenant, nonce, profile/schema v1, seed version, clock UTC, web port và source tạo phiên. File khóa theo run ID được giữ tại thư mục cha để các lần chạy cùng kiểm tra một khóa. Manifest và log không lưu signing key hoặc credential.

Mỗi máy dùng một host API trên cổng 5116; cổng web riêng được giữ theo phiên. Nếu một trong hai cổng đang bận, xử lý cổng đó rồi chạy lại `Resume`. Profile vẫn là tổ hợp A với F01/F03/F04/F05. Fixture hiện hành tái sử dụng cùng opening và policy C.2/F05, clock cố định 09:00 ICT ngày 23/11/2026. Seed đầu ngày có phiên bản nghiệp vụ và chuyển ngày thuộc APP-03.2.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Verify
```

Runner điều khiển API/Vite thật và hai LocalDB synthetic. Nó tạo và giao một đơn 2 EA lô `RET-ORIGIN` qua HTTP, bỏ ACK nghiệp vụ sau khi server commit, dừng rồi mở lại và tra/replay cùng command ID. Dự kiến AR giữ 20.000 VND, giá vốn 6.667 VND, đúng một delivery và một nguồn, row image của opening, policy, command, nguồn, ledger và số dư giữ nguyên sau resume/replay. Runner thử lỗi cổng API, mã cũ, quyền Sales bị thu hồi, kết thúc riêng tiến trình launcher, từ chối Remove khi phiên còn chạy hoặc sai dấu tenant B, và giữ database của một run khác.

SQL và kết quả đối chiếu được ghi vào `TestResults/app03-1b/<RunId>/session-review.json`. Verify diễn tập vòng đời qua HTTP và SQL; bằng chứng browser của tổ hợp nghiệp vụ A/F tiếp tục nằm ở runner .1a.
