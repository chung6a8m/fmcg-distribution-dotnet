# Screen specification - <mã và tên màn>

> Template. Điền theo contract hiện hành; không dùng dữ liệu ví dụ làm policy. Trạng thái: DRAFT / REVIEWED / ADOPTED cùng URL và phiên bản thực.

## 1. Mục tiêu và người dùng

- Nhiệm vụ/câu hỏi người dùng cần giải quyết:
- Actor, assignment, phạm vi xem và phạm vi ghi:
- Thiết bị/input và density; viewport được review:
- Business/API contract, Owner decision và blockers liên quan:

## 2. Vị trí và điều hướng

- Route/menu/page pattern; điểm vào và đường quay lại:
- Scope, filters, selection và scroll cần giữ:
- Hành động chính; secondary; điều kiện cho phép/lý do chặn:

## 3. Nguồn dữ liệu

| Vùng / trường | Nguồn và identity | Đơn vị / precision | Scope / asOf | Thiếu / UNKNOWN / quyền |
|---|---|---|---|---|
| <điền> | <điền> | <điền> | <điền> | <điền> |

Nêu rõ tổng trang, tổng filter, tổng phần đã biết và mẫu số n/N nếu có. Tách snapshot thương mại với authority hiện hành.

## 4. State và command mapping

| Trạng thái API/nguồn | Nhãn UI / tone | Người dùng có thể làm | Tác động / đường phục hồi |
|---|---|---|---|
| <điền> | <điền> | <điền> | <điền> |

Đánh dấu áp dụng/không áp dụng và lý do cho: loading, empty, error, invalid, readonly, disabled, partial, stale, pending, UNKNOWN, rejected, conflict, permission revoked, offline trước gửi, physical action đã xảy ra.

## 5. Form, bảng và bàn phím

- Field/edit buffer, validation khi nào, lỗi nào chặn và cách thoát/sửa:
- Table hay grid; Enter/Tab/Escape; IME và lookup commit:
- Dirty state, đổi dòng/filter, concurrency/version và background refresh:
- Focus, accessible names, sticky footer/safe area:

## 6. Review trước ghi và kết quả

- Nguồn, lượng/tiền và tác động cần nhìn trước khi gửi:
- Xác nhận sự thật nào bắt đầu chưa tích; evidence nào cần thực sự có:
- Identity/version/payload được giữ; lookup outcome; không tạo nghiệp vụ thay thế:
- Kết quả đã ghi và bước tiếp theo; owner/case nếu còn việc:

## 7. Responsive và nội dung

- Chuyển list/detail/table/filters ở không gian nào:
- Nhãn tiếng Việt, số dài/âm/thập phân, tiền tệ, ĐVT, business date/timezone:
- Quyền export/print và metadata cần đi cùng:

## 8. Evidence và quyết định

- Hình/state cần review; link bản vẽ và phiên bản:
- Ca tương tác có rủi ro và kết quả thực:
- Rule UI/UX áp dụng, ngoại lệ có ID/phạm vi/người chấp nhận:
- NOT_RUN/BLOCKED và dependency; không ghi PASS từ ảnh tĩnh:
- Commit, ngày, reviewer và quyết định chấp nhận:
