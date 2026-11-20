# Component specification - <tên thành phần>

> Template. Đây là hợp đồng UI; API và business state đến từ contract của consumer. Không tự chọn thư viện mới chỉ để hoàn tất template.

## 1. Nhu cầu dùng chung

- Bối cảnh dùng, consumer dự kiến và trường hợp không nên dùng:
- Component/pattern hiện có có thể tái sử dụng:
- Rule nền tảng, density, tokens và typography:

## 2. Anatomy và interface

- Tên/nhãn/ngữ nghĩa; vùng dữ liệu; vùng hành động:
- Dữ liệu đầu vào, identity ổn định, scope/asOf, null semantics:
- Event đầu ra; event nào chỉ đọc và event nào thể hiện ý định command:
- Ownership state/buffer/focus; controlled/uncontrolled được chọn rõ khi code:

## 3. Ma trận trạng thái

| State | Hình thức / nhãn | Keyboard / focus | Action / giới hạn |
|---|---|---|---|
| default | <điền> | <điền> | <điền> |
| focus / hover / pressed / selected | <điền> | <điền> | <điền> |
| disabled / readonly | <điền> | <điền> | <điền> |
| loading / empty / error | <điền> | <điền> | <điền> |
| invalid / conflict | <điền hoặc N/A + lý do> | <điền> | <điền> |
| partial / stale / pending / UNKNOWN | <điền hoặc N/A + lý do> | <điền> | <điền> |

## 4. Tương tác và accessibility

- Accessible name, role, state/value và thông báo cập nhật:
- Tab/Enter/Escape/arrows; IME; focus vào/ra; không keyboard trap:
- Light/Dark state matrix; explicit theme/System; color-scheme và portal/native control; forced-colors/reduced-motion trong phạm vi:
- Contrast cặp thật, target, chữ dài, safe area và cách dùng khi hover không có:
- Responsive và mode cảm ứng; sizing theo layout (block/grid min-height; native table theo [recipe](../components.md#compact-table-sizing)), nội dung tăng chiều cao:
- Hàng đọc/editor/validation: row và control đo thực, cell padding/border, focus clearance; target không chồng hàng:

## 5. Dữ liệu và an toàn tác vụ

- Null/0/unknown, số lớn/âm/thập phân, Unicode và copy:
- Tác vụ async, query cũ, unmount, scope/tenant đổi, permission bị thu hồi:
- Không command khi mount/render/select; không auto-confirm sự thật:

## 6. Validation và bàn giao

- State examples và tình huống tương tác cần kiểm:
- Test có rủi ro cụ thể; browser/device/scope thật và kết quả:
- Library/license/dependency ADR nếu có:
- Migration nếu thay component cũ; API consumer bị ảnh hưởng:
- Ngoại lệ, evidence, version và URL chấp nhận:
