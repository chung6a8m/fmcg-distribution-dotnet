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

## 2. Danh sách đủ điều kiện phiên bản 1

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
