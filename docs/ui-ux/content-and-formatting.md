# Nội dung, số liệu và định dạng

**OWNER_APPROVED UI/UX v0.1 / DOCS_ONLY.** Hiệu lực `ADOPTED` sau review, CI HEAD cuối và merge PR #79 theo [OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md). Ngôn ngữ giao diện mặc định: tiếng Việt. Dữ liệu và chính sách theo source/module contract.

## UX-C01 - Từ ngữ và nhãn hành động

Nhãn MUST mô tả đúng việc: “Lưu nháp”, “Gửi đề nghị nộp quỹ”, “Xác nhận đã nhận tiền”, “Tra kết quả lần gửi”. Tránh dùng một nhãn “Xác nhận” cho các hành động có tác động khác nhau khi thiếu ngữ cảnh.

Trạng thái viết ngắn, rõ; lời giải thích nêu lý do và bước tiếp theo. Mã kỹ thuật để trong vùng chi tiết có thể sao chép khi cần hỗ trợ; màn tác vụ dùng tên nghiệp vụ và mã chứng từ quen thuộc.

| Tình huống | Nhãn đề xuất | Nội dung hỗ trợ |
|---|---|---|
| Chưa có nguồn | Chưa có dữ liệu | Nguồn đang thiếu và ai/bước nào bổ sung |
| Số đã biết bằng 0 | 0 + đơn vị | Cùng scope/asOf với số khác |
| Đã tiếp nhận nhưng còn dependency | Đã tiếp nhận, chờ xử lý | Phụ thuộc còn chờ, không hứa thời gian khi chưa có SLA |
| Sau gửi chưa xác định kết quả | Chưa rõ kết quả | Tra lần gửi gốc; chưa kết luận đã ghi hoặc bị từ chối |
| Server từ chối | Chưa được ghi nhận | Lý do có thể xử lý và hành động phù hợp |
| Dữ liệu cũ | Dữ liệu đến HH:mm | Mốc gốc và trạng thái làm mới |
| Policy thiếu | Chưa có chính sách được duyệt | Phạm vi/yêu cầu cần bổ sung |
| Không có quyền | Chưa có quyền thực hiện | Quyền/phạm vi hoặc người có thể xử lý nếu được phép biết |

“Hoàn tất” chỉ dùng khi biết hoàn tất phần nào. Đơn, giao thực, tiền, hóa đơn, ghi sổ và đối soát có nhãn riêng.

## UX-C02 - Số, tiền và đơn vị

- Số tiền/lượng căn phải, chữ số có bề rộng ổn định (`tabular-nums`) trong bảng và KPI.
- Display locale đề xuất `vi-VN`: ví dụ `1.234.567`, `12,5`. Dùng formatter có locale; parsing phải có hợp đồng riêng, không xóa mọi dấu chấm/phẩy rồi đoán.
- Giá trị VND trong ví dụ có thể trình bày `1.234.567 đ` hoặc đặt đơn vị `VND` ở header nhất quán. Không suy số lẻ, tax hay rounding từ cách demo đang hiển thị.
- Giữ precision theo UoM, currency và source contract; không âm thầm làm tròn giá trị nhập, số cần đối chiếu hoặc số đã chốt. Nêu quy tắc làm tròn khi nó ảnh hưởng quyết định.
- Với input số, giữ chuỗi nhập trung gian như dấu âm hoặc dấu thập phân đang gõ, rồi validate theo loại trường. Không dùng floating point làm quyền quyết định số tiền ở UI; tính toán có thẩm quyền thuộc contract/server.
- Số âm cần dấu rõ; không chỉ dùng màu. Không dùng màu đỏ mặc định cho mọi số âm nếu nó có nghĩa hợp lệ như điều chỉnh.
- Đơn vị đứng gần số hoặc ở header dễ thấy. Không cộng Thùng, Chai và SKU khác nhau thành một tổng chưa có định nghĩa.
- Tổng theo trang, tổng lọc và tổng nghiệp vụ MUST có nhãn phạm vi khác nhau.
- Không cắt phần đầu số tiền, phần dấu âm hoặc đơn vị quan trọng. Cho xem đầy đủ bằng mở chi tiết/copy/resize có thể dùng bàn phím; tooltip hover không là cách duy nhất.

## UX-C03 - Ngày, giờ và ngày nghiệp vụ

Hiển thị ngày đề xuất `dd/MM/yyyy`, giờ `HH:mm` theo đồng hồ 24 giờ. Date-only như hạn thanh toán không được tự coi là UTC instant và dịch ngày theo timezone trình duyệt.

Business date và timezone lấy từ seller/tenant contract; không suy từ máy người dùng hoặc cứng hóa `Asia/Bangkok`/`Asia/Ho_Chi_Minh` vì mẫu được tạo tại Việt Nam. Khi thời điểm có thể hiểu khác, hiển thị vùng giờ/offset và nguồn.

`asOf` là mốc dữ liệu được xác nhận, không phải lúc trang vừa render. “Cập nhật 5 phút trước” có thể là nhãn phụ, nhưng thời điểm đầy đủ vẫn truy được. Stale threshold, biên overdue, ngày nghỉ, lịch giao và SLA phải có định nghĩa sản phẩm; số phút trong prototype không là cấu hình mặc định.

## UX-C04 - Dữ liệu dài, rỗng và tìm kiếm

Tên khách, SKU, địa chỉ tiếng Việt phải đọc đủ ở nơi cần quyết định. Mã giữ nguyên hoa/thường và ký tự theo contract; không tự sửa dữ liệu nguồn để hiển thị đẹp.

Không tìm thấy do filter khác với chưa có dữ liệu, lỗi tải hoặc thiếu quyền. Empty state cho biết nguyên nhân và hành động phù hợp như bỏ lọc, tạo nháp nếu có quyền, hoặc tải lại.

Tìm kiếm không dấu/viết tắt là năng lực phải đặc tả và kiểm chứng riêng; không hứa khi backend chưa hỗ trợ. Input và copy/paste giữ Unicode đúng, không làm mất dấu hay chuẩn hóa identity tùy ý.

## UX-C05 - Xuất, in và thông báo

Export/print MUST giữ nguồn, phạm vi, bộ lọc, đơn vị, mốc dữ liệu, trạng thái một phần và quyền đọc tương ứng. Chọn cột chỉ thay trình bày, không nâng quyền. Mẫu A4/A5, số bản in và lề do từng chứng từ quy định; chưa có một khổ in chung được chốt cho project này.

Thông báo thành công ngắn có thể dùng toast nhưng kết quả quan trọng vẫn nằm ở nguồn/trang kết quả. Lỗi chặn, UNKNOWN, mất quyền và dependency cần nội dung bền trên màn; không chỉ tồn tại trong toast tự biến mất.

Nội dung phục hồi cần cho biết điều đã biết, điều chưa biết và bước tiếp theo. Không hứa “đã lưu an toàn”, “đã hoàn tiền” hoặc “sẽ tự đồng bộ” khi chưa có kết quả/cơ chế được kiểm chứng.
