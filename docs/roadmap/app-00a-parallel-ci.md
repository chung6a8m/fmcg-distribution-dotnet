# APP-00A — Bốn nhóm CI song song

Ngày quyết định: **24/11/2026**. Owner đồng ý phương án bốn job độc lập và yêu cầu triển khai sau phân tích thời lượng CI.

Baseline nguồn: `7aada55d0889c4fd6d025816c76e7e4a83cd4102`, merge PR [#144](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/144). PR #144 tăng timeout 30 → 45 phút; tài liệu này mô tả cải tiến thời gian hoàn tất tiếp theo. Lịch sử và điều kiện closeout APP-05.0 vẫn nằm trong [roadmap APP-05](app-05.md#app-050--ổn-định-ci-nền).

Phạm vi: **SYNTHETIC_NONPRODUCTION / CI**. Source candidate được kiểm chứng và review trên HEAD cuối; biên nhận thực thi ghi trong PR tương ứng. Chỉ có biên nhận của commit đã merge mới xác nhận lượt `main` của thay đổi này.

## 1. Căn cứ và mục tiêu

Lượt APP-00A [45397979755](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45397979755), source `96f3e8c0c96b57a248fcb2c458922048c5c1d2fa`, thành công trong **28m59s**. Log thực tế ghi **355/355** test .NET, failed 0, skipped 0. Đây là số đã quan sát trên lượt đó; không nhầm với discovery floor trong YAML.

| Nhóm trong job nối tiếp cũ | Thời lượng step cộng lại |
| --- | ---: |
| Setup / LocalDB / restore / build | 1m35s |
| Test .NET chính | 4m17s |
| Kiểm tra provenance / SQL evidence | 15s |
| Web setup / audit / build / Chromium / browser chung | 1m45s |
| C.1–C.6 | 2m44s |
| F01–F08 | 3m07s |
| App03A1 và APP-03.2–.7 | 7m20s |
| D.1–D.3 | 7m38s |
| Upload / kiểm tra PWA cuối / kết thúc | 11s |

Phần còn lại là thời gian giữa các mốc điều phối. Chín workflow còn lại trên cùng source hoàn tất trong 24s–2m21s. Lượt cuối của PR #144 [45424683631](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45424683631) thành công trong **30m07s** với budget 45 phút.

**Mục tiêu thử nghiệm:** APP-00A khoảng **10–14 phút/lượt** sau tách, khi runner được cấp kịp thời. Đây là ước tính từ các nhóm hiện có, không phải kết quả hay SLA đã đạt. Ghi riêng elapsed từ workflow start, thời gian queue của từng job, thời gian thực thi từng job và tổng runner time. Setup lặp trên bốn máy có thể làm tổng runner time tăng dù người dùng nhận kết quả sớm hơn.

## 2. Phân chia thực thi

Workflow vẫn là `APP-00A foundation`, chạy trên mọi `pull_request` và push `main`, quyền `contents: read`. Bốn worker `windows-2022` giữ budget 45 phút làm mức trần dự phòng. Các worker không có `needs` lẫn nhau.

| Job ID | Công việc sở hữu |
| --- | --- |
| `core_sql` | Immutable business contract; toàn bộ test .NET và các verifier provenance, TRX, coverage, SQL oracle hiện có |
| `web_sales` | npm install và hai audit; Workbox/Tailwind/health/theme/tokens; build PWA, browser chung và kiểm tra PWA build; C.1–C.6; F01–F08 |
| `app03` | App03A1; Verify, VerifyWorkspace, VerifyHandoff, VerifyField, VerifySettlement, VerifyFinance |
| `distribution` | D.1–D.3 đầy đủ browser, SQL matrix, fault/rollback và restart recovery |
| `validate` | Check tổng hợp phụ thuộc cả bốn worker, luôn đánh giá kết quả và chỉ thành công khi đủ bốn kết quả `success` |

Mỗi worker có checkout, toolchain, dependency và LocalDB setup phù hợp. Không truyền database hoặc build output giữa worker. Các runner C/D/F/APP-03 vẫn chạy tuần tự trong nhóm, giữ cổng API 5116, nonce, migration, cleanup và kiểm tra source của assembly như trước.

Giữ nguyên SDK `10.0.404`, Release, Node 26 và floor bảo mật `26.13.1`. Invocation .NET vẫn có `--no-build`, discovery floor hiện hành, exactly-one-TRX, zero failure/skip và coverage. Không đổi test, application source, migration, runner script, business snapshot, retry, cache hoặc path filter.

## 3. Check tổng hợp và nguồn được kiểm chứng

`validate` dùng `if: always()` và `needs: [core_sql, web_sales, app03, distribution]`. Gate nhận JSON kết quả qua biến môi trường, kiểm đủ đúng bốn job và từ chối mọi trạng thái khác `success`, dữ liệu sai kiểu, thiếu job/result hoặc JSON lỗi. Unit tests chạy trong check tổng hợp và kiểm cả CLI exit code.

Một worker bị lỗi setup, fail assertion, bị hủy hoặc skip không thể tạo kết quả tổng hợp xanh. Job upload evidence giữ `always()` tại nơi tạo evidence; trạng thái aggregate không thay thế các verifier nội dung của từng worker.

Giữ checkout mặc định của pull-request workflow. `SourceHeadSha` của bằng chứng .NET ghi PR head; `WorkflowSha` ghi `github.sha`. DEV runners tiếp tục lấy source thực sự checkout bằng `git rev-parse HEAD` và so với version assembly. Biên nhận PR phân biệt các SHA này; không đổi checkout sang PR head để làm các SHA trùng nhau.

## 4. Artifact và cách đọc evidence

Toàn bộ evidence của APP-00A gồm các artifact dưới đây. Hai tên gốc được giữ cho worker tương ứng; evidence các nhóm được tách sang artifact riêng, nên không chỉ tải hai artifact tên cũ để kết luận đã đủ toàn workflow.

| Producer | Artifact | Nội dung |
| --- | --- | --- |
| `core_sql` | `app00a-test-evidence` | TRX, coverage, oracle và provenance .NET; chẩn đoán LocalDB nếu bootstrap lỗi |
| `web_sales` | `app00b-playwright-evidence` | Browser/PWA chung, C/F browser outputs trong các đường dẫn đang có |
| `web_sales` | `app00a-test-evidence-web-sales` | `TestResults` của C/F và chẩn đoán LocalDB |
| `app03` | `app00b-playwright-evidence-app03` | Browser/receipt APP-03; gồm đường dẫn App03A1 kế thừa dưới `web/test-results-app02c5/` |
| `app03` | `app00a-test-evidence-app03` | Toàn bộ `TestResults` APP-03 và chẩn đoán LocalDB |
| `distribution` | `app00b-playwright-evidence-distribution` | Browser evidence D.1–D.3 |
| `distribution` | `app00a-test-evidence-distribution` | SQL/evidence D.1–D.3 và chẩn đoán LocalDB |

Các đường dẫn trong bảng là đường dẫn tại worker. `upload-artifact` dùng thư mục tổ tiên chung của các search paths làm gốc ZIP, nên gốc của hai browser archive thay đổi khi tách producer:

| Browser artifact | Gốc ZIP so với checkout | Ví dụ member khi mở ZIP |
| --- | --- | --- |
| `app00b-playwright-evidence` | `web/` | `test-results/playwright-junit.xml`, không có tiền tố `web/` như archive cũ |
| `app00b-playwright-evidence-app03` | Gốc repository, vì có cả `web/` và `TestResults/` | `TestResults/app03-3/...` và `web/test-results-app02c5/...` |
| `app00b-playwright-evidence-distribution` | `web/` | `test-results-app02d1/<run>/junit.xml` |

Khi tái dựng cây evidence theo checkout, giải nén browser archive web/distribution vào thư mục `web/` của bộ evidence và browser archive APP-03 vào gốc. Tải các artifact `app00a-test-evidence*` vào gốc theo cây `TestResults` của chúng. Đối chiếu ZIP members thực tế trong lượt CI cuối trước khi kết luận đủ evidence. Quy tắc đóng gói: [upload-artifact — multiple paths](https://github.com/actions/upload-artifact#upload-using-multiple-paths-and-exclusions).

Các upload giữ `if-no-files-found: warn` như baseline; một nhóm không tạo được evidence do thất bại vẫn giữ kết quả thất bại. Mỗi worker thu `**/TestResults/**/*`, các browser paths được chuyển theo nơi thực sự sinh ra. Điều kiện startup diagnostics chỉ chạy khi bootstrap thất bại vẫn được giữ nguyên.

## 5. Kiểm chứng và biên nhận

Kiểm chứng tại máy có Python:

```text
python3 -B -m unittest discover -s tests/ci -p 'test_*.py' -v
python3 -B tools/validate_business.py
python3 -B tools/validate_e_sim_01.py
git diff --check
```

Ngoài unit tests, đối chiếu YAML với baseline: mọi step kiểm chứng cũ có cùng command/env/condition; setup có thể được lặp; hai upload cũ được phân chia nhưng phải bao phủ mọi đường dẫn và đúng producer. Chạy actionlint khi có công cụ. Kiểm tra source diff để bảo đảm application, runner scripts, business và các workflow khác không thay đổi.

CI trên Windows phải xác nhận: bốn worker và `validate` thành công; số test/suite và SQL/browser receipts không giảm; các upload hiện hữu, nguồn/SHA đúng; ZIP members đúng gốc/đường dẫn đã công bố; các workflow regression còn lại được đánh giá trên cùng final HEAD. `VerifyAcceptance` và F09 All không được workflow baseline gọi, nên không được tính là gate CI hiện có.

Biên nhận cuối ghi trong PR body: final PR HEAD, tested workflow SHA, run/attempt, URL, per-job elapsed/queue, test/evidence counts, các lần fail nếu có và independent review. Cách ghi này tránh tạo commit mới chỉ để ghi nhận chính lượt CI vừa chạy, khiến HEAD đã kiểm chứng bị thay thế.

Live branch-protection settings cần được đối chiếu khi đánh giá merge readiness; connector đã trả 403 ở lần đọc ban đầu. Giữ nguyên tên `validate` không tự chứng minh cấu hình protected checks. Kết quả Linux/static không chứng minh Windows LocalDB, lượt `main`, laptop hay physical PREPROD.

Kế hoạch triển khai: [2026-11-24-app00a-parallel-ci](../superpowers/plans/2026-11-24-app00a-parallel-ci.md).

## 6. Hủy lượt PR bị thay thế

Follow-up mục 3 được Owner yêu cầu ngày 25/11/2026: [chính sách concurrency cho PR CI](pr-ci-concurrency.md). Chính sách ở cấp workflow giữ bốn worker song song, yêu cầu hủy lượt PR bị thay thế trong cùng workflow và dùng group riêng cho từng lượt push `main`/manual. Tài liệu liên kết ghi phạm vi 16 workflow, giới hạn khi re-run HEAD cũ và cách kiểm chứng tự hủy; biên nhận thực thi nằm trong PR tương ứng.

## 7. Đo package cache và dùng lại build trong worker

Follow-up mục 5 được Owner yêu cầu ngày 25/11/2026 trên main sau PR #147: [số đo và thiết kế build/cache](app-00a-build-cache.md). Cải tiến này giữ sơ đồ bốn worker và toàn bộ gate, thêm cache NuGet với restore thật và opt-in dùng lại Release build vừa tạo trong cùng job. Hai launcher kiểm tra source và output trước khi bỏ build lặp; không truyền compiled artifact giữa worker. Đây là thay đổi kế tiếp so với phạm vi ban đầu ở mục 2; baseline, các giới hạn và biên nhận cold/warm được ghi riêng.
