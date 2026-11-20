# Accessibility và responsive

**PROPOSED UI/UX v0.1.** Đây là yêu cầu thiết kế và kế hoạch kiểm chứng, không là tuyên bố ứng dụng đạt WCAG. Các tiêu chí dưới đây phải được kiểm trên component và luồng thật trong phạm vi increment đã được cấp phép.

## UX-A01 - Phạm vi và ngoại lệ đang có

Định hướng đề xuất cho UI nghiệp vụ mới là thiết kế phù hợp WCAG 2.2 AA, đi kèm kiểm tra bàn phím và công nghệ hỗ trợ. Nhãn “WCAG compliant” chỉ được dùng sau đánh giá có phạm vi, phiên bản và evidence tương ứng.

[OWN-APP00B-13-02](../decisions/owner-app00b-13-02-125-scale-exception.md) đã loại 125% browser zoom/Windows display scale khỏi gate và công việc bắt buộc tương lai của APP-00B/C synthetic/nonproduction. Giữ trạng thái ngoại lệ `EXCEPTION_ACCEPTED / NOT_TESTED`; không coi PASS, không mở lại thành backlog bắt buộc và không tự mở rộng ngoại lệ sang mọi sản phẩm/tương lai.

Mọi ma trận nghiệm thu mới có thể xung đột ngoại lệ này phải ghi rõ phạm vi và có quyết định Owner trước khi biến thành gate. Hướng dẫn accessibility không thay đổi một quyết định Owner đang có bằng cách đổi tên tiêu chí.

## UX-A02 - Contrast và dấu hiệu nhận biết

- Văn bản thông thường: mục tiêu tối thiểu **4,5:1**. Tiêu chí WCAG cho chữ lớn có ngưỡng riêng; project ưu tiên giữ 4,5:1 cho mọi nhãn quan trọng để đơn giản hóa việc dùng token.
- Thành phần/biểu tượng/dấu hiệu cần để nhận biết control hoặc trạng thái: **3:1** với màu kề tương ứng. Không phải mọi đường kẻ trang trí đều phải đạt 3:1.
- Không dùng màu làm dấu hiệu duy nhất: trạng thái có chữ, lỗi có nội dung, dòng chọn có dấu hiệu ngoài nền nhạt nếu cần nhận biết selection.
- `border.subtle=#DCE3EC` chỉ để phân cách nhẹ; contrast với trắng khoảng 1,29:1. `border.control=#7D899A` là đề xuất cho ranh giới input cần nhận biết, khoảng 3,55:1 trên trắng và 3,34:1 trên nền trang.
- Focus đề xuất 2 px theo token theme (Light `#315DA8`, Dark `#93C5FD`), offset 2 px; kiểm trên mọi nền thực tế, không suy pass từ một cặp màu.

Light và Dark được kiểm riêng trên mọi trạng thái thực, kể cả selected/invalid/readonly/disabled, popup và native controls. Theme tối không thay thế forced-colors/high-contrast của hệ điều hành. Trong forced colors, giữ outline/dấu chọn và nhãn theo system colors; không vô hiệu bằng `forced-color-adjust: none` trên toàn ứng dụng. Reduced motion cũng là preference riêng. Chi tiết adapter và kiểm theme ở [Tailwind v4 và Light/Dark](tailwind-v4-and-themes.md).

Nguồn: [WCAG 1.4.3 Contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html), [WCAG 1.4.11 Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html). Tỷ lệ palette trong gói này do tính từ RGB token; chưa bao gồm overlay, opacity, trạng thái thực và render của ứng dụng.

## UX-A03 - Focus, tên và trạng thái

Mọi thao tác có thể thực hiện bằng bàn phím. Focus visible luôn hiện cho keyboard, thứ tự phù hợp cấu trúc. Footer sticky, banner kết nối, popup và bàn phím mobile không được làm mất điểm đang thao tác.

Label liên kết programmatically với input. Icon-only button có accessible name; tooltip không thay tên. Button, link, tab, table và form dùng semantics phù hợp. Dialog có tên, focus vào vùng hợp lý, giữ focus trong modal và trả focus khi đóng.

Error text liên kết với field; invalid được khai báo theo semantics của control. Status cập nhật dùng vùng thông báo phù hợp, tránh đọc lặp từng giây hoặc mọi lần polling. Kết quả không được chỉ hiện bằng animation hoặc toast không truy lại được.

Tham chiếu: [WCAG 2.4.11 Focus Not Obscured](https://www.w3.org/WAI/WCAG22/Understanding/focus-not-obscured-minimum.html) và [WAI-ARIA APG](https://www.w3.org/WAI/ARIA/apg/). Chi tiết keyboard ở [interaction patterns](interaction-patterns.md).

## UX-A04 - Vùng chạm và thiết bị

Project đề xuất **48×48 CSS px** cho thao tác chạm thường dùng của Field; hình icon có thể nhỏ hơn nhưng vùng tương tác đạt kích thước này. Desktop control compact 32 px và mục tiêu icon tối thiểu 32×32 là lựa chọn thiết kế khi dùng chuột/bàn phím; không tự dùng compact desktop cho màn cảm ứng tại kho.

WCAG 2.5.8 AA có mức 24×24 CSS px hoặc các ngoại lệ khoảng cách/loại control được mô tả trong tiêu chí. **48 px là mục tiêu sản phẩm của bộ Field**, không gọi đó là ngưỡng WCAG AA. Nguồn: [Target Size Minimum](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html).

Thao tác kéo cần lựa chọn không kéo khi áp dụng; hover không phải cách duy nhất mở chi tiết hoặc thao tác. Chừa safe area; CTA không bị che khi bàn phím mở. Chỉ dùng gesture đặc biệt khi có cách tương đương dễ khám phá.

## UX-A05 - Bố cục theo không gian khả dụng

Các khoảng dưới đây là **đề xuất điểm chuyển bố cục**, đo bằng CSS viewport/container. Chúng không mô tả độ phân giải vật lý hoặc các thiết bị đã nghiệm thu.

| Khoảng đề xuất | Bố cục |
|---|---|
| Từ 1280 px | Sidebar, nội dung và panel chi tiết khi các cột vẫn đủ đọc |
| 1024–1279 px | Sidebar có thể thu gọn; giảm cột phụ hoặc mở detail thành drawer/page |
| 768–1023 px | Ưu tiên nội dung; nav mở theo yêu cầu; xem list/detail luân phiên |
| 320–767 px | Một cột; Field dùng thẻ nguồn, bộ lọc dạng sheet, CTA theo tác vụ |

1440×900 desktop và 430×932 mobile là **khung review tham chiếu** từ prototype. Không đặt chiều cao trang cố định, không dùng kích thước PNG xuất ảnh làm viewport CSS.

Nếu nội dung dài không vừa trước breakpoint, chuyển bố cục sớm hơn thay vì co font xuống. 320 px là mốc hẹp tối thiểu trong acceptance hiện hành; không cố định toàn trang rộng 1440 px.

Bảng có nhu cầu so sánh hai chiều có thể cuộn ngang trong vùng bảng có nhãn và keyboard access. Phần shell, form và toolbar vẫn cần đọc và thao tác được. Chọn cột/ưu tiên cột khác với giấu dữ liệu bắt buộc để ra quyết định. Tham chiếu [WCAG Reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html).

## UX-A06 - Chữ, chuyển động và kiểm chứng

Cỡ chữ là mặc định, không là cấm người dùng phóng to. Row/control dùng min-height; tăng chiều cao khi chữ hoặc nội dung cần. Không cắt dấu tiếng Việt, error message hoặc label dài bằng chiều cao cứng.

Hỗ trợ reduced motion; chuyển động nhẹ chỉ phản hồi trạng thái. Không chớp nháy hoặc tự di chuyển focus khi dữ liệu nền cập nhật.

Ma trận cho increment UI mới phải ghi thiết bị/browser/version thật, CSS viewport, zoom và phạm vi được Owner cho phép. Ít nhất kiểm các viewport 1440×900, 1024×768, 430×932 và 320×800 tại 100% nếu màn đó thuộc phạm vi; kiểm keyboard, dữ liệu dài/rỗng/invalid và trạng thái nguồn. Thiết bị Field thực tế (Android/iOS/máy quét) còn cần xác nhận khi lập kế hoạch nghiệm thu tương ứng.

Automated checks và ảnh chụp hỗ trợ review; cần manual keyboard/focus và các kiểm tra công nghệ hỗ trợ đã nằm trong phạm vi được chấp nhận. Chưa chạy thì ghi NOT_RUN. Không nhân rộng evidence của shell health sang 52 màn nghiệp vụ chưa triển khai.
