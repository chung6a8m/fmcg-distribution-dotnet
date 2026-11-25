# APP-00A — Đo cache package và dùng lại build trong job

Ngày quyết định: **25/11/2026**. Owner yêu cầu thực hiện mục 5 trên `main` sau PR [#147](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/147), source `a7c8dc970d6aa15c9a3264b83c4c2e0f49a1434c`. Phạm vi **SYNTHETIC_NONPRODUCTION / CI**.

## Căn cứ đo trước khi sửa

Lượt xanh [APP-00A 45509913554, attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45509913554) trên source PR #147 `9c68b3b2d0c8c8ff55b02e9d12e7f8325ba25a04` có cùng Git tree với merge trên. Workflow hoàn tất trong **645s (10m45s)**; bốn worker cộng **2.107 runner-seconds**, cộng thêm 5s cho `validate`. Đây là elapsed quan sát, không phải hóa đơn.

| Worker | Tổng job | Restore NuGet | Build solution | npm ci | Chromium | Build Demo lặp: số lần / tổng MSBuild elapsed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| core_sql | 411s | 38s | 29s | — | — | 0 |
| web_sales | 575s | 36s | 28s | 10s | 17s | 14 / 22,89s |
| app03 | 616s | 49s | 25s | 10s | 12s | 8 / 10,98s |
| distribution | 505s | 38s | 19s | 6s | 17s | 3 / 3,17s |

`app03` quyết định đường găng; 37,04s build Demo lặp là **tổng trên ba worker**, chỉ 10,98s nằm trên đường găng này. Lượt test .NET ghi 355 test, failed 0, skipped 0. Có 24 lời gọi launcher trực tiếp và một `NextDay` lồng bên trong APP-03 Verify. Frontend chỉ build production một lần; các scenario C/D/F/APP-03 dùng Vite dev server như thiết kế hiện hành.

Kết hợp các phase đã hoàn tất của [lượt main 45514041244](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45514041244) cho 7 mẫu restore thành công: median 38s, khoảng 35–49s; build median 28s, khoảng 19–30s. Năm mẫu npm ci: 6–10s; Chromium: 12–17s. Job distribution của main lỗi bootstrap trước restore, được loại khỏi số đo hiệu năng. Một lượt xanh và vài phase chưa đủ xác lập SLA hay tỷ lệ cải thiện ổn định.

## Thử nghiệm và quyết định chọn phương án

Candidate `fc2d79f443f489f2293b8ab66861e613c54cd569` đã thử cache NuGet global-packages dưới RUNNER_TEMP bằng restore/save v6, exact key theo OS/architecture/runner family và SDK/dependency inputs. Mọi restore/build/test vẫn chạy. [Run 45518899130, attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45518899130/attempts/1) xanh 5/5 job; toàn bộ 10 workflow có 24/24 job xanh. APP-00A mất **643s**, gần bằng baseline 645s; `web_sales` 610s là đường găng của mẫu này, `app03` 582s. Không suy ra cải thiện wall time ổn định từ chênh lệch 2s.

Lượt cold ghi **25 lần dùng lại build, 460ms tổng kiểm tra guard, không còn build Demo lặp**; đủ 355 test .NET, 39 regression prebuild, 15 regression aggregate và toàn bộ scenario/witness gốc đều qua. Số 37,04s build lặp ở baseline và 460ms guard là các phần công việc quan sát được ở hai lượt; không coi hiệu số đó là mức giảm wall time toàn workflow.

Cache đầu tiên chứa **215.779.690 byte (~205,78 MiB)**, lưu ở core trong khoảng 5–6s. Ba worker còn lại tốn 2–3s nén trước khi gặp xung đột lưu cùng key; các step vẫn thành công. Cả bốn lượt đọc cold đều miss.

Chạy lại riêng APP-03 trên cùng reviewed head, cùng checkout `a8f7b2af0b00a8ac50c067924aec94af0e597764`, key và Windows image `20261116.370.1` đã xác nhận exact hit. Các bước chuẩn bị đã hoàn tất cho số đo sau:

| APP-03 | Cold attempt 1 | Warm attempt 2 |
| --- | ---: | ---: |
| Đọc/giải nén cache | 1s, miss | 4s, exact hit |
| dotnet restore | 13s | 8s |
| Save cache | 3s, cùng key đã tồn tại | 0s, skipped |
| Tổng ba bước | 17s | 12s |
| Build solution, ghi riêng | 29s | 18s |

Tải cache + restore mất 12s, chỉ thấp hơn **1s** so với riêng restore 13s khi cache trống. Một cặp phase như vậy chưa chứng minh lợi ích đáng kể sau overhead và biến động runner/network. Không gán chênh lệch build 29→18s cho cache. Restore 12–15s của bốn worker cold cũng không phải lợi ích cache-hit, vì tất cả đều miss; đường dẫn package khác baseline và biến động môi trường chưa được kiểm soát riêng.

**Giới hạn của warm:** [job 138891045926](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45518899130/job/138891045926) thất bại ở APP-03.5 với `HttpClient.Timeout` 3s tại session proxy request; .6/.7 bị skip và `validate` thất bại. Chỉ các phase chuẩn bị hoàn tất được dùng so sánh. Job kết thúc sớm 244s **không** là tiết kiệm hay warm acceptance; không kết luận cache gây ra lỗi. Log và artifact của cold/warm được giữ bằng ID riêng trong PR #148.

**Quyết định về archive:** giữ cơ chế dùng lại build trong cùng job; **bỏ toàn bộ archive cache NuGet thử nghiệm** vì chưa chứng minh được lợi ích đủ rõ. Quyết định này không phủ nhận cache có thể hữu ích ở workload khác; chỉ chọn phần có căn cứ cho increment hiện tại.

### Đối chiếu riêng vị trí package trong job

Candidate tiếp theo `a65a211952a76316ac9b01c407c231ffa6c03262` đã bỏ cả archive lẫn override đường dẫn. Các phase restore đã hoàn tất của [run 45522298067](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45522298067) quay về **35–39s** trên bốn worker. Đây là mẫu đối chứng bổ sung; trạng thái hoàn tất toàn workflow được ghi riêng, không suy từ các phase.

| Nhóm mẫu restore đã hoàn tất | Vị trí package | n | Median | Khoảng |
| --- | --- | ---: | ---: | ---: |
| PR #147 và main cùng tree | Mặc định, không override | 7 | 38s | 35–49s |
| 3d7d645, các browser worker; mọi cache read miss | RUNNER_TEMP | 3 | 16s | 12–23s |
| fc2d79f, bốn worker cold; mọi cache read miss | RUNNER_TEMP | 4 | 12,5s | 12–15s |
| a65a211, bỏ archive và override | Mặc định, không override | 4 | 36s | 35–39s |

Đối chiếu Git xác nhận **30 đầu vào dependency được track** (SDK, solution, project, props/targets và config/lock nếu có) giống byte giữa các source trên; các log đã hoàn tất xác nhận Windows image `20261116.370.1`. Median initial build ở các nhóm vẫn khoảng 25–29s, khác với khoảng tách biệt của restore.

Chuỗi default → temp/miss → default có khoảng restore không giao nhau, đủ căn cứ chọn **một cấu hình tối thiểu chỉ chuyển NuGet global-packages sang thư mục tạm của job**, không thêm archive. Các mẫu trong một lượt có tương quan và không được randomize; không tuyên bố nguyên nhân vật lý của chênh lệch hoặc SLA. Phải kiểm chứng lại cấu hình path-only này trên final source, giữ nguyên dependency inputs, restore thật và toàn bộ gate.

## Thiết kế được giữ lại

1. Ba browser worker dùng lại **Release build vừa tạo trong cùng job**. Chỉ sau build thành công mới bật `FMCG_CI_PREBUILT_SHA` bằng SHA checkout thực tế. Hai launcher kiểm tra CI Windows, source sạch và trùng marker, đủ Demo/API DLL và runtime metadata, cùng ProductVersion đúng SHA. Kiểm cả bản API DLL được copy vào output Demo vì Demo nạp trực tiếp dependency này, ngoài bản API chạy server ở output API. Opt-in không hợp lệ phải fail trước khi start LocalDB/ứng dụng; thiếu opt-in giữ build mặc định. Marker kế thừa tới `NextDay`. Nhánh `Stop` độc lập không import helper hay cần checkout.
2. Mỗi worker đặt `NUGET_PACKAGES` dưới `RUNNER_TEMP/app00a-nuget-packages` bằng `GITHUB_ENV` sau checkout. Package chỉ tồn tại trên worker đang chạy; không tải/lưu archive giữa các lượt. Luôn chạy `dotnet restore`, build Release solution và toàn bộ test/evidence. Không cache `bin`, `obj`, database, receipt, TestResults, frontend dist hoặc browser output. Không thêm NuGet lockfile, không đổi resolution hay bỏ audit.
3. Giữ nguyên bốn worker, `validate`, concurrency, bootstrap LocalDB, scripts scenario, floors, audits, nonce/ownership và source evidence. Không đổi business/application/C# witness hoặc cách phục vụ Vite; không tăng timeout/retry để làm lượt warm thành công.
4. Chưa áp dụng cache npm, Chromium hoặc chia sẻ compiled artifact giữa job. Chi phí install hiện nhỏ; chưa có bằng chứng transfer/extraction thắng chi phí đó. Không thêm job prebuild nối tiếp làm thay đổi đường găng. Các workflow B tiếp tục như cũ.

Tham chiếu cho thí nghiệm: [NuGet global-packages](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [actions/cache restore/save](https://github.com/actions/cache), [GitHub cache scope](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#restrictions-for-accessing-a-cache). Playwright lưu ý restore browser cache thường tốn tương đương tải: [CI guidance](https://playwright.dev/docs/ci#caching-browsers).

## Kiểm chứng và tiêu chí giữ thay đổi

- Regression thực thi kiểm tra opt-in vắng mặt, build failure, source bẩn/sai SHA, thiếu output, output lẫn commit, actual merge SHA, nested invocation và Stop độc lập; fixture chỉ mô phỏng ranh giới process, không tuyên bố thay thế Windows/SQL acceptance.
- actionlint, Python aggregate-gate tests, business validators và kiểm tra diff bảo đảm mọi command/env/condition/evidence cũ vẫn có mặt.
- Thí nghiệm miss/exact-hit ở trên được giữ như lịch sử đo, bao gồm negative evidence. Cache không đạt tiêu chí lợi ích đủ rõ nên bị loại; không chạy thêm vòng benchmark để tìm một mẫu đẹp.
- Chạy lại toàn bộ CI trên **final source chỉ dùng thư mục package tạm và build reuse, không archive cache**. Đối chiếu phase restore với các nhóm mẫu trên, đầy đủ 25 lần reuse/guard elapsed, không có build lặp, đủ scenario/audit/evidence, kể cả APP-03.5/.6/.7; một PASS tiền nhiệm không thay kết quả final HEAD.
- Ghi run/attempt, reviewed head, checkout SHA, runner image, SDK, phase elapsed, job/workflow elapsed và tổng runner time. Không gọi failed-path hoặc carried-forward job là lượt chạy nhanh hơn; không thêm retry tự động.
- Biên nhận thực thi cuối ghi tại PR body, không tạo commit chỉ để ghi lại CI vừa chạy. Chưa merge không suy ra main đã được kiểm chứng. Vấn đề native LocalDB đang điều tra không được coi là đã giải quyết bởi cache.

Kế hoạch: [2026-11-25-app00a-build-cache](../superpowers/plans/2026-11-25-app00a-build-cache.md).
