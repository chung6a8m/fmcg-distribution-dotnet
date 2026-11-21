# Demo chuyến bán hàng trên laptop

Demo local dùng Windows LocalDB và SDK .NET theo `global.json`. Lệnh tự build từ checkout và mở trình duyệt tại địa chỉ loopback trên cổng do máy cấp. Các biểu mẫu HTML dùng token và font của UI v0.1. Bản demo chạy với .NET, kể cả khi Docker và WSL đang tắt.

| Lệnh PowerShell từ thư mục gốc | Trạng thái khi mở |
| --- | --- |
| `.\scripts\Start-Demo.ps1 -Mode Fresh` | Hai tenant riêng, có các vai trò giả lập, dữ liệu nghiệp vụ trống |
| `.\scripts\Start-Demo.ps1 -Mode AC03Start` | Chuyến mẫu T0 và tồn kho 100 ở từng tenant |
| `.\scripts\Start-Demo.ps1 -Mode AC03Complete` | Từng tenant có chuyến mẫu đi đến T6, bảng đối soát bảy mốc và dòng sổ |
| `.\scripts\Start-Demo.ps1 -Resume <RunId>` | Mở lại dữ liệu của đúng phiên |
| `.\scripts\Start-Demo.ps1 -Remove <RunId>` | Xóa dữ liệu của đúng phiên sau khi kiểm tra quyền sở hữu hai database |

Giữ cửa sổ PowerShell trong lúc thao tác. Dừng bằng Ctrl+C và lưu RunId để mở lại. Thoát host sẽ giữ hai database LocalDB và manifest dưới `%LOCALAPPDATA%\FmcgDms\Demo`. Nếu khởi tạo thất bại, lấy RunId từ đầu ra để dọn các database đã có đủ dấu sở hữu. Lệnh dọn từ chối database thiếu dấu hoặc sai tenant. Mỗi phiên chỉ có một host hoạt động.

## Chạy và xem AC-03

1. Trong Fresh chọn vai trò Kho. Nhập danh mục, tồn đầu kỳ, giá vốn, mã đơn và mã chuyến rồi tạo chuyến. Dùng tồn 100 và giá vốn đơn vị 10 để đối chiếu với AC-03. Đơn và chuyến giới hạn trong kịch bản một SKU, một đơn A 40, quota B 30, giá bán 15, hạn mức công nợ B 100 và khoản thu giả lập 200.
2. Đổi vai trò Kho, Bán hàng và Thủ quỹ theo từng bước. Gửi lại một bước đã hoàn tất dùng đúng định danh lệnh ban đầu. Mỗi phiên có hai bộ dữ liệu; chọn tenant trong danh sách vai trò để xem hoặc thao tác.
3. Bấm số đọc từ SQL trong bảng đối soát để xem các dòng cấu thành tại mốc đó. Bấm mã chứng từ trong phần chi tiết để xem ID nguồn, dòng chứng từ và bút toán. **Báo cáo JSON** có mốc UTC, số kỳ vọng, số đọc độc lập từ SQL, sai lệch, commit nguồn, phiên bản seed và băm oracle. Phiên có tồn hoặc giá vốn ban đầu khác mẫu hiển thị số thực tế của phiên và ghi kỳ vọng là `null` trong JSON.
4. Dùng mã RunId của phiên hoàn tất để mở lại. Mỗi lần chọn một trong ba chế độ dựng sẽ tạo phiên mới, với GUID và thời điểm riêng.

**Xuất Excel** tải một workbook cho tenant đang xem. Trong sheet **Đối soát**, bấm ô **Thực tế SQL** để đến nhóm dòng cấu thành tại mốc tương ứng. Sheet **Chứng từ phát sinh** ghi mỗi chứng từ và dòng sổ tại mốc quan sát đầu tiên. Sheet **Nguồn bằng chứng** ghi RunId, commit, seed, băm oracle, thời gian xuất và phạm vi kiểm chứng.

Các vai trò và chứng cứ chính sách, nhận tiền, đếm tiền đều thuộc demo giả lập. Evidence của demo ghi môi trường `SYNTHETIC_NONPRODUCTION`. PREPROD vật lý cần bằng chứng riêng.

## Kiểm tra

Mốc tham chiếu là artifact `app00a-test-evidence` của [CI 44858477763](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44858477763). Hai báo cáo APP-01E.5 cho source `22e70256b99a759bd8c27d296206c0a41960da49` khớp 182 ô oracle và 518 phương trình SQL, với database tenant đối chứng giữ nguyên dấu vân tay. Demo tạo báo cáo mới từ các lệnh APP-01 và SQL của phiên hiện tại.

```powershell
dotnet build FmcgDms.slnx
python tests/smoke-demo.py
```

Smoke test chạy HTTP qua loopback với LocalDB thật. Bài test dựng và dọn các phiên riêng, chạy từ trống, thử sai vai trò và sai tenant, đối chiếu với bản seed, kiểm tra replay, rồi mở lại dữ liệu.
