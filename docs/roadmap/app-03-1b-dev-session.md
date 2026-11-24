# APP-03.1b — Phiên DEV dùng lại được

## Khởi chạy và quản lý phiên

Trên source có APP-03.3, dùng [điểm vào chung `/` và mã theo vai](app-03-3-workspace.md). Hướng dẫn `/sales` dưới đây mô tả điểm vào khi bàn giao .1b.

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

- **Stop** hoặc Ctrl+C dừng API và Vite, giữ hai database. Lệnh `Stop` dùng manifest, khóa và tín hiệu Windows theo run ID, nên chạy được từ bản sao script khi checkout hoặc DLL Release vắng. Trong lúc chạy, `Remove` và `Resume` cùng run ID bị khóa. Windows Job Object thu hồi API và Vite đã được gắn vào job nếu tiến trình launcher bị kết thúc. Kiểm chứng kết thúc đột ngột chạy sau khi cả hai host báo sẵn sàng.
- **Resume** dùng lại URL loopback và clock cũ. Nếu trình duyệt vẫn giữ tab có lệnh chờ, tải lại trang, kết nối bằng mã DEV mới rồi tra cứu lệnh trong workspace. Các mã ký trước khi dừng hết hiệu lực trên host mới. Quyền đọc, ghi và replay vẫn theo grant và phân công hiện hành trong DB.
- **Remove** đối chiếu đủ hai database và dấu sở hữu trước khi xóa phiên Ready đã dừng. Nếu phiên Ready thiếu một database, kể cả sau lần xóa dở dang, lệnh từ chối và giữ database còn lại cùng manifest để phục hồi. Với phiên khởi tạo dở dang, lệnh có thể dọn phần đã ghi đủ dấu sở hữu. Nếu LocalDB bị gián đoạn ngay sau CREATE DATABASE và chưa ghi xong dấu sở hữu, lệnh từ chối xóa database đó.

Manifest nằm dưới `%LOCALAPPDATA%\FmcgDms\App03Dev\<RunId>\session.json`. Manifest giữ instance, tên và binding hai tenant, nonce, profile/schema v1, seed version, clock UTC, web port và source tạo phiên. File khóa theo run ID được giữ tại thư mục cha để các lần chạy cùng kiểm tra một khóa. Manifest và log không lưu signing key hoặc credential.

Mỗi máy dùng một host API trên cổng 5116; cổng web riêng được giữ theo phiên. Nếu một trong hai cổng đang bận, xử lý cổng đó rồi chạy lại `Resume`. Profile vẫn là tổ hợp A với F01/F03/F04/F05. Phiên .1b giữ fixture C.2/F05 và clock cố định 09:00 ICT ngày 23/11/2026. Từ APP-03.2, phiên Start mới dùng [bootstrap VND và lệnh NextDay](app-03-2-day-bootstrap.md).

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Verify
```

Runner điều khiển API/Vite thật và hai LocalDB synthetic. Nó tạo và giao một đơn 2 EA lô `RET-ORIGIN` qua HTTP, bỏ ACK nghiệp vụ sau khi server commit, dừng rồi mở lại và tra/replay cùng command ID. Dự kiến AR giữ 20.000 VND, giá vốn 6.667 VND, đúng một delivery và một nguồn, row image của opening, policy, command, nguồn, ledger và số dư giữ nguyên sau resume/replay. Runner thử lỗi cổng API, mã cũ, quyền Sales bị thu hồi, kết thúc riêng tiến trình launcher, Stop từ thư mục không có Git hoặc DLL, và Remove khi phiên còn chạy, thiếu tenant B hoặc sai dấu sở hữu. Database của một run khác giữ nguyên.

SQL và kết quả .1b lịch sử nằm tại `TestResults/app03-1b/<RunId>/session-review.json`. Từ APP-03.2, Verify chạy lại các ca lifecycle trên bootstrap mới và ghi vào `TestResults/app03-2/<RunId>/session-review.json`. Verify diễn tập vòng đời qua HTTP và SQL; bằng chứng browser của tổ hợp nghiệp vụ A/F tiếp tục nằm ở runner .1a.

## Kết quả local ngày 23/11/2026

Source thực chạy `f7d0090816dd01508bb0736d72c94fe3b8dce50f`, tree `142b38f27cad19271cc3cd85265f46d63fb2aa87`. Môi trường Windows LocalDB, .NET 10.0.404 và Node 26.13.1.

- **APP-03.1b DEV_VERIFIED_LOCAL** · run `04f834ec276649019f698f7ddc81bb11`. Giữ ID đơn, delivery, nguồn và cùng URL qua Resume. Lookup và replay lệnh đã commit trả cùng nguồn, AR 20.000 VND và giá vốn 6.667 VND. Row image của 17 bảng trên mỗi tenant khớp qua Resume, replay và lỗi khởi động. Mã cũ và thao tác bằng Sales sau thu hồi bị từ chối. Kết thúc riêng launcher sau khi sẵn sàng thu hồi hai host. Remove từ chối khi đang chạy hoặc khi marker tenant B sai, sau đó xóa đúng phiên và giữ database đối chứng.
- **APP-03.1a Chromium → API → SQL** · run `2f671ed4ebf74cfcb5ba985f85b0b1f5` · 1/1. Smoke demo AC-03 qua Fresh, AC03Start, AC03Complete, Resume, replay và cách ly tenant.
- **DEV-F09 Functional PASS** · run `1fdf23c09e3f4a6f80d7f05b2d4d8656` · .NET 352/352 và Chromium 8/8 trên F01–F08. Catalog LocalDB khớp trước/sau. CI từ xa và PREPROD vật lý có trạng thái riêng.

| Artifact local | SHA-256 |
| --- | --- |
| `TestResults/app03-1b/04f834ec276649019f698f7ddc81bb11/session-review.json` | `597DED3772BFF9B4E9D3EFF8E7C350009EBCDCF5CE096422B97F4F0505D6F56C` |
| `TestResults/app03-1b-final.log` | `10F75206086926B54A187CBEE6CA8478B00C2054BD29DB906C0F1999DE9B0E94` |
| `TestResults/app03-1b-ac03.log` | `2D1027B555F9A624B9EE3860578FFA8534ED026013CD6E966F234AE90FF46CA2` |
| `TestResults/dev-f09/1fdf23c09e3f4a6f80d7f05b2d4d8656/run.json` | `0DD93B4EEDC71E5A15228CF293390881F8B61C8F3BF4CF54D126047AED753517` |
| `TestResults/dev-f09-1fdf23c09e3f4a6f80d7f05b2d4d8656.trx` | `F25C328EC10623CA087EDA7BFA8687891FA705E4700D4D4404BAC6D52198B87E` |

### Kiểm chứng sau review PR #135

Source thực chạy `4ec43025e060397a2b1cbcd1efd2ce592e8fae51`, tree `de52734de4bd43cc6769310ad16e2ffb82af7e8a`. Cả hai ca hồi quy tái hiện lỗi trước sửa và PASS sau sửa trên Windows LocalDB.

- Run APP-03.1b `ea9b6252baa14653af7c90a8354749a3` xác nhận `Remove` từ chối khi tenant B tạm vắng, giữ nguyên từng byte của manifest và row image nghiệp vụ tenant A. `Stop` từ bản sao script trong thư mục không có Git hoặc DLL dừng đúng hai host và giữ nguyên row image.
- APP-03.1a Chromium → API → SQL 1/1, smoke AC-03 và .NET 352/352 đều qua trên source sửa.

| Artifact local | SHA-256 |
| --- | --- |
| `TestResults/app03-1b/ea9b6252baa14653af7c90a8354749a3/session-review.json` | `13E83C69BE8911BF8A38CA496CA3489670C487B7CEE00C2BD3C2CE285C744606` |
| `TestResults/app03-1b-review-final.log` | `0274527B3B9F2103BEEF244BDA610254D7A3DC6B2986BCE45ADA0D461B0CBA7A` |
| `TestResults/app03-1a/aa38a0eda100411fab084a4e78eac23b/sql-review.json` | `838BD7A7901BA39BA01CBF368A0693C4386956C5762FF8E4AD14D07F417280A1` |
| `TestResults/app03-1b-review.trx` | `77B2102CA2B01A7E9E854A4ABE0AA98A63EC1E2864019236F759EFD7C15DBB6C` |
