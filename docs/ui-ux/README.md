# FMCG UI/UX Standard v0.1

**Trạng thái: PROPOSED_FOR_ADOPTION / DOCS_ONLY.** Ngày lập: 20/11/2026. Baseline repository: `6abafe25d21678579122c11d0c206818fb1cb0fd`.

Bộ quy chuẩn chuyển ngôn ngữ thiết kế của năm bộ prototype thành tài liệu có thể dùng khi đặc tả, triển khai và review UI của `fmcg-distribution-dotnet`. Hướng đã được người dùng yêu cầu là **gọn, nhiều dữ liệu hữu ích trên cùng màn hình**, bao phủ Sales A, Kho/điều phối, Hiện trường, Thủ quỹ/kế toán và Chủ NPP/vận hành.

Các con số kích thước, font chuẩn hóa và cách chia breakpoint trong v0.1 là **đề xuất của designer**, được ghi trong [sổ quyết định](decisions.md). Không gán nhãn `OWNER_APPROVED` cho lựa chọn chưa có câu trả lời. Yêu cầu lập quy chuẩn không tự phê duyệt component library, tính năng nghiệp vụ hoặc thực thi roadmap.

## Đọc và áp dụng

| Tài liệu | Dùng khi nào |
|---|---|
| [Foundations](foundations.md) | Màu, chữ, khoảng cách, mật độ, shell và semantic tokens |
| [Component contracts](components.md) | Cấu trúc và trạng thái thành phần dùng chung |
| [Interaction patterns](interaction-patterns.md) | Bàn phím, bảng, lookup, form, focus và cập nhật dữ liệu |
| [Business UX patterns](business-patterns.md) | Nguồn, phạm vi, tiền/hàng, quyền, approval, UNKNOWN và kết chuyến |
| [Accessibility và responsive](accessibility-and-responsive.md) | Contrast, keyboard, vùng chạm, màn nhỏ và phạm vi nghiệm thu |
| [Nội dung và định dạng](content-and-formatting.md) | Tiếng Việt, số, tiền, đơn vị, thời gian và microcopy |
| [Quản trị và review](governance-and-review.md) | MUST/SHOULD, ngoại lệ, phiên bản và bằng chứng PR |
| [Mapping prototype](prototype-mapping.md) | Truy nguồn 52 bản màn hình và các điểm đã chuẩn hóa |
| [Sổ quyết định](decisions.md) | Phần đã có căn cứ, đề xuất và lựa chọn cần chốt khi triển khai |
| [Kiểm tra bộ tài liệu](verification.md) | Token/CSS parity, contrast và phạm vi bằng chứng |
| [Adoption roadmap](adoption-roadmap.md) | Các increment áp dụng độc lập, chưa tự được cấp phép thực thi |
| [Screen spec template](templates/screen-spec.md) | Đặc tả một màn hình/luồng trước khi code |
| [Component spec template](templates/component-spec.md) | Đặc tả thành phần trước khi tạo hoặc mở rộng |
| [Tokens JSON](tokens/tokens.json) / [CSS reference](tokens/tokens.css) | Giá trị dùng chung; CSS chưa được import vào ứng dụng |
| [Bảng tham chiếu thị giác](assets/design-system-sheet.svg) | Review nhanh palette, typography, density và trạng thái |

![Bảng tham chiếu Design System v0.1](assets/design-system-sheet.svg)

## Thứ tự ưu tiên khi có khác biệt

1. Chỉ dẫn và quyết định Owner có hiệu lực trong đúng phạm vi; không suy quyền từ một ngoại lệ của increment khác.
2. Hợp đồng nghiệp vụ/API được phê duyệt, `business/` đã khóa và các quyết định/module contracts hiện hành. Nếu chúng mâu thuẫn, ghi blocker để giải quyết; không tự chọn cách dễ dựng UI hơn.
3. Bản UI/UX Standard đã được chấp nhận và screen/component spec của increment.
4. Prototype và fixture minh họa. Screenshot giúp hiểu bố cục; không là API schema, policy, máy trạng thái hoặc evidence runtime.

Trong bản đề xuất này, **MUST** diễn đạt điều kiện của chuẩn được đề nghị; các yêu cầu nghiệp vụ đã tồn tại vẫn bắt buộc theo nguồn của chúng. **SHOULD** là hướng mặc định có thể có ngoại lệ được ghi rõ. [Governance](governance-and-review.md) quy định cách ghi nhận và chấp nhận phiên bản.

## Nguyên tắc thiết kế

- **Đọc nhanh và truy được nguồn:** giá trị quan trọng có tên, đơn vị, phạm vi, mốc dữ liệu và đường mở chi tiết.
- **Gọn theo công việc:** giảm phần lặp và chuyển màn, giữ thông tin cần so sánh; chiều cao dòng được tăng khi nội dung cần.
- **Một ngữ cảnh, một ưu tiên hành động:** người dùng biết thao tác nào đang thực hiện và bước nào tiếp theo.
- **Trạng thái đúng nghĩa:** thiếu dữ liệu, bằng 0, đang xử lý, bị từ chối và chưa rõ kết quả có cách trình bày riêng.
- **Bàn phím và chạm có hợp đồng riêng:** desktop thao tác bảng; Field dùng bố cục một cột và vùng chạm rộng.
- **Phục hồi giữ liên kết nghiệp vụ:** tra lại yêu cầu gốc khi chưa rõ kết quả; mọi ghi nhận dựa trên kết quả có thẩm quyền.

## Phạm vi kỹ thuật hiện tại

Repository đã chốt React + TypeScript + Vite + PWA online-first và Tailwind v4. UI hiện hành trong `web/src/main.tsx` là shell health/PWA; `web/src/style.css` đang dùng Segoe UI 14 px. v0.1 đề xuất font Noto Sans từ prototype, có fallback Segoe UI; việc đưa font/tokens vào runtime thuộc UI-01 riêng.

Không có lựa chọn thư viện component, grid, icon hoặc workspace tabs được phê duyệt bởi tài liệu này. Các stack của repository khác không tự áp dụng sang project này.

## Giới hạn bằng chứng

Lần bàn giao này gồm tài liệu, token tham chiếu và hình minh họa chính xác. Việc kiểm JSON, contrast palette, liên kết và render không chứng minh ứng dụng đã có các component hay đạt usability/accessibility/E2E. Không nâng trạng thái APP/PREPROD/EV/P0, không sửa `business/` hoặc `business.lock.json`, và không mở offline business writes.

Ngoại lệ [OWN-APP00B-13-02](../decisions/owner-app00b-13-02-125-scale-exception.md) về 125% tiếp tục có hiệu lực trong phạm vi được ghi. Chuẩn này không đưa lại kiểm tra 125% thành gate bắt buộc hoặc backlog bắt buộc của APP-00B/C.
