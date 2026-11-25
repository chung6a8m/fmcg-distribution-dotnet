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

## Thiết kế và phạm vi

1. Chỉ cache **NuGet global-packages** của bốn worker APP-00A, đặt dưới `RUNNER_TEMP` để không làm bẩn checkout. Một step sau checkout ghi `NUGET_PACKAGES` vào `GITHUB_ENV` cho các step kế tiếp; không dùng context `runner` ở job-level `env`. Dùng `actions/cache/restore@v6` và `actions/cache/save@v6`; key có schema, OS, architecture, `windows-2022` và hash các đầu vào SDK/solution/project/props/targets/NuGet config/lock. Không dùng restore prefix. Save chỉ sau job thành công và khi chưa exact-hit; dùng lại primary key của restore.
2. Luôn chạy `dotnet restore`, build Release solution và toàn bộ test/evidence. Cache không chứa `bin`, `obj`, database, receipt, TestResults, frontend dist hoặc browser output. Repo hiện không có NuGet lockfile; thay đổi này không đổi chính sách resolution bằng cách thêm lockfile chỉ để bật caching.
3. Ba browser worker dùng lại **Release build vừa tạo trong cùng job**. Chỉ sau build thành công mới bật `FMCG_CI_PREBUILT_SHA` bằng SHA checkout thực tế. Hai launcher kiểm tra CI Windows, source sạch và trùng marker, đủ Demo/API DLL và runtime metadata, cùng ProductVersion đúng SHA. Kiểm cả bản API DLL được copy vào output Demo vì Demo nạp trực tiếp dependency này, ngoài bản API chạy server ở output API. Opt-in không hợp lệ phải fail trước khi start LocalDB/ứng dụng; thiếu opt-in giữ build mặc định. Marker kế thừa tới `NextDay`. Nhánh `Stop` độc lập không import helper hay cần checkout.
4. Giữ nguyên bốn worker, `validate`, concurrency, bootstrap LocalDB, scripts scenario, floors, audits, nonce/ownership và source evidence. Không đổi business/application/C# witness hoặc cách phục vụ Vite.
5. Chưa đưa npm, Chromium hoặc compiled artifact chia sẻ giữa job vào cache. Chi phí install hiện nhỏ; chưa có bằng chứng transfer/extraction thắng chi phí đó. Không thêm job prebuild nối tiếp làm thay đổi đường găng. Các workflow B tiếp tục như cũ; bước này ưu tiên APP-00A đang quyết định thời gian chờ.

NuGet hỗ trợ `NUGET_PACKAGES` để chọn global-packages: [Microsoft](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders). Hợp đồng restore/save và cache-hit: [actions/cache](https://github.com/actions/cache). Cache PR thuộc merge ref, không tự làm ấm main hay PR khác: [GitHub cache scope](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#restrictions-for-accessing-a-cache). Playwright lưu ý restore browser cache thường tốn tương đương tải: [CI guidance](https://playwright.dev/docs/ci#caching-browsers).

## Kiểm chứng và tiêu chí giữ thay đổi

- Regression thực thi kiểm tra opt-in vắng mặt, build failure, source bẩn/sai SHA, thiếu output, output lẫn commit, actual merge SHA, nested invocation và Stop độc lập; fixture chỉ mô phỏng ranh giới process, không tuyên bố thay thế Windows/SQL acceptance.
- actionlint, Python aggregate-gate tests, business validators và kiểm tra diff bảo đảm mọi command/env/condition/evidence cũ vẫn có mặt.
- Chạy toàn bộ CI ở final source. Đo lượt cache miss và lượt exact hit trên cùng source/key với runner mới. Ghi run/attempt, reviewed head, checkout SHA, runner image, SDK, cache key/hit/size, restore/install/save elapsed, job elapsed, workflow wall và tổng runner time.
- So sánh **cache restore + dotnet restore + cache save**, kể cả chi phí miss/save; không chỉ so riêng dotnet restore. Kiểm tra log không còn 25 build lặp và ghi chi phí guard. Các suite và audit phải còn chạy thật.
- Chỉ giữ cache khi warm-hit chứng minh lợi ích sau overhead. Nếu kết quả gần nhiễu, không công bố tỷ lệ ổn định; đo thêm có giới hạn hoặc bỏ phương án không có ích. Phân biệt benchmark rerun với rerun do LocalDB lỗi, lưu negative evidence và không thêm retry tự động.
- Biên nhận thực thi cuối ghi tại PR body, không tạo commit chỉ để ghi lại CI vừa chạy. Chưa merge không suy ra main đã được kiểm chứng. Vấn đề native LocalDB đang điều tra không được coi là đã giải quyết bởi cache.

Kế hoạch: [2026-11-25-app00a-build-cache](../superpowers/plans/2026-11-25-app00a-build-cache.md).
