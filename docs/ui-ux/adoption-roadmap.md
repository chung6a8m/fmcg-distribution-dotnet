# Roadmap áp dụng UI/UX Standard

**Owner:** `OWNER_APPROVED_9_OF_9 / DOCS_ONLY` ngày 20/11/2026 ([OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md)). Design Baseline v0.1 chuyển sang `ADOPTED` **khi PR hồ sơ quyết định được review, CI final HEAD và merge**; trước merge: `ADOPTION_PENDING_DECISION_PR_MERGE`. Owner cho phép **UI-01 có điều kiện sau merge**, không cho phép các UI increments khác hoặc runtime business.

## UI-00 — Design Reference

[PR #78](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/78) đã merge UI-00 `DOCS_ONLY`: quy chuẩn, 101 token nền, 29 Dark semantic overrides, Tailwind v4 adapter, SVG, templates, 52 prototype mappings và reference render evidence. Bằng chứng này **không là runtime acceptance**; không có import CSS, font hoặc theme controller vào `web/src/`. Sau phỏng vấn, Owner duyệt các quyết định thiết kế tương ứng, không làm thay đổi provenance lịch sử của PR #78.

## Kế hoạch theo increment

| Increment | Quyền thực thi | Deliverables và evidence |
|---|---|---|
| **UI-01 — Runtime Foundations** | **OWNER_AUTHORIZED_CONDITIONAL_ON_DECISION_PR_MERGE** (OWN-UX-09). **PR triển khai độc lập**, chỉ bắt đầu sau khi PR hồ sơ 9 quyết định đã merge. | Noto Sans 400/700 self-hosted + fallback, license/font loading; semantic tokens và Tailwind CSS v4 adapter; Light/Dark/System preference, storage fallback, first paint/CSP, `html[data-theme]`, native controls/portal/PWA; Desktop Compact, nền tảng Field-first và demo controls có states; keyboard/focus, viewport/theme/font và regression trên final HEAD. |
| **UI-02 — Form, lookup, table/grid** | **NOT_AUTHORIZED** bằng OWN-UX-01..09 | Input, validation, lookup identity, keyboard/IME/caret, Enter/Tab/Escape, filter/selection, decimal, dữ liệu dài; ADR grid/library nếu cần. |
| **UI-03 — Source, command & recovery UX** | **NOT_AUTHORIZED** | Scope/asOf, result panel, pending/UNKNOWN, preflight/conflict, permission states theo contract/API được cấp phép. |
| **UI-04 — Vertical slice nghiệp vụ** | **NOT_AUTHORIZED** | Một luồng E2E chỉ sau khi hợp đồng nghiệp vụ, APP-02C blockers và source authorization đã chốt; không suy từ screenshot. |
| **UI-05 — Mở rộng năm nhóm vai trò** | **NOT_AUTHORIZED** | Mỗi role một spec/PR, nghiệm thu riêng Field/Finance/Owner trên browser/thiết bị phù hợp. |

Các đường dẫn `web/src/ui/`, tests và component interfaces chỉ là dự kiến; trước mỗi increment cần kế hoạch file, spec, ADR khi thực sự cần và ma trận bằng chứng.

## Baseline Owner đã chốt cho UI-01

- **Palette:** Light teal/navy và semantic tokens hiện hành, 29 Dark overrides; không có tenant brand override (OWN-UX-02/06).
- **Font:** Noto Sans 400/700 self-hosted, fallback Segoe UI/system-ui/sans-serif; không CDN (OWN-UX-03).
- **Desktop:** Compact mặc định body 14/20px, table 13/18px, control min32px, hàng đọc min36px; comfortable control40/row44 là tùy ngữ cảnh, không bắt buộc density switcher. Editor/focus, lỗi và nội dung dài được tăng chiều cao (OWN-UX-04).
- **Field:** body 16/24px, touch target mục tiêu 48×48 CSS px, một cột và từ 320 CSS px, safe area/bàn phím ảo. UI-01 chỉ dựng foundations/demo, **chưa có màn Field hoặc device acceptance** (OWN-UX-05).
- **Theme:** Light, Dark, System; explicit Light/Dark thắng OS; invalid/missing fallback Light. Root `html[data-theme]`, kiểm first paint/CSP, native control, portal và PWA (OWN-UX-06).
- **Shell:** sidebar ~200px, collapse ~64px, topbar ~52px; adaptive list/detail chuyển drawer/page khi hẹp. Không bắt buộc multi-tab/docking (OWN-UX-07).
- **Libraries:** ADR cho từng increment; không mặc định chọn full component suite, grid, icon, docking từ repo khác (OWN-UX-08).

## Gates khi triển khai UI-01

1. Xác nhận **PR hồ sơ OWN-UX-01..09 đã được review, CI final HEAD và merge**. Chỉ khi đó baseline là `ADOPTED` và UI-01 có quyền triển khai.
2. Mở **PR code UI-01 riêng**, nêu phạm vi `web/src/`, specs/demo states, library ADR nếu phát sinh, test matrix và evidence; không gộp với PR quyết định.
3. Kiểm font/license, font thực render, token JSON/CSS/Tailwind parity, contrast cặp màu trong control thật; reference compiler/ảnh UI-00 không chứng minh runtime PASS.
4. Kiểm Light-on-Dark-OS, Dark-on-Light-OS, System theo OS, preference invalid/storage blocked, CSP-safe first paint, reload, `color-scheme`, native control, portal, PWA/cache/update; keyboard/focus, long Vietnamese content, viewport 100% trong phạm vi được duyệt.
5. Independent technical review, sửa findings và chạy regression/CI trên **source HEAD cuối** trước khi merge; ghi `NOT_RUN/BLOCKED` đúng phạm vi khi không chạy được.
6. Giữ [ngoại lệ 125% APP-00B/C](../decisions/owner-app00b-13-02-125-scale-exception.md) `EXCEPTION_ACCEPTED/NOT_TESTED` đúng phạm vi synthetic/nonproduction; không đưa lại 125% thành gate bắt buộc của APP-00B/C và không tự mở rộng ngoại lệ sang UI production khác.

## Giới hạn cấp quyền

- **Không** triển khai UI-02…05, complex grid/lookup, các màn nghiệp vụ, API/business posting thật, offline business writes, GPS/scan/ảnh/chữ ký hoặc tenant brand override chỉ vì đã duyệt UI-01.
- **Không** thay `business/`, `business.lock.json`, tenant/security policies hoặc APP-02C blockers C0-R01..06; không nâng PREPROD/EV/P0, Pilot, Go-Live hoặc production.
- UI-01 foundations/demo không tương đương accessibility certification, E2E nghiệp vụ hay acceptance thiết bị Field thực.
