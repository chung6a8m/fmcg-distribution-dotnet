# APP-05 — Trạng thái mô tả

**Snapshot nguồn:** `af8ecaabfd3417a258d866da7d52f01e492722fa`, 26/11/2026 (DOC-CI-02A). Đây là bản tách nguyên trạng mô tả từ [roadmap APP-05](../roadmap/app-05.md), `AGENTS.md` và README, không tái xác nhận evidence, không cấp authority hoặc mở gate. Đối chiếu PR/CI thật và roadmap trước khi quyết định increment tiếp theo. Không dùng Markdown này làm đầu vào runtime, test, build hoặc classifier.

## Current descriptive index — snapshot tại baseline

Mẫu cập nhật mỗi dòng: increment · trạng thái quan sát · phạm vi (LOCAL/PR CI/MAIN CI/physical/Owner) · link biên nhận/review · việc chưa xác minh. Không tự nâng trạng thái từ snapshot cũ; khi không có SHA/run thực tế ghi NOT_RECORDED hoặc PENDING, không suy diễn PASS. Lịch sử bên dưới không được chỉnh thành authority.

## Trạng thái từng increment tại baseline

| Increment | Trạng thái đã ghi trong roadmap |
| --- | --- |
| APP-05.0 | `MERGED / MAIN_CI_VERIFIED` (CI-only; còn investigation riêng) |
| APP-05.1 | `MERGED / FINAL_HEAD_CI_VERIFIED` |
| APP-05.2 | `MERGED / LOCAL_DEV_VERIFIED / PR_CI_INCOMPLETE` (#153) |
| APP-05.3 | `MERGED / MAIN_CI_VERIFIED` (#154) |
| APP-05.4a | `MERGED / MAIN_CI_VERIFIED` |
| APP-05.4b | `MERGED / FINAL_HEAD_CI_VERIFIED / MAIN_FEATURE_CI_VERIFIED` |
| APP-05.5 | `MERGED / FINAL_HEAD_CI_VERIFIED / MAIN_FEATURE_CI_VERIFIED` |
| APP-05.6 | `MERGED / FINAL_HEAD_CI_VERIFIED / MAIN_FEATURE_CI_VERIFIED` |
| APP-05.7 | `IMPLEMENTED / SYNTHETIC_DEV_VERIFIED_LOCAL` |

APP-05.7 giữ phạm vi nghiệm thu synthetic DEV; [biên nhận APP-05.7](../roadmap/app-05-7-acceptance.md) và ca Owner tự thao tác toàn ca chưa được thay đổi bởi DOC-CI-02A. Các scope, phụ thuộc, acceptance checklist và fixture ở [roadmap](../roadmap/app-05.md), không suy ra từ bảng này.

## Các snapshot mô tả trước khi tách

Các dòng dưới là lịch sử nguyên trạng tại baseline, có thể cũ hơn bảng trên; không dùng chúng làm current status hay điều kiện triển khai.

### Header cũ của roadmap

**Ngày chốt roadmap:** 24/11/2026. **Trạng thái:** `ROADMAP_APPROVED / APP05_0_MAIN_CI_VERIFIED / APP05_1_MERGED / APP05_2_LOCAL_DEV_VERIFIED`. Các biên nhận nằm ở mục 5. Phạm vi xác minh là synthetic DEV và CI.

### Tiến độ đã ghi trong AGENTS

Status at roadmap commit: `ROADMAP_APPROVED / IMPLEMENTATION_NOT_STARTED`; this commit is docs-only.

APP-05.0 CI reliability is `MERGED / MAIN_CI_VERIFIED` (PR #144–#149; full main run 45556640479 on `f7d13a5`, 10/10 workflows and 25/25 checks, attempt 1); see the owning roadmap closeout and retain unresolved intermittent/bootstrap investigation. APP-05.3 is `MERGED / MAIN_CI_VERIFIED` via PR #154, merge `ad5ed70`, main run 45608725142 attempt 2. APP-05.4a is `MERGED / MAIN_CI_VERIFIED` via PR #155, merge `4cf8ee8`, main run 45632314509 attempt 1. APP-05.4b is merged via PRs #157, #159 and #160 with full final-source CI and main synthetic feature evidence on `0f3f471`. See the owning closeout for the full main run and its C.6 retries. APP-05.5 source is merged via PR #161 (`cc95229`) and PR #162 (`a06f92a`) with independent review, full final-HEAD CI and main synthetic feature evidence on `a06f92a`. See the owning closeout for the full main run and C.6 retry history. APP-05.6 is merged via PR #165 (`a7883ab`) with server-readable next-day work, audited reassignment, source-bound D.4 proof and command recovery. See the owning closeout for independent review, final-HEAD CI and main run. Continue with APP-05.7 integrated shift acceptance, keeping full final-HEAD CI and independent review.

### Đoạn giới thiệu cũ của README

**Bước tiếp theo — APP-05: hoàn tất ca DEV và tiếp tục việc tồn ngày sau.** [Roadmap ngày 24/11/2026](../roadmap/app-05.md) theo dõi trạng thái từng increment (APP-05.0 `MERGED / MAIN_CI_VERIFIED` trong phạm vi CI synthetic/nonproduction; [closeout](../roadmap/app-05.md#app-050--ổn-định-ci-nền)), dựa trên [thiết kế bàn giao](../superpowers/specs/2026-11-24-dev-daily-close-design.md). Owner chọn hoàn tất ca trên dữ liệu mẫu: phần A chưa giao cần người phụ trách và hạn trước khi đóng chuyến; chọn chuyến giao tiếp vào ngày sau. APP-05.0 đã merge PR #144–#149; [full main CI #45556640479](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479) đạt 10/10 workflow, 25/25 checks attempt 1 trên merge `f7d13a5`, APP-00A 10m54s. Lỗi gián đoạn chưa rõ nguyên nhân giữ riêng; không xác nhận physical/PREPROD/EV/P0. **Tiếp theo .1 readiness/việc tồn (`NOT_STARTED`)**; mỗi increment có phụ thuộc và tiêu chí nghiệm thu riêng.

[Hồ sơ biên nhận APP-05](../receipts/app-05-receipts.md). APP-02/03, UI, LAB giữ nguồn hiện hữu; xem [DOC-CI audit](../roadmap/doc-ci-02a-consumer-audit.md).
