# Nền tảng thị giác và bố cục — đề xuất v0.1

## 1. Trạng thái và phạm vi

Tài liệu này đề xuất nền tảng UI dùng chung cho `fmcg-distribution-dotnet`, kế thừa năm bộ prototype Sales A, Kho, Field, Finance và Owner. Các từ “phải” và “không được” dưới đây mô tả hợp đồng thiết kế dự kiến khi áp dụng v0.1; chúng không xác nhận ứng dụng hiện tại đã đáp ứng.

Ưu tiên **gọn, có thêm dữ liệu hữu ích trong cùng ngữ cảnh** là yêu cầu đã có của Owner. Font, cỡ chữ, chiều cao dòng, sidebar và các kích thước production trong tài liệu này là đề xuất của designer, chưa phải các quyết định số học đã được Owner phê duyệt.

- Nguồn và khác biệt giữa các bản vẽ: [prototype-mapping.md](prototype-mapping.md).
- Giá trị token dùng cho bước triển khai: [tokens/tokens.json](tokens/tokens.json).
- Cấu trúc và trạng thái thành phần: [components.md](components.md).
- Hành vi nghiệp vụ và phục hồi: [interaction-patterns.md](interaction-patterns.md).
- Yêu cầu tiếp cận, responsive và kiểm chứng: [accessibility-and-responsive.md](accessibility-and-responsive.md).

Đây là tài liệu thiết kế, không thay đổi `business/`, API, quyền, phê duyệt triển khai hoặc các ngoại lệ Owner hiện hành. Tài liệu không thay đổi phạm vi ngoại lệ zoom 125% đã có và không tuyên bố ứng dụng đạt WCAG.

## 2. Một ngôn ngữ thị giác, hai cách thao tác

| Bối cảnh | Cấu trúc ưu tiên | Cách tăng dữ liệu hữu ích |
|---|---|---|
| Sales, Kho, Finance, Owner tại bàn | Hàng đợi, bảng, bộ lọc, dải chỉ số, chi tiết bên cạnh | Giữ nguồn, số lượng/tiền, trạng thái và bước tiếp theo cùng ngữ cảnh |
| Field trên điện thoại | Một cột, thẻ nguồn, cặp nhãn–giá trị, nhập thực tế, CTA dễ tiếp cận | Giảm lặp, ưu tiên dữ liệu quyết định tác vụ hiện tại, mở chi tiết khi cần |

Không ép số lượng thông tin của desktop vào điện thoại bằng cách thu nhỏ chữ. Màn hình nhỏ được cuộn dọc; không yêu cầu mọi tác vụ vừa một ảnh chụp.

Phạm vi đang xem, thời điểm dữ liệu và trạng thái kết quả phải đọc được trước khi người dùng quyết định. Trang nhiều số liệu vẫn phải cho biết việc cần làm tiếp theo và đường mở về nguồn.

## 3. Hệ màu

Palette nền tảng được giữ từ cả năm bộ prototype. Tên vai trò dưới đây giải thích mục đích sử dụng; cấu trúc khóa máy đọc nằm trong tệp token.

| Vai trò | Giá trị | Quy tắc dùng |
|---|---|---|
| Nền trang | `#F6F8FB` | Nền workspace, không dùng thay bề mặt input |
| Bề mặt | `#FFFFFF` | Card, bảng, form và vùng hành động |
| Chữ chính | `#172B4D` | Nội dung, tiêu đề, số liệu cần đọc |
| Chữ phụ | `#5F6F84` | Nhãn phụ, hướng dẫn, metadata vẫn cần đọc |
| Viền nhẹ | `#DCE3EC` | Phân cách nhóm và hàng; không là dấu hiệu duy nhất của control |
| Viền control — mới đề xuất | `#7D899A` | Nhận biết input/control tương tác trên nền sáng |
| Hành động chính / tích cực | `#0F766E` | Primary action và trạng thái tích cực có nhãn chữ riêng |
| Nền tích cực / lựa chọn | `#E7F5F1` | Badge tích cực, dòng đang chọn; bổ sung chỉ dấu lựa chọn |
| Thông tin / focus | `#315DA8` | Thông tin và focus ring; hai vai trò có token riêng |
| Nền thông tin | `#EAF0FC` | Thông báo và badge thông tin |
| Chờ / cảnh báo | `#975D0B` | Chờ, thiếu điều kiện, chưa rõ hoặc cần chú ý theo mapping |
| Nền cảnh báo | `#FFF4DE` | Notice/badge cảnh báo |
| Lỗi / nguy hiểm | `#B54148` | Lỗi, bị chặn hoặc hành động phá hủy theo ngữ cảnh |
| Nền lỗi | `#FDECEF` | Notice/badge lỗi |
| Nền navigation / header bảng | `#F0F4F8` | Vùng phụ và header bảng |
| Hàng bảng xen kẽ | `#FBFCFE` | Hỗ trợ dò hàng; không mã hóa trạng thái nghiệp vụ |

`action.primary` và trạng thái tích cực có thể cùng dùng một primitive màu, nhưng phải là hai ý nghĩa riêng trong component. Màu không quyết định quyền, kết quả ghi nhận, A/B hoặc tính đầy đủ của đối soát.

Mỗi trạng thái cần có nhãn chữ. Lỗi tại trường có nội dung lỗi; lựa chọn có chỉ dấu rõ; focus có vòng bao; không chỉ đổi màu nền hoặc màu chữ.

**v0.1 chỉ đặc tả giao diện sáng.** Token ngữ nghĩa cho phép bổ sung theme sau này; chưa có bảng màu, trạng thái hoặc bằng chứng kiểm tra cho dark mode.

## 4. Typography

Font đề xuất là **Noto Sans**, weight **400/700**, giữ khả năng hiển thị tiếng Việt của bộ prototype. Fallback: `"Noto Sans", "Segoe UI", system-ui, sans-serif`.

Ứng dụng hiện tại dùng Segoe UI trong [web/src/style.css](../../web/src/style.css). Việc đưa Noto Sans vào runtime, đóng gói font, font loading và kiểm tra lại bố cục là một increment triển khai riêng; tài liệu này không thực hiện migration đó.

| Vai trò chữ | Desktop compact: cỡ / line-height | Field: cỡ / line-height | Weight |
|---|---|---|---|
| Nội dung chính | 14 / 20 | 16 / 24 | 400 |
| Dữ liệu bảng | 13 / 18 | Dùng thẻ hoặc cặp nhãn–giá trị 16 / 24 | 400; nhấn chọn lọc 700 |
| Caption / metadata | 12 / 16 | 12 / 16, chỉ cho nội dung phụ | 400 |
| Tiêu đề trang | 24 / 32 | 24 / 32; được xuống dòng | 700 |
| Tiêu đề vùng | 18 / 24 | 18 / 24 | 700 |
| Giá trị chỉ số | 24 / 32 | 24 / 32 | 700 |

Các số trên là CSS px tham chiếu, triển khai bằng đơn vị có thể co giãn phù hợp. Không đặt `height` khóa theo một dòng chữ khi nội dung có thể xuống dòng hoặc người dùng phóng chữ.

- Tiền và số lượng trong cùng cột căn phải, đơn vị có vị trí ổn định.
- Có thể dùng tabular numerals khi font hỗ trợ để so sánh số theo cột.
- Caption không thay body cho số tiền, số lượng cần xác nhận hoặc lý do bị chặn.
- Không dùng weight giả 500/600 nếu chỉ đóng gói 400/700.
- Nội dung nhập trên Field dùng cỡ chữ chính; không thu nhỏ input để lấy thêm dòng.
- Mã, tên dài và tiếng Việt có dấu phải có đường đọc đầy đủ; không chỉ cung cấp hover.

Chữ bảng 11,2–12 và hàng 26 của một số bản vẽ là mật độ minh họa. Chúng không trở thành mức tối thiểu production trong v0.1.

## 5. Khoảng cách, viền và hình dạng

Spacing scale đề xuất, đơn vị CSS px: **0 / 2 / 4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 / 48**.

| Mục đích | Giá trị khởi điểm |
|---|---|
| Khoảng cách nội dung rất gần, icon–nhãn | 4 hoặc 8 |
| Khoảng cách giữa trường / nhóm nhỏ | 12 hoặc 16 |
| Padding card / vùng tác vụ | 16; dùng 24 khi cần tách rõ nội dung |
| Gutter giữa các vùng chính | 16 hoặc 24 |
| Padding ngang trang | 16 ở màn nhỏ; 24 khi có đủ không gian |
| Tách nhóm lớn | 24 hoặc 32 |

Bo góc đề xuất: **4 / 6 / 8**; **999** chỉ cho dạng pill khi có mục đích rõ. Control ưu tiên 4; card/panel dùng 6 hoặc 8. Trạng thái không được chỉ phân biệt bằng hình dạng góc.

Viền cơ sở 1px. Phân cấp nội dung chủ yếu bằng vị trí, khoảng cách, tiêu đề và bề mặt; không thêm nhiều shadow để bù bố cục thiếu rõ ràng. Dialog/popover có thể cần phân tách khỏi nền theo đặc tả component sau này.

Focus ring đề xuất: **2px `#315DA8`, offset 2px**. Phải kiểm tra trên từng bề mặt, không bị ancestor/scroll container cắt mất và không bị action bar che. Focus vẫn cần tồn tại khi màu nền selected hoặc error đang hiển thị.

## 6. Mật độ và kích thước thao tác

| Thuộc tính | Desktop compact — mặc định đề xuất | Desktop comfortable | Field / thao tác cảm ứng |
|---|---:|---:|---:|
| Chiều cao control tối thiểu | 32 | 40 | 48 |
| Chiều cao dòng dữ liệu tối thiểu | 36 | 44 | Theo nội dung; vùng thao tác tối thiểu 48 |
| Body | 14 / 20 | 14 / 20 trở lên theo cài đặt chữ | 16 / 24 |
| Lề trang khởi điểm | 24 | 24 | 16 |

“Tối thiểu” không có nghĩa khóa chiều cao. Lỗi nhiều dòng, nhãn dài, nội dung bổ sung và text zoom phải làm vùng tương ứng tăng chiều cao.

Chế độ mật độ là tùy chọn trình bày; không đổi số liệu, quyền, bộ lọc hay dòng đang chọn. Không tự chuyển sang compact chỉ vì viewport hẹp. Người dùng cảm ứng cần vùng thao tác phù hợp dù đang xem bảng desktop.

Icon nhỏ có thể nằm trong target lớn hơn. Các target cạnh nhau phải có khoảng tách và không chồng lấn; việc tăng vùng bấm không được làm người dùng kích hoạt nhầm hàng kế bên.

## 7. Khung trang và responsive

Desktop tham chiếu **1440×900**, sidebar **200**, topbar **52** là đề xuất chuẩn hóa. Sales trước đây dùng 208/64; các bộ desktop sau dùng 188/52. Không trình bày 200 là giá trị đã có trong cả năm prototype.

- Sidebar có thể thu gọn khoảng 64 khi hữu ích; icon vẫn phải có tên truy cập và đường đọc nhãn bằng bàn phím/cảm ứng.
- Không bắt buộc navigation chỉ có icon; dùng menu mở được nếu nhãn hoặc ngữ cảnh cần thiết.
- Bố cục list/detail dùng phần danh sách linh hoạt và panel vừa nội dung; không khóa mọi màn vào cùng tỷ lệ cột.
- Khi không đủ chiều ngang, detail chuyển thành drawer hoặc trang riêng và giữ bộ lọc/dòng chọn khi quay lại.
- Dải chỉ số xuống dòng theo không gian; không cắt số, đơn vị hoặc trạng thái thiếu.
- Bảng so sánh có thể cuộn ngang trong vùng bảng, giữ nguồn và tiêu đề cần thiết; trang không được tràn ngang ngoài ý muốn.

Field tham chiếu **430×932**, chiều ngang hỗ trợ tối thiểu đề xuất **320 CSS px**. Chiều cao trang do nội dung quyết định; không đặt chiều cao cố định theo 932 hoặc 900.

Header, bottom navigation và action bar phải tính safe area, bàn phím ảo và text zoom. Chỉ dùng vị trí sticky/fixed khi nội dung cuối trang và control đang focus vẫn có thể nhìn thấy, cuộn tới và thao tác.

Breakpoint được chọn theo lúc nội dung không còn đọc/thao tác được, theo [accessibility-and-responsive.md](accessibility-and-responsive.md). Các kích thước canvas và tỷ lệ xuất PNG của prototype không phải breakpoint runtime.

## 8. Dữ liệu và ngôn ngữ hiển thị

Nhãn giao diện ưu tiên tiếng Việt cụ thể: việc gì, nguồn nào, số nào và bước nào tiếp theo. Tên trạng thái cần mapping được kiểm soát; không dịch tùy ý cùng một trạng thái ở mỗi màn.

| Thông tin | Quy tắc trình bày |
|---|---|
| Số lượng | Luôn có ĐVT trong ngữ cảnh; tổng chỉ trong phạm vi có thể cộng hợp lệ |
| Tiền | Nêu tiền tệ và cách làm tròn theo hợp đồng nguồn; không lấy fixture làm chính sách |
| Thiếu / chưa rõ | Hiện nhãn cụ thể; không dùng 0 hoặc dấu trống gây hiểu nhầm |
| Dự kiến / đã ghi | Nhãn và nhóm riêng; màu không thay thế ý nghĩa |
| Thời điểm | Phân biệt ngày nghiệp vụ, lúc dữ liệu được cập nhật và lúc người dùng đang xem |
| Độ đầy đủ | Nêu phạm vi n/N; mẫu số chưa biết phải ghi rõ chưa xác định |
| Nguồn / người xử lý | Có đường mở nguồn theo quyền, chủ trì và bước tiếp nếu còn việc |

Chuẩn UI không chốt timezone, VAT, hạn nợ, grant, ngưỡng cảnh báo hoặc permission từ dữ liệu minh họa. Tất cả phải đến từ cấu hình/hợp đồng nghiệp vụ hợp lệ.

## 9. Cách áp dụng

Giữ token ở một nguồn; component dùng vai trò ngữ nghĩa thay vì tự thêm mã màu/kích thước cho từng màn. Ngoại lệ cần nêu lý do, ngữ cảnh và kiểm chứng liên quan trong thay đổi thiết kế.

Khi bắt đầu một increment UI, chọn page pattern, density và trạng thái cần hỗ trợ trước khi dựng màn. Đối chiếu dữ liệu dài, bàn phím, viewport nhỏ, lỗi, mất kết nối và UNKNOWN theo các tài liệu liên kết; không lấy việc giống ảnh tĩnh làm bằng chứng hoàn thành.
