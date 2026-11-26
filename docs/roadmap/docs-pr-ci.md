# APP-00A — Phân loại PR tài liệu

Ngày yêu cầu: **25/11/2026**, mục 6 của chuỗi cải tiến CI, trên main
`bcdc5f54dc51e7201ce265fcb900b582ebdc8500` sau PR #148. Phạm vi:
**SYNTHETIC_NONPRODUCTION / CI**. Biên nhận thực thi của candidate cuối nằm
trong PR triển khai; tài liệu này mô tả chính sách, không tự xác nhận CI PASS.

## 1. Điều gì được rút ngắn

Workflow `APP-00A foundation` vẫn được kích hoạt trên mọi `pull_request` và
push `main`. Job Ubuntu `classify` phân loại toàn bộ thay đổi và chạy kiểm tra
business bất biến, link, E-SIM. Chỉ PR đủ điều kiện mới bỏ qua bốn
worker Windows `core_sql`, `web_sales`, `app03`, `distribution`.

`validate` giữ tên và luôn chạy để đánh giá kết quả. Chín workflow CI đồng hành
giữ nguyên cấu hình và các job hiện có. Không thêm `paths-ignore`, nhãn bỏ qua,
quy tắc theo tiêu đề/commit hay đầu vào dispatch để tự chọn đường rút gọn.

| Tình huống | Bốn worker APP-00A | Điều kiện để `validate` thành công |
| --- | --- | --- |
| PR chỉ sửa văn bản đủ điều kiện | Đều `skipped` | Classifier và kiểm tra nhẹ thành công, biên nhận docs hợp lệ và khớp nguồn |
| PR có code/config, file ngoài danh sách, hoặc không xác minh được diff | Đều chạy | Classifier thành công ở chế độ `full`, cả bốn worker `success` |
| Push `main`, kể cả commit chỉ sửa tài liệu | Đều chạy | Cùng điều kiện full |
| Classifier lỗi/hủy/thiếu hoặc biên nhận sai | Không được coi là docs hợp lệ | `validate` thất bại; không chấp nhận một tập worker bị skip như thành công |

Đường full giữ nguyên toàn bộ command, SDK/Node pin, timeout, LocalDB,
build reuse, SQL/browser gate, test và artifact của bốn worker. Bước phân loại
thêm một khoảng chờ ngắn trước worker; số đo thực tế phải ghi cả phần này.

## 2. Danh sách đủ điều kiện phiên bản 1 (baseline lịch sử)

Danh sách chính thức là `ALLOWED_DOC_PATHS` trong
[`tools/classify_app00a_pr.py`](../../tools/classify_app00a_pr.py). Phiên bản
đầu có **16 đường dẫn chính xác**, phân biệt chữ hoa/thường:

| Nhóm | Đường dẫn |
| --- | --- |
| README gốc | `README.md` |
| Ghi chú CI | `docs/roadmap/app-00a-parallel-ci.md` |
| Ghi chú CI | `docs/roadmap/app-00a-build-cache.md` |
| Ghi chú CI | `docs/roadmap/localdb-ci-bootstrap.md` |
| Ghi chú CI | `docs/roadmap/pr-ci-concurrency.md` |
| Chính sách này | `docs/roadmap/docs-pr-ci.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-16-issue-13-pwa-external-update.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-20-ui01-runtime-foundations.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-22-app-02d2.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-22-app-02d3.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-24-app00a-parallel-ci.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-25-app00a-build-cache.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-25-app03-read-diagnostics.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-25-localdb-bootstrap.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-25-pr-ci-concurrency.md` |
| Kế hoạch | `docs/superpowers/plans/2026-11-25-docs-pr-ci.md` |

Mỗi phía tồn tại của file phải có Git mode `100644`. Thêm/xóa một đường dẫn
trong danh sách được xét với cùng điều kiện; link/business validator vẫn chạy
và sẽ từ chối việc xóa README bắt buộc. Symlink, executable, gitlink, đổi loại
file, diff rỗng, đường dẫn lạ hoặc file mới ngoài danh sách đều chạy full.
Trước quyết định docs-only, bộ phân loại còn kiểm tất cả old/new object ID
trong hai phép so sánh thực sự tồn tại và có loại `blob`; thiếu nội dung của
một phiên bản file cũ cũng chọn full.

Không dùng `docs/**` hay `**/*.md`: C.6 và D.1–D.4 đọc/hash các hợp đồng và
acceptance matrix Markdown; E-SIM đọc bảng; test bảo mật đọc decision;
`docs/ui-ux/` chứa CSS, token, font và công cụ build. Các instruction,
business/lock, decision, security, evidence, spec, deploy, source, test, script,
tool, dependency và workflow đều ở ngoài ngoại lệ này.

Khi thêm file đủ điều kiện, phải rà soát nơi đọc file và cập nhật classifier
cùng regression trong PR chạy full. Nếu code mới bắt đầu đọc một file đã có
trong danh sách, loại file đó khỏi danh sách trong cùng PR. Rà soát hiện tại
không phải bảo đảm cho các consumer chưa tồn tại trong tương lai.

## 3. Toàn bộ PR và biên nhận nguồn

Checkout vẫn là synthetic merge commit mặc định của GitHub. `classify` lấy
đủ history, xác nhận checkout cùng `WorkflowSha`, hai parent đúng event base
và PR head, và có đúng một merge base. Bộ phân loại đọc cả diff merge-base →
PR head lẫn diff event-base → checkout merge. Do đó, một commit cuối chỉ sửa
README không che được thay đổi code còn nằm trong các commit trước của PR.

Diff dùng định dạng raw/NUL và `--no-renames`, có old/new mode. Đổi tên được
xét thành xóa và thêm, nên chuyển file source vào một tên Markdown đủ điều
kiện vẫn chạy full. Không dùng danh sách file API có thể bị cắt, không chuẩn
hóa tên file, không thay thế byte UTF-8 lỗi. Thiếu history, sai SHA/parent,
merge base mơ hồ, timeout hoặc kết quả quá giới hạn đều chọn full; không lấy
một phần kết quả rồi tuyên bố docs-only.

Job lưu `TestResults/app00a-classification.json` vào artifact phân loại riêng
theo run/attempt và ghi tóm tắt ngay trong Actions. Biên nhận có event, base
SHA, source head SHA, workflow SHA, merge-base SHA, mode, lý do và proof của
những thay đổi đủ điều kiện. Với mode `full`, danh sách `changes` rỗng vì không
đưa ra proof để skip. `validate` so khớp với context của chính lượt đang chạy,
kiểm schema/JSON và kết quả từng worker; không chấp nhận JSON thiếu, key trùng,
mode lạ hoặc nguồn khác.

**Docs success có nghĩa `runtime workers NOT_RUN`.** Artifact phân loại không
phải biên nhận LocalDB, .NET, SQL hay browser. Không dùng nó để closeout một
thay đổi runtime hoặc suy ra physical/PREPROD/Owner acceptance. Sau merge,
push `main` vẫn cần một lượt full CI riêng cho commit đã merge.

## 4. Kiểm chứng và cách đo

Các regression Python dùng Git repo tạm thực để kiểm whole-PR range, merge
result, đổi tên, mode, thiếu history và push main. Gate tests kiểm cả CLI exit
code, lỗi phân loại, nguồn sai, full có worker fail/skip, docs trên push và
kết quả worker trộn lẫn. Chạy tại repository:

```text
python3 -B -m unittest discover -s tests/ci -p 'test_*.py' -v
python3 -B tools/validate_business.py
python3 -B tools/validate_e_sim_01.py
pwsh -NoProfile -File scripts/Verify-Business.ps1
git diff --check
```

Kiểm thêm actionlint và đối chiếu toàn bộ steps của bốn worker, các workflow
đồng hành với baseline. Local/static PASS không chứng minh lịch chạy hoặc
Windows LocalDB trên GitHub.

PR triển khai sửa workflow/tools/tests nên phải đi đường **full**, dù commit
cuối chỉ sửa tài liệu. Để kiểm đường docs trước khi merge, đóng băng candidate
đã review rồi tạo một draft PR con: base là nhánh triển khai, head thêm đúng
một thay đổi văn bản đủ điều kiện. Quan sát Jobs API ghi bốn worker `skipped`,
kiểm tra nhẹ và `validate` thành công, receipt khớp SHA, và các workflow đồng
hành vẫn chạy. Đóng PR kiểm chứng mà không merge; giữ URL và artifact trong
PR triển khai. Nếu classifier/workflow thay đổi sau đó, chứng minh lại trên
candidate mới.

Biên nhận cuối trong PR ghi rõ source/merge SHA, run ID, attempt, trạng thái
từng job, elapsed/queue, runner time, artifact và các lần lỗi nếu có. So sánh
đường full và docs là số đo của các lượt cụ thể, không phải SLA hay kiểm chứng
runtime tương đương. Không tạo commit chỉ để ghi nhận chính lượt CI vừa chạy.

Thiết kế và kế hoạch: [design](../superpowers/specs/2026-11-25-docs-pr-ci-design.md),
[implementation plan](../superpowers/plans/2026-11-25-docs-pr-ci.md).
Quy tắc GitHub: [required checks và job/workflow bị skip](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks).

## 5. Docs PR CI v2 — roadmap được Owner yêu cầu ngày 26/11/2026

Trạng thái tại lần bổ sung roadmap: **ROADMAP_APPROVED / IMPLEMENTATION_NOT_STARTED**.
Phạm vi **SYNTHETIC_NONPRODUCTION / CI / DOCS_GOVERNANCE**. Owner yêu cầu thực
hiện đề xuất bổ sung track v2 sau phân tích PR #163. Đây là roadmap triển khai
theo các PR độc lập, không phải biên nhận v2 đã hoạt động. Các mục 1–4 mô tả chính sách v1 lịch sử; chính sách candidate 02B ở mục 5.4 và handoff bên dưới. Chỉ áp dụng sau review, probe và kiểm chứng. Không thay đổi phạm vi business, APP runtime,
production, physical/PREPROD, Pilot, Owner decisions hoặc các acceptance gate
nghiệp vụ. Track này độc lập với tiến độ APP-05.7 và các increment business.

### 5.1. Căn cứ và mục tiêu

- [PR #149](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/149)
  đã merge v1 tại `f7d13a5`: 16 đường dẫn chính xác, bốn worker APP-00A,
  chín workflow đồng hành chưa rút gọn.
- [PR #150](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/150)
  là probe đóng không merge: APP-00A 37 giây, toàn bộ 10 workflow 2 phút
  13 giây, bốn worker Windows thực sự skipped. Đây là số đo một probe v1.
- [PR #163](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/163)
  chỉ thay `AGENTS.md` và `docs/roadmap/app-05.md`, nhưng cả hai ngoài
  allowlist. [Log classify của lần phân tích](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45737155919/job/139633057384)
  ghi `full (change_not_allowlisted_prose)` trên source lúc đó `68d3f91`.
  Cả bốn worker chạy. Workflow B.4 tenant của lượt đó còn gặp
  [InstanceStartFailed](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45737155740/job/139633056566).
  Đây là hành vi đúng v1, không phải lỗi classifier. #163 đã merge;
  dùng ca lịch sử để thiết kế regression, không mở lại hay cấp lại PASS cho PR cũ.

Mục tiêu: tách trạng thái và biên nhận thuần túy khỏi authority, hợp đồng,
acceptance matrix và chỉ dẫn. Chỉ các PR đã được chứng minh là văn bản không
ảnh hưởng thực thi mới đi đường `docs-only` (Light). Hướng tới toàn bộ PR CI
1–3 phút trong các lượt đại diện không lỗi, không khởi động LocalDB, build
ứng dụng hoặc browser nếu không cần. Đây là mục tiêu đo, không phải SLA;
không giảm độ phủ full để đạt số đo. Push main luôn full, kể cả docs-only.

### 5.2. Cấu trúc tài liệu và quyền quyết định

| Nhóm | Vai trò | Chính sách v2 dự kiến |
| --- | --- | --- |
| `AGENTS.md` và instruction khác | Quy tắc ổn định, authority, các link điều hướng | Full |
| `docs/roadmap/` | Thiết kế, scope, hợp đồng, thứ tự triển khai và điều kiện nghiệm thu | Full mặc định; chỉ ngoại lệ đã audit |
| `docs/status/` | Trạng thái mô tả của APP-02, APP-03, APP-05, UI và LAB | Chỉ đường dẫn cụ thể đã audit mới Light |
| `docs/receipts/` | Biên nhận tham chiếu PR, SHA, run/attempt, artifact và giới hạn | Chỉ đường dẫn cụ thể đã audit mới Light |
| `business/`, lock, decisions, security, specs, acceptance/evidence inputs, UI assets, source/test/tool/script/config/workflow | Authority hoặc đầu vào thực thi | Full |

Hai thư mục mới là cấu trúc dự kiến, không phải wildcard allowlist. Ưu tiên
file Markdown ổn định theo track, ví dụ status APP-05 và receipt APP-05 dạng
append/index để không cần thay classifier mỗi lần ghi một PR mới. Không tạo
một file allowlisted mới cho mỗi run; nếu cần đường dẫn mới, audit và bổ sung
trong PR full. Có thể giữ đường dẫn hiện hữu nếu audit chứng minh an toàn,
không di chuyển acceptance contract chỉ để đạt docs-only.

AGENTS dẫn tới nguồn trạng thái hiện hành thay vì chép SHA, PR number và
tiến độ mỗi lần. Status/receipt chỉ là thông tin điều hướng và hồ sơ mô tả;
không tự cấp authority, không thay thế Owner decision, không trở thành
machine-readable input cho test/build/runtime, không tự động mở gate hoặc
cho phép triển khai tiếp dựa trên chữ PASS trong Markdown. Codex/reviewer
đối chiếu trạng thái với roadmap/authority và evidence thực tế. Giữ lịch sử
nguồn, các giới hạn và trạng thái riêng (LOCAL, PR CI, MAIN CI, physical,
Owner acceptance); không suy diễn từ docs PASS. Khi di chuyển nội dung,
giữ link/anchor cần thiết hoặc cập nhật mọi consumer, đối chiếu nội dung
trước/sau và không mất receipt cũ. Không tạo chuỗi PR tự ghi nhận CI của
chính PR ghi nhận.

### 5.3. Ma trận chọn đường chạy

| Tình huống | PR CI sau v2 | Push main |
| --- | --- | --- |
| Chỉ cập nhật status/receipt hoặc prose đã audit, đủ mọi điều kiện | Light | Full |
| Thay AGENTS/instruction, authority, business contract hoặc acceptance input | Full | Full |
| Thay source, schema/migration, dependency, config, test/tool/script, workflow/policy classifier | Full | Full |
| PR trộn file Light với bất kỳ file Full/không phân loại | Full | Full |
| Diff rỗng, rename/type/mode bất thường, thiếu history/blob, SHA sai hoặc không đủ proof | Full nếu classifier xác định fallback hợp lệ; nếu lỗi gate phải fail | Full |
| Classifier/receipt/gate thiếu, lỗi, hủy, malformed hoặc kết quả worker không đúng mode | Không PASS | Không PASS |

Không dùng nhãn, tiêu đề, commit message, path filter ở cấp workflow, flag
dispatch hoặc lựa chọn thủ công để bỏ qua gate. Giữ whole-PR range và
integration diff, raw/NUL, exact path, mode/blob verification và
source-bound receipt của v1. Trường hợp không chắc chắn không được Light.
Thay chính sách phải tự chạy full; không cho candidate thay classifier tự
cấp quyền Light. Kiểm các thay đổi policy/entrypoint ở base và integration,
bao gồm regression sửa hoặc xóa classifier/workflow để tự miễn test.

### 5.4. Increments triển khai

Mỗi increment một PR độc lập, đọc lại main và quy tắc hiện hành trước khi
bắt đầu. Các tên file và cơ chế điều phối mới là đề xuất kỹ thuật trong
roadmap; chọn thiết kế tối thiểu có thể kiểm chứng, ghi lý do nếu khác.
Không bắt đầu increment sau khi predecessor còn blocking finding hoặc chưa
có biên nhận merge/main tương ứng. Không cập nhật status chỉ để tự tạo
closeout mới.

| Increment | Phụ thuộc | Phạm vi | Trạng thái |
| --- | --- | --- | --- |
| DOC-CI-02A — Consumer audit & document boundaries | v1 | Inventory và tách trạng thái khỏi instruction/hợp đồng; [audit và handoff](doc-ci-02a-consumer-audit.md) | MERGED / MAIN_CI_VERIFIED / CLOSED ([receipt #169](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/169#issuecomment-7188635128)) |
| DOC-CI-02B — Status/receipt & classifier v2 | 02A | Chuẩn hóa status/receipt, exact candidate allowlist và diagnostics | SOURCE_CANDIDATE / REVIEW_AND_CI_PENDING |
| DOC-CI-02C — Companion CI routing | 02B | Áp dụng cùng policy cho chín workflow đồng hành | NOT_STARTED |
| DOC-CI-02D — Independent acceptance & closeout | 02C | Probe hosted, full controls, main receipt và đo hiệu quả | NOT_STARTED |

#### DOC-CI-02A — Consumer audit & document boundaries

- Chốt baseline source SHA, inventory workflow/job/check đang chạy và
  inventory các file Markdown/consumer. Dùng search toàn tree (tracked files),
  không chỉ GitHub code search: đọc cả literal path, glob, directory scan,
  import/copy, hash, fixture, acceptance, validator, build, runtime và agent
  authority. Rà soát `src/`, `web/`, `tests/`, `tools/`, `scripts/`,
  `.github/`, deploy và build manifests; lưu lệnh, kết quả và giới hạn audit.
- Lập ma trận path → consumer → loại dữ liệu → quyết định Full/ứng viên
  Light → lý do → regression cần thiết. Có consumer chưa hiểu hoặc đầu vào
  quyết định nghiệp vụ thì giữ Full. Kiểm lại 16 path v1, không giả định
  không có consumer mới sau #149.
- Tách phần tiến độ lặp khỏi AGENTS và roadmap APP-05 làm mẫu; mở rộng
  inventory tới APP-02/03, UI, LAB. Giữ nguyên quy tắc, authority, thứ tự
  và điều kiện business. Giữ link điều hướng, snapshot/receipt cũ và
  khả năng truy vết. Không cần chuyển tất cả lịch sử trong một PR.
- Acceptance: reviewer đối chiếu trước/sau không mất authority, status,
  evidence hoặc link; business/link/E-SIM và full final-HEAD CI PASS.
  Bàn giao inventory và danh sách path đã audit cho 02B. Chưa sửa routing.

#### DOC-CI-02B — Status/receipt & classifier v2

- Chuẩn hóa mẫu cho các file ổn định theo track: increment, trạng thái và
  phạm vi, source/merge SHA, workflow run/attempt, kết quả thực tế,
  artifact/digest khi có, link review, giới hạn và điều kiện còn mở.
  Không biến trường mô tả thành input nghiệp vụ. Không ghi PASS nếu chưa
  có evidence tương ứng. Tách trạng thái current khỏi lịch sử append.
- Chỉ bổ sung exact path đã audit ở 02A, loại path v1 nếu phát hiện
  consumer mới. Giữ case-sensitive, kiểm mọi mode/blob/old-new path;
  không cho phép wildcard cả thư mục. Trong cùng PR thêm regression native
  Git và gate tests tương ứng. Nếu cần thêm consumer sau này, loại path
  khỏi Light trong cùng PR full và ghi vào inventory.
- Bổ sung diagnostics cho Full: offending path(s) hoặc lý do không thể
  xác minh, phase whole-PR/integration, status/mode nếu đã xác minh.
  Giới hạn số record/kích thước, escape an toàn đường dẫn lạ trong log
  và Markdown summary; không in nội dung file/secrets. Diagnostics không
  phải proof để skip; version/schema receipt có test tương thích và
  fail-closed. Giữ tên `validate` và logic explicit skipped/success v1.
- Acceptance: các ca status/receipt-only hợp lệ Light, AGENTS/roadmap
  contract/mixed/new-unlisted/rename/symlink/executable/missing blob và
  policy edits Full hoặc fail đúng hợp đồng; probe hosted trên candidate
  frozen thấy bốn APP-00A worker skipped, chín companion giữ nguyên.
  PR triển khai và main vẫn full. Bàn giao common policy contract cho 02C.

**02B candidate policy (chưa merge):** [contract và audit disposition](doc-ci-02b-classifier-v2.md). Hai đường dẫn APP-05 được đề xuất; 16 path v1 trở về Full; chỉ sửa file đã tồn tại mode 100644 (A/D cũng Full). Receipt v2 có diagnostics bounded theo phase, không dùng v1 receipt để chọn v2 Light. Giữ nguyên triggers, worker và validate. Nếu consumer chưa được chứng minh, giữ Full; không merge candidate trước independent review, full CI và hosted Light proof.

#### DOC-CI-02C — Companion CI routing

- Inventory chính xác chín workflow PR đồng hành: B.2 runtime CI,
  B.3 identity, B.4 tenant, B.4 web, B.5 authorization, B.6 policy,
  B.7 backup, B.8 audit, security/picomatch. Đối chiếu toàn bộ check name,
  job name, trigger, permissions, dependency, evidence và required
  checks/rulesets hiện hữu trước thay đổi. Nếu không đọc được protection,
  ghi BLOCKED cho việc thay check contract; không suy đoán hoặc tự bỏ
  required check. Giữ các workflow ngoài phạm vi.
- Dùng cùng classifier/policy và cùng hợp đồng xác minh receipt, không
  sao chép 10 allowlist. Ưu tiên reusable logic chạy trong chính context
  của từng workflow; mỗi lượt tự xác nhận source/base/merge, event và
  policy version. Không lấy artifact latest/khác run/SHA làm authority;
  không dựa vào output từ workflow PR khác để tự skip. Không cấp thêm
  quyền ghi token hoặc chạy untrusted PR với secrets.
- Giữ workflow trigger và check context cần thiết luôn xuất hiện. Mỗi
  required check/gate phải xác nhận classifier, mode, exact job inventory
  và kết quả: Full yêu cầu đủ worker success; Light chỉ chấp nhận explicit
  skips đúng tập heavy jobs cùng các light checks success. Job gate phải
  chạy khi dependency fail/skip (trừ lượt hủy không thể PASS); không coi
  skipped check tự nhiên là bằng chứng. Nếu cần tách worker/gate, giữ
  tên check công khai tương thích và tránh trùng tên; mọi migration
  protection phải được xác nhận riêng trước khi áp dụng.
- Light vẫn chạy immutable business, business/link/E-SIM và kiểm tra
  policy/receipt/regression cần thiết; giữ kiểm tra tĩnh về bảo mật và
  các policy gates không thể chứng minh độc lập với runtime. Chỉ bỏ
  LocalDB, build, browser hoặc runtime suite đã audit không bị ảnh hưởng;
  nếu một gate cần runtime để có kết luận hợp lệ thì gate đó vẫn chạy.
  Full giữ tất cả command, versions, security audit, test, timeout,
  fault/recovery, cleanup và evidence như baseline, không giảm assertion
  hoặc mở waiver mới. Main luôn full.
- Acceptance: actionlint, native Git, aggregate truth tables và fault
  injection kiểm mode sai, source sai, receipt sai/thiếu, worker fail/
  canceled/skip bất ngờ, tập job thiếu và gate failure. So sánh inventory
  và full steps trước/sau, chạy full hosted trên HEAD cuối; probe Light
  hosted chứng minh đúng các job không chạy thực tế và không có required
  check bị Pending do workflow không được kích hoạt.

#### DOC-CI-02D — Independent acceptance & closeout

- Freeze candidate đã review; lập ma trận probe và full controls dưới đây.
  Dùng PR con hoặc branch test được kiểm soát để kiểm route trước merge,
  không merge patch probe. Tách kiểm chứng route khỏi kiểm chứng runtime.
  Nếu source/policy thay đổi, chạy lại bằng chứng bị ảnh hưởng trên HEAD mới.
- Đối chiếu toàn bộ 10 workflow và từng check/gate, source/base/test merge/
  tree, run/attempt, artifact, kết quả explicit skip hoặc execution.
  Ghi thời gian từ run đầu đến gate cuối, runner time, queue, số Windows
  runner/LocalDB/browser khởi chạy; giữ cả failure/rerun, không chọn riêng
  attempt nhanh. Đo ít nhất ba lượt Light đại diện và một full control
  mới hoàn chỉnh; so với v1/baseline tương ứng, mục tiêu 1–3 phút không
  dùng làm lý do giảm test. Nếu chưa đạt, ghi bottleneck và giới hạn.
- Independent review xác nhận không còn blocking findings, full final-HEAD
  CI trên PR triển khai, full main trên merge SHA sau merge. Nếu main
  chưa PASS, giữ MAIN_CI_PENDING/FAILED; không dùng probe thay thế.
  Closeout bằng PR body/comment và biên nhận nguồn trong increment có
  thay đổi thực chất, không tạo vòng PR chỉ ghi nhận CI của chính nó.

### 5.5. Acceptance matrix v2 bắt buộc

| ID | Ca kiểm | Kết quả cần quan sát |
| --- | --- | --- |
| DOC-V2-01 | Sửa status APP-05 đã audit | Light; các heavy jobs eligible thực sự skipped; gates/light checks PASS |
| DOC-V2-02 | Chỉ append receipt đã audit; hoặc prose v1 vẫn đủ điều kiện | Light; không tuyên bố runtime PASS |
| DOC-V2-03 | Ca tương đương #163 sau tách tài liệu (chỉ status + receipt) | Light; không đổi AGENTS/contract; có thể tra lại PR/SHA/run |
| DOC-V2-04 | Thay AGENTS hoặc roadmap contract như #163 lịch sử | Full; summary chỉ đúng offending path |
| DOC-V2-05 | Source-only, migration/config/security/decision hoặc workflow/policy edit | Full; giữ toàn bộ test/evidence theo inventory |
| DOC-V2-06 | Mixed PR, kể cả commit cuối docs-only | Full dựa trên toàn bộ PR và merge result |
| DOC-V2-07 | File lạ, rename, mode/type sai, blob/history thiếu, merge ambiguity | Không Light; fallback full hoặc gate fail đúng hợp đồng |
| DOC-V2-08 | Receipt sai/missing, mode lạ, dependency failure/cancel/skip, gate thiếu, self-exemption attempt | Không có required gate success sai |
| DOC-V2-09 | Push main sau PR Light và Full | Full; receipt trên merge SHA thực tế |
| DOC-V2-10 | Kiểm branch protection/check names và so full coverage | Không required check Pending do filter; không mất test, security gate hoặc artifact |

Các ca âm tính dùng regression local/native Git và fault injection, cộng
hosted control/probe phù hợp; không cần cố tình làm hỏng main để thử.
Biên nhận phải phân biệt LOCAL_VERIFIED, PR_CI_VERIFIED, MAIN_CI_VERIFIED
và NOT_RUN/BLOCKED. V2 chỉ closeout khi các gate trên đạt và có bằng chứng
hosted; không suy diễn physical, real PREPROD, P0/EV hoặc production PASS.

### 5.6. Handoff và rollback

Bắt đầu DOC-CI-02A trên main mới sau khi PR roadmap được review/merge.
Các PR kế tiếp tự cập nhật trạng thái trong roadmap/nguồn status đã được
chốt; không nới allowlist tạm để xử lý #163 hay PR tương tự. Trong thời gian
02A–02D chưa hoàn tất, giữ policy thực sự đã merge tại từng increment và
yêu cầu full khi không chắc chắn. V2 không sửa nợ LocalDB bootstrap, C.6
HTTP timeout hoặc runtime intermittency; tiếp tục ghi/chẩn đoán chúng ở
track CI tương ứng, không dùng Light để che lỗi full.

Rollback khi có routing/gate sai: khôi phục đường full cho mọi event bằng
PR CI được review, giữ check names, permissions, test/evidence và lịch sử
receipt. Không disable workflow/required check để unblock merge. Giữ v1
làm baseline đối chiếu; sau sửa phải chứng minh lại cả Light và Full.
