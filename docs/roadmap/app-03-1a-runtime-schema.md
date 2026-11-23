# APP-03.1a — Runtime/schema tích hợp đầu tiên

## Khởi chạy

Trên Windows, dùng .NET SDK 10.0.404, Node 26 LTS và LocalDB `MSSQLLocalDB`. Cài dependency web bằng `npm --prefix web ci`. Launcher cần cổng loopback 5116 cho API và chọn cổng trống cho Vite.

```powershell
pwsh -NoProfile -File ./scripts/Run-App02C1.ps1 -Increment App03A1
```

Mở URL `/sales` launcher in ra và dán mã DEV từ clipboard. Mã `source` dùng cho các bước Sales A của fixture và viếng thăm. Mở **Trả hàng · DEV** rồi nhập `sales`, `receiver`, `finance` hoặc `qa` trong terminal để sao chép mã phù hợp. Fixture hồi quy dùng cùng actor Sales cho đơn và visit, cùng các actor độc lập của DEV-F05 cho các bước nhận, duyệt và QA. Ctrl+C dừng launcher và dọn đúng cặp scratch đã xác minh. Phiên giữ dữ liệu qua lần dừng thuộc .1b.

## Profile và schema v1

| Cấu hình | Phạm vi |
| --- | --- |
| `App03:Profile=APP-03.1a-v1` | Synthetic auth, dedicated runtime C.1–C.5, F01/F03/F04/F05 trên cùng cặp LocalDB. Tenant lấy từ danh tính đã xác thực. |
| Base đến `202611220010` | Baseline scratch của DEV-F05, gồm các migration không tag của `ScratchMigrator.Migrate`. |
| `202611230004` → `202611230005` → `202611230006` | Migration theo tag DEV-F03 (đơn vị/lô), F04 (giá VND), F05 (trả hàng theo nguồn). |
| `202611230002` | DEV-F01, thêm visit và phân công khách hàng. Độc lập với chuỗi F03–F05, áp dụng được sau F05 trên DB đã có chứng từ. |
| D, worker, F02/F06–F08 | Ngoài tổ hợp v1. API từ chối nếu bật cùng profile. |

`ScratchMigrator.MigrateApp03A1` nhận baseline scratch đã bind hoặc chuỗi migration v1 đã chạy một phần/đầy đủ. Trên predecessor DEV-F05, migration chỉ bổ sung F01 và giữ các ID, snapshot và sổ hiện có. Launcher nạp phân công visit cho khách hàng fixture. Startup kiểm tra lịch sử migration và các bảng nguồn bắt buộc trước khi mở route trên cả hai tenant. Các launcher C, D và F cũ vẫn sử dụng cấu hình và đường migration riêng.

## Kịch bản kiểm chứng

```powershell
npx --prefix web playwright install chromium
pwsh -NoProfile -File ./scripts/Run-App02C1.ps1 -Increment App03A1 -Verify
```

- Tenant A tạo đơn, Confirm và Reserve trên DEV-F05 qua Chromium. Nâng schema tại chỗ rồi chạy lại migration một lần. API đọc lại cùng đơn và SQL so khớp row image trước/sau. Tenant B cài v1 từ baseline sạch.
- Nạp và giao 2 EA từ đơn cũ qua runtime tích hợp. Dự kiến SQL ghi lô `RET-ORIGIN`, giá phải thu và dư nợ 20.000 VND, giá vốn 6.667 VND. Cả visit và giao hàng dùng clock fixture 09:00 ICT ngày 23/11/2026.
- Ghi một visit `NO_ORDER`, rồi tạo một hồ sơ trả 1 EA gắn với đúng delivery và lô. Visit và đề nghị trả giữ nguyên đơn, AR và tồn. Tenant B có 0 đơn, visit, delivery, AR và hồ sơ trả. Đọc/ghi chéo tenant và đọc/replay sau thu hồi Sales bị từ chối theo hợp đồng từng endpoint.
- API phải từ chối khởi động khi thiếu schema v1, tắt F04 hoặc bật F02 trong profile v1.

SQL đối chiếu nằm ở `TestResults/app03-1a/<run-id>/sql-review.json`. JUnit và ảnh Chromium nằm ở `web/test-results-app02c5/<run-id>/app03-1a/`. Phạm vi kiểm chứng là synthetic Windows LocalDB và Chromium.
