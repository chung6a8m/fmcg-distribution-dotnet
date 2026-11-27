# APP-05 — Biên nhận mô tả đã có

Tách từ [roadmap APP-05](../roadmap/app-05.md) tại baseline `af8ecaabfd3417a258d866da7d52f01e492722fa` (26/11/2026), giữ nguyên nội dung các đoạn lịch sử/closeout bên dưới, chỉ sửa đường dẫn tương đối và thêm anchor điều hướng. Đây không phải biên nhận CI của DOC-CI-02A. Các hợp đồng, điều kiện, lịch sử lỗi và giới hạn vẫn có hiệu lực theo nguồn sở hữu, không đọc file này làm input test/build/runtime, không mở gate bằng chữ PASS. Các biên nhận local trộn với hướng dẫn kỹ thuật trong roadmap vẫn để tại chỗ. [Trạng thái](../status/app-05-status.md).

## Quy cách append receipt (mô tả, không cấp quyền)

Mỗi mục mới ghi increment và ngày; scope và disposition thực tế; source HEAD, tested integration/merge SHA (nếu có); workflow URL, run ID, attempt và kết quả job; artifact ID/digest nếu đã kiểm; link review; lỗi/rerun và giới hạn/điều kiện còn mở. Dùng NOT_RUN/PENDING/NOT_RECORDED khi chưa có evidence, không tự tạo số liệu. Không ghi đè historical receipt; append correction dẫn nguồn cũ. Không dùng file này thay hợp đồng, decision hoặc kết luận runtime/physical.

<a id="app05-0-history"></a>

#### Biên nhận lịch sử APP-05.0 — thay đổi nguồn, chờ CI và review (24/11/2026)

Đối chiếu tại `main` `a795b844dd45fa38edc17b6dfb2f9dde77c6fe36`, tree `e065ca2c2ed99a33423d88423a6ea755756ba402`. Thời lượng dưới đây là elapsed từ `run_started_at` tới `updated_at` của workflow, gồm cả phần kết thúc job; không phải riêng thời lượng test.

| Workflow APP-00A | Source HEAD | Thời lượng | Kết quả |
| --- | --- | --- | --- |
| [45379497955](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45379497955) | `515a9ab13acad28ab7811caf2a08287977b888ba` | 25m04s | SUCCESS |
| [45385147365](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45385147365) | `d349acaa994df3e6039fc6d5b869310549ee6899` | 26m41s | SUCCESS |
| [45397979755](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45397979755) | `96f3e8c0c96b57a248fcb2c458922048c5c1d2fa` | 28m59s | SUCCESS |
| [45402915855](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45402915855) | `29bc4bc2b4a14ad45386f57a8f6bb25a697005ee` | 30m14s | CANCELLED |

Các lượt PR 45369399575, 45367280154, 45365300779, 45360021334 và 45355301169 cũng có conclusion CANCELLED sau khoảng 30 phút; không suy ra lỗi nghiệp vụ cho từng lượt chỉ từ trạng thái này.

**Chẩn đoán:** [job main validate 138475900001](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45402915855/job/138475900001) chạy từ 13:23 UTC; D.3 bắt đầu 13:51:58 UTC, bị hủy 13:53:23 UTC (log cancellation/TaskCanceledException, không phải assertion SQL FAIL). Các bước 1–71 thành công, ngoại trừ diagnostics startup chỉ chạy khi startup fail (SKIPPED đúng điều kiện). Bước 72 D.3 CANCELLED, 73 upload browser evidence SUCCESS, 74 `npm run test:pwa-build` SKIPPED, 75 upload test evidence SUCCESS. Artifact đã upload không đồng nghĩa D.3 hoàn tất hoặc cleanup của runner hoàn tất. [Job PR 138458742273](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45397979755/job/138458742273) chạy D.3 từ 13:19:26 đến 13:22:43 UTC và các bước 72–75 đều SUCCESS.

**Thay đổi:** duy nhất budget job `validate` từ 30 → 45 phút (+15 phút); giữ job/check name, Windows runner, event triggers, pins, mọi step/test/condition, số lượng tối thiểu test, SQL oracle, audit, browser, PWA và hai upload `always()` nguyên vẹn. Budget mới lớn hơn ~1.55 lần lượt thành công 28m59s; không phải SLA hay chứng minh CI đã ổn định. Chọn tăng budget trước khi cân nhắc tách job; không retry tự động, không continue-on-error, không làm nhẹ gate. Không đổi script runner, scratch/nonce/cleanup hoặc nguồn nghiệp vụ. Không đưa VerifyAcceptance/F09 All vào danh sách gate vốn có.

**Kiểm nguồn:** đối chiếu workflow trước/sau, hoàn nguyên đúng đoạn comment/budget mới cho kết quả byte-for-byte bằng text nguồn đã đọc; không thêm/xóa/đổi thứ tự step, tên hay artifact path. Đây là kiểm tra tĩnh, không phải execution Windows, YAML parser độc lập hoặc CI PASS.

**Điều kiện còn mở tại thời điểm 24/11/2026 (lịch sử, trước các PR tiếp theo):** PR final HEAD phải có APP-00A SUCCESS, kiểm step 72–75 đầy đủ, artifact và source/workflow SHA thực tế, cleanup đúng ownership/nonce; giữ toàn bộ regression và independent review. Sau merge cần một lượt main hoàn tất với budget mới trước khi close .0; không đánh dấu hoàn tất theo lượt PR cũ. Lỗi LocalDB login timeout riêng tại [B.3 run 45421920678](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45421920678) trên main roadmap là lỗi bootstrap, chưa có kết quả SQL test; không coi việc tăng budget APP-00A là bản vá B.3. Không thay đổi PREPROD/EV/P0/Pilot hay phạm vi nghiệm thu. Trạng thái tại biên nhận lịch sử `SOURCE_CANDIDATE / CI_PENDING`.

<a id="app05-0-closeout"></a>

#### Biên nhận closeout APP-05.0 — 25/11/2026

**Disposition: `MERGED / MAIN_CI_VERIFIED / SYNTHETIC_NONPRODUCTION_CI_ONLY`.** Phạm vi của .0 là ổn định budget, điều phối CI, bootstrap/diagnostics và giảm công việc lặp mà không bỏ kiểm chứng bắt buộc. Chuỗi PR nguồn đã merge; tiêu chí full CI trên main đã được đáp ứng trên merge cuối #149. Đây là closeout cho increment CI, không phải kết luận mọi failure gián đoạn đã được chữa.

| PR sở hữu | Thay đổi đã merge | Reviewed source HEAD | Merge commit | Biên nhận APP-00A push main tại từng merge |
| --- | --- | --- | --- | --- |
| [PR #144](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/144) | Timeout 30 → 45 phút, giữ các step | [`ba219e5`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/ba219e5151ffb143bf580904e4ee0737b3f2b24d) | [`7aada55`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/7aada55d0889c4fd6d025816c76e7e4a83cd4102) | [45433288644](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45433288644) SUCCESS attempt 1; tổng 9/10 workflow (B4 SQL bootstrap FAIL [45433289084](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45433289084)) |
| [PR #145](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/145) | 4 Windows worker song song, `validate` fail-closed, artifact riêng | [`c0952c9`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/c0952c9bf27a35dfe5ee8e4fe1bd5b0095b34b70) | [`dfa5937`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/dfa59378c871cbe8fcfcdaaf92c5f3bbdd5bcefd) | [45445278098](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45445278098) SUCCESS attempt 1; tổng 10/10 workflow, B5 SQL cần attempt 2 |
| [PR #146](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/146) | Concurrency 16 workflow; hủy PR cũ cùng PR/workflow, giữ push main/manual độc lập | [`54484a3`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/54484a3ed9d705a17e84969dfb3d9f09151c245f) | [`ac2ca0d`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/ac2ca0d5f0f28b14cb7bc186ae3b8628b7a9bc59) | [45499265711](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45499265711) SUCCESS attempt 1; tổng 9/10 workflow (B4 SQL bootstrap FAIL [45499265441](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45499265441)) |
| [PR #147](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/147) | Bootstrap LocalDB tường minh, bounded, diagnostics có provenance; không retry business assertion | [`9c68b3b`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/9c68b3b2d0c8c8ff55b02e9d12e7f8325ba25a04) | [`a7c8dc9`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/a7c8dc970d6aa15c9a3264b83c4c2e0f49a1434c) | [45514041244](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45514041244) FAIL (core test và distribution bootstrap, validate từ chối); tổng 8/10 workflow, B8 bootstrap FAIL [45514041187](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45514041187) |
| [PR #148](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/148) | Reuse Release cùng job có guard; NuGet RUNNER_TEMP; không giữ archive cache; diagnostics/redaction .4/.5 | [`a5cf6fb`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/a5cf6fb3954ff664759ace69efcade794329f6c6) | [`bcdc5f5`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/bcdc5f54dc51e7201ce265fcb900b582ebdc8500) | [45541831309](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45541831309) SUCCESS attempt 2; tổng 10/10 workflow, 24/24 job sau rerun web_sales/validate |
| [PR #149](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/149) | Classify PR tài liệu theo 16 đường dẫn chính xác, receipt và gate; main luôn full | [`1ead72c`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/1ead72c3c1caf789e18ff3e0b4a6dbd4a7072312) | [`f7d13a5`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/f7d13a5a9f761055784cdd98ab4c8a75357e6a0b) | [45556640479](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479) SUCCESS attempt 1; tổng **10/10 workflow, 25/25 checks**, không rerun |

Các hồ sơ thiết kế/đo riêng được giữ nguyên: [parallel CI](../roadmap/app-00a-parallel-ci.md), [concurrency](../roadmap/pr-ci-concurrency.md), [LocalDB bootstrap](../roadmap/localdb-ci-bootstrap.md), [build/cache](../roadmap/app-00a-build-cache.md), [docs-only classifier](../roadmap/docs-pr-ci.md). Chứng minh concurrency A → B nằm tại [45496331271](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45496331271) (superseded, CANCELLED) và [45496489462](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45496489462) (16/16 workflow, 32/32 job PASS attempt 1); việc hủy PR cũ không phải mất acceptance của HEAD mới.

**Final source và final main (điều kiện đóng .0):**

- Reviewed PR #149 source `1ead72c3c1caf789e18ff3e0b4a6dbd4a7072312`, base `bcdc5f54dc51e7201ce265fcb900b582ebdc8500`, tested workflow SHA `ad39fd694a5ca8b5b8f6ecfbd135d24bc0ec5779`; tree `f89f581a17c442c07372e30e511b82b924f9f695` giống merge thật. [Independent review](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/149#pullrequestreview-6052518768): không còn finding mới cần xử lý; local 60/60 Python tests, actionlint 1.7.12, immutable-business, business/link, E-SIM và diff whitespace PASS. Đối chiếu 120 worker steps và 15 workflow khác giữ nguyên so với #148.
- Final PR APP-00A [45550830467](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45550830467) SUCCESS attempt 2 (core_sql, distribution và validate rerun; giữ các worker đã PASS từ attempt 1); 10/10 workflow, 25/25 latest checks. Probe [PR #150](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/150) / [45553846784](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45553846784) SUCCESS attempt 1, closed without merge: classifier/validate PASS, 4 runtime workers SKIPPED với runner/steps rỗng, 19/19 companion jobs PASS. Receipt ghi `runtime workers NOT_RUN`, không phải runtime verification.
- Merge `f7d13a5a9f761055784cdd98ab4c8a75357e6a0b`, source/workflow SHA của push main đều đúng merge SHA; APP-00A [45556640479](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479) chọn `full / non_pull_request`. [Biên nhận post-merge đầy đủ](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/149#issuecomment-7154436463) xác nhận cả 6 APP-00A jobs và 19 companion jobs SUCCESS attempt 1; bốn worker thực thi đủ các bước, chỉ skip diagnostics startup theo điều kiện không có failure. `validate` 60/60 Python tests PASS, kiểm receipt và đủ worker success. Không hạ assertion, audit, SQL oracle, test selection hay gate; không tạo thêm runtime skip trên push main.
- Core 355/355 .NET tests, 0 failed/skipped, TRX/coverage và oracle A.4 24/24 nhóm, 60/60 checkpoints, 1440/1440 SQL cells; web/C/F, APP-03 và D.1–D.3 hoàn tất. Đủ 8 artifact APP-00A đúng merge SHA; [classification ZIP](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479/artifacts/14996241857) và [core ZIP](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479/artifacts/14996635570) đã đối chiếu byte count, SHA-256, CRC; kiểm nội dung core có trọng tâm A11-09, không tuyên bố audit toàn bộ nội dung archive runtime khác. Guard/ownership/nonce và cleanup giữ phạm vi synthetic scratch. `VerifyAcceptance`/F09 All vẫn không được coi là gate APP-00A baseline.
- GitHub branch metadata tại lần kiểm tra merge #149 không có enforced branch protection; ghi **hosted CI verified**, không ghi PROTECTED CI VERIFIED. Không thay rule, permission hay bypass để lấy PASS.

**Số đo thời gian chờ (elapsed quan sát, không phải SLA):**

| Mẫu | Elapsed |
| --- | ---: |
| Final PR #144 serial, [45424683631](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45424683631) (budget 45 phút) | 30m07s |
| Main #145 parallel, [45445278098](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45445278098) (tới aggregate cuối) | 11m42s |
| Final PR #148, [45540281582](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45540281582) (run creation → job cuối; attempt 1, 25 lần reuse) | 9m53s |
| Main #149 full, [45556640479](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45556640479) (run creation → job cuối) | **10m54s** |
| Docs probe #150, [45553846784](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45553846784) (run creation → job cuối) | **37s APP-00A / 2m13s toàn bộ 10 workflow** |

Mục tiêu thử nghiệm full 10–14 phút đã quan sát được trên các lượt thành công; các mốc bắt đầu/kết thúc của mẫu cũ có khác biệt nhỏ, không dùng để công bố phần trăm tiết kiệm chính xác. Main #149 dùng 2147 runner-seconds cho 6 job APP-00A; song song giảm thời gian chờ, không đồng nghĩa giảm runner cost. Final PR #149 có 24m50s từ run creation đến validate cuối do lỗi/rerun; không loại failure khỏi lịch sử hoặc coi riêng thời gian attempt cuối là toàn bộ thời gian chờ. Cache archive NuGet không được chọn vì lợi ích sau overhead chưa đủ rõ; chỉ giữ RUNNER_TEMP và build reuse đã kiểm chứng. Không tự mở rộng allowlist sang roadmap/authority/evidence.

**Nợ và giới hạn vẫn mở:**

- Native LocalDB SQL 5173 / Event 528 chưa có nguyên nhân gốc; [LAB-E03/#75](../roadmap/localdb-ci-bootstrap.md) vẫn có investigation riêng. Các lượt bootstrap FAIL và core test FAIL ở các merge trung gian giữ nguyên disposition lịch sử; lượt main #149 xanh không hồi tố chúng thành PASS. Bootstrap guard bounded và diagnostics không tự sửa system DB, không retry assertion.
- PR #148 ghi các lỗi chậm request ở APP-03.4/.5, D.1 và C.6 HttpClient 3s ở main attempt 1; PR #149 giữ lỗi A11-09 restart/outbox tenant-b và D.2 authenticated session GET (3s) trước acceptance ở attempt 1. Trong main #149 A11-09 PASS 48/48 SQL cells mỗi tenant và D.2 PASS ngay attempt 1; không tái hiện không chứng minh root cause đã được sửa. Giữ negative evidence và chẩn đoán theo source/run/attempt; không thêm retry tự động, không nới business assertion.
- Không close LAB-E03/#75, không chấp thuận thêm security/operations debt hoặc thay trạng thái nghiệm thu APP-02B PREPROD, actual EV, production P0, Pilot/Go-Live, physical/owner-device hay scoped 125% exception. Closeout CI không chứng minh một ca APP-05 trên laptop đã thực hiện.

<a id="app05-1-closeout"></a>

#### Closeout APP-05.1 — 25/11/2026

[PR #152](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/152) đã merge lúc 15:21 ICT theo yêu cầu Owner. Source cuối `6497469f68160f11d1e8d2787263b60aba2dbd36`, merge `6938a7b6f25af575e8a49bd10212ac60780f55a7`, cùng tree `e758169a5d745d7b4abeae357a6c6703bc6cf0fc`. Review độc lập đã chỉ ra khoản credit F08 đang giữ chỗ. Bản sửa và regression API/SQL được review lại trên diff.

Final-source CI đạt 10/10 workflows và 25/25 checks. [APP-00A run 45571418949, attempt 3](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45571418949/attempts/3) giữ nguyên source khi chạy lại `web_sales` sau lỗi khởi động LocalDB 5178 và `NativeProcessTimeout`. [Artifact APP-03](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45571418949/artifacts/15003585247) ghi readiness `259e8535614c45aeb9adab58f6331b80` đạt 61/61 và upgrade `768ee72139794679a9a301be58853c1c` đạt. Lượt local trên source cuối `a1259c50a1ac4bfeaaab5dff5e52305c` và `4240af7dc57c4acea3834880225947f5` cũng đạt.

[APP-00A trên main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45575522246) chạy riêng trên merge commit. Attempt 1 đạt job `app03`, gồm hai bước APP-05.1. Job `distribution` dừng khi request dựng fixture D.3 vượt timeout HTTP 3 giây. Failed jobs được chạy lại trên cùng merge commit.

<a id="app05-3-closeout"></a>

#### Closeout APP-05.3 — 25/11/2026

**Disposition** `MERGED / MAIN_CI_VERIFIED / SYNTHETIC_DEV_ONLY`.

- [PR #154](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/154) merge lúc 18:54 ICT. Final source `6a87bb9e835cbf632f86b7670b5d5c0096b5099b`. Merge `ad5ed70ca238aeca35fedb9b6c60ea65efa823e6`. Hai finding review đã được sửa về giữ nguyên lệnh UNKNOWN qua 403 và kiểm ACK khớp nguồn trước khi xác nhận.
- Final source đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2 sau lỗi khởi động LocalDB của distribution ở lượt đầu. [CI final source](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45602682722).
- Merge trên `main` đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2. Lượt đầu hết thời gian chờ dòng báo cáo trong ca Chromium APP-03.7 accounting, trước bước .3. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45608725142).
- Artifact `VerifyASuccessor` trên đúng merge `ad5ed70`, tree `9c28082d163920ef09c527fd42ec2583c963e8a3`, lượt `6066ca8d2587419397b7dea75408b1ea` đạt Chromium 4/4 và SQL 75/75, zero diff. SHA256 `run.json` `46E222450689706BD29E0EF536131B2DEE93B5E3BFC9004316F93AF9218B21A6`.
- Kết quả thuộc LocalDB synthetic DEV. Reserve phần chưa giữ chuyển sang .4a. Nạp/giao kế nhiệm thuộc .4b. Nghiệm thu ca tích hợp của Owner tại .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

<a id="app05-4a-closeout"></a>

#### Closeout APP-05.4a — 25/11/2026

**Disposition** `MERGED / MAIN_CI_VERIFIED / SYNTHETIC_DEV_ONLY`.

- PR #155 merge lúc 21:16 ICT. Final source `903ac58bc7da41c13291bdbbb407c34f2134f621`, merge `4cf8ee89754ae44b87f33b52df4cf9801f683dce`. Review độc lập đã kiểm lại phạm vi thao tác Kho, replay sau mất ACK và nguồn lô FEFO. [PR #155](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/155).
- Final source đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2 sau khi lượt đầu thiếu bằng chứng hoàn tất hai ca C.6 DEV-07/A và DEV-08/A trong `sql-review.json`. Job APP-03 đã đạt ở attempt 1. Lượt `e0e32507f72f491aa84a4264e031d499` trên test merge `92c685d87cdb79c576ffe51a5be91bf212a43b2f` đạt Chromium 4/4 và SQL 109/109, zero diff. SHA256 `run.json` `5F4F42D33A2CF2268470C55E080DFBE383629EE0397FDC42876A9264D6C1EF98`. [CI final source](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45622795218).
- Commit merge trên `main` đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 1. Artifact `VerifyASuccessor` ghi đúng merge `4cf8ee8`, tree `6b4afffd11c7013d994e2e62a3813c145f38cd55`, lượt `e01bd57f420d4b5386efac6c95ed2bed`, Chromium 4/4 và SQL 109/109, zero diff. SHA256 `run.json` `C8DD5265F08B50DC342FC306A18561E08E02CDF1EBBDC67DC395838AD0C51064`. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45632314509).
- Kết quả thuộc LocalDB synthetic DEV. Nạp, giao liên chuyến và báo cáo toàn dòng chuyển sang .4b. Nghiệm thu ca tích hợp của Owner thuộc .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

<a id="app05-4b-closeout"></a>

**Closeout APP-05.4b — 26/11/2026**

- PR #157 đưa báo cáo AR toàn dòng lên `main`. PR #159 mở nạp và giao phần Reserve mới trên chuyến kế nhiệm. PR #160 mở nạp lại HELD có nguồn, giao tiếp và hoàn tất việc A khi đã giao đủ. Cả hai PR nạp/giao đã được rà soát độc lập trên source cuối.
- PR #159 source `12d772f769012af9b51b39ed894886208134de1b` đạt 10/10 workflow và 25/25 checks, APP-00A lần 3. Merge `4eba6a48d479babfeac7c52a1382ae139a494ca0`. [CI PR #159](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45730253618).
- PR #160 source `7335a9433c256a4a73ab2a57da8a3c25036e7274` đạt 10/10 workflow và 25/25 checks, APP-00A lần 4. Ba lượt trước dừng ở C.6 khi client HTTP chạm 3 giây trước khi đủ bằng chứng. Lượt cuối chạy lại cùng source và giữ đầy đủ gate. Merge `0f3f47150bb77f5ed6c2765c2385570b455a94fa`. [CI PR #160](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45731497374).
- Artifact kiểm chứng từ test merge `03c02e1eeac1eede4e1958877e84e52c931d5579` có cùng tree `546d533ec9b117ebffafaad0380995c9ade70191` với merge #160. Lượt `c3016cf2a4774939830a36e39cc69356` đạt Chromium 4/4 và SQL 118/118, không có chênh lệch. Đã đối chiếu 40/25/15, 5/4/3/1/2, lần giao cạnh tranh trên cùng manifest, nguồn lot/cost, tiền lũy kế, thời hạn công nợ theo ngày giao, công việc A và tenant đối chứng. SHA256 `run.json` `58912A8F9ED468EA9278628F30A8432FE2F882042FCC093760F3B3B7711D5BEE`.
- C.6 chạy lại local trên đúng source PR #160, lượt `993806f3592145eea7eba87dc88079c2` đạt 15/15 ca. SHA256 `sql-review.json` `4ED516127F9651C33BECD458DF55B6D63659077D1EA1E13570528A4FAE46B80D`. Kiểm chứng local và retest hosted ghi riêng.
- Job APP-03 trên đúng merge #160 đã đạt. Artifact `5b37701a6d3c433b8062f0581543587a` ghi source `0f3f47150bb77f5ed6c2765c2385570b455a94fa`, Chromium 4/4 và SQL 118/118, không có chênh lệch. SHA256 `run.json` `F3D06A7F486457B3041F0BEC30EF1623F129366AD1E62AEAEF6CB354AA52D303`.
- Gate tổng trên `main` chạy riêng. Lượt 1 và 2 thiếu bằng chứng C.6 sau timeout HTTP 3 giây. Link CI ghi các lượt chạy lại trên cùng commit. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45736671361).

Phạm vi nghiệm thu là synthetic DEV cho chuyến kế nhiệm hợp lệ. Phần đã xếp lên chuyến kế nhiệm quá ngày cần đường phục hồi có nguồn riêng. Báo cáo D.4 và công việc kế toán giữ mốc xác nhận độc lập. Triển khai tenant, Pilot và nghiệm thu vận hành theo mốc riêng.

<a id="app05-5-closeout"></a>

#### Closeout APP-05.5 — 26/11/2026

- PR #161 đưa đề nghị đóng có nguồn lên `main`. Source `60a4b392b6008059afb66650778f74270ff4f77f`, merge `cc952290135e69dfc473e472b75c943e563b7413`. Review vòng hai xác nhận đã xử lý P2 #4703316778. CI final HEAD đạt 10/10 workflows và 25/25 checks, APP-00A attempt 1. Artifact PR `f6545fc664b649cf80e2c307d318a07c` trên tree `afba63f307edeafe98c87202b6d12811d15ec8d0` đạt Chromium 4/4 và SQL 121/121, zero diff. Ca lệch revision giữ nguyên yêu cầu để tra, ca mất ACK hợp lệ tìm lại đúng biên nhận. SHA256 `run.json` `ACDD1591ACCA52B0AA892DF81C4A0BDDF4E440164C73D99ED0ABE5503006E370`. [CI PR #161](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45745518237).
- PR #162 đưa duyệt độc lập, đóng nghĩa vụ chưa giao và giải phóng HELD theo quyền/lô lên `main`. Source `8b545e27fefb3a36793c1f272887fa604e2b07d0`, merge `a06f92a18cf04bacbe1a3734497eb913412a6290`. Review vòng hai xác nhận đã xử lý P2 #4703317026 và #4703317033 cùng sửa biên nhận kế thừa. Sau retarget, base mới có cùng tree với source #161, và tree merge #162 `0a999279fabff4dee28d88cb89c4275d6124cad3` trùng tree đã kiểm chứng. CI final HEAD đạt 10/10 workflows và 25/25 checks, APP-00A attempt 2 sau timeout D2-10/B. B.3 đạt sau khi chạy lại bước kiểm LocalDB. [CI PR #162](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45746608720).
- Lượt PR hosted `d29f2bb23aa443309273a7afd560039f` đạt Chromium 6/6 và SQL 139/139, zero diff trên tree cuối. Ca kiểm xác nhận duyệt và xếp chuyến chỉ có một kết quả hiệu lực. Các ca duyệt đua Transfer, Reserve, C.4 intake mới và C.4 replay trả từ chối nghiệp vụ cho lệnh duyệt khi nguồn chưa đủ điều kiện. SQL đối chiếu bảy mã duyệt bị từ chối và xác nhận on-vehicle, RECEIVED và PENDING không tạo approval effect. Ca duyệt hợp lệ giải phóng đúng HELD, còn đúng 0,001 trước lần duyệt cuối, giữ Confirm, lần giao, tồn vật lý, AR, COGS và tenant đối chứng. SHA256 `run.json` `E4984C85F315BD7D1E75EC24D049C19B370AAB4EE5B418CFDF4C34303D7233`.
- Job APP-03 trên đúng merge `a06f92a18cf04bacbe1a3734497eb913412a6290` đạt. Artifact `a8027fca9fcc499c95d17799e9747b95` ghi Chromium 6/6 và SQL 139/139, zero diff. SHA256 `run.json` `32051D45FD307E202B99BE7A7D8EF201FCE2803129C9393EE40AC184C0C43C60`.
- Gate tổng trên `main` chạy riêng trên cùng merge. Lượt đầu C.6 thiếu bằng chứng DEV-02, DEV-04, DEV-07 và DEV-08 sau timeout HTTP 3 giây. Link CI ghi các lượt chạy lại trên cùng SHA. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45751699716).

APP-05.5 hoàn tất trong phạm vi triển khai synthetic DEV. Bàn công việc, phục hồi từ browser mới và nghiệm thu trọn ca giữ mốc APP-05.6/APP-05.7. Tiếp tục theo dõi bootstrap LocalDB và timeout HTTP ở CI nền. Nghiệm thu tenant, Pilot và vận hành thực tế theo mốc riêng.

<a id="app05-6-closeout"></a>

#### Closeout APP-05.6 — 26/11/2026

- PR #165 đã merge vào `main`. Source `52672fae4f40279ba9b620a7b430778d9c0dc1e9`, merge `a7883ab6020c43e67d1771bca656e73a9a0eaa9b`. Tree `2f1026df21527771e54619635389514d56bbb014` trùng source, PR integration và merge. Review độc lập trên source cuối xác nhận xử lý phạm vi từng vai, truy vết C.3/C.4, bằng chứng nguồn hoàn tất và liên kết snapshot. Browser kiểm từng vai riêng. [PR #165](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/165).
- CI final HEAD đạt 10/10 workflow và 25/25 check ở lượt đầu. Hosted integration `7da95d2bfcb7a6c3ca081784baebc488c43a5a02` có `VerifyASuccessor` run `adf6776fbe2c4b10a5f73a7d5afd84e5` đạt Chromium 6/6, SQL 140/140, zero diff. SHA256 `run.json` `C31555B3AD712F2B291B9FD4937DAEDB27E20DB77CBCE84D454BFEC67FC4FF49`. `VerifyCloseReadiness` run `ef07c5955275432881eede963a22355a` đạt Chromium 1/1 và SQL 67/67, zero diff. [CI PR #165](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45759741381).
- Local trên source cuối đạt xUnit/MTP 398/398, web tests 15/15 và build. Hai runner Chromium/LocalDB `997758a59f274294a2316d1f5d9416ce` và `13fccd01ecbb4ba5a82db5f6b6822606` đạt cùng số ca và đối chiếu SQL. Cả hai đã Remove phiên scratch theo nonce.
- Job APP-03 trên đúng merge `a7883ab` đạt ở lượt đầu. Hosted run `1a8e386eb2284e5f8f341dfa1c6efe9a` đạt Chromium 6/6 và SQL 140/140, zero diff. SHA256 `run.json` `DEEA186FE6887391DFA896CE5E570DD055E18278B86271DEA99F42E605AB6D19`. Readiness run `0a6f09e0becc449cad593cb6ecf49960` đạt Chromium 1/1 và SQL 67/67, zero diff.
- Gate tổng trên merge đạt 10/10 workflow và 25/25 check ở lượt 4. Lượt đầu D1-09/A và bước nâng cao của C.6 hết timeout HTTP 3 giây. C.6 tiếp tục hết hạn ở lượt 2 và 3. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45761753522).
- PR #166 xử lý timeout lặp lại ở bộ thu thập bằng chứng C.6: HTTP probe sau Chromium có deadline 15 giây để nhận kết quả từ đường nghiệp vụ có thể chờ khóa SQL 10 giây. Startup/health giữ deadline riêng. Budget áp dụng cả client khôi phục và client cạnh tranh. Runner ghi method, path và thời gian khi nhận header sau 3 giây hoặc khi bị hủy. Probe loopback trả HTTP 409 sau 3,6 giây tái hiện timeout cũ và xác nhận client mới nhận đúng 409. Giữ ma trận, SQL oracle, quyền và yêu cầu về số lượng bằng chứng. [PR #166](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/166).

Bằng chứng APP-05.6 áp dụng cho synthetic DEV. APP-05.7 sở hữu nghiệm thu ca tích hợp. Nghiệm thu tenant, Pilot và vận hành thực tế theo mốc riêng.

<!-- DOC-CI-02C frozen Light probe #173: descriptive text only; DO NOT MERGE; no status or acceptance change. -->
