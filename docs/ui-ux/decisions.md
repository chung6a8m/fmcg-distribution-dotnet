# Sổ quyết định UI/UX

**Ngày lập 20/11/2026; v0.1 PROPOSED_FOR_ADOPTION.** Phân biệt yêu cầu người dùng, quyết định repository và lựa chọn do designer đề xuất. Các câu hỏi tùy chọn trong lượt chuẩn hóa chưa có lựa chọn trả về; không ghi chúng thành Owner approval.

| ID | Nội dung | Trạng thái / căn cứ |
|---|---|---|
| UX-D01 | Kế thừa năm vai trò và ngôn ngữ thị giác của prototype | USER_REQUESTED trong cuộc trao đổi thiết kế |
| UX-D02 | Ưu tiên giao diện gọn, nhiều dữ liệu hữu ích cùng màn | USER_SELECTED trước khi dựng các bộ sau |
| UX-D03 | React + TypeScript + Vite + PWA online-first; Tailwind v4 | EXISTING_REPO_DECISION; xem AGENTS và Owner APP/APP-00B |
| UX-D04 | Quy chuẩn + token tham chiếu + template/review; component triển khai theo increment | DESIGNER_PROPOSED; phù hợp yêu cầu chuẩn hóa, chưa mở rộng sang code ứng dụng |
| UX-D05 | Giữ palette teal/navy, bổ sung semantic tokens và viền control đủ nhận biết | DESIGNER_PROPOSED dựa trên palette chung năm bộ |
| UX-D06 | Noto Sans 400/700; fallback Segoe UI/system sans | DESIGNER_PROPOSED; artifact dùng Noto Sans, runtime hiện tại Segoe UI |
| UX-D07 | Desktop body14/table13, row min36/control min32; comfortable row44/control40 | DESIGNER_PROPOSED; không coi cỡ 11,2–12 của bản vẽ là chuẩn sản phẩm |
| UX-D08 | Field body16, control/target48; bố cục một cột | DESIGNER_PROPOSED; khớp định hướng Field, cần kiểm thiết bị thật |
| UX-D09 | Light kế thừa prototype; thêm Dark semantic reference và hợp đồng Light/Dark/System trong review PR #78 | DESIGNER_PROPOSED theo trọng tâm review của người dùng; chưa là Owner adoption hoặc runtime/forced-colors acceptance |
| UX-D10 | Sidebar + trang/list-detail; không tự chọn thư viện grid/component/icon/docking | DESIGNER_PROPOSED; library choice cần ADR khi thực sự triển khai |
| UX-D11 | Giữ ngoại lệ 125% APP-00B/C đúng phạm vi | EXISTING_OWNER_DECISION, OWN-APP00B-13-02 |
| UX-D12 | Không lấy prototype làm máy trạng thái/permission/policy đã duyệt | EXISTING_CONTRACT + ranh giới artifact; đặc biệt C0-R01…06 |

### Phạm vi bổ sung khi review PR #78

Người dùng yêu cầu chú ý Tailwind CSS v4 và Light/Dark. Review bổ sung hợp đồng adapter, Dark semantic values, generator/checker và checklist theme trong cùng phạm vi tài liệu. Đây là đề xuất kỹ thuật có thể review; không ghi rằng Owner đã chọn palette Dark cụ thể hoặc cấp quyền triển khai UI-01. Giữ nguyên các kích thước/font đề xuất và business authorization.

## Các cách áp dụng đã cân nhắc

1. **Quy chuẩn và tokens tham chiếu:** phù hợp ngay khi UI nghiệp vụ chưa được triển khai, giữ nguồn dùng chung cho Codex; cần increment component sau. Đây là phương án designer dùng để chuẩn bị v0.1.
2. **Quy chuẩn cùng component library đầy đủ:** có giá trị khi bắt đầu xây UI hàng loạt; đòi hỏi lựa chọn thư viện, screen contracts, runtime testing và authorization riêng. Chưa nằm trong thay đổi tài liệu này.

Không có lựa chọn người dùng trả về cho hai câu hỏi tùy chọn về phạm vi/mật độ. Các giá trị dùng trong bản thảo là mặc định đề xuất để tạo kết quả có thể review, không là biên bản Owner đã chọn phương án 1.

## Cần quyết định khi có phạm vi triển khai tương ứng

| Chủ đề | Người quyết định / bước cần có | Mặc định hiện tại |
|---|---|---|
| Thiết bị thực tế, Android/iOS, kho có cảm ứng/scanner | Owner khi chốt acceptance của UI tương ứng | Chưa suy thiết bị từ kích thước ảnh |
| Mức kiểm zoom/phóng chữ ngoài phạm vi đã có | Owner ghi rõ phạm vi, giữ lịch sử ngoại lệ | Không mở lại 125% APP-00B/C |
| Component/grid/icon library | ADR so sánh nhu cầu, license, accessibility, keyboard và chi phí | Không chọn thư viện chỉ vì dùng ở repo khác |
| Runtime theme selection / brand override | Owner chốt adoption và scope UI-01; spec, bootstrap, portal/native controls và theme verification | Light/Dark reference đã có; System là quy tắc resolve đề xuất; chưa có switcher runtime; brand override chưa chốt |
| Confirm/Reserve, ShipTo, policy validity, partial/repeated load | Contract increments của APP-02C và C0-R01…06 | Giữ trạng thái blocking/deferred ở baseline |
| Mốc stale, SLA, tuổi nợ, KPI, giá/thuế, hạn mức | Business/policy contract được phê duyệt | Chưa thiết lập; không lấy số demo |
| Offline drafts, GPS/ảnh/chữ ký bắt buộc, scan | Product/security contract riêng | Không hứa các năng lực chưa có |

## Nguồn

- [AGENTS.md](../../AGENTS.md), [Owner APP 15/11](../decisions/owner-app-2026-11-15.md), [web package](../../web/package.json), [CSS hiện hành](../../web/src/style.css).
- [Ngoại lệ 125%](../decisions/owner-app00b-13-02-125-scale-exception.md).
- [APP-02C.0 contract](../roadmap/app-02c0-a-online-e2e-contract.md).
- [Prototype mapping và provenance](prototype-mapping.md).
