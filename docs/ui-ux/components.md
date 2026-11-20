# Hợp đồng thành phần UI — đề xuất v0.1

## 1. Phạm vi hợp đồng

Các thành phần dưới đây dùng [nền tảng chung](foundations.md), [token](tokens/tokens.json) và nguồn trong [prototype-mapping.md](prototype-mapping.md). Chúng mô tả anatomy, dữ liệu, trạng thái và hành vi cần có; chưa phải danh sách component đã được lập trình.

Hợp đồng giữ độc lập với framework. Ứng dụng hiện tại dùng React, TypeScript, Vite, PWA và Tailwind v4; đối chiếu [package](../../web/package.json) và [README project](../../README.md). v0.1 không chọn một component library hoặc icon library mới.

Các trạng thái ghi nghiệp vụ, permission, UNKNOWN và phục hồi theo [interaction-patterns.md](interaction-patterns.md). Chi tiết bàn phím, focus, target và responsive theo [accessibility-and-responsive.md](accessibility-and-responsive.md); việc liệt kê chúng không chứng nhận accessibility của ứng dụng.

## 2. Quy tắc chung

- Mỗi control có tên truy cập, nhãn và mục đích ổn định; dùng đúng ngữ nghĩa link, button, input hoặc bảng.
- Phân biệt focus, hover, pressed, selected, disabled và read-only; không dùng một style cho tất cả.
- Mọi semantic color lấy theo Light/Dark hiện tại, gồm foreground của action/status, viền input và focus; kiểm cả hai theme khi áp dụng. Popup/dialog render qua portal phải kế thừa theme trên `html`, theo [hợp đồng theme](tailwind-v4-and-themes.md).
- Hover chỉ hỗ trợ; thông tin cần thiết phải đọc được bằng bàn phím và cảm ứng.
- Disabled cần lý do nhìn thấy hoặc truy cập được và đường xử lý phù hợp; không chỉ làm nhạt nút.
- Read-only vẫn cho đọc/copy dữ liệu được phép; không làm dữ liệu khó đọc như control disabled.
- Loading giữ ngữ cảnh và có nhãn; không biến thành trạng thái thành công hoặc dữ liệu 0.
- Error có nội dung và hướng xử lý, không chỉ viền đỏ. Giữ dữ liệu hợp lệ đã nhập.
- Mật độ desktop và Field thay đổi kích thước theo foundations; không đổi ý nghĩa/hành vi.
- Không tự tích xác nhận thực nhận, thực chi, đã giao hoặc đã kiểm đếm. Dấu tích trong prototype thể hiện một thời điểm sau thao tác.
- Một primary trong mỗi vùng hành động; không lặp lại cùng primary ở panel và footer nếu không có nhu cầu được giải thích.

## 3. App shell và page header

**Anatomy:** tên workspace/phạm vi; navigation; tìm kiếm nếu có; kết nối; người dùng/vai hiện hành; breadcrumb; tiêu đề trang; mô tả; thời điểm dữ liệu; hành động theo ngữ cảnh.

**Trạng thái:** navigation hiện hành, collapsed/expanded, loading phạm vi, không đủ quyền, kết nối gián đoạn, dữ liệu cũ.

**Sử dụng:** desktop ưu tiên navigation bên trái; Field dùng header tác vụ và bốn tab theo bản thiết kế. Giữ nhãn nguồn/khách/chuyến đủ rõ khi mở trang con. Menu và tên vai không tự chứng minh quyền ghi; trạng thái quyền tại tác vụ phải theo nguồn hiện hành.

Header không chứa nhiều CTA ngang nhau. Thay phạm vi phải cập nhật dữ liệu, timestamp và độ đầy đủ cùng nhau; không để số của phạm vi trước mang tiêu đề mới.

## 4. Button và action bar

| Biến thể | Khi dùng | Nội dung / hành vi |
|---|---|---|
| Primary | Bước tiếp chính trong vùng tác vụ | Động từ + đối tượng; nêu lượng/tiền khi giúp xác nhận |
| Secondary | Xem nguồn, quay lại, chỉnh sửa, tác vụ hỗ trợ | Không cạnh tranh thị giác với primary |
| Tertiary / link | Điều hướng hoặc tác vụ phụ ít nhấn | Giữ target và dấu hiệu tương tác rõ |
| Destructive | Hủy/xóa/đảo được phép với hậu quả cần nhận biết | Nhãn cụ thể và hình thức nguy hiểm phân biệt với primary thông thường |
| Disabled | Thiếu điều kiện có thể giải thích | Hiện lý do và bước xử lý; không cho gửi |
| Pending | Đúng yêu cầu đang được xử lý | Ngăn gửi lặp, giữ nhãn tác vụ và tham chiếu |

**Action bar anatomy:** trạng thái/lời giải thích ngắn, secondary nếu cần, primary. Phần dữ liệu có hệ quả phải ở trong màn review, không chỉ trong nhãn nút.

Sticky bar phải chừa không gian cho nội dung, safe area và bàn phím. Chỉ hiển thị tác vụ của nguồn đang được chọn; sort hoặc refresh không được làm nút âm thầm trỏ sang nguồn khác.

Kết quả không rõ chuyển sang tra yêu cầu gốc. Không đổi nút thành “Thử lại” nếu thao tác đó tạo một yêu cầu nghiệp vụ mới.

## 5. Card, metric strip và source line

| Thành phần | Anatomy | Quy tắc |
|---|---|---|
| Card / section | Tiêu đề, metadata tùy chọn, nội dung, hành động tùy chọn | Nhóm một chủ đề; không bao từng dữ liệu nhỏ trong một card riêng |
| Metric strip | Nhãn, giá trị, đơn vị, phạm vi, trạng thái thiếu | Ưu tiên 4–5 chỉ số desktop; xuống dòng khi cần; mỗi số mở nguồn khi có quyền |
| Source line | Mã/loại nguồn, đối tượng, thời điểm, trạng thái, đường mở | Mã đầy đủ có thể đọc/copy; không che quyền hoặc hợp nhất nguồn khác nhau |
| Key-value list | Nhãn và giá trị cùng hàng hoặc xếp dọc | Tiền/lượng căn ổn định; nhãn dài được xuống dòng |

**Trạng thái:** có dữ liệu, thiếu, chưa rõ, cũ, loading, bị giới hạn quyền. Số dự kiến và số đã ghi có nhãn riêng; không dùng màu xanh để suy là đã ghi nhận.

Không biến toàn bộ card thành vùng click nếu bên trong có các control khác gây xung đột. Nếu card điều hướng, mục đích, focus và target phải rõ.

## 6. Table và danh sách dữ liệu

**Anatomy:** tiêu đề/phạm vi; bộ lọc; kết quả/tổng trong phạm vi; header cột; hàng; lựa chọn nếu cần; hành động theo hàng; phân trang hoặc tải thêm.

| Phần | Hợp đồng |
|---|---|
| Header | Nhãn cột rõ, ĐVT/tiền tệ khi áp dụng; sort có trạng thái và thao tác bàn phím |
| Số liệu | Tiền/lượng căn phải; số so sánh dùng cùng cơ sở; không cắt mất đơn vị |
| Hàng chọn | Nền chọn + chỉ dấu/ngữ nghĩa lựa chọn; khác với hover và focus |
| Mã / tên dài | Có đường đọc đầy đủ; không phụ thuộc tooltip hover |
| Cột nhiều | Chọn/ẩn cột phụ; cuộn ngang trong vùng bảng; giữ nguồn và header cần đối chiếu |
| Hành động | Nhãn cụ thể; tác vụ đang sửa gắn ID nguồn ổn định, không gắn chỉ số hàng |
| Tổng | Phân biệt tổng trang, tổng bộ lọc và phần đã biết; không cộng các đơn vị không tương thích |
| Dữ liệu lớn | Phân trang/tải thêm có trạng thái; chỉ dùng virtualization khi giữ được ngữ nghĩa và focus |

**Trạng thái bắt buộc:** loading ban đầu, refreshing, có dữ liệu, không có dữ liệu, lọc không có kết quả, lỗi, không đủ quyền, snapshot cũ, selection và hàng đang xử lý.

Sort/filter không âm thầm chuyển nguồn đang sửa. Refresh giữ lựa chọn hợp lệ; nếu nguồn biến mất hoặc quyền đổi, nêu rõ và dừng hành động không còn hợp lệ.

Chuyển bảng thành thẻ trên mobile khi mục đích là xử lý từng nguồn. Giữ bảng cuộn có kiểm soát khi nhiệm vụ thực sự cần so sánh nhiều cột; không thu nhỏ chữ để ép vừa.

## 7. List/detail và hàng đợi công việc

**Anatomy:** danh sách nguồn, trạng thái chọn, panel chi tiết, bằng chứng/điều kiện, bước tiếp theo và người xử lý khi có bàn giao.

Hàng đợi ưu tiên trả lời “việc nào đang chờ tôi, vì sao, mở nguồn nào, làm gì tiếp”. Thứ tự ưu tiên phải có cơ sở được khai báo; số việc không phải tổng tiền hoặc số giao dịch đã ghi.

Desktop cho xem danh sách và chi tiết cùng lúc. Màn hẹp dùng drawer/trang chi tiết, giữ bộ lọc, vị trí và lựa chọn khi quay lại. Không tự mở item đầu tiên rồi cho phép commit trước khi người dùng hiểu nguồn.

Nếu đổi dòng khi có dữ liệu chưa lưu, xử lý nháp có chủ đích. Nếu nguồn đang chọn đã thay đổi, giữ nội dung để so sánh và yêu cầu kiểm tra lại theo interaction patterns.

## 8. Form, input và checkbox

**Anatomy:** nhãn hiển thị, yêu cầu bắt buộc/tùy chọn, input, đơn vị hoặc ngữ cảnh, hướng dẫn, lỗi, trạng thái đã lưu/chưa lưu khi cần.

**Trạng thái:** trống, có giá trị, focus, invalid, disabled, read-only, validating, pending submit, conflict/nguồn thay đổi.

- Label không chỉ nằm trong placeholder; lỗi liên kết với đúng trường.
- Nhóm trường theo tác vụ: nguồn, thực tế nhập, điều kiện, dự kiến sau ghi.
- Số đang nhập khác số tính toán; giữ đơn vị sát giá trị. Validation theo hợp đồng nguồn, không theo fixture.
- Dấu phân cách số được xử lý nhất quán; không tự biến giá trị chưa hợp lệ thành 0.
- Field dùng bàn phím nhập phù hợp, vẫn có thể nhập/sửa đầy đủ dữ liệu hợp lệ.
- Checkbox sự thật bắt đầu chưa xác nhận cho tác vụ mới; mỗi nhãn nói rõ điều người dùng xác nhận.
- Thay số tiền/lượng có thể làm mất hiệu lực bước kiểm trước; phải cho thấy trạng thái cần kiểm lại.
- Không dùng read-only input để giả vờ một giá trị có thể sửa; chọn presentation phù hợp.
- Submit bị chặn do lỗi có tóm tắt và vị trí sửa, giữ dữ liệu hợp lệ và focus phù hợp.

## 9. Lookup, filter và quantity stepper

**Lookup anatomy:** nhãn, query, kết quả, ID/nhãn đủ phân biệt, lựa chọn hiện tại, clear nếu được phép. Hỗ trợ loading, empty, lỗi, không quyền và kết quả cũ.

Gõ tìm không tự chọn item đầu. Mũi tên duyệt; Enter chọn; Escape đóng và giữ dữ liệu hợp lệ. Lựa chọn hiển thị cả thông tin giúp phân biệt nguồn trùng tên. Quyền/phạm vi được kiểm ở nguồn và lúc ghi theo hợp đồng.

**Filter anatomy:** nhãn phạm vi, giá trị, số điều kiện đang áp dụng, áp dụng/đặt lại khi cần. Thay filter và tải dữ liệu mới có trạng thái rõ; không hiển thị tổng cũ dưới filter mới.

**Stepper anatomy:** giảm, giá trị sửa trực tiếp, tăng, ĐVT, giới hạn/lý do chặn. Mỗi nút có target phù hợp; không chỉ cho nhập bằng cách bấm tăng nhiều lần. Min/max/step đến từ nghiệp vụ và UoM, không mặc định mọi SKU chỉ nhận số nguyên.

## 10. Dialog và drawer

| Loại | Khi dùng | Hợp đồng |
|---|---|---|
| Dialog xác nhận | Quyết định có hệ quả cần kiểm lại trong phạm vi ngắn | Tiêu đề hành động, nguồn, dữ liệu chính, tác động, secondary quay lại, một primary |
| Dialog thông tin | Nội dung ngắn cần focus tạm thời | Có cách đóng rõ; không dùng để thay mọi notice |
| Drawer chi tiết | Xem/sửa nguồn trong ngữ cảnh danh sách | Giữ nguồn và trạng thái nháp; responsive thành trang khi cần |

Dialog modal quản lý focus, có tên truy cập, chặn tương tác nền và trả focus về nơi mở hợp lệ. Escape/đóng xử lý theo trạng thái nháp; không làm mất dữ liệu hoặc ngầm hủy một yêu cầu đã gửi.

Không dùng chuỗi modal lồng nhau để thực hiện quy trình dài. Form nhiều nguồn/hệ quả dùng trang review. Với phá hủy, hành động nguy hiểm có nhãn riêng và không nhận focus theo cách dễ kích hoạt nhầm.

Drawer phải công bố rõ có modal hay không. Không vừa khóa focus vừa cho kỳ vọng thao tác bảng phía sau.

## 11. Status badge, notice và toast

| Thành phần | Mục đích | Hợp đồng |
|---|---|---|
| Status badge | Trạng thái ngắn của đúng thực thể | Chữ + tone; state mapping riêng cho đơn, hàng, tiền, invoice, request |
| Inline notice | Điều cần hiểu/xử lý trong ngữ cảnh | Tiêu đề tùy độ dài, giải thích, bước tiếp, nguồn khi cần |
| Page notice | Sự kiện ảnh hưởng phạm vi trang | Nêu phạm vi và tác động; không đẩy lỗi trường xuống một banner chung |
| Toast | Phản hồi ngắn cho tác vụ ít cần lưu lâu | Không là nơi duy nhất chứa lỗi, nguồn hoặc kết quả tài chính cần đối chiếu |

**Tone:** thông tin, tích cực, cảnh báo, lỗi; UNKNOWN có nhãn “Chưa rõ kết quả” và hành động tra gốc. Không suy “thất bại” từ timeout hoặc “đã xong” từ một badge xanh của bộ phận khác.

Thông báo quan trọng tồn tại trong màn/trạng thái nguồn. Toast có thể thông báo thành công đã được xác nhận, nhưng người dùng vẫn tìm lại được kết quả và chứng từ. Tránh thông báo tự biến mất trước khi có thể đọc/thao tác.

## 12. Empty, loading và dữ liệu thiếu

| Trạng thái | Nội dung cần có | Hành động |
|---|---|---|
| Chưa có bản ghi | Giải thích đúng phạm vi | Tạo mới khi có quyền hoặc mở nguồn liên quan |
| Không khớp bộ lọc | Nhắc filter đang áp dụng | Điều chỉnh/đặt lại filter |
| Chưa tải xong | Nhãn đang tải, skeleton/spinner phù hợp | Không lặp submit; hủy truy vấn đọc nếu được hỗ trợ |
| Không đọc được dữ liệu | Lỗi cụ thể có thể công bố | Thử tải lại dữ liệu đọc hoặc đường hỗ trợ |
| Thiếu một phần | Phần có dữ liệu, phần thiếu, độ đầy đủ | Mở nguồn thiếu/người cần cập nhật |
| Không đủ quyền | Giải thích trong phạm vi được phép | Đổi phạm vi hoặc liên hệ người có trách nhiệm |

Skeleton không mang số giả. Khi refresh, giữ snapshot có timestamp nếu chính sách cho phép và ghi rõ đang cập nhật. Không hiển thị “0 kết quả” trong lúc chưa biết kết quả.

## 13. Connection và freshness

**Anatomy:** trạng thái kết nối, mốc dữ liệu đã xác nhận, nhãn dữ liệu cũ/thiếu khi có, hành động làm mới hoặc tra kết quả phù hợp.

Kết nối mạng, dữ liệu mới và kết quả command là ba thông tin riêng. “Đã kết nối” không chứng minh mọi nguồn đã cập nhật; “làm mới” không được gửi lại nghiệp vụ.

Khi offline, chặn business write theo hợp đồng project; nháp nếu được phép có nhãn nơi lưu rõ ràng. Khi mất phản hồi sau gửi, giữ identity/nội dung và UNKNOWN; không dùng offline banner để che trạng thái yêu cầu.

Thông báo có snapshot mới không làm nhảy dòng đang kiểm. Khi áp dụng, cập nhật số, thời điểm và phạm vi cùng nhau; kiểm lại điều kiện hiện hành trước commit.

## 14. Tabs, tiến trình và lịch sử nguồn

**Tabs anatomy:** nhãn, trạng thái đang chọn, số lượng khi có ý nghĩa, panel liên quan. Chỉ dùng tabs cho các góc nhìn đồng cấp của cùng ngữ cảnh; chuyển bước ghi nghiệp vụ không được giả thành thao tác mở tab.

Nhãn và số đếm phải có cùng phạm vi. “Lần giao (3)” không đồng nghĩa ba lần giao đủ. Tab có dữ liệu thiếu hoặc lỗi cần giữ được đường xử lý và không xóa bản nháp khi người dùng chuyển góc nhìn.

**Tiến trình:** ghi rõ đại lượng và mẫu số, chẳng hạn lượng đã xử lý / lượng yêu cầu của đúng nguồn. Tỷ lệ hoàn tất một phần không đại diện cho tiền, hóa đơn, kế toán hoặc toàn chuyến.

**Lịch sử:** mỗi sự kiện có thời điểm, tác vụ, người/nguồn được phép công bố và kết quả. Phân biệt sự kiện đã ghi, đề nghị, dự kiến và kết quả chưa rõ. Không thêm mốc “hoàn tất” chỉ vì người dùng đã đi qua màn xác nhận.

Trên mobile, lịch sử có thể mở theo nguồn và tải thêm. Thứ tự thời gian, nhãn và thông tin kết quả vẫn đọc được nếu màu hoặc đường nối timeline không hiển thị.

## 15. Hợp đồng dữ liệu tối thiểu của component

Đây là nội dung cần thỏa thuận khi triển khai, không phải API schema mới. Component không tự tạo quyền, policy hoặc trạng thái nghiệp vụ từ dữ liệu hiển thị.

| Nhóm dữ liệu | Thông tin cần có khi phù hợp |
|---|---|
| Identity | ID nguồn ổn định; loại thực thể; nhãn hiển thị riêng với ID |
| Phạm vi | Bộ lọc/ngày/đơn vị đang áp dụng; phần có và thiếu dữ liệu |
| Freshness | Mốc snapshot đã xác nhận; trạng thái đang tải/lỗi/cũ độc lập |
| Action | Loại tác vụ; điều kiện cho phép; lý do chặn; nguồn tác động |
| Command | Identity và nội dung gốc khi đã gửi; kết quả đang chờ/đã biết/UNKNOWN |
| Editing | Giá trị hiện có; draft; dirty state; lỗi tại trường; xung đột phiên bản |
| Disclosure | Nhãn truy cập; thông tin có thể công bố; đường đọc nội dung đầy đủ |

Consumer cung cấp ý nghĩa và dữ liệu đúng hợp đồng; component trình bày nhất quán và phát sự kiện thao tác rõ ràng. Mount, mở tab, chọn dòng hoặc render lại không tự phát lệnh ghi nghiệp vụ.

## 16. Tiêu chí review component

Mỗi component được triển khai cần có ví dụ trạng thái liên quan, nội dung dài/thiếu, thao tác bàn phím, viewport nhỏ, phóng chữ trong phạm vi kiểm đã được chấp nhận và nhãn tiếng Việt. Page pattern phải được kiểm với nguồn thật của hợp đồng, không chỉ một dòng dữ liệu demo. Giữ ngoại lệ 125% hiện hành theo tài liệu accessibility; không dùng danh sách này để mở lại gate đã được loại.

Review xác nhận anatomy, mục đích, state mapping, mất/giữ context và kết quả thực tế của thao tác. Bằng chứng render đúng chỉ xác nhận hình ảnh; kết quả nghiệp vụ, kiểm thử runtime và chấp thuận vận hành được báo cáo riêng.
