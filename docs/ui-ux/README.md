# FMCG UI/UX Standard v0.1

**Trạng thái: `OWNER_APPROVED_9_OF_9 / DOCS_ONLY`.** Baseline UI/UX v0.1 `ADOPTED` **sau khi PR quyết định Owner được review, xác nhận CI HEAD cuối và merge**; trước đó `ADOPTION_PENDING_DECISION_PR_MERGE`. Ngày lập 20/11/2026, reference ban đầu PR #78. [Biên bản OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md).

Bộ quy chuẩn chuyển ngôn ngữ thiết kế của năm bộ prototype thành tài liệu có thể dùng khi đặc tả, triển khai và review UI của `fmcg-distribution-dotnet`. Hướng đã được người dùng yêu cầu là **gọn, nhiều dữ liệu hữu ích trên cùng màn hình**, bao phủ Sales A, Kho/điều phối, Hiện trường, Thủ quỹ/kế toán và Chủ NPP/vận hành.

Owner đã chọn phương án 1 cho 9/9 quyết định: teal/navy, Noto Sans self-hosted, Desktop Compact, Field-first, Light/Dark/System, adaptive list/detail và chiến lược library theo ADR. [Sổ quyết định](decisions.md) tách các lựa chọn đã duyệt khỏi thiết bị Field cụ thể, library cụ thể, brand override và các hợp đồng nghiệp vụ còn cần quyết định. **UI-01 chỉ được triển khai sau khi PR hồ sơ quyết định merge, trong PR code độc lập**; UI-02…05 vẫn chưa được cấp quyền.

## Đọc và áp dụng

| Tài liệu | Dùng khi nào |
|---|---|
| [Tailwind v4 và Light/Dark](tailwind-v4-and-themes.md) | Adapter CSS-first, theme selection, portal/native controls và giới hạn kiểm chứng |
| [Foundations](foundations.md) | Màu, chữ, khoảng cách, mật độ, shell và semantic tokens |
| [Component contracts](components.md) | Cấu trúc và trạng thái thành phần dùng chung |
| [Interaction patterns](interaction-patterns.md) | Bàn phím, bảng, lookup, form, focus và cập nhật dữ liệu |
| [Business UX patterns](business-patterns.md) | Nguồn, phạm vi, tiền/hàng, quyền, approval, UNKNOWN và kết chuyến |
| [Accessibility và responsive](accessibility-and-responsive.md) | Contrast, keyboard, vùng chạm, màn nhỏ và phạm vi nghiệm thu |
| [Nội dung và định dạng](content-and-formatting.md) | Tiếng Việt, số, tiền, đơn vị, thời gian và microcopy |
| [Quản trị và review](governance-and-review.md) | MUST/SHOULD, ngoại lệ, phiên bản và bằng chứng PR |
| [Mapping prototype](prototype-mapping.md) | Truy nguồn 52 bản màn hình và các điểm đã chuẩn hóa |
| [Sổ quyết định](decisions.md) / [Owner Decisions](../decisions/owner-uiux-2026-11-20.md) | 9 quyết định đã chốt, các mục còn mở và giới hạn cấp quyền |
| [Kiểm tra bộ tài liệu](verification.md) | Token/CSS parity, contrast và phạm vi bằng chứng |
| [Adoption roadmap](adoption-roadmap.md) | Các increment áp dụng độc lập, chưa tự được cấp phép thực thi |
| [Screen spec template](templates/screen-spec.md) | Đặc tả một màn hình/luồng trước khi code |
| [Component spec template](templates/component-spec.md) | Đặc tả thành phần trước khi tạo hoặc mở rộng |
| [Tokens JSON](tokens/tokens.json) / [CSS reference](tokens/tokens.css) / [Tailwind v4 adapter](tokens/tailwind-v4.css) | Giá trị dùng chung; CSS chưa được import vào ứng dụng |
| [Bảng tham chiếu thị giác](assets/design-system-sheet.svg) | Review nhanh palette, typography, density và trạng thái |

![Bảng tham chiếu Design System v0.1](assets/design-system-sheet.svg)

## Thứ tự ưu tiên khi có khác biệt

1. Chỉ dẫn và quyết định Owner có hiệu lực trong đúng phạm vi; không suy quyền từ một ngoại lệ của increment khác.
2. Hợp đồng nghiệp vụ/API được phê duyệt, `business/` đã khóa và các quyết định/module contracts hiện hành. Nếu chúng mâu thuẫn, ghi blocker để giải quyết; không tự chọn cách dễ dựng UI hơn.
3. Bản UI/UX Standard đã được chấp nhận và screen/component spec của increment.
4. Prototype và fixture minh họa. Screenshot giúp hiểu bố cục; không là API schema, policy, máy trạng thái hoặc evidence runtime.

Trong baseline được Owner duyệt (hiệu lực sau merge PR quyết định), **MUST** là yêu cầu trong phạm vi được áp dụng và **SHOULD** là mặc định có thể có ngoại lệ được ghi rõ. Business/security contracts hiện hành vẫn có thẩm quyền cao hơn. [Governance](governance-and-review.md) quy định versioning và exception.

## Nguyên tắc thiết kế

- **Đọc nhanh và truy được nguồn:** giá trị quan trọng có tên, đơn vị, phạm vi, mốc dữ liệu và đường mở chi tiết.
- **Gọn theo công việc:** giảm phần lặp và chuyển màn, giữ thông tin cần so sánh; chiều cao dòng được tăng khi nội dung cần.
- **Một ngữ cảnh, một ưu tiên hành động:** người dùng biết thao tác nào đang thực hiện và bước nào tiếp theo.
- **Trạng thái đúng nghĩa:** thiếu dữ liệu, bằng 0, đang xử lý, bị từ chối và chưa rõ kết quả có cách trình bày riêng.
- **Bàn phím và chạm có hợp đồng riêng:** desktop thao tác bảng; Field dùng bố cục một cột và vùng chạm rộng.
- **Phục hồi giữ liên kết nghiệp vụ:** tra lại yêu cầu gốc khi chưa rõ kết quả; mọi ghi nhận dựa trên kết quả có thẩm quyền.

## Phạm vi kỹ thuật hiện tại

Repository đã chốt React + TypeScript + Vite + PWA online-first và Tailwind v4. Runtime hiện hành trong `web/src/` vẫn là health/PWA shell, dùng Segoe UI 14px, **chưa import reference tokens/font hoặc triển khai theme**. OWN-UX-03 phê duyệt Noto Sans 400/700 self-hosted; OWN-UX-02/06 phê duyệt Light và 29 Dark semantic overrides, preference Light/Dark/System. [Hợp đồng theme](tailwind-v4-and-themes.md) mô tả CSP-safe first paint, native controls, portal và PWA cho UI-01.

OWN-UX-07 chốt Sidebar + Adaptive List/Detail; OWN-UX-08 chốt **ADR theo từng increment** chứ chưa chọn thư viện component/grid/icon/docking cụ thể. OWN-UX-09 **chỉ** cho phép UI-01 foundations/demo controls sau merge hồ sơ quyết định, không cho phép grid phức tạp, API nghiệp vụ hoặc các increment UI-02…05.

## Giới hạn bằng chứng

PR #78 đã bàn giao tài liệu, tokens tham chiếu và bằng chứng render UI-00. Việc phê duyệt Design Baseline không chứng minh ứng dụng đã có component hoặc đạt usability/accessibility/runtime E2E. Không nâng trạng thái APP/PREPROD/EV/P0, không sửa `business/` hoặc `business.lock.json`, và không mở offline business writes.

Ngoại lệ [OWN-APP00B-13-02](../decisions/owner-app00b-13-02-125-scale-exception.md) về 125% tiếp tục có hiệu lực trong phạm vi được ghi. Chuẩn này không đưa lại kiểm tra 125% thành gate bắt buộc hoặc backlog bắt buộc của APP-00B/C.
