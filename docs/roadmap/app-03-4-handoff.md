# APP-03.4 — Sales bàn giao Kho

## Làm việc trong phiên DEV

Mở ứng dụng theo [hướng dẫn phiên APP-03](app-03-3-workspace.md). Trong **Đơn A và giữ hàng**, dùng **Lọc theo chuyến** và **Bước xử lý** để mở một đơn cụ thể.

| Bước | Thao tác |
| --- | --- |
| Confirm | Dùng mã `source` để xác nhận đơn nháp. Màn hình giữ ShipTo, quy đổi và giá VND đã chốt theo nguồn. |
| Reserve | Dùng mã `receiver` để mở đơn đã Confirm, kiểm số lượng còn thiếu và giữ hàng theo đơn vị cơ sở. Quyền Reserve theo grant Warehouse hiện hành trong tenant. |
| Load | Chọn **Nạp đơn lên xe** trên công việc đủ điều kiện. C.3 kiểm phân công chuyến, quyền giữ hàng và lô còn hạn. Nếu liên kết đã cũ, màn hình giữ phạm vi đơn. Lệnh nạp đang chờ của đơn khác được khôi phục từ chuyến của lệnh gốc. |
| Đối chiếu | Từ đợt nạp, dùng **Xem đơn và lịch sử lô** để quay lại dòng nguồn vừa ghi. Danh sách giữ, nạp và xuất giao theo lô gồm hạn dùng, số lượng và mã nguồn. Sales mở công nợ theo đơn từ nguồn Confirm. |

Trong tenant A, chuyến mới giao quyền nạp cho `receiver` và quyền giao cho `source`. Chuyến đã có tiếp tục dùng phân công hiện tại. Resume và NextDay giữ lịch sử và phân công cũ. APP-03.4 dùng schema C.2/C.3/F03/F04 đã cài trong profile A/F01/F03/F04/F05.

**Đang giữ** là lượng chưa tiêu thụ của quyền giữ hàng, gồm lượng đang ở kho và trên xe. **Đã nạp** là lượng nạp lũy kế. **Còn thiếu giữ** lấy lượng đặt trừ lượng giao và lượng đang giữ. Các trường ghi theo đơn vị cơ sở từ snapshot nguồn. Confirm cần phương án giá đang hiệu lực, có chứng cứ và người duyệt độc lập. Reserve kiểm nguồn đã Confirm và lô khả dụng, giữ nguyên giá đã chốt.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyHandoff
```

Runner dùng hai LocalDB synthetic riêng của phiên. Hai đơn đặt 2 PACK mỗi đơn với hệ số 2 EA/PACK, cùng yêu cầu giữ 4 EA từ một lô có 6 EA. Một yêu cầu giữ 4 EA thành công và yêu cầu kia bị từ chối. Sau khi giữ thêm 2 EA cho đơn còn lại, hai chuyến nạp và xuất tổng 6 EA. Runner kiểm replay không tăng nguồn, lệnh dựa trên số giữ cũ bị từ chối, và đọc/ghi chéo tenant không tạo chứng từ. Ba ca thiếu plan, plan hết hiệu lực và người Confirm cũng là checker đều bị từ chối. Tổng snapshot hai đơn là 80.000 VND, tổng giao thực là 60.000 VND.

Artifact trong `TestResults/app03-4/<RunId>/` gồm JUnit, ảnh Chromium và đối chiếu SQL độc lập. Phạm vi là synthetic DEV trên LocalDB. PREPROD, thiết bị thật, EV/P0 và production thuộc các mốc nghiệm thu riêng.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** trên working tree APP-03.4 từ base `2c55373c8b1f50b83dacc370cd483ab10b146067`. Windows LocalDB, .NET 10.0.404, Node 26.13.1.

- **APP-03.4** · Chromium → Vite → API → SQL 1/1, run `5f5e4373d1d044298b34abdc4cef88bf`. Tenant A có hai đơn đã Confirm và ba nháp bị từ chối do plan, tổng giữ/nạp/xuất đều là 6 EA trên cùng lô. SQL ghi nhận 0 nguồn từ các lệnh bị từ chối. Tenant B có 0 chứng từ và giữ nguyên 6 EA ở MAIN. Kiểm bàn phím, Dark ở 320 CSS px, chặn nạp offline và liên kết đến đúng dòng nguồn.
- **Hồi quy APP-03** · Workspace .3 Chromium 1/1, run `8a7bececaa9c4c58a2495b566065abaf`. .2/.1b vòng đời và ngày tiếp theo qua ở run `a6f5ff0db46b4302abbe5dd3b062e62b`. .1a kiểm nâng cấp và tổ hợp profile 1/1, run `973c20d194634655b6bdf2da2d22b142`.
- **Hồi quy F03/F04** · Chromium và SQL qua, run F03 `723a2e87f4f640a0b67a72f8012abf4d` và F04 `1610b64c639f4bc28295a8b3bc2fc572`.
- **.NET và web** · 352/352 .NET với TRX. Web build và unit/dependency/PWA 23/23. Playwright web 54 qua, một bài cài PWA standalone bỏ qua theo cấu hình local.

| Artifact trong thư mục run APP-03.4 | SHA-256 |
| --- | --- |
| `sql-review.json` | `AF89846EDA7B9612A1615B8C2A1253C134E3174D307E7BEED8E08F5F98221155` |
| `junit.xml` | `5D94715641EED2CE3B9EED2511078D6DD0CDA6AF73F99B12670570100C7286DA` |

Workflow Windows chạy `VerifyHandoff` và tải artifact. CI từ xa và merge còn chờ xác nhận.
