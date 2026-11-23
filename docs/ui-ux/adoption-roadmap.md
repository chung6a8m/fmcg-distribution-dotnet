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
| **UI-04 — Vertical slice nghiệp vụ** | **Sales A DEV C.1–C.5 MERGED / DEV_INTEGRATION_VERIFIED; C.6 DEV acceptance đã ký** theo [quyết định 22/11/2026](../decisions/owner-app02c-dev-first-2026-11-22.md) và [closeout C.6](../closeout/2026-11-22-app-02c6-localdb.md). [PR #86](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/86) vẫn OPEN / DOCS_ONLY readiness candidate, cần rebase/đối chiếu; không là prerequisite của C.1 hoặc implementation đã merge. | Mỗi C increment có UI tối thiểu chạy thật UI→API→SQL trong DEV riêng với dữ liệu synthetic và guard LocalDB hiện hành; C.5 tích hợp workspace/report. Đóng contract/expectations liên quan trước behavior phụ thuộc theo [DEV handoff](../roadmap/app-02c-dev-handoff.md); physical acceptance giữ [gate riêng](../roadmap/app-02c-operations-debt.md). |
| **UI-05 — Mở rộng năm nhóm vai trò** | **ROADMAP_APPROVED / IMPLEMENTATION_NOT_STARTED** trong [APP-03](../roadmap/app-03.md), Owner đồng ý ngày 23/11/2026 | .3 chủ trì phiên/shell; .4 Sales/Kho; .5 Hiện trường; .6 bàn giao tiền/trả/hoàn ứng; .7 Finance; .8 Owner; .9 acceptance chung. Dùng lại màn C/D/F, một increment chủ trì mỗi màn, giữ actor/phân nhiệm độc lập. Không tạo thêm năm PR xây lại cùng workspace. |

UI-01 đặt primitives/theme adapter trong `web/src/ui/` và evidence browser trong `web/e2e/`. Source HEAD `2575651ae18e2e765b8072ef0b82409afce3319e` có 10/10 workflows và 20/20 checks thành công, merge `a0081b081221de725279e1d81b7d3dd8f4f93488`; [post-merge closeout](../closeout/2026-11-20-ui-01.md) giữ rõ phạm vi CI và các `NOT_VERIFIED`.

UI-02 có reviewed final source HEAD `8d3d38309d4f77b7936578839a8a84ce4f2bbd3e`, 10/10 workflows và 20/20 checks SUCCESS; Playwright 38/38, SQL/.NET 296/296, 4/4 technical findings đã sửa. Merge commit `51388a040ca500743ad8d95591a3e8c56b8d433e` có source tree trùng bản đã review; [closeout và evidence](../closeout/2026-11-20-ui-02.md). Đây chỉ là CI synthetic ở 100% browser zoom; 125%, IME/thiết bị thực và production business E2E chưa nghiệm thu.

UI-03 có final reviewed source HEAD `dcd2870b5e6b4f4f90ee579ec9ec30f4267ac2c9`, merge `f17397e4de0912c0d6551eb9bba0ea1f0a557b62` ngày 20/11/2026. [PR #84](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84) có 10/10 workflows, 20/20 jobs SUCCESS trên đúng source HEAD; Playwright 51/51 PASS; GitHub compare giữa source và merge có 0 tệp khác biệt. [Closeout](../closeout/2026-11-20-ui-03.md) phân biệt rõ UNKNOWN/ACK, quyền đọc và giới hạn sessionStorage. Đây chỉ là synthetic browser CI ở 100% zoom: 125%, Field/assistive physical, PREPROD, server command và EV/P0 chưa nghiệm thu. Mỗi increment tiếp theo vẫn phải đúng scope; C.1–C.5 dùng standing DEV authorization ngày 22/11/2026. UI-05 tích hợp tiếp theo dùng [roadmap APP-03 được Owner đồng ý ngày 23/11/2026](../roadmap/app-03.md); không lặp lại blanket authorization trong cùng phạm vi. Khả năng ngoài các phạm vi đó giữ quyết định riêng.

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

- **UI-01 không tự cấp phép** UI-02…05. Lệnh trực tiếp của Owner ngày 20/11/2026 đã mở riêng UI-02 synthetic Lab và PR #82 đã merge trong phạm vi này. Lệnh Owner tiếp theo mở UI-03 **chỉ cho synthetic Lab** và PR #84 đã merge trong phạm vi đó; không tự mở API/business writes. Quyết định riêng ngày 22/11/2026 hiện cho phép UI Sales A và API/SQL synthetic DEV của C.1–C.5. Ngày 23/11/2026 Owner đồng ý UI-05 trong phạm vi [APP-03 synthetic DEV](../roadmap/app-03.md), chưa triển khai/nghiệm thu. Real-world business posting, offline business writes, GPS/scan/ảnh/chữ ký và tenant brand override vẫn ngoài phạm vi này.
- **Không** thay `business/` hoặc `business.lock.json`; giữ tenant/security invariants. C0-R01..04 được giải quyết trong increment sở hữu trước behavior phụ thuộc; R06 expectations theo feature, R05 và R06 physical thuộc operations/acceptance. Đây là phân kỳ theo quyết định Owner, không phải PASS hoặc nới PREPROD/EV/P0, Pilot, Go-Live hay production.
- UI-01 foundations/demo không tương đương accessibility certification, E2E nghiệp vụ hay acceptance thiết bị Field thực.
