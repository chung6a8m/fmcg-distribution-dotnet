# Quản trị quy chuẩn và review

## UX-G01 - Trạng thái và trách nhiệm

v0.1 là **PROPOSED_FOR_ADOPTION**. Owner review có thể chấp nhận một phiên bản tài liệu và những ngoại lệ cụ thể; chỉ ghi `ADOPTED` khi có quyết định/PR chấp nhận truy được. Merge tài liệu không tự tạo component hoặc nâng gate runtime.

Designer/implementer chuẩn bị đặc tả và evidence. Reviewer kiểm tính nhất quán với hợp đồng và chuẩn được chấp nhận. Với solo developer, AI review được ghi đúng là independent technical review nếu dùng ngữ cảnh riêng; không giả danh người vận hành, Human Security hay người phê duyệt độc lập nghiệp vụ.

MUST = bắt buộc trong bản chuẩn được chấp nhận và phạm vi liên quan; SHOULD = mặc định có thể lệch với lý do; MAY = tùy chọn. Quy định nghiệp vụ hiện hành vẫn có hiệu lực dù v0.1 còn proposed.

## UX-G02 - Một nguồn token và phiên bản

`tokens/tokens.json` là nguồn giá trị. `tokens/tokens.css` là bản tham chiếu được sinh từ JSON; không sửa riêng CSS, không import thẳng vào ứng dụng trước increment UI-01.

Schema token là **schema dự án**, không tuyên bố tương thích DTCG hay công cụ Figma. Mỗi token có type, value và CSS variable; `provenance` phân biệt inherited/proposed. Tham chiếu giữa token dùng `{token.name}` và phải được resolve/kiểm vòng lặp trước xuất.

Thay đổi phá hành vi, nghĩa token hoặc default tương tác cần changelog/migration notes và review các màn dùng nó. Thêm token/component phải chứng minh nhu cầu dùng chung; không tạo hàng chục biến thể chỉ để khớp một screenshot.

Dark theme, brand override hoặc density mới cần bộ cặp contrast và trạng thái riêng. Không suy tất cả theme pass từ Light.

## UX-G03 - Checklist cho Codex trước khi viết UI

1. Đọc README quy chuẩn, trạng thái/decisions và hợp đồng module/Owner có hiệu lực.
2. Xác định scope task có cho phép code/runtime hay chỉ docs. Xem các gate của increment, không suy authorization từ roadmap đích.
3. Điền screen/component spec; mapping API status → nhãn UI/action, quyền đọc/ghi và nguồn số liệu.
4. Tìm component đã có; dùng token và pattern chung. Library mới cần ADR/license/security review theo quy trình project.
5. Ghi trạng thái tải/rỗng/lỗi/invalid/readonly/disabled/pending/UNKNOWN/thiếu quyền và cách phục hồi có liên quan.
6. Chốt keyboard/focus/mobile cùng việc hiển thị; không để thành phụ lục sau khi code xong.

## UX-G04 - Review trước khi chấp nhận một increment UI

| Nhóm | Bằng chứng cần review |
|---|---|
| Hợp đồng | Nguồn số liệu, quyền, mốc, snapshot, command mapping đúng contract hiện hành |
| Trình bày | Token, typography, density, giá trị dài/âm/thập phân, nhãn/đơn vị và cột quan trọng |
| Tương tác | Keyboard, focus, IME, lookup, sửa ô, giữ buffer/filter/selection |
| Trạng thái | Initial load/empty/error, refresh cũ, pending, rejected, UNKNOWN và conflict |
| Thực tế | Checkbox nhận/chi không tự tick; thông báo đã ghi có kết quả nguồn; tách physical event và posting |
| Responsive | Các viewport/thiết bị đã chốt cho màn, vùng chạm, safe area, bàn phím mobile |
| Accessibility | Contrast cặp thực, accessible name, semantics, focus/keyboard, kiểm bổ sung trong phạm vi được duyệt |
| Regression | Kiểm component/luồng có rủi ro cụ thể và PWA behavior liên quan; exact final commit evidence |
| Đầy đủ | Screenshot đúng trạng thái + kết quả tương tác; không chỉ happy path hoặc ảnh tĩnh |

Không bắt viết test hình thức cho mỗi đổi copy/spacing nhỏ. Tập trung regression vào contract có rủi ro: focus/caret, lookup identity, số/đơn vị, selection khi đổi scope, permission/revocation, pending/UNKNOWN, chống ghi lặp và PWA reload. Chỉ đánh dấu đã chạy khi có kết quả thật; ghi NOT_RUN/BLOCKED rõ phạm vi.

Playwright đã là harness browser mặc định trong repo. Test hiện hành của health/PWA không chứng minh màn nghiệp vụ tương lai. Component/state tests bổ sung khi component được triển khai; không thêm thư viện chỉ để làm checklist đẹp.

## UX-G05 - Ngoại lệ

Ngoại lệ phải có ID, yêu cầu bị lệch, màn/phạm vi, lý do, rủi ro, người chấp nhận, ngày, thời hạn/điều kiện xem lại nếu có, evidence và biện pháp thay thế. Ngoại lệ hết hạn không tự gia hạn.

Không dùng một ngoại lệ global để bỏ mọi quy tắc accessibility, bảo mật hoặc business. Ngoại lệ prototype chỉ giải thích hình; không tự áp dụng runtime. Không sửa lịch sử Owner decision để làm tài liệu mới có vẻ đã thống nhất.

### Mẫu ngoại lệ

- ID / phiên bản chuẩn:
- Màn, component, increment và phiên bản ứng dụng:
- Rule bị lệch / lý do:
- Tác động và biện pháp thay thế:
- Evidence / giới hạn chưa kiểm:
- Người có thẩm quyền chấp nhận / ngày / URL:
- Thời hạn hoặc điều kiện xem lại (chỉ khi thực sự được quyết định):

## UX-G06 - Bàn giao PR

PR nêu vấn đề người dùng, hành vi đổi, màn/component/rule liên quan, quyết định mới, cách kiểm và phần chưa kiểm. Đính evidence có scope/commit; không đưa fixture thành production data.

Các file `business/` và `business.lock.json` bất biến. Quy chuẩn UI không sửa chúng để khớp màn hình. Giữ các Owner exceptions và ranh giới SYNTHETIC / SOURCE_ONLY / PREPROD / production theo hợp đồng riêng.
