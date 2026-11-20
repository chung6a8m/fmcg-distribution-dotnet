# Roadmap áp dụng UI/UX Standard

**PROPOSED / PLANNING ONLY.** Đây là cách phân rã áp dụng để review; chưa là authorization thực thi các increment UI-01…05 hoặc APP-02C/D/E. Mỗi bước cần kiểm điều kiện của roadmap ứng dụng và Owner decision liên quan trước khi viết code.

## Phạm vi hiện tại: UI-00

**UI-00 - Design baseline và reference tokens:** bộ tài liệu `docs/ui-ux/`, JSON/CSS reference, adapter Tailwind v4, Light/Dark palette, bảng thị giác Light, template, mapping 52 hình, đường dẫn từ README/AGENTS. Trạng thái bàn giao là đề xuất để review. Không import token vào runtime, không đổi `web/src`, package/lockfile, business hoặc CI gate hiện hữu.

Kiểm tra phù hợp thay đổi: JSON/alias/CSS parity, contrast của cặp màu đã khai báo, liên kết đến cây Git nền, không đổi business/lock, render visual reference và independent review. Đó là kiểm tra artifact, không kiểm thử sản phẩm.

## Các increment tiếp theo

| Increment | Kết quả có thể review độc lập | Vùng file dự kiến | Điều kiện / evidence |
|---|---|---|---|
| UI-01 Foundations runtime | Áp dụng adapter Tailwind v4, font được cấp phép, Light/Dark/System theo baseline được chấp nhận, density và các control cơ bản trong demo có trạng thái | `web/src/ui/`, `web/src/style.css`, tests liên quan | Owner chấp nhận baseline; chốt adapter/library khi cần; build/PWA regression; theme bootstrap/native controls/portal; Light/Dark contrast/focus/keyboard trên control thật |
| UI-02 Form, lookup và bảng | Input, validation, lookup identity và table/grid contract dùng chung | `web/src/ui/`, `web/e2e/` và unit/state tests khi phù hợp | Chốt grid/library bằng ADR nếu có; kiểm IME/caret, Enter/Tab/Escape, filter/selection, dữ liệu dài/decimal |
| UI-03 Source, command và recovery UX | Scope/asOf, result panel, pending/UNKNOWN, preflight/conflict và permission states | UI wrappers + API adapter được module cho phép | API contract và runtime scope được cấp phép; lookup cùng identity, không double write; tách ảnh demo với API integration evidence |
| UI-04 Một vertical slice | Một luồng nghiệp vụ trọn vẹn dùng component đã có | Màn/route/module API theo screen spec | Chọn luồng đã đủ authorization; riêng A phải giải quyết blockers APP-02C liên quan; review theo contract, không theo screenshot đơn lẻ |
| UI-05 Mở rộng năm vai trò | Các role patterns và responsive được tích hợp từng PR | Screen specs, routes/components và tests tương ứng | Bằng chứng riêng cho Field, Finance và Owner; device/acceptance được chốt; không suy pass từ UI-04 |

Tên thư mục trên là dự kiến phục vụ kế hoạch; chưa tạo skeleton và chưa định nghĩa API component. Trước mỗi increment, viết design/spec và kế hoạch thực thi có interfaces/files/tests cụ thể theo lựa chọn đã được chấp nhận. Không lấy roadmap này thay một implementation plan.

## Gate trước khi bắt đầu increment

1. Xác nhận phạm vi code/runtime và những gì Owner đã cho phép; giữ mọi blocker của roadmap nghiệp vụ.
2. Chọn phiên bản UI/UX baseline và các ngoại lệ được chấp nhận cho màn/thiết bị đó.
3. Hoàn thành screen/component spec, ADR cần thiết và state mapping.
4. Đặt bằng chứng cho rủi ro cụ thể, theo commit cuối. Ghi rõ NOT_RUN khi chưa có runtime/thiết bị.
5. Review độc lập, sửa findings và giữ regression của project trong phạm vi thực thi được phép.

## Điều kiện không được suy rộng

- Chấp nhận UI-00 không cấp quyền nghiệp vụ hoặc triển khai PREPROD.
- UI-01 demo controls không chứng minh invoice/cash/stock posting.
- UI-02 browser tests không tự sửa hay miễn hợp đồng Confirm/Reserve/load.
- UNKNOWN được minh họa bằng fixture không chứng minh lost-ACK recovery thật.
- Ngoại lệ 125% APP-00B/C tiếp tục được ghi đúng; gate mới xung đột cần Owner quyết định phạm vi.
