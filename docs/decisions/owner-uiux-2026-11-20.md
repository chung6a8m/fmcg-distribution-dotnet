# OWN-UX-01..09 — Owner Decisions: FMCG UI/UX Standard v0.1

**Ngày quyết định:** 20/11/2026. **Nguồn:** cuộc phỏng vấn trực tiếp Owner sau khi [PR #78](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/78) đã merge. Owner chọn **phương án 1 cho cả chín câu hỏi**.

**Quyết định:** `OWNER_APPROVED_9_OF_9`. Design Baseline v0.1 chính thức `ADOPTED` **sau khi PR hồ sơ quyết định được independent review, xác nhận CI trên HEAD cuối và merge vào main**. Trước merge: `ADOPTION_PENDING_DECISION_PR_MERGE`. Đây là phê duyệt thiết kế, **không** là xác nhận triển khai/nghiệm thu runtime.

**Hồ sơ quyết định:** [PR #79](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/79). Khi PR này đã merge sau các gate trên, baseline trong `main` có hiệu lực `ADOPTED` và điều kiện cấp quyền UI-01 tại OWN-UX-09 được đáp ứng. Các nhãn proposed trong evidence PR #78 và `provenance` token mô tả lịch sử tạo reference, không phải trạng thái phê duyệt hiện tại.

## Quyết định đã chốt

| ID | Lựa chọn | Hợp đồng được phê duyệt |
|---|---|---|
| **OWN-UX-01** | 1 — Design Baseline chính thức | Phê duyệt UI/UX Standard v0.1 và semantic tokens/templates làm chuẩn sau closeout PR quyết định; mỗi increment runtime có scope riêng. |
| **OWN-UX-02** | 1 — Giữ teal/navy và semantic tokens | Phê duyệt palette Light/HEX hiện hành của PR #78, semantic tokens theo vai trò và nguyên tắc contrast; Dark tại OWN-UX-06. |
| **OWN-UX-03** | 1 — Noto Sans self-hosted | Noto Sans 400/700, fallback Segoe UI → system-ui → sans-serif, không CDN; UI-01 kiểm license, font loading, tiếng Việt, số liệu và layout/performance. |
| **OWN-UX-04** | 1 — Desktop Compact mặc định | Body 14/20px, table 13/18px, control min 32px, hàng đọc min 36px; comfortable 40/44px khi cần. Không bắt buộc density switcher UI-01; editor/focus, lỗi/nội dung dài được cao hơn. |
| **OWN-UX-05** | 1 — Field-first touch | Body 16/24px, touch target mục tiêu 48×48 CSS px, một cột/card/nhãn–giá trị, hỗ trợ từ 320 CSS px, safe area và bàn phím ảo; thiết bị thực cần acceptance riêng. |
| **OWN-UX-06** | 1 — Light/Dark/System | Phê duyệt palette Light và đủ 29 Dark semantic overrides v0.1; explicit Light/Dark ưu tiên OS, System theo `prefers-color-scheme`, invalid/missing fallback Light. Runtime root `html[data-theme]`, CSP-safe first paint, native/portal/PWA. Không brand override. |
| **OWN-UX-07** | 1 — Sidebar + Adaptive List/Detail | Sidebar khoảng 200px (thu gọn khoảng 64px), topbar khoảng 52px; detail chuyển drawer/page khi hẹp. Multi-tab/docking không bắt buộc v0.1. |
| **OWN-UX-08** | 1 — ADR theo từng increment | Không khóa component suite, grid, icon hay docking library trước UI-01; đánh giá license, security, accessibility, keyboard, hiệu năng và bảo trì khi cần. Không mặc định dùng stack repo khác. |
| **OWN-UX-09** | 1 — Ủy quyền UI-01 trong PR riêng | **Sau khi PR quyết định merge**, cho phép UI-01 Runtime Foundations và demo controls: Noto Sans, Tailwind v4 adapter/tokens, Light/Dark/System + preference/first paint/CSP/native/portal/PWA, desktop density, nền tảng Field-first; independent review, sửa findings và CI final HEAD. |

## Ranh giới và điều kiện hiệu lực

- **Phê duyệt** (`OWNER_APPROVED_9_OF_9`) đã được Owner đưa ra trong cuộc phỏng vấn 20/11/2026. **Chấp nhận baseline** (`ADOPTED`) chỉ có hiệu lực khi PR tài liệu quyết định này được review/CI/merge; PR #78 trước đó chỉ bàn giao UI-00 reference `DOCS_ONLY`.
- **UI-01** được ủy quyền có điều kiện, phải triển khai trong **PR code riêng sau merge hồ sơ quyết định**; chỉ runtime foundations, demo control states và tests. Không được tự mở UI-02…05, grid/lookup nghiệp vụ, màn nghiệp vụ hoặc business/API writes.
- **Evidence** UI-00 (tokens/Tailwind compiler/ảnh Chromium/focus/SVG) không chứng minh UI-01 runtime đã hoàn tất; cần font/contrast/component/keyboard/theme/first paint/PWA và regressions thật trên HEAD cuối.
- **Chưa chốt:** tên component/grid/icon library cụ thể, docking, tenant brand override, Android/iOS hoặc thiết bị quét tại Field, GPS/ảnh/chữ ký, offline drafts/writes và acceptance ngoài scope.
- **Business/security:** không thay `business/`, `business.lock.json`, policy, tenant identity hoặc hợp đồng nguồn. Không tự mở APP-02C/D/E, PREPROD thực tế, EV/P0, production, Pilot hoặc Go-Live.
- **Ngoại lệ 125%** [OWN-APP00B-13-02](owner-app00b-13-02-125-scale-exception.md) giữ đúng APP-00B/C synthetic/nonproduction (`EXCEPTION_ACCEPTED / NOT_TESTED`), không mở lại gate và không tự mở rộng ngoại lệ sang sản phẩm khác.

## Tài liệu liên quan

- [UI/UX Standard](../ui-ux/README.md)
- [Sổ quyết định UI/UX](../ui-ux/decisions.md)
- [Adoption roadmap và gate UI-01](../ui-ux/adoption-roadmap.md)
- [Theme contract](../ui-ux/tailwind-v4-and-themes.md)
- [Reference verification](../ui-ux/verification.md)
- [PR #78](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/78) — UI-00 đã merge
