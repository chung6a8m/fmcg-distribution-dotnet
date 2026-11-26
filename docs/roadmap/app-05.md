# APP-05 — Hoàn tất ca DEV và tiếp tục việc tồn ngày sau

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Ngày chốt roadmap:** 24/11/2026. **Trạng thái:** `ROADMAP_APPROVED / APP05_0_MAIN_CI_VERIFIED / APP05_1_MERGED / APP05_2_LOCAL_DEV_VERIFIED`. Các biên nhận nằm ở mục 5. Phạm vi xác minh là synthetic DEV và CI.

**Goal:** Owner tự hoàn tất một ca trên laptop Windows với dữ liệu mẫu APP-03: xử lý A chưa giao, đóng chuyến có bàn giao trách nhiệm, rồi tiếp tục đúng đơn và nguồn vào ngày sau trên cùng database.

**Architecture:** mở rộng các command/read model C/D/F và phiên APP-03 hiện có. Thêm quyết định chốt bất biến, việc theo dõi có nguồn và liên kết thực hiện A qua nhiều chuyến; giữ một đơn, một Confirm và các sổ độc lập. Dapper dùng transaction tường minh, migration tiến trên LocalDB có ownership/nonce.

**Tech Stack:** toolchain đã pin trong repo: .NET 10.0.404, ASP.NET Core, Dapper, FluentMigrator, SQL Server LocalDB theo tenant, React/TypeScript/Vite, Node 26 LTS, Tailwind v4; xUnit v3/MTP v2 và Playwright.

**Spec:** [Thiết kế ca DEV và bàn giao ngày sau](../superpowers/specs/2026-11-24-dev-daily-close-design.md). Roadmap này là nơi quản lý thứ tự và trạng thái APP-05; spec giữ lý do, quy tắc và các phương án đã cân nhắc. Không lập thêm một kế hoạch trùng nội dung.

## 1. Quyết định của Owner và mục tiêu bàn giao

Owner đã chọn hai phương án trong phỏng vấn:

1. **“Hoàn tất một ca trên dữ liệu mẫu: xử lý phần chưa giao, đóng chuyến và tiếp tục việc tồn hôm sau”.** Tận dụng dữ liệu APP-03; luồng tự thiết lập danh mục/nhập kho/giá/phân công chưa phải mục tiêu đợt này.
2. **“Có người phụ trách và hạn xử lý; chọn chuyến giao tiếp vào ngày sau”.** Trước khi đóng chuyến cũ, việc tồn phải có người, hạn và hành động kế tiếp; chưa bắt buộc có TripId kế nhiệm.

Lệnh tiếp theo là **“Tạo roadmap theo thiết kế và đề xuất trên, commit vào repo”**. Roadmap ghi nhận hai lựa chọn này và cụ thể hóa thiết kế thành các increment review được. Các mặc định kỹ thuật, fixture và thứ tự PR là phương án triển khai của thiết kế, không được ghi thành những quyết định kinh doanh riêng mà Owner chưa trả lời. Đợt commit này chỉ lập kế hoạch; không tự thực hiện các increment.

Ba mốc người dùng sẽ nhận:

| Mốc | Đầu ra trên laptop | Increment hoàn tất |
| --- | --- | --- |
| Bàn giao cuối ca | Xem điều kiện còn thiếu; giao người/hạn; chốt sạch hoặc chốt có việc tồn; đọc lại biên bản | .1–.2 |
| Xử lý A ngày sau | Chọn chuyến, chuyển đúng phần HELD, giữ bổ sung khi đủ tồn, nạp/giao tiếp hoặc đóng phần còn bằng nguồn duyệt | .3–.5 |
| Trọn ca qua hai ngày | Mở trình duyệt mới tìm lại công việc/lệnh; lịch sử và số dư đúng qua NextDay, restart và hai tenant | .6–.7 |

APP-05 là mã mới. Các đề xuất **APP-04.1–.6** cũ tiếp tục giữ [ánh xạ sang DEV-F và APP-03](app-03.md#ánh-xạ-backlog-app-04-từng-được-đề-xuất); không đổi nghĩa hoặc triển khai lại chúng.

## 2. Hiện trạng làm cơ sở

Baseline đối chiếu ngày 24/11/2026: `main` **`29bc4bc2b4a14ad45386f57a8f6bb25a697005ee`**, tree **`313e4d85be0cfd850b9a928cf8a20159d52d2fc2`**, sau [PR #143](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/143). Khi bắt đầu code, kiểm lại main/PR mới để tránh làm lại việc đã merge sau baseline.

| Đã có | Phần APP-05 cần bổ sung |
| --- | --- |
| APP-03.1a–.9: phiên bền, dữ liệu qua ngày, workspace liên vai, acceptance synthetic DEV | Hoàn tất vòng đời chuyến sau RECONCILING và tiếp tục nghĩa vụ A của chính đơn cũ |
| D.3/.6: thu nhận tiền, trả tồn xe, A tiếp tục HELD, giải phóng quota B | Readiness và chốt có transaction; nhánh bàn giao bằng 0 cho chuyến có nghĩa vụ A nhưng chưa có manifest |
| C.2/C.3/C.4: Confirm, Reserve, Load, giao từng phần và snapshot | Quyền thực hiện trên chuyến kế nhiệm, nạp lại HELD đã trả, tổng lượng và tiền qua mọi chuyến |
| D.4/F: đối soát, trả/QA, tạm ứng, giá vốn, credit/refund có nguồn riêng | Theo dõi phần tồn có người/hạn, liên kết hồ sơ gốc, hoàn tất theo nguồn thực |
| UI recovery và APP-03 Stop/Resume | Tra lệnh và việc từ phiên trình duyệt mới, không chỉ từ sessionStorage của tab cũ |

[APP-03.9](app-03-9-acceptance.md#nghiệm-thu-cuối-pr-143) ghi lượt local trên source `d349acaa994df3e6039fc6d5b869310549ee6899`: Chromium 2/2, SQL 450/450 và F09 trên cùng source; đây là bằng chứng tiền nhiệm, không phải PASS của APP-05. Final PR head `96f3e8c0c96b57a248fcb2c458922048c5c1d2fa` và merge có cùng tree. Lượt push của baseline main có 9/10 workflow thành công; [APP-00A run 45402915855](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45402915855) bị hủy ở job giới hạn 30 phút, khi chạy D.3. APP-05.0 xử lý độ tin cậy này; không gán lỗi assertion nghiệp vụ từ trạng thái cancelled và không coi CI main đã xanh toàn bộ.

## Global Constraints

1. Giữ nguyên `business/`, `business.lock.json`, hồ sơ E đã đóng băng và bằng chứng lịch sử. Dùng [quyết định C](../decisions/owner-app02c-2026-11-19.md), [SOP A](../../business/workflows/module-a.md), [SOP Mixed](../../business/workflows/mixed.md) và [ngoại lệ kết ca](../../business/workflows/assurance-and-exceptions.md).
2. Phạm vi synthetic DEV, `invoiceEligible=false`, offline business writes tắt, B_NO_CHANNEL bị chặn. Tenant lấy từ danh tính server; kiểm grant/assignment hiện hành ở read, write, lookup và replay. Owner/assignee không tự thành superuser.
3. Giữ guard instance/name/ownership/nonce khi migrate hoặc Remove. Upgrade hai tenant trên phiên có dữ liệu, có hồi phục nếu gián đoạn; không reset để làm sạch ca kiểm. Không ghép mọi profile bằng cách bỏ startup guard.
4. Bảo toàn dòng Confirm: **đã giao + đã đóng phần còn hợp lệ + còn phải thực hiện = lượng Confirm**. HELD là phần thực đã giữ, có thể nhỏ hơn còn phải giao. Một phần giữ chỉ có một quyền dùng còn hiệu lực; không cộng các thế hệ quyền thành entitlement mới.
5. Giữ OrderId/OrderLineId/ConfirmSourceId và snapshot; thêm liên kết kế nhiệm có nguồn, không sửa `AOrder.TripId`, sao chép đơn hoặc Confirm lại. Close/xếp chuyến không sinh physical movement, AR, COGS, receipt hay quỹ. Reserve bổ sung chỉ cấp giữ từ tồn hợp lệ; nạp/giao thực mới phát sinh hiệu ứng tương ứng.
6. Giữ nghĩa riêng của ACK, business outcome, POSTED, RECONCILED và UNKNOWN. Close lưu receipt/snapshot/follow-up/outcome nguyên tử; lịch sử chốt không bị viết lại khi việc hoàn thành. V1 không mở lại quyền ghi nghiệp vụ mới trên chuyến đã đóng.
7. Clock APP-03 giữ 09:00 ICT. D.4 cần `OccurredAt < cutoff <= asOf <= now`: nguồn ngày một có thể chờ đối soát đến NextDay. Không đổi đồng hồ để ép fixture đạt, không giả RECONCILED hoặc tạo DIFF giả. Hạn việc theo ngữ cảnh DEV, múi giờ rõ và do người giao chọn; không đặt SLA thật từ fixture.
8. Dùng UI/UX baseline hiện có: Compact, Noto Sans, Light/Dark/System, controls và shell chung. Một increment sở hữu mỗi phần UI mới. PREPROD, EV/P0, Pilot, Go-Live, thiết bị thực và ngoại lệ 125% giữ [phạm vi riêng](app-02c-operations-debt.md); không thêm vòng xin phép vận hành làm điều kiện cho công việc DEV đã được chọn.

## Review Focus

| Rủi ro cần review | Test/đối chiếu quyết định | Increment sở hữu |
| --- | --- | --- |
| RF-01: chuyến đóng nhưng bàn giao thiếu, hoặc nguồn thay đổi sau preview | Close đua với thay đổi nguồn, failure trước/sau COMMIT, thiếu người/hạn, một closure/trip, cấm ghi mới sau đóng | .2 |
| RF-02: nhân đôi quyền giữ hoặc mất HELD đã trả | Hai chuyến tranh cùng nguồn; 40/25/15 và 5/4/3/1/2; transfer giữ allocated/physical/cost; Reserve mới không vượt phần chưa giữ | .3, .4a |
| RF-03: giao/đóng phần còn làm sai tiền và snapshot | Tổng lượng/làm tròn toàn dòng qua nhiều chuyến; due từ ngày giao mới + N gốc; cancel không đảo delivery/AR đã ghi | .4b, .5 |
| RF-04: nhầm số dư bình thường thành blocker/case hoặc quyền quá rộng | AR chưa đến hạn, D.4 chưa được cutoff bao phủ, D/F case trùng nguồn, assignee/Owner không có quyền command, revoked/wrong tenant | .1, .2, .6 |
| RF-05: mất kết quả sau browser mới hoặc viết lại lịch sử | Lost ACK + Stop/Resume + đóng toàn bộ browser; đăng nhập mới tìm cùng ID; upgrade giữ nguồn cũ; snapshot lúc chốt khác trạng thái việc hiện tại | .2, .6, .7 |

## 3. Danh sách increment và phụ thuộc

Mỗi hàng là một PR có thay đổi thực chất, kiểm chứng và cập nhật trạng thái trong chính PR đó. Có thể tách thêm PR kỹ thuật nhỏ khi cần, nhưng giữ ID sở hữu và tiêu chí cuối; không tạo chuỗi PR xác nhận lại tài liệu. Không đặt lịch ngày hoàn thành khi chưa đo thời lượng thực hiện.

| Increment | Phụ thuộc | Đầu ra review được | Trạng thái |
| --- | --- | --- | --- |
| **APP-05.0 — Ổn định CI nền** | Baseline hiện tại | Chẩn đoán timeout, điều chỉnh budget hoặc tách job mà giữ coverage/artifact; CI main hoàn tất | `MERGED / MAIN_CI_VERIFIED` (CI-only; còn investigation riêng) |
| **APP-05.1 — Readiness và việc tồn** | APP-03; CI .0 đã đạt | Đọc cuối chuyến; giao người/hạn/hành động cho nguồn A/D/F; fixture có phiên bản | `MERGED / FINAL_HEAD_CI_VERIFIED` |
| **APP-05.2 — Đóng chuyến** | .1 | CLOSED/CLOSED_WITH_OPEN_CASES nguyên tử, gồm chuyến có đơn nhưng chưa nạp; biên bản và replay | `MERGED / LOCAL_DEV_VERIFIED / PR_CI_INCOMPLETE` (#153) |
| **APP-05.3 — Chọn chuyến và chuyển HELD** | .2 | Liên kết thực hiện kế nhiệm và chuyển đúng quyền đã giữ, không đổi đơn gốc | `MERGED / MAIN_CI_VERIFIED` (#154) |
| **APP-05.4a — Reserve phần chưa giữ** | .3 | Giữ bổ sung có nguồn trên chuyến kế nhiệm; thiếu tồn vẫn là việc chờ | `MERGED / MAIN_CI_VERIFIED` |
| **APP-05.4b — Nạp và giao tiếp** | .4a | Load/Delivery trên chuyến mới; bảo toàn lot/cost/snapshot và due; báo cáo toàn dòng | `IN_PROGRESS` |
| **APP-05.5 — Đóng phần A còn lại** | .4b | Đề nghị/duyệt độc lập, giải phóng đúng phần giữ, hoàn tất việc theo nguồn | `NOT_STARTED` |
| **APP-05.6 — Công việc ngày sau và phục hồi** | .2, .4b, .5 | Bàn công việc theo người/hạn; tái giao có audit; browser mới tìm lại nguồn/lệnh | `NOT_STARTED` |
| **APP-05.7 — Nghiệm thu trọn ca** | .0–.6 | Hai ngày cùng DB, đủ nhánh sạch/có tồn/bị chặn/chưa nạp, direct operator và oracle độc lập | `NOT_STARTED` |

Thứ tự mặc định: **.0 → .1 → .2 → .3 → .4a → .4b → .5 → .6 → .7**. Phân tích .1 có thể song song .0; merge code cần kết quả CI áp dụng trên source cuối. Không chờ .6 mới cung cấp UI tối thiểu: mỗi increment có đường thao tác dùng được cho phần mình sở hữu. .6 gom công việc và tăng khả năng phục hồi, không xây lại màn đã có.

## 4. Hợp đồng giao tiếp giữa các increment

Các tên dưới đây là **tên logic dự kiến**, chưa phải route/type tồn tại. Implementer ánh xạ vào convention hiện có trong increment sở hữu và cập nhật bảng khi chọn tên thật; không đổi ý nghĩa hoặc lược bỏ trường nguồn. Quantity dùng kiểu base quantity hiện có, tiền giữ precision/currency hiện có, không dùng floating-point. Tenant/actor server lấy từ danh tính, không tin trường client tự chọn.

| Contract dự kiến | Dữ liệu/đầu ra bắt buộc | Chủ trì |
| --- | --- | --- |
| `TripCloseReadiness` | Trip, business date, asOf, source revision/digest, độ phủ n/N hoặc unknown, blockers, nghĩa vụ và liên kết A/D/F; phân biệt remaining/held/scheduled/loaded/delivered/closed remainder | .1 |
| `AssignTripFollowUp` | CommandId, TripId, typed source ref, assignee, DueAt có offset, next action, reason, expected revision; trả stable FollowUpId và audit | .1 |
| `CloseTrip` | CommandId, TripId, expected revision; server tự suy CLOSED/CLOSED_WITH_OPEN_CASES, lưu một ClosureId, snapshot nguồn, người/hạn lúc chốt và outcome | .2 |
| `AssignASuccessor` (`POST /assign-successor`) | CommandId, FollowUpId, exact OrderLine/Confirm source, SuccessorTripId, QuantityBase, expected revision; tạo binding kế nhiệm có ID ổn định kể cả HELD=0, không cần StockRight, không cấp giữ hoặc tạo physical/financial effect | .3 |
| `TransferARemainder` (`POST /transfer-held`) | CommandId, FollowUpId, binding kế nhiệm, exact source/right generation, QuantityBase, expected revision; trả nguồn transfer bảo toàn HELD, không tạo physical/financial effect | .3 |
| `ReserveARemainder` | CommandId, FollowUpId, liên kết thực hiện kế nhiệm, QuantityBase, expected revision; cấp giữ từ free pool đúng scope hoặc từ chối không có effect | .4a |
| Load/Delivery kế nhiệm | Dùng command C.3/C.4 với binding kế nhiệm hợp lệ, quyền chuyến mới và nguồn đơn cũ; tổng lượng/tiền kiểm toàn dòng | .4b |
| `Propose/ApproveCloseARemainder` | Source line/link, quantity, reason, request identity; approval actor độc lập và version/payload được duyệt; trả nguồn đóng phần còn và release hợp lệ | .5 |
| Read follow-ups/command history | Tìm theo quyền hiện hành, người, hạn, trạng thái, Trip/Order/source; trả command ID/outcome và provenance đủ để tra/replay cùng ID sau browser mới | .6 |

Revision từ read không thay thế recheck trong transaction. Close, assign/reassign, transfer, reserve, load/deliver và đóng phần còn cần cùng quy tắc khóa/tuần tự hóa cho dữ liệu giao nhau; thứ tự khóa được nêu và kiểm trong PR sở hữu. Same identity/payload trả kết quả cũ sau khi kiểm quyền hiện hành; đổi payload conflict; ID mới không tạo closure hoặc entitlement trùng.

## 5. Công việc và điều kiện hoàn tất từng increment

### APP-05.0 — Ổn định CI nền

**Cải tiến thời gian chờ đã được Owner yêu cầu ngày 24/11/2026:** [APP-00A — bốn nhóm CI song song](app-00a-parallel-ci.md) kế thừa baseline merge PR #144, giữ đầy đủ kiểm chứng và tách worker cùng check `validate` tổng hợp. Đây là follow-up triển khai có source/evidence riêng; lịch sử timeout và điều kiện closeout bên dưới vẫn được giữ.

Follow-up ngày 25/11/2026, sau merge PR #147: Owner yêu cầu [đo rồi áp dụng build/cache có ích](app-00a-build-cache.md). Thí nghiệm đã đo cache package NuGet và dùng lại build trong cùng worker APP-00A; quyết định giữ build reuse và vị trí package tạm sau đối chứng đường dẫn, bỏ archive cache giữa các lượt vì chưa chứng minh lợi ích đủ rõ sau overhead. Toàn bộ gate vẫn giữ. Biên nhận final source và lịch sử cold/warm, kể cả lỗi warm, ghi trong PR sở hữu; yêu cầu có lượt main hoàn tất trước closeout `.0` vẫn giữ nguyên.

**Files:** `.github/workflows/app00a.yml`; các script runner liên quan chỉ khi phép đo cho thấy cần sửa. Bắt đầu bằng thời lượng/job/step của run baseline và các lượt PR thành công. `VerifyAcceptance`/F09 All chưa được gọi bởi workflow APP-00A ở baseline; không gọi chúng là gate CI đang có.

- [x] Ghi thời lượng, step cuối và nguyên nhân hủy; tách lỗi môi trường/timeout khỏi assertion, chỉ ra các gate/artifact chưa chạy được.
- [x] Chọn thay đổi nhỏ nhất đủ tin cậy: budget có căn cứ hoặc chia các runner độc lập thành job. Nếu tách, mỗi job giữ scratch/nonce riêng, setup cần thiết, source SHA và artifact không đè nhau. Không xóa test hoặc biến failure thành success.
- [x] Xác minh YAML/runner và chạy CI trên source cuối. Đối chiếu danh sách gate trước/sau, upload artifact cả khi fail và giữ cleanup/guard đúng phạm vi; đối chiếu full main trên merge #149. Xem biên nhận closeout dưới đây và phạm vi kiểm tra artifact tại từng PR.
- [x] Commit `ci: stabilize LocalDB validation budget` đã merge trong PR #144; các cải tiến tiếp theo ở #145–#149. Ghi run/source thực và trạng thái trong roadmap; .0 đóng theo full main PASS cuối, không chỉ theo thay đổi timeout.

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

Các hồ sơ thiết kế/đo riêng được giữ nguyên: [parallel CI](app-00a-parallel-ci.md), [concurrency](pr-ci-concurrency.md), [LocalDB bootstrap](localdb-ci-bootstrap.md), [build/cache](app-00a-build-cache.md), [docs-only classifier](docs-pr-ci.md). Chứng minh concurrency A → B nằm tại [45496331271](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45496331271) (superseded, CANCELLED) và [45496489462](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45496489462) (16/16 workflow, 32/32 job PASS attempt 1); việc hủy PR cũ không phải mất acceptance của HEAD mới.

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

- Native LocalDB SQL 5173 / Event 528 chưa có nguyên nhân gốc; [LAB-E03/#75](localdb-ci-bootstrap.md) vẫn có investigation riêng. Các lượt bootstrap FAIL và core test FAIL ở các merge trung gian giữ nguyên disposition lịch sử; lượt main #149 xanh không hồi tố chúng thành PASS. Bootstrap guard bounded và diagnostics không tự sửa system DB, không retry assertion.
- PR #148 ghi các lỗi chậm request ở APP-03.4/.5, D.1 và C.6 HttpClient 3s ở main attempt 1; PR #149 giữ lỗi A11-09 restart/outbox tenant-b và D.2 authenticated session GET (3s) trước acceptance ở attempt 1. Trong main #149 A11-09 PASS 48/48 SQL cells mỗi tenant và D.2 PASS ngay attempt 1; không tái hiện không chứng minh root cause đã được sửa. Giữ negative evidence và chẩn đoán theo source/run/attempt; không thêm retry tự động, không nới business assertion.
- Không close LAB-E03/#75, không chấp thuận thêm security/operations debt hoặc thay trạng thái nghiệm thu APP-02B PREPROD, actual EV, production P0, Pilot/Go-Live, physical/owner-device hay scoped 125% exception. Closeout CI không chứng minh một ca APP-05 trên laptop đã thực hiện.

**Exit .0:** đủ bằng chứng source và main full CI để kết thúc increment. APP-05.1 là increment tiếp theo theo roadmap đã duyệt; closeout .0 chỉ ghi nhận công việc của .0. Mọi PR .1 vẫn phải qua full CI trên final HEAD, review và cập nhật evidence của chính increment. Hồ sơ này chỉ ghi lại receipts đã tồn tại, không thay runtime/workflow và không phải bằng chứng CI của commit tài liệu tự thân.

### APP-05.1 — Readiness, việc tồn và fixture dùng chung

- [x] Viết và chạy ca thất bại cho contract readiness, migration profile và đường đọc LocalDB. Bộ kiểm pin lượng A, thiếu người/hạn, sai tenant/quyền, AR, cutoff, chuyến trống và phạm vi xe dùng chung.
- [x] Bổ sung read và audit append-only qua `GET /api/synthetic/app05/trip-close/trips[/{tripId}]` và `POST /api/synthetic/app05/trip-close/trips/{tripId}/follow-ups`. Dùng revision phát hành từ nguồn server, lock theo command rồi theo chuyến, transaction Serializable và kiểm grant/assignment hiện hành trước đọc, ghi hoặc replay.
- [x] Bổ sung fixture `APP-05.1-DAILY-CLOSE-v1` theo schema tiến `202611250001`. Tenant Ops và người nhận việc dùng grant theo chuyến. Điều phối dùng Receiver/Kho hiện có. Maker và checker tín dụng mẫu là hai actor riêng.
- [x] Nâng profile `APP-05.1-v1` trên hai tenant, giữ source/opening cũ và phục hồi plain Resume sau khi migration một tenant đã commit.
- [x] Mở màn **Chuẩn bị đóng chuyến** trong APP-03, hiển thị nguồn, blockers, A, AR và form người/hạn/next action. Link D/F mở hồ sơ gốc theo quyền của màn đích.
- [x] Chạy Chromium → API → LocalDB và kiểm chứng tenant đối chứng bằng SQL. Full final-HEAD CI và review là gate của PR.

**Thao tác DEV.** Dùng `./scripts/Run-App03Dev.ps1 -Action Start -CloseReadiness` cho phiên mới. Dùng `-Action Resume -RunId <nonce> -CloseReadiness` để nâng phiên APP-03 đã dừng. Các lần Resume/NextDay sau đọc profile từ manifest. Chọn mã truy cập `ops`, mở **Chuẩn bị đóng chuyến**, chọn chuyến và giao việc. Hạn nhập theo ICT (+07:00). `assignee` có quyền đọc readiness của chuyến được phân công, còn quyền ghi thuộc `ops`. Dùng `peer-ops` để mở tenant đối chứng. Hồ sơ gốc có thể yêu cầu đăng nhập bằng vai phụ trách ở tab mới.

**Cách đọc.** Chuyến bootstrap trống có trạng thái UNUSED và độ phủ 0/0. Độ phủ n/N đếm các kiểm tra nguồn A, chứng cứ giao, AR, D.4, tồn và quyền theo chuyến, manifest và đối chiếu tiền. Chuyến có đơn đã xác nhận mà chưa nạp hiện yêu cầu biên nhận bàn giao bằng 0 ở .2. HELD hiển thị phần đang giữ tại kho, có tính hàng đã trả từ xe. AR thông thường hiện riêng theo hạn. D.4 dùng một việc cho mỗi dòng nguồn giao và giữ liên kết tới case gốc khi có. F05, F06 và F08 trỏ đúng hồ sơ gốc qua chứng từ giao. F02 không có liên kết chuyến trong profile hiện tại. Credit chỉ xuất hiện trong hồ sơ refund có chuỗi nguồn đến chứng từ giao.

**Kiểm chứng.** Chạy `./scripts/Run-App03Dev.ps1 -Action VerifyCloseReadiness` và `-Action VerifyCloseReadinessUpgrade`. Bước đầu ghi expected/actual/diff và source/command IDs trong `TestResults/app05-1/`. Bước sau kiểm tra đơn/giao có trước migration, ngắt có chủ đích sau tenant A, Resume cả hai tenant rồi kiểm quyền thu hồi và dọn DB theo nonce. Bằng chứng thuộc synthetic DEV. Quyết định chốt và biên bản nguyên tử thuộc APP-05.2.

**Biên nhận local trước commit.** Windows / MSSQLLocalDB, baseline `fd606d30a06c680bfb71b6659fc81f7b4a57a46a+working-tree`.

- Readiness `07fa441b745d4d8e8cf1c1c43df4e45c` đạt Chromium 1/1 và SQL 39/39, zero diff. Kiểm A40/giao25/trả15, A5/giữ4/nạp3/giao1/trả2, Confirm5 chưa giữ, D.4 qua cutoff ngày sau, xe dùng chung, custody/offer, mở đúng hồ sơ D.4/F05, replay/thu hồi quyền và tenant đối chứng. SHA256 `run.json` `4D576A6ACEBAC582D5C4AFDB85565B5280847FA61B4774AAC4009AFF66EFA328`.
- Upgrade `216bb2fb10264450877107c2106862c6` quan sát migration A/B từ [1,0] đến [1,1] sau Resume. Order `59f426bb-b6a3-49d8-a1cd-0dbe2875c801` và command giao `9128e846-13af-4036-bd0c-bdf7b5e5ef3c` có trước lần nâng. SHA256 `upgrade-result.json` `3CF4ED08BDC58B12014839D442CB0C58911E1C69D48DF18DA68E429EB92BBCC3`.


**Review F08, PR #152.** Readiness dùng phần credit còn có thể duyệt sau khi trừ thực chi và các khoản đã duyệt đang giữ chỗ. Đề nghị đã duyệt tiếp tục hiện đến khi thực chi. Lượt local `3cda9679e8134f159484ee3ca40ac035` tái hiện đề nghị thứ hai vẫn hiện dù lệnh duyệt trả `F08_CREDIT_EXCEEDED`. Sau sửa, lượt `3147424f1abe46ccaff8aacaa740080a` đạt Chromium 1/1 và 61 đối chiếu, zero diff. Ca dùng lệnh thật qua ba mốc trước duyệt, đã duyệt chưa chi, và sau chi. Kiểm đúng ID việc và điều kiện cần giao việc, lịch sử hai đề nghị và tenant đối chứng. SHA256 `run.json` `1AD297EFB280CF2F763147168BE5012780FA48CE9D1709002AA28A2866880E63`.

#### Closeout APP-05.1 — 25/11/2026

[PR #152](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/152) đã merge lúc 15:21 ICT theo yêu cầu Owner. Source cuối `6497469f68160f11d1e8d2787263b60aba2dbd36`, merge `6938a7b6f25af575e8a49bd10212ac60780f55a7`, cùng tree `e758169a5d745d7b4abeae357a6c6703bc6cf0fc`. Review độc lập đã chỉ ra khoản credit F08 đang giữ chỗ. Bản sửa và regression API/SQL được review lại trên diff.

Final-source CI đạt 10/10 workflows và 25/25 checks. [APP-00A run 45571418949, attempt 3](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45571418949/attempts/3) giữ nguyên source khi chạy lại `web_sales` sau lỗi khởi động LocalDB 5178 và `NativeProcessTimeout`. [Artifact APP-03](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45571418949/artifacts/15003585247) ghi readiness `259e8535614c45aeb9adab58f6331b80` đạt 61/61 và upgrade `768ee72139794679a9a301be58853c1c` đạt. Lượt local trên source cuối `a1259c50a1ac4bfeaaab5dff5e52305c` và `4240af7dc57c4acea3834880225947f5` cũng đạt.

[APP-00A trên main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45575522246) chạy riêng trên merge commit. Attempt 1 đạt job `app03`, gồm hai bước APP-05.1. Job `distribution` dừng khi request dựng fixture D.3 vượt timeout HTTP 3 giây. Failed jobs được chạy lại trên cùng merge commit.

### APP-05.2 — Quyết định đóng chuyến và lịch sử

**Files:** phần mới .1; `src/FmcgDms.Api/Persistence/App01dD3Unload.cs`, `App01dD3UnloadPlan.cs`, `App01dCashierUnloadService.cs`, `App02d3Cash.cs`, `App02d3Routes.cs`, `App01a2CommandService.cs`, các route/command kiểm trạng thái Trip; `web/src/sales/TripCloseWorkspace.tsx` dự kiến. Tạo `App05TripClosure.cs`/migration và `tests/FmcgDms.Api.Tests/App05TripClosureTests.cs` khi phù hợp.

- [x] Viết ca đỏ cho điều kiện quyết định. Chạy ca live thiếu giao việc, revision cũ sau khi đổi giao việc, hai lệnh tranh cùng chuyến, mất ACK, lỗi trước COMMIT, đổi payload, dùng lại ID giữa hai loại lệnh, ID mới sau chốt, sai tenant và quyền bị thu hồi.
- [x] Bàn giao bằng 0 cho Trip có nghĩa vụ A chưa nạp. D.3 đối chiếu nguồn của riêng chuyến và ghi nguồn Confirm theo dòng, gồm Confirm đã giữ và chưa Reserve. Chuyến bootstrap trống vẫn ngoài mẫu số kết ca.
- [x] Dùng lock command rồi `APP01C:TRIP:<tenant>:<trip>` và transaction Serializable để đọc lại nguồn cùng phân công. Ghi status, biên bản và kết quả lệnh trong cùng giao dịch. Chuyến chốt có việc giữ người/hạn và liên kết chính xác, chưa cần chuyến kế nhiệm.
- [x] Lưu biên bản bất biến, mở được từ phiên mới và hiển thị riêng số việc lúc chốt và số đang mở. Hồi quy live từ chối draft, delivery và giao việc mới sau chốt.
- [x] Đối chiếu SQL sau rollback, bàn giao bằng 0 và close. Các bước này giữ tồn, AR, COGS, thu/chi và nguồn tenant đối chứng. CI full trên source cuối là gate của PR.

**Thao tác DEV.** Khởi chạy `./scripts/Run-App03Dev.ps1 -Action Start -TripClosure` hoặc bật trên phiên cũ qua `-Action Resume -RunId <nonce> -TripClosure`. Profile `APP-05.2-v1` bổ sung migration `202611250002` qua chuỗi nguồn của .1. Kho dùng bàn giao D.3. Tenant Ops chốt từ **Chuẩn bị đóng chuyến**. API nhận `POST /api/synthetic/app05/trip-close/trips/{tripId}/close`, trả biên nhận theo cùng command ID và payload. `GET .../{tripId}/closure` tìm biên nhận bền sau khi đọc lại quyền hiện hành. Snapshot lưu dữ kiện, nguồn và trách nhiệm tại lúc đóng. Tiến trình D/F cập nhật số đang mở theo nguồn.

**Biên nhận local.** `VerifyTripClosure` lượt `83221198f40446f594bb17b3ac1d6895` trên working tree đạt Chromium 2/2 và 30/30 đối chiếu SQL, zero diff. Kiểm A40/25/15 có hai việc lúc chốt và còn một sau D.4 cutoff ngày sau, chốt sạch sau đối soát, và bàn giao bằng 0 cho hai dòng Confirm5 có HELD5 và Reserve0. Lượt chạy dùng trigger rollback có guard trong DB synthetic và bỏ ACK sau khi server đã commit. SHA256 `run.json` `8A2BC90467E3A888AB2B9B032A41AC84C0244B100B8DAF5A3D38D7B2E11B5049`. Nhánh C.4 evidence đang chờ có guard chung trước bàn giao và chốt. Hành trình này chạy blocker tồn xe và chưa tạo riêng một C.4 evidence đang chờ.

**Hai findings review PR #153.** Close đối chiếu physical MAIN với nguồn mở/tồn và đối chiếu pool theo toàn bộ quyền A/B cùng tenant, kho và SKU. Lượng đang trên xe được trừ khỏi lượng giữ tại kho. Lượng trả xe và quota B giải phóng lấy từ chứng từ D.3 đã áp dụng. Kiểm tra cũng áp dụng cho bàn giao bằng 0 có HELD. Lượt đỏ `97e9aa2e637a45de83ec86f28e90955f` tái hiện A15 đã trả xe vẫn có thể chốt khi pool là 0. Lượt sửa kiểm chặn cả A15 sau trả xe và ca pool chung thiếu phần giữ của chuyến khác, sau đó phục hồi đúng số liệu DEV rồi thao tác thành công.

Lượt `2d88fb52e850491db819809fceab4a6b` trên working tree đạt Chromium 3/3 và 62 đối chiếu SQL, zero diff. C.4 đi qua intake thật và dừng có chủ đích trước khi posting commit. D.3 và Close bị chặn với `RECEIVED` thật và biến thể `PENDING` được đặt trên cùng bản ghi synthetic có guard. Gửi lại lệnh thật sau khi tháo fault ghi đúng một kết quả giao. Ca Mixed kiểm custody và offer đang chờ rồi nhận bàn giao đúng nguồn. Ca cạnh tranh xác minh Close đang đợi đúng lock chuyến, D.4 post thành công trong lúc đó, revision cũ bị từ chối và revision mới chốt với đúng nguồn. SHA256 `run.json` `2767C1433480398C3241A3903D667C1837C5BB16AE1DCD60D888419AF7A437EF`.

### APP-05.3 — Liên kết kế nhiệm và chuyển HELD

Migration tiến `202611250003` thêm binding nguồn cùng thế hệ quyền HELD kế nhiệm, chi tiết lô và biên nhận lệnh. Bindings tham chiếu hồ sơ A lúc đóng, Trip gốc/đích và exact OrderLine/Confirm/SKU. Quyền kế nhiệm tham chiếu AReservation và StockRight gốc qua cùng tenant, Trip, kho, SKU và loại A. Chọn Trip có thể xếp một phần lượng còn lại, kể cả Confirm chưa từng Reserve. Tenant Ops cần phân công hiện hành ở cả hai Trip. Chuyến đích phải PREPARED, mới hơn chuyến gốc và thuộc ngày nghiệp vụ DEV đang mở.

| Đường hiện hành | Ràng buộc giữ trong .3 và điểm nối tiếp |
| --- | --- |
| `IntegratedSchema`, `App01b1PhysicalStock`, `App01c1PostingSchema` | Giữ quyền tồn và manifest cùng tenant/Trip/kho/SKU/loại quyền. Binding và thế hệ mới có FK riêng. |
| `App02c2Commands` / `App02c2Reads` | Reserve legacy tiếp tục theo đơn gốc. Đọc HELD toàn dòng chỉ đếm quyền tồn vật lý làm nguồn đảm bảo, một lần. Reserve trên binding thuộc .4a. |
| `App02c3Load` / `App02c3Schema` | JOIN đơn/quyền cùng Trip, nguồn `STOCK_ALLOC` và khóa manifest legacy giữ nguyên. Nạp trên quyền kế nhiệm thuộc .4b. |
| `App02c4Schema` / `App02c4Delivery` | `FK_SaleDelivery_AOrderIdentity` giữ Trip gốc. `FK_SaleDelivery_FrozenAConfirm` giữ exact Order/Line/SKU. Nhánh posting kế nhiệm cần đường ràng buộc riêng ở .4b. |
| `App02d3UnloadSchema`, `App03SettlementSchema`, `App02DevF03Lots` | Transfer đọc lượng chưa nạp và đã trả từ nguồn quyền, nạp và D.3/F03 trả lô. D.3 tiếp tục bàn giao nguồn cũ. Close chuyến đích nhận blocker trong lúc binding chờ thực hiện. |

**Bảo toàn.** HELD còn ở Trip gốc bằng lượng chưa từng nạp cộng lượng D.3 đã trả, trừ toàn bộ lượng đã chuyển theo từng nguồn. HELD của Trip đích là tổng các thế hệ trên binding của nó. Hai vế bù trừ từng lượng chuyển. StockRight gốc tiếp tục đảm bảo tồn MAIN và giữ lịch sử physical/cost. Chuyến gốc đã đóng nên C.2/C.3/C.4 legacy không thể sử dụng quyền đó. Báo cáo Owner trừ lượng đã chuyển ở Trip gốc và cộng cùng lượng ở chuyến đích. Lô còn hạn được chọn theo FEFO và lưu riêng hai phần chưa nạp/đã trả. Command lock đến trước lock hai Trip theo thứ tự ID, sau đó transaction Serializable kiểm quyền, revision, số lượng, nguồn và pool MAIN. Biên nhận ghi cùng giao dịch với binding hoặc quyền kế nhiệm.

**Thao tác DEV.** Dùng `./scripts/Run-App03Dev.ps1 -Action Start -ASuccessor` hoặc `-Action Resume -RunId <nonce> -ASuccessor` để mở profile `APP-05.3-v1`. Từ **Chuẩn bị đóng chuyến**, mở việc A trên chuyến đã đóng, xếp chuyến rồi chọn quyền gốc và lượng HELD để chuyển. Việc còn mở giữ người và hạn lúc đóng. Dưới `/api/synthetic/app05/trip-close/trips/{tripId}/a-remainders/{lineId}`, các route là `GET /successors`, `POST /assign-successor` và `POST /transfer-held`. Hai POST nhận FollowUpId, ConfirmSourceId, DestinationTripId, Quantity, ExpectedRevision và CommandId. Transfer nhận thêm BindingId và OriginalRightId.

**Kiểm chứng.** `./scripts/Run-App03Dev.ps1 -Action VerifyASuccessor` chạy hành trình .2, nâng cặp LocalDB synthetic lên .3 rồi chạy Chromium và đối chiếu SQL trong `TestResults/app05-3/`. Ca kiểm gồm chuyển một phần HELD đã trả, hai đích tranh quyền chưa nạp, Confirm chưa Reserve, lỗi trước commit, mất ACK, replay và thu hồi quyền. Nạp/giao tiếp và Reserve mới thuộc APP-05.4a/b.

**Biên nhận local.** Lượt `c92d8f79016e4ce5856ac93708bc22fa` trên working tree của `807f250` đạt Chromium 4/4, 71/71 đối chiếu SQL, zero diff. Lượng đã trả chuyển 9/15 EA qua hai lệnh, báo cáo giữ nguyên tổng HELD và chuyển đúng 9 EA sang Trip đích. Hai Trip tranh quyền chưa nạp 4 EA với yêu cầu 3 và 2, chỉ một lệnh thành công. Confirm 5 EA chưa Reserve xếp được chuyến và không có right/lô giám sát mới. Hai DB scratch được dọn theo nonce. SHA256 `run.json` `81F7E7C068589981FFF9CADFAA817EB12A805061706F0B5CA18A2FE0329C3100`. Rà soát độc lập đã kiểm lại năm findings về FollowUp/revision, thu hồi quyền, FK SKU, báo cáo HELD và D.3.

Lỗi ma trận D.3 trên final source của PR #153 vẫn là lịch sử hosted CI. Lượt hồi quy D.3 local `cf67a8869bf448fba8dca2b4959beb04` đạt Chromium 2/2 và 36/36 ca. Workflow APP-00A chạy `VerifyASuccessor` trên PR .3, bao gồm hành trình đóng chuyến .2 và nâng cấp sang .3.

**Bổ sung theo review PR #154.** Giao diện giữ nguyên CommandId và payload đang chờ khi mất ACK, bị từ chối truy cập hoặc nhận phản hồi thiếu biên nhận khớp nguồn. Server gửi lại mã lệnh, nguồn, binding và lượng từ biên nhận đã lưu sau khi kiểm quyền hiện hành và payload. Giao diện kiểm exact lượng và tổng hai phần chưa nạp/đã trả trước khi xác nhận thành công. Lượt local `cfcda3eeaa7e49658b16f4b91b2b2947` đạt Chromium 4/4 và 75/75 đối chiếu SQL, zero diff. Ca kiểm thu hồi rồi cấp lại quyền sau commit mất ACK, JSON thành công rỗng hoặc sai nguồn/số lượng và xếp chuyến với lượng có số 0 đầu. Cùng lệnh gửi lại giữ một binding và đúng một thế hệ quyền tương ứng. SHA256 `run.json` `F165EE85980B9C564E1327A0D47920D00DF9BFF8AE083B79DE6F7E44AE693E0F`.

#### Closeout APP-05.3 — 25/11/2026

**Disposition** `MERGED / MAIN_CI_VERIFIED / SYNTHETIC_DEV_ONLY`.

- [PR #154](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/154) merge lúc 18:54 ICT. Final source `6a87bb9e835cbf632f86b7670b5d5c0096b5099b`. Merge `ad5ed70ca238aeca35fedb9b6c60ea65efa823e6`. Hai finding review đã được sửa về giữ nguyên lệnh UNKNOWN qua 403 và kiểm ACK khớp nguồn trước khi xác nhận.
- Final source đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2 sau lỗi khởi động LocalDB của distribution ở lượt đầu. [CI final source](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45602682722).
- Merge trên `main` đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2. Lượt đầu hết thời gian chờ dòng báo cáo trong ca Chromium APP-03.7 accounting, trước bước .3. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45608725142).
- Artifact `VerifyASuccessor` trên đúng merge `ad5ed70`, tree `9c28082d163920ef09c527fd42ec2583c963e8a3`, lượt `6066ca8d2587419397b7dea75408b1ea` đạt Chromium 4/4 và SQL 75/75, zero diff. SHA256 `run.json` `46E222450689706BD29E0EF536131B2DEE93B5E3BFC9004316F93AF9218B21A6`.
- Kết quả thuộc LocalDB synthetic DEV. Reserve phần chưa giữ chuyển sang .4a. Nạp/giao kế nhiệm thuộc .4b. Nghiệm thu ca tích hợp của Owner tại .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

### APP-05.4a — Reserve riêng cho phần chưa giữ

Migration tiến `202611250004` bổ sung liên kết bất biến từ quyền Reserve tới binding, dòng A, SKU, kho và chuyến đích. Dùng `-ASuccessor` trên phiên mới hoặc Resume phiên DEV đã có. Kho cần quyền hiện hành và phân công C.3 trên cả chuyến gốc lẫn chuyến đích. Từ **Đơn hàng**, mở dòng trên chuyến gốc đã đóng, chọn binding và kho rồi **Giữ bổ sung**. UI hiển thị lượng đã xếp, đã giữ và còn chờ giữ. Nạp/giao quyền kế nhiệm thuộc APP-05.4b.

API `GET /reserve-options` và `POST /reserve-unheld` dùng chung tiền tố dòng A của .3. Lệnh Reserve nhận CommandId, FollowUpId, ConfirmSourceId, BindingId, DestinationTripId, WarehouseId, Quantity và ExpectedRevision. Transaction kiểm lượng trống của binding và phần chưa giữ toàn dòng. C.2 A, D.1 B và Reserve kế nhiệm dùng cùng khóa tenant/kho/SKU trong profile tích hợp. Cấp giữ ghi cùng giao dịch quyền, pool, lô FEFO, nguồn `STOCK_ALLOC`, hiệu ứng `RIGHT_A` và biên nhận. Replay kiểm lại quyền và cùng payload.

**Biên nhận local.** `./scripts/Run-App03Dev.ps1 -Action VerifyASuccessor` lượt `a45c4009402a44eca5205d1eee447b5f` đạt 4/4 Chromium và 86/86 đối chiếu SQL, zero diff. Ca 5/4/3/1/2 còn 4 EA, chuyển đúng HELD3, rồi giữ thêm 1 EA qua UI. Confirm5 chưa Reserve nhận đủ 5 EA qua binding sau một lỗi precommit có chủ đích và replay cùng command. Hai actor và Trip tranh một pool không quản lý lô bằng Reserve A và quota D.1 B. Thiếu hàng giữ nguyên phần chờ. Tồn vật lý, cost, AR và tenant đối chứng được giữ nguyên. C.3 legacy chưa thấy quyền nạp mới. SHA256 `run.json` `7218866C05BAF9E65053AC920E1C8282FF52C9116F6F459E455232719E3D3738`.

**Bổ sung theo review PR #155.** API Đơn hàng trả quyền Reserve kế nhiệm theo dòng và phân công Kho trên chuyến gốc. UI giữ nguyên yêu cầu đang chờ khi mất quyền. Ca Chromium bỏ ACK sau commit, kiểm sai actor/tenant, thu hồi quyền từng chuyến, rồi cấp lại để replay đúng yêu cầu và biên nhận. Oracle SQL đối chiếu đúng một quyền và hiệu ứng. Fixture synthetic mới tách lô FEFO 2 EA và 4 EA còn hạn, lô 5 EA hết hạn và lô gốc đã hết khả dụng. Oracle đối chiếu từng mã lô, hạn, kho, SKU và lượng. Khi MAIN còn 5 EA nhưng hết lô đủ điều kiện, lệnh trả từ chối và giữ nguyên quyền, lô, nguồn và sổ phân bổ. Lượt local `b11191a77b624655abaf39849d959096` đạt Chromium 4/4 và 109/109 đối chiếu SQL, zero diff. SHA256 `run.json` `68C2E480D2FB62C11828D02EBBD2541467AD35F2ADC3B7CF0FDB32A591D4E620`.

#### Closeout APP-05.4a — 25/11/2026

**Disposition** `MERGED / MAIN_CI_VERIFIED / SYNTHETIC_DEV_ONLY`.

- PR #155 merge lúc 21:16 ICT. Final source `903ac58bc7da41c13291bdbbb407c34f2134f621`, merge `4cf8ee89754ae44b87f33b52df4cf9801f683dce`. Review độc lập đã kiểm lại phạm vi thao tác Kho, replay sau mất ACK và nguồn lô FEFO. [PR #155](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/155).
- Final source đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 2 sau khi lượt đầu thiếu bằng chứng hoàn tất hai ca C.6 DEV-07/A và DEV-08/A trong `sql-review.json`. Job APP-03 đã đạt ở attempt 1. Lượt `e0e32507f72f491aa84a4264e031d499` trên test merge `92c685d87cdb79c576ffe51a5be91bf212a43b2f` đạt Chromium 4/4 và SQL 109/109, zero diff. SHA256 `run.json` `5F4F42D33A2CF2268470C55E080DFBE383629EE0397FDC42876A9264D6C1EF98`. [CI final source](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45622795218).
- Commit merge trên `main` đạt 10/10 workflow và 25/25 checks. APP-00A đạt attempt 1. Artifact `VerifyASuccessor` ghi đúng merge `4cf8ee8`, tree `6b4afffd11c7013d994e2e62a3813c145f38cd55`, lượt `e01bd57f420d4b5386efac6c95ed2bed`, Chromium 4/4 và SQL 109/109, zero diff. SHA256 `run.json` `C8DD5265F08B50DC342FC306A18561E08E02CDF1EBBDC67DC395838AD0C51064`. [CI main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45632314509).
- Kết quả thuộc LocalDB synthetic DEV. Nạp, giao liên chuyến và báo cáo toàn dòng chuyển sang .4b. Nghiệm thu ca tích hợp của Owner thuộc .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

### APP-05.4b — Nạp, giao tiếp và báo cáo toàn dòng

**Files:** `src/FmcgDms.Api/Persistence/App02c3Load.cs`, `App02c3Reads.cs`, `App02DevF03Lots.cs`, `App02c4Delivery.cs`, `App02c4Reads.cs`, `App02c4ShipTo.cs`, `App02c5Report.cs`, `App03HandoffReads.cs`, `App03OwnerReport.Sql.cs`; `web/src/sales/LoadWorkspace.tsx`, `DeliveryWorkspace.tsx`, `OrderWorkspace.tsx`. Test mới dự kiến `App05SuccessorDeliveryTests.cs`; mở rộng runner/evidence C.3/C.4/F03.

- [ ] Viết ca đỏ nạp lại HELD đã trả, lô hết hạn ở ngày mới, source/right sai, hai delivery tranh lượng còn của toàn dòng và partial deliveries qua nhiều Trip có làm tròn tiền.
- [ ] Load/Delivery xác thực binding kế nhiệm và quyền trên Trip mới, giữ Order/Confirm/snapshot cũ. Phân biệt lượng chưa từng nạp với lượng trả có source; không trừ lifetime Loads khiến lượng trả bị mất hoặc được dùng hai lần.
- [ ] Bảo toàn lot/UoM/source cost và residual; tổng payable/discount/tax rounding theo toàn dòng qua mọi Trip. Due của lần giao sau lấy ngày giao sau + N gốc. Không amendment giá/terms/ShipTo chỉ vì xếp chuyến.
- [ ] UI và báo cáo hiện Trip gốc cùng các Trip thực hiện, drill-down delivery/source; MAIN không bị cộng lặp. Khi giao đủ theo nguồn, follow-up A hoàn tất; D.4 liên quan hoàn tất theo nguồn/báo cáo riêng.
- [ ] Chạy hai fixture mục 6, hồi quy C.3/C.4/F03/F04, tenant đối chứng và race; commit đề xuất: `feat: load and deliver A remainder across business days`.

**Bước đầu — báo cáo AR theo dòng đơn.** Bộ lọc `orderLineId` dùng mã dòng Confirm gốc. Chi tiết và bản xuất giữ mã đơn, mã dòng, chuyến gốc, chuyến thực giao và nguồn Confirm/nạp/giao. Quyền đọc vẫn theo phân công trên chuyến ghi nhận lần giao. Lượt DEV `e910b63ecc464c9cb4b56cf085bc5033` kiểm tra bộ lọc, bản xuất, dữ liệu đối chứng và ca không có dòng khớp. Chromium đạt 1/1, SQL 13/13 khớp, SHA256 đối chiếu `E53E8249BE0710806B2CD4D9FA18818A622EDE8F7BCD1B810DA430B5B0B2A7BD`.

**Bổ sung theo review PR #157.** Gửi mã dòng sai định dạng ẩn kết quả và nút xuất cũ, đưa focus về trường lỗi. Sau khi sửa, báo cáo hiện mã dòng đang áp dụng. Fixture C.5 có hai dòng đã giao trên cùng một Confirm với công nợ 375 và 44. Ca kiểm đối chiếu riêng số lần, số dư và nguồn Confirm/nạp/giao trong API và bản xuất từng dòng. Thử tạm điều kiện lọc theo đơn làm ca kiểm nhận 419 ở dòng 375 và thất bại đúng kỳ vọng. Lượt C.5 `84b42742a2474cb48dd7d1b2916ca351` đạt Chromium 1/1, SQL 24/24 khớp. SHA256 đối chiếu `445B6A1D79238139055B120F1485A33237E196E8DC7E17E11380459A0E2ED3EC`. Hồi quy C.4 `864a9d126e964c149706d54a50daff54` đạt Chromium 1/1 và SQL 13/13.

Để bật nạp và giao liên chuyến, migration cần ràng buộc manifest với binding, đúng quyền nền và chuyến thực hiện. Nguồn của Reserve mới và nguồn của HELD chuyển tiếp đi qua hai nhánh rõ ràng. C.3 cần đối chiếu phần đã dùng của từng thế hệ quyền theo lượng và lô. C.4 cần khóa theo dòng Confirm khi tính phần còn và làm tròn, đồng thời khóa chuyến thực hiện khi ghi evidence. C.5 và đường mở báo cáo từ hồ sơ đơn cần đọc theo binding của manifest và phân công trên từng chuyến thực giao. Đích kiểm chứng là báo cáo cùng mã dòng với hai chuyến khác nhau sau khi chạy các fixture mục 6.

### APP-05.5 — Đóng phần còn bằng nguồn được duyệt

**Files:** command service/binding .3–.4, cơ chế approval/authority hiện có, follow-up và UI chi tiết; tạo `App05ARemainderClosure.cs`, `App05ARemainderClosureTests.cs` khi cần. Giữ correction/refund F ở nguồn nghiệp vụ tương ứng.

- [ ] Viết ca đỏ tự duyệt, payload/quantity đổi sau đề nghị, mất quyền, close-remainder đua transfer/Reserve/delivery và lượng vẫn trên xe hoặc có evidence pending.
- [ ] Tạo đề nghị có source/quantity/reason; actor độc lập đủ quyền duyệt đúng version. Chỉ kết thúc nghĩa vụ chưa giao và release phần HELD hợp lệ; không đảo phần đã giao hoặc giảm AR đã ghi vì thao tác này.
- [ ] Recheck toàn dòng và right generation dưới transaction/khóa chung; giải quyết partial closure, replay, payload conflict. Việc chỉ xong khi phần nghĩa vụ còn lại bằng 0 theo delivery/nguồn duyệt.
- [ ] UI thể hiện người đề nghị/người duyệt, nguồn release và phần còn; xác minh conservation/financial no-effect cùng tenant guards; commit đề xuất: `feat: approve closure of undelivered A remainder`.

### APP-05.6 — Bàn công việc ngày sau và browser mới

**Files:** `src/FmcgDms.Api/Persistence/App03WorkspaceRoutes.cs`, `App03HandoffReads.cs`, `App02d4Accounting.Reconcile.cs`, read/history của .1–.5; `web/src/workspace/Workspace.tsx`, `FinanceQueue.tsx`, `web/src/sales/customer-api.ts`, `MixedTripWorkspace.tsx`, `TripCloseWorkspace` và danh sách công việc mới nếu cần; `src/FmcgDms.Demo/App03DevSession.cs`. Test dự kiến `App05RecoveryTests.cs`, mở rộng `web/e2e-app05/journey.spec.ts`. `web/src/ui/command-recovery.ts` thuộc UI Lab, không phải durable business queue để thay server history.

- [ ] Viết ca đỏ bỏ ACK sau COMMIT, Stop/Resume và đóng toàn bộ browser context. Phiên đăng nhập mới không có sessionStorage cũ phải tìm được command/source đúng quyền; thêm wrong tenant, revoked và changed-payload negatives.
- [ ] Cấp server-readable history đủ tìm lại kết quả nguồn/lệnh theo phạm vi actor; không giữ bearer token lâu dài hoặc dùng cache client làm bằng chứng duy nhất. UI hướng dẫn tra/replay cùng identity khi UNKNOWN.
- [ ] Bàn việc lọc theo người/hạn/trạng thái, mở đúng hồ sơ nguồn và Trip kế nhiệm. Reassign/đổi hạn cần reason/audit, không đổi snapshot lúc chốt. Quá hạn chỉ là nhãn theo clock DEV, không tự hoàn tất/xếp Trip hoặc phát sinh SLA/automation ngoài phạm vi.
- [ ] Đối soát D.4 ngày một sau NextDay theo `From < Cutoff <= AsOf <= now` và `OccurredAt < Cutoff`; kiểm report line/source hash bao phủ đúng nguồn trước khi hoàn tất việc, không đóng việc ngày hai bằng báo cáo ngày một. NextDay tự nó không chốt Trip hoặc hoàn tất việc.
- [ ] Kiểm keyboard, thông báo trạng thái, Compact, context và đóng/mở browser thực trong runner; commit đề xuất: `feat: resume daily follow-ups from a new browser session`.

### APP-05.7 — Nghiệm thu ca tích hợp

**Files:** `src/FmcgDms.Demo/App03AcceptanceEvidence.cs`, `App03AcceptanceOracle.cs`, `App03DevSessionEvidence.cs`, `App03SettlementVehicleEvidence.cs`, `web/e2e-app03-9/journey.spec.ts` để tái sử dụng; thêm `App05DailyCloseEvidence.cs`/oracle độc lập, mở rộng `web/e2e-app05/journey.spec.ts` và config APP-05; `scripts/Run-App03Dev.ps1` hoặc launcher riêng nếu việc mở rộng action làm hợp đồng khó dùng. Bổ sung hướng dẫn/biên nhận `docs/roadmap/app-05-7-acceptance.md` khi có execution thật.

- [ ] Công bố manifest fixture/expected và ma trận mục 7 trước run. Thêm action runner rõ tên, output và cleanup; tên dự kiến `VerifyDailyClose`, **chưa tồn tại tại baseline**, nên chưa đưa vào lệnh copy-run trước khi implement.
- [ ] Chạy trực tiếp một ca trên laptop và một ca tự động UI→API→SQL trên cùng cặp DB qua hai ngày. Bao phủ clean, carry-forward, blocked, zero-manifest và unused Trip; phiên có lịch sử từ trước upgrade và browser mới.
- [ ] Đối chiếu SQL độc lập expected/actual/diff theo source, lượng, tiền, lot/right generation, lịch sử closure và tenant đối chứng; không lấy response của chính command làm oracle.
- [ ] Chạy F09 All và gate áp dụng trên source cuối, cộng kiểm mới của APP-05; ghi local/CI/merge/operator riêng. Nếu CI chỉ chạy tập con, nêu rõ; không đổi tên local thành hosted CI hoặc tái dùng PASS từ source cũ.
- [ ] Bàn giao đường chạy, giới hạn, run/source/tree, artifacts/hashes và hành động Remove an toàn. Cập nhật bảng trạng thái cùng PR có evidence; commit đề xuất: `test: verify daily close and next-day fulfillment journey`.

## 6. Fixture và các số phải đối chiếu

Fixture **additive**, có phiên bản: SKU có lô `DAY-A-EA`, customer DEV riêng, kho hiện có, opening **60 EA / 180.000 VND** cho từng tenant; unit cost 3.000 VND, giá 10.000 VND/EA, discount/tax 0, terms 7 ngày, credit limit synthetic 500.000 VND được actor độc lập duyệt. Lô đủ hạn cho lịch 23–30/11/2026, không nghĩa vụ quà. Đây là dữ liệu kiểm chứng, không phê duyệt giá/tín dụng thật. Không sửa opening hoặc nguồn cũ.

| Mốc của dòng A chính | MAIN | HELD A | Trên xe của A | AR phát sinh từ giao | COGS lũy kế |
| --- | --- | --- | --- | --- | --- |
| Opening | 60 EA / 180.000 VND | 0 | 0 | 0 | 0 |
| 23/11: Confirm40, giữ/nạp40, giao25, trả15 | 35 EA / 105.000 VND | 15 EA | 0 | 250.000 VND, due 30/11 | 75.000 VND |
| Chốt và chọn chuyến ngày 24/11 | Giữ nguyên | 15 EA; quyền kế nhiệm có nguồn | 0 | Không thêm | 75.000 VND |
| 24/11: nạp/giao tiếp15 | 20 EA / 60.000 VND | 0 | 0 | Thêm 150.000 VND, due 01/12 | 120.000 VND |

AR ở bảng là **phát sinh**, không phải dư nợ sau thu tiền. Nếu ca có thu, đối chiếu receipt/allocation/custody/fund riêng. Kết ca phải giải quyết đúng custody/CashHandover; trường hợp không thu có bằng chứng số dư bằng 0. Expected của B/F/Mixed công bố riêng theo nguồn và đơn vị, không cộng vào cột A này.

Ngày một của ca chính có **hai việc mở tại lúc chốt**: A15 và đối soát nguồn ngày một D.4 chưa được cutoff bao phủ. RECEIVED/POSTED không đồng nghĩa RECONCILED. Ngày hai giao tiếp làm xong việc A; báo cáo D.4 bao phủ nguồn ngày một làm xong việc kế toán độc lập. Nguồn ngày hai có vòng đối soát riêng. Chuyến CLOSED sạch dùng ca đã giao đủ và được đối soát, có thể chốt sau NextDay, ghi thời điểm thật, không backdate.

Fixture phụ: **Confirm5/giữ4/nạp3/giao1/trả2 → remaining4, HELD3**. Transfer chỉ được chuyển3; phần1 cần Reserve mới. Nếu thiếu tồn thì còn chờ người/hạn. Thêm ca toàn bộ A chưa nạp và **Confirm5/Reserve0 → close không manifest → AssignASuccessor → Reserve5 → nạp/giao5**; không dùng manifest/right giả hoặc transfer số lượng 0. Fixture lỗi lô hết hạn có expected bị chặn, không kéo dài hạn nguồn đã chốt để ép PASS.

## 7. Ma trận nghiệm thu và truy vết

| Mã | Given/When trọng tâm | Then cần chứng minh | Chủ trì |
| --- | --- | --- | --- |
| DC-01 | Chuyến đầy đủ nguồn, không nghĩa vụ còn chờ | CLOSED, không follow-up giả, không effect hàng/tiền mới | .2, .7 |
| DC-02 | A40/giao25/trả15, người/hạn đầy đủ, chưa chọn Trip mới | CLOSED_WITH_OPEN_CASES; hai việc của ca chính bền cùng closure | .1, .2, .7 |
| DC-03 | Thiếu người/hạn; custody/offer/evidence pending; source unknown/drift | Không closure; lý do và bước xử lý đúng nguồn | .1, .2 |
| DC-04 | Có đơn nhưng chưa manifest; Confirm giữ đủ hoặc chưa Reserve | Bàn giao bằng 0 có nguồn, không movement giả; Trip chưa sử dụng ngoài mẫu số | .2 |
| DC-05 | Hai Trip tranh HELD, partial transfer, request retry/rollback | Một quyền dùng cho mỗi phần; allocated/physical/cost/AR bảo toàn | .3 |
| DC-06 | Ca 5/4/3/1/2, Reserve phần1 thành công hoặc thiếu tồn | Không dùng lượng chưa giữ; nghĩa vụ còn lại luôn hiển thị đúng | .4a |
| DC-07 | Ngày hai nạp/giao15 cùng Order/Confirm | Số mục 6 khớp, snapshot cũ giữ, due 01/12; report mọi Trip không đếm trùng | .4b |
| DC-08 | HELD đã trả/chưa nạp, lô hết hạn, giao cạnh tranh và rounding | Source/lot/value toàn vẹn; không over-deliver hay over-post toàn dòng | .3, .4b |
| DC-09 | Duyệt đóng phần còn; đua transfer/Reserve/delivery | Maker/checker độc lập; release đúng; AR/delivery đã ghi không bị đảo | .5 |
| DC-10 | Đổi source sau preview hoặc grant/assignment bị thu hồi | Recheck quyết định hiện hành; stale preview không vượt guard | .1–.5 |
| DC-11 | Mất ACK sau COMMIT; Stop/Resume; browser mới, same ID/đổi payload | Tìm lại nguồn/lệnh; cùng payload một effect; payload đổi conflict | .2–.6 |
| DC-12 | NextDay và D.4 report bao phủ đúng nguồn ngày một | Việc kế toán hoàn tất theo nguồn; không nhận nhầm nguồn ngày hai | .1, .6 |
| DC-13 | Việc được tái giao/hoàn tất sau close | Snapshot số việc/người/hạn lúc chốt không đổi; hiện tại đọc đúng audit | .2, .6 |
| DC-14 | Wrong tenant/actor, assignee thiếu quyền command | Đọc/ghi/replay bị chặn; ảnh số dư/nguồn tenant đối chứng giữ nguyên | Mỗi increment, .7 |
| DC-15 | Upgrade phiên có đơn/giao, gián đoạn giữa hai DB, replay, Remove | Hash nguồn/opening/snapshot cũ giữ; nâng phiên tiếp được; thu hồi đúng nonce | .1, .3, .7 |
| DC-16 | Confirm5/Reserve0, close không manifest, chọn Trip kế nhiệm | Binding từ Confirm không cần right; Reserve5 rồi mới nạp/giao5; thiếu tồn giữ việc chờ có người/hạn | .2, .3, .4a, .4b, .7 |

## 8. Lệnh nền và quy tắc bàn giao cho Codex

Đọc [AGENTS](../../AGENTS.md), spec APP-05, các module C/D/F đang sửa và [UI/UX Standard](../ui-ux/README.md). Dùng lệnh thực tế ở HEAD hiện hành. Các lệnh nền **đã có**, chạy trên Windows/LocalDB theo hướng dẫn phiên:

```powershell
pwsh -NoProfile -File ./scripts/Verify-Business.ps1
python tools/validate_business.py
python tools/validate_e_sim_01.py
dotnet test --solution FmcgDms.slnx -- --report-trx

pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Verify
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifySettlement
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyFinance
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyAcceptance
pwsh -NoProfile -File ./scripts/Run-DevFunctionalRegression.ps1 -Suite All

npm --prefix web run build
npm --prefix web test
npm --prefix web run test:dependencies
npm --prefix web run test:pwa-build
```

Không chạy toàn bộ các lệnh trên trong mọi PR chỉ để tăng số PASS. Chọn test/runners theo rủi ro và file thay đổi, giữ gate repo bắt buộc; .7 chạy acceptance mới và hồi quy cuối. Playwright business integration cần launcher/API/SQL thực tương ứng, không chỉ `npm test` hoặc browser fixture mock. Chưa có `VerifyDailyClose` ở baseline; PR sở hữu phải bổ sung và ghi cách dùng trước khi công bố lệnh mới.

Biên nhận mỗi increment ghi scope, command, môi trường, source/tree, run ID, expected/actual/diff, command/source IDs, artifact và SHA-256. Tách `IMPLEMENTED`, `DEV_VERIFIED_LOCAL`, `CI_VERIFIED`, `MERGED` và thao tác trực tiếp của Owner. Việc chưa chạy ghi `NOT_RUN`; không gắn PASS tiền nhiệm cho code vừa sửa. Review độc lập tập trung RF-01–05 và các nhánh thực sự bị thay đổi.

Increment hiện tại là **APP-05.4b — Nạp, giao tiếp và báo cáo toàn dòng**. APP-05.4a đã merge qua PR #155. Đích nghiệm thu tổng thể là Owner tự chốt ca có A15 và người/hạn, mở lại trình duyệt hôm sau, xử lý tiếp bằng đơn gốc và xem lịch sử cả hai ngày. Chặng nghiệm thu tích hợp thuộc APP-05.7. [PR nháp báo cáo AR theo dòng Confirm](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/157).
