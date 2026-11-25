# APP-05 — Hoàn tất ca DEV và tiếp tục việc tồn ngày sau

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Ngày chốt roadmap:** 24/11/2026. **Trạng thái:** `ROADMAP_APPROVED / APP05_0_SOURCE_CANDIDATE / CI_PENDING`. Commit lập roadmap là `DOCS_ONLY`; các checkbox chỉ ghi nhận công việc đã thực sự thực hiện; APP-05.1–.7 chưa triển khai.

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
| **APP-05.0 — Ổn định CI nền** | Baseline hiện tại | Chẩn đoán timeout, điều chỉnh budget hoặc tách job mà giữ coverage/artifact; CI main hoàn tất | `SOURCE_CANDIDATE / CI_PENDING` |
| **APP-05.1 — Readiness và việc tồn** | APP-03; CI .0 trước merge code | Đọc cuối chuyến; giao người/hạn/hành động cho nguồn A/D/F; fixture có phiên bản | `NOT_STARTED` |
| **APP-05.2 — Đóng chuyến** | .1 | CLOSED/CLOSED_WITH_OPEN_CASES nguyên tử, gồm chuyến có đơn nhưng chưa nạp; biên bản và replay | `NOT_STARTED` |
| **APP-05.3 — Chọn chuyến và chuyển HELD** | .2 | Liên kết thực hiện kế nhiệm và chuyển đúng quyền đã giữ, không đổi đơn gốc | `NOT_STARTED` |
| **APP-05.4a — Reserve phần chưa giữ** | .3 | Giữ bổ sung có nguồn trên chuyến kế nhiệm; thiếu tồn vẫn là việc chờ | `NOT_STARTED` |
| **APP-05.4b — Nạp và giao tiếp** | .4a | Load/Delivery trên chuyến mới; bảo toàn lot/cost/snapshot và due; báo cáo toàn dòng | `NOT_STARTED` |
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
| `AssignASuccessor` | CommandId, FollowUpId, exact OrderLine/Confirm source, SuccessorTripId, QuantityBase, expected revision; tạo binding kế nhiệm có ID ổn định kể cả HELD=0, không cần StockRight, không cấp giữ hoặc tạo physical/financial effect | .3 |
| `TransferARemainder` | CommandId, FollowUpId, binding kế nhiệm, exact source/right generation, QuantityBase, expected revision; trả nguồn transfer bảo toàn HELD, không tạo physical/financial effect | .3 |
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
- [ ] Xác minh YAML/runner và chạy CI trên source cuối. Đối chiếu danh sách gate trước/sau, upload artifact cả khi fail và cleanup đúng phạm vi. Có lượt main hoàn tất trước khi đóng .0.
- [ ] Commit đề xuất: `ci: stabilize LocalDB validation budget`; ghi run/source thật trong PR và hàng trạng thái, không đánh dấu .0 xong chỉ vì đổi timeout.

#### Biên nhận APP-05.0 — thay đổi nguồn, chờ CI và review (24/11/2026)

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

**Điều kiện còn mở:** PR final HEAD phải có APP-00A SUCCESS, kiểm step 72–75 đầy đủ, artifact và source/workflow SHA thực tế, cleanup đúng ownership/nonce; giữ toàn bộ regression và independent review. Sau merge cần một lượt main hoàn tất với budget mới trước khi close .0; không đánh dấu hoàn tất theo lượt PR cũ. Lỗi LocalDB login timeout riêng tại [B.3 run 45421920678](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45421920678) trên main roadmap là lỗi bootstrap, chưa có kết quả SQL test; không coi việc tăng budget APP-00A là bản vá B.3. Không thay đổi PREPROD/EV/P0/Pilot hay phạm vi nghiệm thu. Trạng thái hiện tại `SOURCE_CANDIDATE / CI_PENDING`.

### APP-05.1 — Readiness, việc tồn và fixture dùng chung

**Files hiện có:** `src/FmcgDms.Api/Persistence/App03HandoffReads.cs`, `App01dD3UnloadPlan.cs`, `App03WorkspaceRoutes.cs`, `App03OwnerReport.cs`, `App03OwnerReport.Sql.cs`, `App03FinanceCaseLinksSchema.cs`; `src/FmcgDms.Demo/App03DayBootstrap.cs`, `App03DevSession.cs`; `web/src/workspace/Workspace.tsx`, `web/src/sales/MixedTripWorkspace.tsx`. Có thể tạo các file nhỏ `App05CloseReadiness.cs`, `App05FollowUps.cs`, migration tương ứng và `web/src/sales/TripCloseWorkspace.tsx`; đây là file mới dự kiến. Test mới dự kiến: `tests/FmcgDms.Api.Tests/App05CloseReadinessTests.cs`, `web/e2e-app05/journey.spec.ts` và `web/playwright.app05.config.ts` cho đường API/SQL thật.

- [ ] Viết ca thất bại có ý nghĩa cho thiếu người/hạn, wrong tenant/revoked, A remaining khác HELD, AR bình thường, D.4 chưa tới cutoff, D/F nguồn trùng, chuyến bootstrap trống và xe dùng chung. Chạy để thấy lỗi do thiếu behavior mới.
- [ ] Bổ sung schema/read/assignment audit theo contract mục 4. Tách hard blocker (custody/offer, hàng chưa trả, evidence pending, source drift/unknown) khỏi việc được phép chuyển tiếp. Chỉ gắn F02/credit vào Trip khi có source link thật, không suy từ cùng actor/customer.
- [ ] Bootstrap version mới bổ sung fixture mục 6 bằng source có định danh ổn định. Giữ opening/master/policy hiện có; upgrade/replay trên phiên cũ không seed lại chứng từ và không nhân đôi dữ liệu. Ghi quyền Tenant Ops, assignee, dispatcher và maker/checker rõ trong fixture.
- [ ] Nâng profile/manifest đồng bộ với `App03DevSession.ValidProfile`, `App03A1Profile`, `ScratchMigrator`, `Tenancy/TenantConnections.cs` và startup `Program.cs`; kiểm exact migration steps, Resume phiên tiền nhiệm và hồi phục upgrade gián đoạn. Dùng migration tiến mới, không sửa migration lịch sử để làm phiên cũ khớp.
- [ ] UI mở từ workspace chung, hiển thị blocker/hành động, nguồn, số lượng/đơn vị, người/hạn; liên kết đúng hồ sơ D/F. Readiness chưa được quảng bá là đã đóng chuyến.
- [ ] Kiểm UI→API→SQL và tenant đối chứng, cập nhật hướng dẫn/phạm vi trong PR; commit đề xuất: `feat: add trip close readiness and follow-up assignment`.

### APP-05.2 — Quyết định đóng chuyến và lịch sử

**Files:** phần mới .1; `src/FmcgDms.Api/Persistence/App01dD3Unload.cs`, `App01dD3UnloadPlan.cs`, `App01dCashierUnloadService.cs`, `App02d3Cash.cs`, `App02d3Routes.cs`, `App01a2CommandService.cs`, các route/command kiểm trạng thái Trip; `web/src/sales/TripCloseWorkspace.tsx` dự kiến. Tạo `App05TripClosure.cs`/migration và `tests/FmcgDms.Api.Tests/App05TripClosureTests.cs` khi phù hợp.

- [ ] Viết và chạy ca đỏ RF-01: close đua với source/assignment/evidence thay đổi, thiếu người/hạn, mất ACK, lỗi trước COMMIT, cùng ID đổi payload, ID mới sau chốt và quyền bị thu hồi. Kiểm các đường ghi cũ không được tiếp tục hoạt động trên chuyến đã đóng.
- [ ] Thêm nhánh bàn giao bằng 0 cho Trip có nghĩa vụ A nhưng chưa có manifest: chứng minh không có physical effect của riêng chuyến, không pending cash/evidence; chuyển RECONCILING có nguồn. Bao phủ Confirm có HELD và Confirm chưa Reserve, không tạo manifest/movement giả. Trip hoàn toàn chưa dùng hiển thị ngoài mẫu số kết ca, không tạo case rỗng.
- [ ] Close recheck trong transaction, tuần tự hóa với command nguồn liên quan; đối chiếu lock `APP01C:TRIP:<tenant>:<trip>` hiện dùng bởi D.3/C.4 intake và thứ tự khóa với cash offer/accept, unload, assignment, Reserve/transfer/cancel. Ghi closure, snapshot, follow-ups và command outcome nguyên tử. Server quyết định CLOSED khi sạch, CLOSED_WITH_OPEN_CASES khi chỉ còn việc được phép đã giao đủ thông tin. Không bắt TripId kế nhiệm.
- [ ] Lưu immutable snapshot và một closure/trip; UI tách việc mở lúc chốt khỏi việc mở hiện tại. Tồn tại follow-up chưa hoàn tất không mở lại quyền business write trên Trip cũ; correction sau chốt đi tiến theo D/F.
- [ ] Đối chiếu SQL không có movement/AR/COGS/thu/chi mới do close, chạy hồi quy D.3/.6 và command-state bị ảnh hưởng; commit đề xuất: `feat: close trips with durable follow-up handoff`.

### APP-05.3 — Liên kết kế nhiệm và chuyển HELD

**Files:** `src/FmcgDms.Api/Persistence/App01a2CommandService.cs`, `App02c2Commands.cs`, `App02c2Reads.cs`, `App02c3Schema.cs`, `App01b1PhysicalStock.cs`, `App02c4Schema.cs`, `App01c1PostingSchema.cs`, `App02DevF03Lots.cs`; migration và `App05AFulfillment.cs` dự kiến. UI chọn chuyến thuộc `TripCloseWorkspace`/chi tiết follow-up; test mới `App05AFulfillmentTests.cs`.

- [ ] Lập bảng FK/trigger/query trước khi sửa: `FK_SaleDelivery_AOrderIdentity` có TripId, manifest/right binding, C.3 join Trip của order/right và filter nguồn `STOCK_ALLOC`, C.4 posting join, công thức tổng nạp của F03. `FK_SaleDelivery_FrozenAConfirm` giữ exact Order/Line/SKU; không nhầm nó thành FK Trip và không bỏ bảo vệ Confirm. Đối chiếu thêm `IntegratedSchema.cs`, `App02d3UnloadSchema.cs` và `App03SettlementSchema.cs`; áp dụng migration tiến, giữ nhánh legacy.
- [ ] Viết ca đỏ hai chuyến đích tranh HELD, partial transfer, rollback/replay, sai right/source/tenant và HELD chưa nạp so với đã trả. Thêm Confirm chưa từng Reserve: có thể gán chuyến bằng nguồn OrderLine/Confirm, không yêu cầu một right chưa tồn tại. Chốt phương trình triệt tiêu old/new right generation trước migration.
- [ ] Thêm binding thực hiện có nguồn và FK/check tương ứng, giữ bản ghi cũ. `AssignASuccessor` chỉ xếp chuyến kể cả HELD=0, không cần right giả hay transfer số lượng 0; không nhân nghĩa vụ khi gán một phần hoặc cạnh tranh hai chuyến. Command transfer riêng dùng binding để chuyển đúng HELD, giữ tổng allocated MAIN và quantity/cost; không chạm physical/financial ledger. Không nới JOIN/FK chung chỉ để cho một Trip khác qua.
- [ ] UI chọn Trip đủ quyền/ngày/trạng thái, lượng chuyển không quá HELD; thể hiện còn phải giao và còn chờ giữ. Chọn Trip không hoàn tất công việc. Phần chưa chuyển vẫn có người/hạn.
- [ ] Kiểm upgrade dữ liệu trước migration, hai tenant, conservation và report không cộng mọi Granted qua thế hệ; commit đề xuất: `feat: transfer held A quantities to successor trips`.

### APP-05.4a — Reserve riêng cho phần chưa giữ

**Files:** `src/FmcgDms.Api/Persistence/App01a2CommandService.cs`, `App02c2Commands.cs`, `App02c2Reads.cs`, `App02DevF03Lots.cs` và binding .3; `web/src/sales/OrderWorkspace.tsx`, `LoadWorkspace.tsx`; mở rộng `App05AFulfillmentTests.cs` và hồi quy `App02c2Tests.cs`.

- [ ] Dùng ca Confirm5/Reserve4/Load3/Deliver1/Return2: remaining4, HELD3. Test đỏ việc chuyển4, tự cấp giữ khi chọn Trip, Reserve quá phần chưa giữ và hai yêu cầu tranh free pool.
- [ ] Dùng ca Confirm5/Reserve0: đóng qua bàn giao không manifest, chọn Trip kế nhiệm bằng `AssignASuccessor`, Reserve5 lần đầu qua binding đó rồi nạp/giao trong .4b. Nếu thiếu tồn, không cấp giữ và vẫn có người/hạn; không phụ thuộc transfer một StockRight chưa tồn tại.
- [ ] Bổ sung Reserve có source trên binding kế nhiệm, kiểm nghĩa vụ toàn dòng, quyền hiện hành, tồn đủ điều kiện/lô/FEFO và transaction. Transfer3 không tự tạo phần1; Reserve1 thành công mới cho nạp phần1.
- [ ] Khi thiếu tồn, trả lý do và giữ phần1 trong follow-up; UI phân biệt scheduled/held/loadable. Replay hoặc rollback không nhân đôi right/allocated.
- [ ] Kiểm cạnh tranh A/B trên pool dùng chung trong tổ hợp đã hỗ trợ, không đổi tổng physical/cost/AR do Reserve; commit đề xuất: `feat: reserve unheld A remainder on successor trips`.

### APP-05.4b — Nạp, giao tiếp và báo cáo toàn dòng

**Files:** `src/FmcgDms.Api/Persistence/App02c3Load.cs`, `App02c3Reads.cs`, `App02DevF03Lots.cs`, `App02c4Delivery.cs`, `App02c4Reads.cs`, `App02c4ShipTo.cs`, `App02c5Report.cs`, `App03HandoffReads.cs`, `App03OwnerReport.Sql.cs`; `web/src/sales/LoadWorkspace.tsx`, `DeliveryWorkspace.tsx`, `OrderWorkspace.tsx`. Test mới dự kiến `App05SuccessorDeliveryTests.cs`; mở rộng runner/evidence C.3/C.4/F03.

- [ ] Viết ca đỏ nạp lại HELD đã trả, lô hết hạn ở ngày mới, source/right sai, hai delivery tranh lượng còn của toàn dòng và partial deliveries qua nhiều Trip có làm tròn tiền.
- [ ] Load/Delivery xác thực binding kế nhiệm và quyền trên Trip mới, giữ Order/Confirm/snapshot cũ. Phân biệt lượng chưa từng nạp với lượng trả có source; không trừ lifetime Loads khiến lượng trả bị mất hoặc được dùng hai lần.
- [ ] Bảo toàn lot/UoM/source cost và residual; tổng payable/discount/tax rounding theo toàn dòng qua mọi Trip. Due của lần giao sau lấy ngày giao sau + N gốc. Không amendment giá/terms/ShipTo chỉ vì xếp chuyến.
- [ ] UI và báo cáo hiện Trip gốc cùng các Trip thực hiện, drill-down delivery/source; MAIN không bị cộng lặp. Khi giao đủ theo nguồn, follow-up A hoàn tất; D.4 liên quan hoàn tất theo nguồn/báo cáo riêng.
- [ ] Chạy hai fixture mục 6, hồi quy C.3/C.4/F03/F04, tenant đối chứng và race; commit đề xuất: `feat: load and deliver A remainder across business days`.

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

**Bước tiếp theo:** kiểm chứng và review **APP-05.0** trên PR final HEAD, hoàn tất lượt main sau merge, rồi **APP-05.1**. Đích nghiệm thu là Owner tự chốt ca có A15 và người/hạn, mở lại trình duyệt hôm sau, xử lý tiếp bằng đơn gốc và xem lịch sử cả hai ngày; các trạng thái này chỉ được công bố sau execution có bằng chứng.
