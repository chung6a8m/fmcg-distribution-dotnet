# Sổ quyết định UI/UX

**Cập nhật hiện hành 23/11/2026:** Owner đồng ý [roadmap APP-03](../roadmap/app-03.md), gồm ánh xạ UI-05 vào .3–.8 và nghiệm thu tích hợp .9. Trạng thái `ROADMAP_APPROVED / IMPLEMENTATION_NOT_STARTED`; commit ghi nhận chỉ thay tài liệu. C/D/F đã có implementation và bằng chứng DEV theo [APP-02](../roadmap/app-02.md). Các đoạn ngày 20–22/11 bên dưới giữ provenance lịch sử, không phải trạng thái implementation hiện tại.

> Hồ sơ giữ quyết định và provenance lịch sử. PR #79 đã merge, baseline là `ADOPTED`; UI-01 đã `MERGED / CI_VERIFIED` trong phạm vi synthetic/nonproduction theo [closeout](../closeout/2026-11-20-ui-01.md) và [bằng chứng](ui-01-verification.md).

**Ngày lập 20/11/2026; `OWNER_APPROVED_9_OF_9`.** Baseline v0.1 chính thức `ADOPTED` **sau khi PR ghi nhận [OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md) được review, CI trên HEAD cuối và merge**; trước merge: `ADOPTION_PENDING_DECISION_PR_MERGE`. Phân biệt design approval, implementation và runtime evidence.

| ID | Nội dung | Trạng thái / căn cứ |
|---|---|---|
| UX-D01 | Kế thừa năm vai trò và ngôn ngữ thị giác của prototype | USER_REQUESTED trong cuộc trao đổi thiết kế |
| UX-D02 | Ưu tiên giao diện gọn, nhiều dữ liệu hữu ích cùng màn | USER_SELECTED trước khi dựng các bộ sau |
| UX-D03 | React + TypeScript + Vite + PWA online-first; Tailwind v4 | EXISTING_REPO_DECISION; xem AGENTS và Owner APP/APP-00B |
| UX-D04 | Quy chuẩn, token tham chiếu, templates/review; triển khai component theo increment | OWNER_APPROVED: OWN-UX-01/09; PR #80 UI-01 đã merge/CI-verified trong phạm vi đã ủy quyền |
| UX-D05 | Palette teal/navy Light hiện hành, semantic tokens và viền control nhận biết được | OWNER_APPROVED: OWN-UX-02 |
| UX-D06 | Noto Sans 400/700 self-hosted, fallback Segoe UI/system-ui/sans-serif | OWNER_APPROVED: OWN-UX-03; Noto Sans runtime UI-01 đã source-bound CI-verified |
| UX-D07 | Desktop Compact body14/20, table13/18, row đọc min36/control min32; comfortable row44/control40 | OWNER_APPROVED: OWN-UX-04; editor/focus/nội dung dài được tăng cao |
| UX-D08 | Field-first body16/24, target48×48, một cột, từ 320 CSS px | OWNER_APPROVED: OWN-UX-05; thiết bị thực chốt sau |
| UX-D09 | Light và 29 Dark semantic overrides; preference Light/Dark/System, missing/invalid → Light | OWNER_APPROVED: OWN-UX-02/06; theme UI-01 CI-verified; physical devices chưa nghiệm thu |
| UX-D10 | Sidebar ~200px/collapse~64px, topbar~52px, adaptive list/detail; thư viện theo ADR | OWNER_APPROVED: OWN-UX-07/08; chưa chọn library cụ thể |
| UX-D11 | Giữ ngoại lệ 125% APP-00B/C đúng phạm vi | EXISTING_OWNER_DECISION, OWN-APP00B-13-02 |
| UX-D12 | Không lấy prototype làm máy trạng thái/permission/policy đã duyệt | EXISTING_CONTRACT + ranh giới artifact; đặc biệt C0-R01…06 |

### Phạm vi bổ sung khi review PR #78

PR #78 bổ sung Tailwind v4 adapter, Dark semantic overrides, generator/checker và theme checklist tại phạm vi **UI-00 DOCS_ONLY**. Tại thời điểm đó các lựa chọn chưa có Owner approval. **Sau phỏng vấn ngày 20/11/2026, Owner đã chốt 9/9 phương án 1** trong [biên bản](../decisions/owner-uiux-2026-11-20.md), bao gồm bảng màu Light/Dark và UI-01 conditional authorization. Kết quả CI/render của UI-00 không tự trở thành runtime acceptance.

## Các cách áp dụng đã cân nhắc

1. **Quy chuẩn và tokens tham chiếu:** phù hợp ngay khi UI nghiệp vụ chưa được triển khai, giữ nguồn dùng chung cho Codex; cần increment component sau. Đây là phương án designer dùng để chuẩn bị v0.1.
2. **Quy chuẩn cùng component library đầy đủ:** có giá trị khi bắt đầu xây UI hàng loạt; đòi hỏi lựa chọn thư viện, screen contracts, runtime testing và authorization riêng. Chưa nằm trong thay đổi tài liệu này.

**Lịch sử tại PR #78:** hai câu hỏi tùy chọn lúc đó chưa có câu trả lời, nên reference mang provenance `DESIGNER_PROPOSED`. **Cập nhật:** Owner đã lựa chọn chính thức phương án 1 cho phạm vi (OWN-UX-01), mật độ compact (OWN-UX-04) và toàn bộ 9 quyết định. Không sửa ngược lịch sử provenance của artifact.

## Cần quyết định khi có phạm vi triển khai tương ứng

| Chủ đề | Người quyết định / bước cần có | Mặc định hiện tại |
|---|---|---|
| Thiết bị thực tế, Android/iOS, kho có cảm ứng/scanner | Owner khi chốt acceptance của UI tương ứng | Chưa suy thiết bị từ kích thước ảnh |
| Mức kiểm zoom/phóng chữ ngoài phạm vi đã có | Owner ghi rõ phạm vi, giữ lịch sử ngoại lệ | Không mở lại 125% APP-00B/C |
| Component/grid/icon/docking library | Đã chốt ADR theo từng increment (OWN-UX-08) | Tên thư viện cụ thể vẫn chưa chọn; không kế thừa repo khác |
| Runtime theme selection / brand override | Light/Dark/System đã được duyệt (OWN-UX-06); UI-01 được ủy quyền có điều kiện (OWN-UX-09) | Controller/switcher Light/Dark/System đã có trong UI-01 synthetic; chưa có tenant brand override hay nghiệm thu thiết bị thực |
| Confirm/Reserve, ShipTo, policy validity, partial/repeated load | [C DEV handoff](../roadmap/app-02c-dev-handoff.md): R02→C.1; R01→C.2; R03→C.2/C.4/C.5; R04→C.2 counters/C.3/C.4 | Chốt trong increment sở hữu trước behavior phụ thuộc; không là blanket gate trước C.1. R06 expectations theo feature; R05 và R06 physical thuộc operations/acceptance |
| Mốc stale, SLA, tuổi nợ, KPI, giá/thuế, hạn mức | Business/policy contract được phê duyệt | Chưa thiết lập; không lấy số demo |
| Offline drafts, GPS/ảnh/chữ ký bắt buộc, scan | Product/security contract riêng | Không hứa các năng lực chưa có |

## Nguồn

- [OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md), [AGENTS.md](../../AGENTS.md), [Owner APP 15/11](../decisions/owner-app-2026-11-15.md), [web package](../../web/package.json), [CSS hiện hành](../../web/src/style.css).
- [Ngoại lệ 125%](../decisions/owner-app00b-13-02-125-scale-exception.md).
- [APP-02C.0 contract](../roadmap/app-02c0-a-online-e2e-contract.md).
- [Prototype mapping và provenance](prototype-mapping.md).

## Ủy quyền UI Sales A trong APP-02C DEV (22/11/2026)

[Quyết định DEV-first của Owner](../decisions/owner-app02c-dev-first-2026-11-22.md) cấp quyền liên tục cho C.1–C.5 source/migrations/UI và chạy thật UI→API→SQL với dữ liệu synthetic trong DEV riêng, ban đầu dùng nonce-owned LocalDB và các guard hiện hành. Mỗi increment có UI tối thiểu của feature; C.5 tích hợp workspace/report. Không cần blanket PREPROD foundation hoặc một vòng cấp quyền lại cho cùng scope. [C DEV handoff](../roadmap/app-02c-dev-handoff.md) giữ các điều kiện nghiệp vụ/kỹ thuật theo feature; [operations debt](../roadmap/app-02c-operations-debt.md) giữ physical/PREPROD/C.6 và Pilot riêng. Tại lúc ghi amendment ngày 22/11, thay đổi là DOCS_ONLY và C implementation `NOT_STARTED`; sau đó C.1–C.5 đã merge, C.6 có [DEV closeout đã ký](../closeout/2026-11-22-app-02c6-localdb.md).

[PR #86](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/86) UI-04 là OPEN / DOCS_ONLY readiness candidate cần rebase và đối chiếu quyết định mới, không là dependency trước C.1 hoặc bằng chứng UI đã triển khai/merge. UI-05 tại mốc amendment 22/11 chưa được ủy quyền; phạm vi tích hợp nay đã được Owner đồng ý trong [APP-03 ngày 23/11](../roadmap/app-03.md), implementation chưa bắt đầu. Quyết định 22/11 thay timing/authorization của DEV; các receipts và giới hạn bằng chứng UI-01..03 dưới đây vẫn là lịch sử đúng scope.

## Ủy quyền UI-03 riêng (20/11/2026)

Owner trực tiếp yêu cầu **“Triển khai UI-03”** và cấp quyền triển khai **Synthetic Source/Command Recovery Lab** riêng. UI-03 đã được review/merge qua [PR #84](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/84), trạng thái **`MERGED / UI03_100_PERCENT_CI_VERIFIED`** chỉ trong synthetic/nonproduction browser CI. Final source `dcd2870`, merge `f17397e`, 10/10 workflows và 20/20 jobs SUCCESS; [closeout](../closeout/2026-11-20-ui-03.md). [Ranh giới ủy quyền](../decisions/owner-ui03-2026-11-20.md) · [ADR/spec](ui-03-source-command-recovery.md) · [verification](ui-03-verification.md). Ủy quyền UI-03 này không cấp phép tích hợp APP-02C hoặc mở UI-04/05; quyền C.1–C.5 DEV đến từ quyết định riêng 22/11/2026 ở trên. Client fixture không phải durable server evidence.

## Ủy quyền UI-02 riêng (20/11/2026)

Owner đã yêu cầu trực tiếp **“Triển khai UI-02”** sau UI-01. [Biên bản giới hạn](../decisions/owner-ui02-2026-11-20.md) cấp quyền implementation cho synthetic Lab riêng; [UI02-ADR-01](ui-02-data-entry.md) chọn native form/table/combobox để kiểm hành vi mà không thêm dependency. Triển khai đã được review và merge qua [PR #82](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/82), trạng thái **`MERGED / UI02_100_PERCENT_CI_VERIFIED`** trong phạm vi synthetic Lab, chỉ với 100% browser zoom; [post-merge closeout](../closeout/2026-11-20-ui-02.md) lưu đúng source/merge SHA, bốn findings đã sửa, CI và những điều chưa kiểm chứng. Không xác nhận sản phẩm thực, physical 125% hay UI-03..05. Các Owner decisions 01..09 và lịch sử UI-01 không bị viết lại.
