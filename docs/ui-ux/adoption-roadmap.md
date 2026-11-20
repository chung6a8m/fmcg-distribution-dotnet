# Roadmap áp dụng UI/UX Standard

Design Baseline v0.1 là **`ADOPTED`** qua [PR #79](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/79). Owner đã duyệt 9/9 ngày 20/11/2026 và cấp quyền UI-01 theo [OWN-UX-09](../decisions/owner-uiux-2026-11-20.md).

## UI-00 — Design Reference

[PR #78](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/78) đã merge UI-00 `DOCS_ONLY`: quy chuẩn, 101 token nền, 29 Dark semantic overrides, Tailwind v4 adapter, SVG, templates, 52 prototype mappings và reference render evidence. Bằng chứng này **không là runtime acceptance**; không có import CSS, font hoặc theme controller vào `web/src/`. Sau phỏng vấn, Owner duyệt các quyết định thiết kế tương ứng, không làm thay đổi provenance lịch sử của PR #78.

## Kế hoạch theo increment

| Increment | Quyền thực thi | Deliverables và evidence |
|---|---|---|
| **UI-01 — Runtime Foundations** | **MERGED / CI_VERIFIED** (synthetic/nonproduction, [PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80)), theo OWN-UX-09. | Shell và demo dùng tokens, Noto 400/700, theme chung, Compact/Comfortable/Field. [Đặc tả](ui-01-runtime-foundations.md) và [ma trận kiểm chứng](ui-01-verification.md). |
| **UI-02 — Form, lookup, table/grid** | **MERGED / UI02_100_PERCENT_CI_VERIFIED** (synthetic/nonproduction, zoom 100%; [PR #82](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/82), [Owner authorization](../decisions/owner-ui02-2026-11-20.md)). | Lab `/ui/data-entry`: input/validation, identity lookup, keyboard/IME event handling, caret, editable grid, staged filter/sort, decimal, density. [Spec/ADR](ui-02-data-entry.md) · [verification](ui-02-verification.md) · [post-merge closeout](../closeout/2026-11-20-ui-02.md). Physical IME/device/125% vẫn NOT_TESTED. |
| **UI-03 — Source, command & recovery UX** | **MERGED / UI03_100_PERCENT_CI_VERIFIED** (synthetic/nonproduction only, zoom 100%; [Owner authorization](../decisions/owner-ui03-2026-11-20.md), [PR #84](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84)) | `/ui/command-recovery`: source asOf/n/N, ACK distinct from POSTED, PENDING/UNKNOWN same-ID lookup, permission/offline/preflight/conflict fixtures; 10/10 workflows, 20/20 jobs, 51/51 browser tests on final source. [Spec](ui-03-source-command-recovery.md) · [verification](ui-03-verification.md) · [closeout](../closeout/2026-11-20-ui-03.md). No real API/posting. |
| **UI-04 — Vertical slice nghiệp vụ** | **NOT_AUTHORIZED** | Một luồng E2E chỉ sau khi hợp đồng nghiệp vụ, APP-02C blockers và source authorization đã chốt; không suy từ screenshot. |
| **UI-05 — Mở rộng năm nhóm vai trò** | **NOT_AUTHORIZED** | Mỗi role một spec/PR, nghiệm thu riêng Field/Finance/Owner trên browser/thiết bị phù hợp. |

UI-01 đặt primitives/theme adapter trong `web/src/ui/` và evidence browser trong `web/e2e/`. Source HEAD `2575651ae18e2e765b8072ef0b82409afce3319e` có 10/10 workflows và 20/20 checks thành công, merge `a0081b081221de725279e1d81b7d3dd8f4f93488`; [post-merge closeout](../closeout/2026-11-20-ui-01.md) giữ rõ phạm vi CI và các `NOT_VERIFIED`.

UI-02 có reviewed final source HEAD `8d3d38309d4f77b7936578839a8a84ce4f2bbd3e`, 10/10 workflows và 20/20 checks SUCCESS; Playwright 38/38, SQL/.NET 296/296, 4/4 technical findings đã sửa. Merge commit `51388a040ca500743ad8d95591a3e8c56b8d433e` có source tree trùng bản đã review; [closeout và evidence](../closeout/2026-11-20-ui-02.md). Đây chỉ là CI synthetic ở 100% browser zoom; 125%, IME/thiết bị thực và production business E2E chưa nghiệm thu.

UI-03 có final reviewed source HEAD `dcd2870b5e6b4f4f90ee579ec9ec30f4267ac2c9`, merge `f17397e4de0912c0d6551eb9bba0ea1f0a557b62` ngày 20/11/2026. [PR #84](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84) có 10/10 workflows, 20/20 jobs SUCCESS trên đúng source HEAD; Playwright 51/51 PASS; GitHub compare giữa source và merge có 0 tệp khác biệt. [Closeout](../closeout/2026-11-20-ui-03.md) phân biệt rõ UNKNOWN/ACK, quyền đọc và giới hạn sessionStorage. Đây chỉ là synthetic browser CI ở 100% zoom: 125%, Field/assistive physical, PREPROD, server command và EV/P0 chưa nghiệm thu. Mỗi increment tiếp theo vẫn cần scope và quyết định riêng.

## Baseline Owner đã chốt cho UI-01

- **Palette:** Light teal/navy và semantic tokens hiện hành, 29 Dark overrides; không có tenant brand override (OWN-UX-02/06).
- **Font:** Noto Sans 400/700 self-hosted, fallback Segoe UI/system-ui/sans-serif; không CDN (OWN-UX-03).
- **Desktop:** Compact mặc định body 14/20px, table 13/18px, control min32px, hàng đọc min36px; comfortable control40/row44 là tùy ngữ cảnh, không bắt buộc density switcher. Editor/focus, lỗi và nội dung dài được tăng chiều cao (OWN-UX-04).
- **Field:** body 16/24px, touch target mục tiêu 48×48 CSS px, một cột và từ 320 CSS px, safe area/bàn phím ảo. UI-01 chỉ dựng foundations/demo, **chưa có màn Field hoặc device acceptance** (OWN-UX-05).
- **Theme:** Light, Dark, System; explicit Light/Dark thắng OS; invalid/missing fallback Light. Root `html[data-theme]`, kiểm first paint/CSP, native control, portal và PWA (OWN-UX-06).
- **Shell:** sidebar ~200px, collapse ~64px, topbar ~52px; adaptive list/detail chuyển drawer/page khi hẹp. Không bắt buộc multi-tab/docking (OWN-UX-07).
- **Libraries:** ADR cho từng increment; không mặc định chọn full component suite, grid, icon, docking từ repo khác (OWN-UX-08).

## Gates khi triển khai UI-01

1. PR #79 đã merge hồ sơ OWN-UX-01..09. Baseline là `ADOPTED`.
2. PR #80 triển khai UI-01 riêng theo đặc tả foundations/demo.
3. Kiểm font/license, font thực render, token JSON/CSS/Tailwind parity, contrast cặp màu trong control thật; reference compiler/ảnh UI-00 không chứng minh runtime PASS.
4. Kiểm Light-on-Dark-OS, Dark-on-Light-OS, System theo OS, preference invalid/storage blocked, CSP-safe first paint, reload, `color-scheme`, native control, portal, PWA/cache/update; keyboard/focus, long Vietnamese content, viewport 100% trong phạm vi được duyệt.
5. Independent technical review, sửa findings và chạy regression/CI trên **source HEAD cuối** trước khi merge; ghi `NOT_RUN/BLOCKED` đúng phạm vi khi không chạy được.
6. Giữ [ngoại lệ 125% APP-00B/C](../decisions/owner-app00b-13-02-125-scale-exception.md) `EXCEPTION_ACCEPTED/NOT_TESTED` đúng phạm vi synthetic/nonproduction; không đưa lại 125% thành gate bắt buộc của APP-00B/C và không tự mở rộng ngoại lệ sang UI production khác.

## Giới hạn cấp quyền

- **UI-01 không tự cấp phép** UI-02…05. Lệnh trực tiếp của Owner ngày 20/11/2026 đã mở riêng UI-02 synthetic Lab và PR #82 đã merge trong phạm vi này. Lệnh Owner tiếp theo mở UI-03 **chỉ cho synthetic Lab** và PR #84 đã merge trong phạm vi đó; không tự mở API/business writes. UI-04…05, màn nghiệp vụ, API/business posting thật, offline business writes, GPS/scan/ảnh/chữ ký và tenant brand override vẫn chưa được ủy quyền.
- **Không** thay `business/`, `business.lock.json`, tenant/security policies hoặc APP-02C blockers C0-R01..06; không nâng PREPROD/EV/P0, Pilot, Go-Live hoặc production.
- UI-01 foundations/demo không tương đương accessibility certification, E2E nghiệp vụ hay acceptance thiết bị Field thực.
