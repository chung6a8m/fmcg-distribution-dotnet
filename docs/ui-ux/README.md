# FMCG UI/UX Standard v0.1

**Design Baseline v0.1 `ADOPTED`** qua [PR #79](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/79), merge commit `71ff1c28890dd45724b71d0140a23ac2810417a1`. Owner duyệt 9/9 ngày 20/11/2026. [Biên bản OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md).

Bộ quy chuẩn chuyển ngôn ngữ thiết kế của năm bộ prototype thành tài liệu có thể dùng khi đặc tả, triển khai và review UI của `fmcg-distribution-dotnet`. Hướng đã được người dùng yêu cầu là **gọn, nhiều dữ liệu hữu ích trên cùng màn hình**, bao phủ Sales A, Kho/điều phối, Hiện trường, Thủ quỹ/kế toán và Chủ NPP/vận hành.

Owner chọn teal/navy, Noto Sans self-hosted, Desktop Compact, Field-first, Light/Dark/System, adaptive list/detail và chiến lược library theo ADR. UI-01 foundations/demo đã `MERGED / CI_VERIFIED` (synthetic/nonproduction) qua [PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80); [closeout UI-01](../closeout/2026-11-20-ui-01.md). UI-02 form/identity lookup/editable grid tại `/ui/data-entry` đã `MERGED / UI02_100_PERCENT_CI_VERIFIED` (synthetic/nonproduction, zoom 100%) qua [PR #82](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/82); [closeout UI-02](../closeout/2026-11-20-ui-02.md). UI-03 được Owner yêu cầu triển khai riêng ngày 20/11/2026, chỉ trong **Synthetic Source/Command Recovery Lab** tại `/ui/command-recovery`; xem [ủy quyền UI-03](../decisions/owner-ui03-2026-11-20.md), [spec/ADR](ui-03-source-command-recovery.md) và [verification](ui-03-verification.md). Chờ independent review/CI của PR triển khai trên HEAD cuối; UI-04/05 vẫn `NOT_AUTHORIZED`. [Sổ quyết định](decisions.md).

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
| [Kiểm tra reference UI-00](verification.md) / [Kiểm chứng runtime UI-01](ui-01-verification.md) / [UI-02 component ADR](ui-02-data-entry.md) / [UI-02 verification](ui-02-verification.md) | Compiler/token parity và evidence runtime theo source HEAD của PR |
| [Adoption roadmap](adoption-roadmap.md) | Các increment áp dụng độc lập, chưa tự được cấp phép thực thi |
| [Screen spec template](templates/screen-spec.md) | Đặc tả một màn hình/luồng trước khi code |
| [Component spec template](templates/component-spec.md) | Đặc tả thành phần trước khi tạo hoặc mở rộng |
| [Tokens JSON](tokens/tokens.json) / [CSS](tokens/tokens.css) / [Tailwind v4 adapter](tokens/tailwind-v4.css) | Token và adapter dùng trực tiếp trong CSS của UI-01 |
| [Bảng tham chiếu thị giác](assets/design-system-sheet.svg) | Review nhanh palette, typography, density và trạng thái |

![Bảng tham chiếu Design System v0.1](assets/design-system-sheet.svg)

## Thứ tự ưu tiên khi có khác biệt

1. Chỉ dẫn và quyết định Owner có hiệu lực trong đúng phạm vi; không suy quyền từ một ngoại lệ của increment khác.
2. Hợp đồng nghiệp vụ/API được phê duyệt, `business/` đã khóa và các quyết định/module contracts hiện hành. Nếu chúng mâu thuẫn, ghi blocker để giải quyết; không tự chọn cách dễ dựng UI hơn.
3. Bản UI/UX Standard đã được chấp nhận và screen/component spec của increment.
4. Prototype và fixture minh họa. Screenshot giúp hiểu bố cục; không là API schema, policy, máy trạng thái hoặc evidence runtime.

Trong baseline đã được áp dụng, **MUST** là yêu cầu trong phạm vi liên quan và **SHOULD** là mặc định có thể có ngoại lệ được ghi rõ. Business/security contracts hiện hành vẫn có thẩm quyền cao hơn. [Governance](governance-and-review.md) quy định versioning và exception.

## Nguyên tắc thiết kế

- **Đọc nhanh và truy được nguồn:** giá trị quan trọng có tên, đơn vị, phạm vi, mốc dữ liệu và đường mở chi tiết.
- **Gọn theo công việc:** giảm phần lặp và chuyển màn, giữ thông tin cần so sánh; chiều cao dòng được tăng khi nội dung cần.
- **Một ngữ cảnh, một ưu tiên hành động:** người dùng biết thao tác nào đang thực hiện và bước nào tiếp theo.
- **Trạng thái đúng nghĩa:** thiếu dữ liệu, bằng 0, đang xử lý, bị từ chối và chưa rõ kết quả có cách trình bày riêng.
- **Bàn phím và chạm có hợp đồng riêng:** desktop thao tác bảng; Field dùng bố cục một cột và vùng chạm rộng.
- **Phục hồi giữ liên kết nghiệp vụ:** tra lại yêu cầu gốc khi chưa rõ kết quả; mọi ghi nhận dựa trên kết quả có thẩm quyền.

## Phạm vi kỹ thuật hiện tại

Health/PWA shell tại `/`, UI-01 `/ui/foundations` và synthetic UI-02 `/ui/data-entry` dùng chung [CSS entry](../../web/src/style.css), Noto Sans WOFF2 self-hosted 400/700, token Light và 29 Dark overrides. Bootstrap [theme-runtime.js](../../web/public/theme-runtime.js) resolve trước React; selector trong shell, demo và native dialog dùng cùng controller. [Đặc tả UI-01](ui-01-runtime-foundations.md) · [Kiểm chứng](ui-01-verification.md).

OWN-UX-07 chốt Sidebar + Adaptive List/Detail; OWN-UX-08 chốt **ADR theo từng increment** chứ chưa chọn thư viện component/grid/icon/docking cụ thể. OWN-UX-09 **chỉ** cho phép UI-01 foundations/demo controls. [Lệnh trực tiếp của Owner ngày 20/11/2026](../decisions/owner-ui02-2026-11-20.md) mở riêng UI-02 synthetic Lab với native ADR, không cho phép API nghiệp vụ, production grid hoặc UI-03…05; UI-02 đã được independent technical review và CI trên exact final source HEAD; [bằng chứng và bốn findings đã xử lý](../closeout/2026-11-20-ui-02.md). Điều đó không mở rộng ủy quyền sang UI-03..05 hoặc sản phẩm thực.

## Giới hạn bằng chứng

PR #78 đã bàn giao tài liệu, tokens tham chiếu và bằng chứng render UI-00. Việc phê duyệt Design Baseline đơn thuần không chứng minh runtime acceptance; UI-01 và UI-02 sau đó đều có kiểm chứng CI trên source HEAD riêng, giới hạn lần lượt cho foundations/demo và form/lookup/grid synthetic; không xác nhận usability/accessibility toàn sản phẩm hay E2E nghiệp vụ. Không nâng trạng thái APP/PREPROD/EV/P0, không sửa `business/` hoặc `business.lock.json`, và không mở offline business writes.

Ngoại lệ [OWN-APP00B-13-02](../decisions/owner-app00b-13-02-125-scale-exception.md) về 125% tiếp tục có hiệu lực trong phạm vi được ghi. Chuẩn này không đưa lại kiểm tra 125% thành gate bắt buộc hoặc backlog bắt buộc của APP-00B/C.
