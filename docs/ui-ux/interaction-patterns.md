# Interaction patterns

**OWNER_APPROVED UI/UX v0.1 / DOCS_ONLY.** Hiệu lực `ADOPTED` sau review, CI HEAD cuối và merge PR #79 theo [OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md). Quy tắc này mô tả tương tác; không thêm command hoặc quyền nghiệp vụ. Đối chiếu [business patterns](business-patterns.md) và hợp đồng module trước khi chốt hành động.

## UX-I01 - Điều hướng và giữ ngữ cảnh

- Quay lại danh sách SHOULD khôi phục bộ lọc, sort, trang/con trỏ, dòng chọn và vị trí cuộn nếu phạm vi vẫn hợp lệ.
- Thay tenant/phạm vi quyền MUST loại bỏ dữ liệu và lựa chọn không còn được phép; không phục hồi nhầm dữ liệu tenant cũ từ cache.
- URL SHOULD biểu diễn màn/phạm vi có thể chia sẻ theo quyền, nhưng không chứa token, mật khẩu hoặc dữ liệu nhạy cảm không cần thiết.
- Tab nội dung đổi vùng xem; mở tab, refresh, drilldown không tự gọi command ghi.
- Panel chi tiết dùng khi cần so sánh nguồn cùng danh sách. Khi không đủ ngang, chuyển thành trang chi tiết có nút quay lại cùng ngữ cảnh.

## UX-I02 - Bàn phím toàn màn

| Phím | Hành vi mặc định |
|---|---|
| Tab / Shift+Tab | Đi theo thứ tự đọc và thao tác; không rơi vào vùng bị che/ẩn |
| Enter / Space trên button | Thực hiện đúng button đang focus, theo trạng thái và quyền hiện hành |
| Enter trong trường nhập | Theo hợp đồng trường; không mặc định ghi cả chứng từ |
| Escape | Đóng lớp tương tác trên cùng hoặc hủy buffer của editor hiện hành; không đảo nghiệp vụ đã ghi |
| Phím tắt riêng | Có nhãn/hướng dẫn, có thể khám phá; tránh phím của trình duyệt/OS và xung đột nhập tiếng Việt |

Form MUST không gửi chỉ vì người dùng đang chọn candidate, xác nhận IME hoặc nhập dòng mới. Không bắt sự kiện Enter trong lúc composition. Sau dialog/popover đóng, focus trở lại điểm mở hoặc điểm kế tiếp có nghĩa nếu điểm cũ không còn tồn tại.

## UX-I03 - Table và editable grid

**Bảng đọc** ưu tiên HTML table với header đúng nghĩa. Chỉ dùng composite grid khi có nhu cầu điều hướng/chỉnh ô và triển khai đầy đủ quản lý focus; thêm `role=grid` vào bảng chưa đủ tạo hành vi grid.

Đề xuất hợp đồng cho editable grid:

| Ngữ cảnh | Mũi tên | Enter / F2 | Tab | Escape |
|---|---|---|---|---|
| Ô đang ở chế độ điều hướng | Chuyển ô, giữ ô focus nhìn thấy | Vào editor của ô có thể sửa | Đi ra vùng focus tiếp theo của trang | Không tạo tác động nghiệp vụ |
| Đang sửa text/number | Giữ hành vi caret/chọn text của editor | Enter xác nhận buffer hợp lệ, trở lại ô; F2 theo editor | Xác nhận buffer hợp lệ và chuyển ô sửa tiếp theo theo spec | Hủy buffer chưa commit, trả giá trị ô trước sửa |
| Lookup đang mở | Di chuyển candidate trong popup theo mẫu lookup | Chọn candidate đang active | Theo UX-I04 | Đóng popup, không tự xóa giá trị đã chọn hợp lệ |

Ở ô sửa cuối cùng, Tab SHOULD rời grid; không tự thêm dòng hoặc gửi chứng từ trừ khi screen spec đã quy định và được duyệt. Commit một cell chỉ cập nhật bản nháp nếu đó là hợp đồng màn; không đồng nghĩa post đơn/phiếu.

Khi ô invalid, giữ buffer và lỗi, đưa focus có thể dự đoán về ô cần sửa. Grid MUST cho phép thoát bằng bàn phím; người dùng không bị mắc kẹt trong editor. Cách giữ/sửa/hủy lỗi phải được mô tả trong component spec.

Sort/filter không được âm thầm bỏ buffer chưa lưu. Copy từ cell giữ giá trị có thể hiểu; số/đơn vị và dữ liệu nhạy cảm theo quyền. Chỉ render editor khi đang sửa; ô đọc vẫn là nội dung đọc, tránh biến mọi cell thành input trong tab order.

Mẫu quản lý focus được tham chiếu từ [WAI-ARIA APG Grid](https://www.w3.org/WAI/ARIA/apg/patterns/grid/); các quyết định Enter/Tab chi tiết trên là lựa chọn UX của project, cần kiểm trên component thật.

## UX-I04 - Lookup và tìm kiếm

- Tách chuỗi tìm kiếm khỏi identity đã chọn. Một tên gõ tự do không mặc nhiên là Customer/SKU đã xác nhận.
- Typing MUST giữ caret và ký tự đầu; không xóa danh sách hoặc đưa caret về đầu do refresh state.
- Popup thể hiện loading, kết quả, không tìm thấy và lỗi đọc; có nhãn nguồn/phạm vi tìm.
- Arrow chọn candidate; Enter chọn candidate active. Tab chỉ chấp nhận khi screen spec cho phép và candidate đã active rõ ràng; nếu không có candidate, không tự chọn dòng đầu.
- Chọn xong đóng popup và đưa focus tới nơi đã đặc tả. Trong grid nhập hàng, mặc định chuyển đến ô kế tiếp ở chế độ điều hướng; việc tự mở editor phải được screen spec chốt.
- Escape đóng popup, giữ selection trước đó còn hợp lệ. Thay đổi text khiến identity không còn khớp phải hiện trạng thái cần chọn lại.
- Response tìm kiếm cũ không được thay kết quả của query mới; quản lý cancellation/sequence khi triển khai.

Tham chiếu ngữ nghĩa combobox và popup: [WAI-ARIA APG Combobox](https://www.w3.org/WAI/ARIA/apg/patterns/combobox/). Điều hướng lựa chọn không được biến thành hành động ghi chứng từ.

## UX-I05 - Form, validation và review trước ghi

Mỗi input có label bền vững; placeholder chỉ là ví dụ. Helper text cho biết đơn vị/quy tắc cần thiết. Required được diễn đạt bằng chữ hoặc ký hiệu có giải thích.

Validation nhập liệu SHOULD chạy khi blur hoặc người dùng chủ động kiểm/gửi; khi đang sửa lỗi, cập nhật đủ nhanh mà không báo đỏ từng phím khi chuỗi còn chưa hoàn chỉnh. Lỗi server/nguồn hiện tại luôn được thể hiện khi nhận được.

Khi gửi không hợp lệ: giữ dữ liệu đã nhập; hiện lỗi tại field, có tóm tắt nếu nhiều lỗi, chuyển focus tới nơi giúp sửa. Phân biệt lỗi nhập, hết quyền, policy chưa có, nguồn đã đổi, rejected và UNKNOWN.

Trước ghi nhận có hậu quả vật lý/tài chính, cho người dùng thấy nguồn, đối tượng, lượng/tiền, tác động và phần xác nhận thực tế. Checkbox xác nhận đã đếm/đã nhận/đã chi MUST bắt đầu chưa chọn. Không dùng việc tích checkbox thay bằng chứng hay xác minh server.

Confirm dialog dùng khi cần xác nhận một quyết định cụ thể, không lặp mọi lần Save. Khi tác vụ đã có màn review đủ thông tin và hành động rõ, không tự thêm một dialog giống hệt.

## UX-I06 - Gửi, chờ và UNKNOWN

Trong lúc gửi, giữ nội dung và phạm vi; bảo vệ hành động lặp cùng ý định. Chặn double click ở UI là hỗ trợ, không thay idempotency server.

Kết quả có thẩm quyền quyết định success/rejected/pending. Timeout sau gửi không chứng minh thất bại. Với UNKNOWN, CTA chính là **Kiểm tra kết quả lần gửi**; giữ identity/payload/version gốc và mốc số dư xác nhận trước. Không đổi ID để né conflict hoặc gửi nghiệp vụ thay thế. Chi tiết ở UX-B03/04.

Lỗi chưa gửi có thể cho gửi sau khi điều kiện hợp lệ; chỉ retry theo hợp đồng command. Không áp một nút Retry chung cho mọi lỗi.

## UX-I07 - Làm mới, PWA và dữ liệu cũ

- Initial load chưa có số: skeleton theo cấu trúc hoặc loading có nhãn; không dựng số 0 tạm.
- Background refresh: giữ số cũ với mốc cũ; lỗi refresh có thông báo và đường thử lại.
- Đổi bộ lọc: kết quả phải gắn filter/scope tạo ra nó. Không hiện tổng cũ dưới tiêu đề phạm vi mới mà không giải thích.
- Có dữ liệu mới khi đang nhập: thông báo có bản mới; không ghi đè buffer hoặc reset vị trí bất ngờ. Kiểm lại source/version trước commit.
- PWA có phiên bản mới: hiện prompt cho người dùng chủ động cập nhật; bảo vệ draft nếu có cơ chế thật. Không silent reload làm mất tác vụ.
- Offline: thể hiện trạng thái kết nối; business writes không được xếp hàng offline. Local draft chỉ được hứa khi đã có đặc tả persistence, bảo mật và kiểm chứng riêng.

## UX-I08 - Bulk, destructive và lịch sử

Bulk action MUST nêu đang áp dụng các dòng nào: đã chọn trên trang, toàn kết quả lọc hay phạm vi khác; tổng lượng/tiền và kết quả từng dòng theo đúng hợp đồng atomic/partial. Không mặc định checkbox đầu bảng chọn toàn dataset.

Hủy bỏ nháp, xóa cấu hình và điều chỉnh chứng từ đã ghi là các hành vi khác nhau. Dùng nhãn chính xác; nếu mất dữ liệu chưa lưu, cung cấp lựa chọn giữ/sửa/rời. Không cho nút Xóa làm đảo số sổ mà không có hợp đồng nguồn.

## Các ca review tối thiểu

Typing tiếng Việt bằng IME; tên dài; số thập phân chưa hoàn chỉnh; popup loading; query trả về đảo thứ tự; Tab ở cuối grid; Escape trong popup nằm trong dialog; refresh trong khi đang sửa; mất phản hồi sau ghi; thu hồi quyền giữa lúc mở và gửi; đổi tenant khi có selection. Chỉ đánh dấu đạt khi đã thực hiện trên UI của increment.
