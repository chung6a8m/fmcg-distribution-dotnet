# Kiểm tra bộ quy chuẩn v0.1

**Phạm vi:** kiểm tài liệu và reference tokens ngày 20/11/2026. Baseline `6abafe25d21678579122c11d0c206818fb1cb0fd`. Không là runtime/E2E/usability/accessibility acceptance.

## Kiểm tra đã thực hiện

- 101 token JSON có tên biến CSS duy nhất, type/value hợp lệ; alias resolve không vòng lặp.
- 101 khai báo CSS khớp giá trị/alias của JSON; không có khai báo thừa.
- Liên kết tương đối đối chiếu với Git tree baseline 395 entry và các file mới.
- Năm PDF prototype có SHA-256 trong mapping; 52 bản màn hình được phân loại theo vai trò.
- Independent technical review với ngữ cảnh mới đã đọc tài liệu, token, hướng dẫn AGENTS/README, business guardrails và ngoại lệ 125%; không còn material blocker trong phạm vi docs. Đây là AI technical review, không là Owner approval hay nghiệm thu người vận hành.

## Contrast của các cặp khai báo

Tính trực tiếp từ sRGB token. So sánh ngưỡng bằng giá trị chưa làm tròn; bảng chỉ làm tròn để đọc. Cặp màu mới trên surface khác, opacity/overlay và mọi trạng thái thực phải kiểm lại khi triển khai.

| Foreground | Background | Tỷ lệ | Ngưỡng | Kết quả palette |
|---|---|---:|---:|---|
| `text.primary` | `surface.card` | 14.102:1 | 4.5:1 | PASS |
| `text.secondary` | `surface.card` | 5.130:1 | 4.5:1 | PASS |
| `text.primary` | `surface.page` | 13.255:1 | 4.5:1 | PASS |
| `action.primary.foreground` | `action.primary.background` | 5.473:1 | 4.5:1 | PASS |
| `action.primary.foreground` | `action.primary.hover` | 6.725:1 | 4.5:1 | PASS |
| `action.primary.foreground` | `action.primary.pressed` | 8.552:1 | 4.5:1 | PASS |
| `action.destructive.foreground` | `action.destructive.background` | 5.505:1 | 4.5:1 | PASS |
| `border.control` | `surface.card` | 3.549:1 | 3:1 | PASS |
| `border.control` | `surface.page` | 3.336:1 | 3:1 | PASS |
| `focus.ring` | `surface.card` | 6.438:1 | 3:1 | PASS |
| `focus.ring` | `surface.page` | 6.051:1 | 3:1 | PASS |
| `status.success.foreground` | `status.success.background` | 4.881:1 | 4.5:1 | PASS |
| `status.info.foreground` | `status.info.background` | 5.631:1 | 4.5:1 | PASS |
| `status.warning.foreground` | `status.warning.background` | 4.952:1 | 4.5:1 | PASS |
| `status.danger.foreground` | `status.danger.background` | 4.829:1 | 4.5:1 | PASS |

Viền nhẹ là trang trí; không đưa nó vào danh sách cặp đạt yêu cầu nhận biết input. Focus 2 px/offset 2 px là đề xuất thiết kế, không tự chứng minh mọi tiêu chí focus/accessibility đã đạt.

## Chưa kiểm trong thay đổi tài liệu

- UI component chưa được triển khai; chưa có browser/device usability hoặc accessibility evaluation cho các màn này.
- Chưa chạy luồng nghiệp vụ thật, lost-ACK/recovery trên UI, hoặc receipt/stock/invoice/accounting integration.
- Không chọn package mới, không import CSS, không đổi web source hay business snapshot.
- Trạng thái GitHub Actions của PR phải xem tại exact commit của PR; kiểm tài liệu ở đây không thay kết quả CI.
- Bộ SVG/PDF minh họa được kiểm render riêng; bản hình không là screenshot của ứng dụng đang chạy.

Xem [governance](governance-and-review.md) để kiểm các increment triển khai sau.
