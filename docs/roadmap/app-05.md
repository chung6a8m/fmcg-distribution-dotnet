# APP-05 — Hoàn tất ca DEV và tiếp tục việc tồn ngày sau

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Ngày chốt roadmap:** 24/11/2026. **Trạng thái:** xem [APP-05 status](../status/app-05-status.md). Biên nhận đã có: [APP-05 receipts](../receipts/app-05-receipts.md) và các mục dẫn tại phần 5. Phạm vi xác minh là synthetic DEV và CI.

**Goal:** Owner tự hoàn tất một ca trên laptop Windows với dữ liệu mẫu APP-03: xử lý A chưa giao, đóng chuyến có bàn giao trách nhiệm, rồi tiếp tục đúng đơn và nguồn vào ngày sau trên cùng database.

**Architecture:** mở rộng các command/read model C/D/F và phiên APP-03 hiện có. Thêm quyết định chốt bất biến, việc theo dõi có nguồn và liên kết thực hiện A qua nhiều chuyến; giữ một đơn, một Confirm và các sổ độc lập. Dapper dùng transaction tường minh, migration tiến trên LocalDB có ownership/nonce.

**Tech Stack:** toolchain đã pin trong repo: .NET 10.0.404, ASP.NET Core, Dapper, FluentMigrator, SQL Server LocalDB theo tenant, React/TypeScript/Vite, Node 26 LTS, Tailwind v4; xUnit v3/MTP v2 và Playwright.

**Spec:** [Thiết kế ca DEV và bàn giao ngày sau](../superpowers/specs/2026-11-24-dev-daily-close-design.md). Roadmap này là nơi quản lý thứ tự, phạm vi, hợp đồng và điều kiện nghiệm thu APP-05; hồ sơ status chỉ mô tả tiến độ, không cấp authority; spec giữ lý do, quy tắc và các phương án đã cân nhắc. Không lập thêm một kế hoạch trùng nội dung.

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

| Increment | Phụ thuộc | Đầu ra review được |
| --- | --- | --- |
| **APP-05.0 — Ổn định CI nền** | Baseline hiện tại | Chẩn đoán timeout, điều chỉnh budget hoặc tách job mà giữ coverage/artifact; CI main hoàn tất |
| **APP-05.1 — Readiness và việc tồn** | APP-03; CI .0 đã đạt | Đọc cuối chuyến; giao người/hạn/hành động cho nguồn A/D/F; fixture có phiên bản |
| **APP-05.2 — Đóng chuyến** | .1 | CLOSED/CLOSED_WITH_OPEN_CASES nguyên tử, gồm chuyến có đơn nhưng chưa nạp; biên bản và replay |
| **APP-05.3 — Chọn chuyến và chuyển HELD** | .2 | Liên kết thực hiện kế nhiệm và chuyển đúng quyền đã giữ, không đổi đơn gốc |
| **APP-05.4a — Reserve phần chưa giữ** | .3 | Giữ bổ sung có nguồn trên chuyến kế nhiệm; thiếu tồn vẫn là việc chờ |
| **APP-05.4b — Nạp và giao tiếp** | .4a | Load/Delivery trên chuyến mới; bảo toàn lot/cost/snapshot và due; báo cáo toàn dòng |
| **APP-05.5 — Đóng phần A còn lại** | .4b | Đề nghị/duyệt độc lập, giải phóng đúng phần giữ, hoàn tất việc theo nguồn |
| **APP-05.6 — Công việc ngày sau và phục hồi** | .2, .4b, .5 | Bàn công việc theo người/hạn; tái giao có audit; browser mới tìm lại nguồn/lệnh |
| **APP-05.7 — Nghiệm thu trọn ca** | .0–.6 | Hai ngày cùng DB, đủ nhánh sạch/có tồn/bị chặn/chưa nạp, direct operator và oracle độc lập |

Trạng thái hiện hành: [APP-05 status](../status/app-05-status.md). Biên nhận chỉ mô tả bằng chứng đã có; thứ tự, hợp đồng và điều kiện nghiệm thu vẫn theo roadmap này.

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
- [x] Commit `ci: stabilize LocalDB validation budget` đã merge trong PR #144; các cải tiến tiếp theo ở #145–#149. Ghi run/source thực và trạng thái trong hồ sơ status/receipt của increment; .0 đóng theo full main PASS cuối, không chỉ theo thay đổi timeout.

#### Biên nhận lịch sử APP-05.0 — thay đổi nguồn, chờ CI và review (24/11/2026)

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-0-history). Các điều kiện ở roadmap không thay đổi.

#### Biên nhận closeout APP-05.0 — 25/11/2026

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-0-closeout). Các điều kiện ở roadmap không thay đổi.

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

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-1-closeout). Các điều kiện ở roadmap không thay đổi.

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

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-3-closeout). Các điều kiện ở roadmap không thay đổi.

- Kết quả thuộc LocalDB synthetic DEV. Reserve phần chưa giữ chuyển sang .4a. Nạp/giao kế nhiệm thuộc .4b. Nghiệm thu ca tích hợp của Owner tại .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

### APP-05.4a — Reserve riêng cho phần chưa giữ

Migration tiến `202611250004` bổ sung liên kết bất biến từ quyền Reserve tới binding, dòng A, SKU, kho và chuyến đích. Dùng `-ASuccessor` trên phiên mới hoặc Resume phiên DEV đã có. Kho cần quyền hiện hành và phân công C.3 trên cả chuyến gốc lẫn chuyến đích. Từ **Đơn hàng**, mở dòng trên chuyến gốc đã đóng, chọn binding và kho rồi **Giữ bổ sung**. UI hiển thị lượng đã xếp, đã giữ và còn chờ giữ. Nạp/giao quyền kế nhiệm thuộc APP-05.4b.

API `GET /reserve-options` và `POST /reserve-unheld` dùng chung tiền tố dòng A của .3. Lệnh Reserve nhận CommandId, FollowUpId, ConfirmSourceId, BindingId, DestinationTripId, WarehouseId, Quantity và ExpectedRevision. Transaction kiểm lượng trống của binding và phần chưa giữ toàn dòng. C.2 A, D.1 B và Reserve kế nhiệm dùng cùng khóa tenant/kho/SKU trong profile tích hợp. Cấp giữ ghi cùng giao dịch quyền, pool, lô FEFO, nguồn `STOCK_ALLOC`, hiệu ứng `RIGHT_A` và biên nhận. Replay kiểm lại quyền và cùng payload.

**Biên nhận local.** `./scripts/Run-App03Dev.ps1 -Action VerifyASuccessor` lượt `a45c4009402a44eca5205d1eee447b5f` đạt 4/4 Chromium và 86/86 đối chiếu SQL, zero diff. Ca 5/4/3/1/2 còn 4 EA, chuyển đúng HELD3, rồi giữ thêm 1 EA qua UI. Confirm5 chưa Reserve nhận đủ 5 EA qua binding sau một lỗi precommit có chủ đích và replay cùng command. Hai actor và Trip tranh một pool không quản lý lô bằng Reserve A và quota D.1 B. Thiếu hàng giữ nguyên phần chờ. Tồn vật lý, cost, AR và tenant đối chứng được giữ nguyên. C.3 legacy chưa thấy quyền nạp mới. SHA256 `run.json` `7218866C05BAF9E65053AC920E1C8282FF52C9116F6F459E455232719E3D3738`.

**Bổ sung theo review PR #155.** API Đơn hàng trả quyền Reserve kế nhiệm theo dòng và phân công Kho trên chuyến gốc. UI giữ nguyên yêu cầu đang chờ khi mất quyền. Ca Chromium bỏ ACK sau commit, kiểm sai actor/tenant, thu hồi quyền từng chuyến, rồi cấp lại để replay đúng yêu cầu và biên nhận. Oracle SQL đối chiếu đúng một quyền và hiệu ứng. Fixture synthetic mới tách lô FEFO 2 EA và 4 EA còn hạn, lô 5 EA hết hạn và lô gốc đã hết khả dụng. Oracle đối chiếu từng mã lô, hạn, kho, SKU và lượng. Khi MAIN còn 5 EA nhưng hết lô đủ điều kiện, lệnh trả từ chối và giữ nguyên quyền, lô, nguồn và sổ phân bổ. Lượt local `b11191a77b624655abaf39849d959096` đạt Chromium 4/4 và 109/109 đối chiếu SQL, zero diff. SHA256 `run.json` `68C2E480D2FB62C11828D02EBBD2541467AD35F2ADC3B7CF0FDB32A591D4E620`.

#### Closeout APP-05.4a — 25/11/2026

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-4a-closeout). Các điều kiện ở roadmap không thay đổi.

- Kết quả thuộc LocalDB synthetic DEV. Nạp, giao liên chuyến và báo cáo toàn dòng chuyển sang .4b. Nghiệm thu ca tích hợp của Owner thuộc .7, các gate physical/PREPROD/EV/P0/Pilot/Go-Live theo mốc riêng.

### APP-05.4b — Nạp, giao tiếp và báo cáo toàn dòng

**Files:** `src/FmcgDms.Api/Persistence/App02c3Load.cs`, `App02c3Reads.cs`, `App02DevF03Lots.cs`, `App02c4Delivery.cs`, `App02c4Reads.cs`, `App02c4ShipTo.cs`, `App02c5Report.cs`, `App03HandoffReads.cs`, `App03OwnerReport.Sql.cs`; `web/src/sales/LoadWorkspace.tsx`, `DeliveryWorkspace.tsx`, `OrderWorkspace.tsx`. Test mới dự kiến `App05SuccessorDeliveryTests.cs`; mở rộng runner/evidence C.3/C.4/F03.

- [x] Kiểm nạp lại HELD đã trả, từ chối cấp giữ từ lô hết hạn ở ngày mới, quyền/nguồn sai, hai lệnh giao tranh cùng lượng và đối chiếu tiền qua nhiều chuyến.
- [x] Load/Delivery xác thực binding kế nhiệm và quyền trên Trip mới, giữ Order/Confirm/snapshot cũ. Phân biệt lượng chưa từng nạp với lượng trả có source; không trừ lifetime Loads khiến lượng trả bị mất hoặc được dùng hai lần.
- [x] Bảo toàn lot/UoM/source cost và residual; tổng payable/discount/tax rounding theo toàn dòng qua mọi Trip. Due của lần giao sau lấy ngày giao sau + N gốc. Không amendment giá/terms/ShipTo chỉ vì xếp chuyến.
- [x] UI và báo cáo hiện Trip gốc cùng các Trip thực hiện, drill-down delivery/source; MAIN không bị cộng lặp. Khi giao đủ theo nguồn, follow-up A hoàn tất; D.4 liên quan hoàn tất theo nguồn/báo cáo riêng.
- [x] Chạy hai fixture mục 6, hồi quy C.3/C.4/F03/F04, tenant đối chứng và race; commit đề xuất: `feat: load and deliver A remainder across business days`.

**Bước đầu — báo cáo AR theo dòng đơn.** Bộ lọc `orderLineId` dùng mã dòng Confirm gốc. Chi tiết và bản xuất giữ mã đơn, mã dòng, chuyến gốc, chuyến thực giao và nguồn Confirm/nạp/giao. Quyền đọc vẫn theo phân công trên chuyến ghi nhận lần giao. Lượt DEV `e910b63ecc464c9cb4b56cf085bc5033` kiểm tra bộ lọc, bản xuất, dữ liệu đối chứng và ca không có dòng khớp. Chromium đạt 1/1, SQL 13/13 khớp, SHA256 đối chiếu `E53E8249BE0710806B2CD4D9FA18818A622EDE8F7BCD1B810DA430B5B0B2A7BD`.

**Bổ sung theo review PR #157.** Gửi mã dòng sai định dạng ẩn kết quả và nút xuất cũ, đưa focus về trường lỗi. Sau khi sửa, báo cáo hiện mã dòng đang áp dụng. Fixture C.5 có hai dòng đã giao trên cùng một Confirm với công nợ 375 và 44. Ca kiểm đối chiếu riêng số lần, số dư và nguồn Confirm/nạp/giao trong API và bản xuất từng dòng. Thử tạm điều kiện lọc theo đơn làm ca kiểm nhận 419 ở dòng 375 và thất bại đúng kỳ vọng. Lượt C.5 `84b42742a2474cb48dd7d1b2916ca351` đạt Chromium 1/1, SQL 24/24 khớp. SHA256 đối chiếu `445B6A1D79238139055B120F1485A33237E196E8DC7E17E11380459A0E2ED3EC`. Hồi quy C.4 `864a9d126e964c149706d54a50daff54` đạt Chromium 1/1 và SQL 13/13.

**Biên nhận merge phần báo cáo — 26/11/2026.** PR #157 đã merge lúc 07:17 ICT, source `29982462741d13c4062498f55b2a9231cf65bf9d`, merge `5ed82e7687c79c128acf1dbaa16619548ab1d2bc`. Review vòng hai xác nhận cả P2 và P3 đã xử lý. Final source đạt 10/10 workflow và 25/25 checks, APP-00A attempt 3. Trên đúng merge, 10/10 workflow và 25/25 checks đạt ngay attempt 1. Artifact C.5 `0953f78771ac49488763ffd383f780ff` có SQL 24/24 khớp và zero effect ở tenant đối chứng. SHA256 `15F167CBE1EDD174D23F44AAA0CA92539B67FA79CB58E2C920188637832F19AB`. [CI trên main](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45727631315).

Để bật nạp và giao liên chuyến, migration cần ràng buộc manifest với binding, đúng quyền nền và chuyến thực hiện. Nguồn của Reserve mới và nguồn của HELD chuyển tiếp đi qua hai nhánh rõ ràng. C.3 cần đối chiếu phần đã dùng của từng thế hệ quyền theo lượng và lô. C.4 cần khóa theo dòng Confirm khi tính phần còn và làm tròn, đồng thời khóa chuyến thực hiện khi ghi evidence. C.5 và đường mở báo cáo từ hồ sơ đơn cần đọc theo binding của manifest và phân công trên từng chuyến thực giao. Đích kiểm chứng là báo cáo cùng mã dòng với hai chuyến khác nhau sau khi chạy các fixture mục 6.

**Nạp và giao phần Reserve mới.** Migration tiến `202611260001` ràng buộc Delivery C.4 với binding, quyền giữ đích và dòng Confirm gốc. Màn nạp và giao hiển thị chuyến gốc trên dòng kế nhiệm. C.5 lấy scope từ chuyến thực giao và giữ mã nguồn gốc để đọc toàn dòng. Lượt `61597f09e5dc4b048646d17a8290b2d7` chạy Chromium 4/4 và SQL 113/113, zero diff. Ca 5/4/3/1/2 giao thêm 1 EA đã Reserve ở chuyến kế nhiệm và kiểm chứng đúng lô, hạn từ ngày giao, lượng MAIN, replay và tenant đối chứng. SHA256 `run.json` `4C10CC713DC279602933DA2926BC0305FA256E1699D057C40625108A358C2764`. APP-05.4b tiếp tục với nạp lại HELD có nguồn và hoàn tất công việc toàn dòng.

Review PR #159 bổ sung kiểm quyền mở báo cáo qua chuyến đích và ẩn lượng giao khả dụng khi chuyến kế nhiệm đã qua ngày DEV. Lệnh mới trên chuyến quá ngày trả từ chối trước bước ghi bằng chứng. Lượt `7472969b81514841953bc2f7ca0f95c3` đạt Chromium 4/4 và SQL 114/114, zero diff. SHA256 `run.json` `71AA8FD50E1C80943F091191F44ADE50ABC2EFDE98E790EBE7E5413A225366A5`.

**Nạp và giao HELD có nguồn.** Migration `202611260002` tạo quyền thực hiện khi C.3 nạp lần đầu từ đúng thế hệ HELD. Quyền gốc giảm tương ứng, lô có nguồn chuyển giữ nguyên, tổng giữ tại MAIN chỉ giảm khi nạp vật lý. C.4 và C.5 dùng đúng dòng Confirm qua hai chuyến. Follow-up A biến mất khỏi việc hiện tại sau khi giao đủ, biên bản gốc vẫn giữ. Lượt `59b95837810640d39426d94ffe9cac5c` đạt Chromium 4/4 và SQL 118/118, zero diff, gồm 40/25/15, 5/4/3/1/2, cuộc đua giao cùng manifest, tiền lũy kế và tenant đối chứng. SHA256 `run.json` `4D16CFE8A65FC97CE376097DE9C0397F6D96A607C41B90535B414848EB84832D`. Chuyển lại phần còn sau khi chuyến kế nhiệm đã qua ngày cần đường phục hồi có nguồn riêng.

**Closeout APP-05.4b — 26/11/2026**

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-4b-closeout). Các điều kiện ở roadmap không thay đổi.

Phạm vi nghiệm thu là synthetic DEV cho chuyến kế nhiệm hợp lệ. Phần đã xếp lên chuyến kế nhiệm quá ngày cần đường phục hồi có nguồn riêng. Báo cáo D.4 và công việc kế toán giữ mốc xác nhận độc lập. Triển khai tenant, Pilot và nghiệm thu vận hành theo mốc riêng.

### APP-05.5 — Đóng phần còn bằng nguồn được duyệt

**Files:** command service/binding .3–.4, cơ chế approval/authority hiện có, follow-up và UI chi tiết; tạo `App05ARemainderClosure.cs`, `App05ARemainderClosureTests.cs` khi cần. Giữ correction/refund F ở nguồn nghiệp vụ tương ứng.

- [x] Kiểm ca tự duyệt, payload đổi sau đề nghị, mất quyền, cạnh tranh với Transfer/Reserve/C.4, hàng còn trên xe và evidence giao chưa xử lý.
- [x] Tạo đề nghị có source/quantity/reason; actor độc lập đủ quyền duyệt đúng version. Chỉ kết thúc nghĩa vụ chưa giao và release phần HELD hợp lệ; không đảo phần đã giao hoặc giảm AR đã ghi vì thao tác này.
- [x] Recheck toàn dòng và right generation dưới transaction/khóa chung; giải quyết partial closure, replay, payload conflict. Việc chỉ xong khi phần nghĩa vụ còn lại bằng 0 theo delivery/nguồn duyệt.
- [x] UI thể hiện người đề nghị/người duyệt, nguồn release và phần còn; xác minh conservation/financial no-effect cùng tenant guards; commit đề xuất: `feat: approve closure of undelivered A remainder`.

**Bước đầu — đề nghị đóng có nguồn.** Migration tiến `202611260003` ghi đề nghị bất biến với dòng Confirm, hồ sơ bàn giao, người đề nghị, lượng, lý do và revision hiện hành. Người có quyền gửi đề nghị từ biên bản chuyến đã đóng và có thể tra lại khi mất ACK. Lượt synthetic DEV `691eb905896346f3a11f5de3fd7ddf17` đạt Chromium 4/4 và SQL 121/121, zero diff. Ca kiểm dùng biên nhận UI bị mất ACK, replay cùng lệnh, đổi payload, trùng ID với lệnh chuyển HELD, revision cũ và quyền sai. SHA256 `run.json` `4D4D80E0C7C39988B75C7B11CA8151C6006D6E97062E9923EA0D051C590D8292`. Đề nghị ở trạng thái chờ duyệt và giữ nguyên tồn, sổ công nợ và nghĩa vụ giao. Duyệt độc lập và giải phóng HELD được kiểm chứng riêng ở bước sau.

**Duyệt và giải phóng có nguồn.** Migration tiến `202611260004` ràng buộc phê duyệt với đúng đề nghị và ghi nguồn phát sinh, lượng giải phóng theo thế hệ quyền, kho và lô. Ops có phân công hiện hành duyệt dưới danh tính khác maker. Duyệt trừ lượng chưa giữ trước, sau đó giải phóng đúng phần HELD tại MAIN. Chặn duyệt khi binding còn nghĩa vụ thực hiện, lượng còn trên xe hoặc bằng chứng giao chưa xử lý. Phần phải giao được tính từ lượng Confirm, đã giao và đã đóng được duyệt. Khi phần này về 0, công việc hiện tại hoàn tất. Biên bản đóng gốc vẫn tra được cùng mã nguồn và người duyệt.

Lượt synthetic DEV `1653d904df61411b8499044d566beed1` đạt Chromium 6/6 và SQL 129/129, zero diff. Ca kiểm có hai lần gửi đồng thời cùng mã, tự duyệt, sai tenant, thu hồi scope, đổi payload, đề nghị cũ, binding đang mở, mất ACK sau COMMIT và phần lẻ 0,999 + 0,001. Ca đua độc lập duyệt và chọn chuyến kế nhiệm cùng dòng cho đúng một kết quả có hiệu lực. Trong lượt này duyệt thắng, giải phóng một HELD và lệnh chọn chuyến trả revision đã cũ. Đối chiếu độc lập xác nhận giữ nguyên Confirm, chứng từ giao, tồn vật lý, COGS, AR và tenant đối chứng. .NET đạt 388/388. SHA256 `run.json` `9EEFE5AE69589E0BBE2E0832ABA2DD0902D686F0FBDC783834BE91185AF48258`. Lượt local này có trước lượt CI source cuối và không chạy các ca đua trực tiếp với Transfer, Reserve hoặc Delivery.

**Bổ sung theo review PR #161 và #162 — 26/11/2026.** Biên nhận đề nghị trả mã Confirm, follow-up và revision đã lưu để đối chiếu cùng mã lệnh, người lập, lượng và lý do. Cùng mã nhưng khác revision trả xung đột và giao diện giữ yêu cầu đang tra cứu. API trả riêng trạng thái đề nghị, lý do bị chặn từ nguồn và quyền duyệt của người xem. Đề nghị lỗi thời hiển thị yêu cầu lập lại theo số liệu hiện tại.

Lượt synthetic DEV `f0f3daab3f6048ebbc55798608827439` đạt Chromium 6/6 và SQL 139/139, không có chênh lệch. Hai client chạy lệnh duyệt đồng thời với Transfer HELD, Reserve và giao C.4. Lệnh kế nhiệm hợp lệ có hiệu lực, lệnh duyệt bị từ chối theo tình trạng nguồn. Âm chứng xác nhận còn hàng trên xe, C.4 RECEIVED và biến thể PENDING trên đúng dòng intake đều chặn duyệt. Oracle kiểm mã lệnh bị từ chối, Confirm, lượng giao, HELD, MAIN, lô, AR, COGS, tồn vật lý và tenant đối chứng. SHA256 `run.json` `F24E214CD2B9C380CD3D2A1630C90BAB30384C0DD96D1FA54A331DD68D9B491B`. Kiểm chứng này thuộc phạm vi synthetic DEV.

#### Closeout APP-05.5 — 26/11/2026

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-5-closeout). Các điều kiện ở roadmap không thay đổi.

APP-05.5 hoàn tất trong phạm vi triển khai synthetic DEV. Bàn công việc, phục hồi từ browser mới và nghiệm thu trọn ca giữ mốc APP-05.6/APP-05.7. Tiếp tục theo dõi bootstrap LocalDB và timeout HTTP ở CI nền. Nghiệm thu tenant, Pilot và vận hành thực tế theo mốc riêng.

### APP-05.6 — Bàn công việc ngày sau và browser mới

- Bàn việc DEV tại `/work/app05-work` đọc việc lúc chốt, phân công hiện tại và lịch sử từ máy chủ. Bộ lọc theo người, hạn và trạng thái. Đổi người/hạn yêu cầu lý do và phiên bản phân công hiện hành. Quá hạn lấy theo clock DEV. Mỗi việc dẫn về hồ sơ gốc và chuyến kế nhiệm mà actor có quyền đọc.
- D.4 dùng cửa sổ `From < Cutoff <= AsOf <= now` và `From <= OccurredAt < Cutoff`. Báo cáo hoàn tất việc phải khớp Delivery, dòng nguồn, timestamp và hash của bằng chứng cam kết. NextDay chỉ chuyển clock/chuẩn bị ngày mới. Biên bản chốt giữ nguyên phân công, nguồn và tổng số việc tại lúc đóng.
- Lịch sử lệnh phân trang theo actor, tenant và quyền hiện hành của từng Trip, tra được theo mã lệnh hoặc chuyến. Có kết quả đóng chuyến, phân công, chọn chuyến, chuyển HELD, giữ hàng bổ sung, nạp/giao kế nhiệm và đề nghị/duyệt đóng phần A. Các lần nạp đã áp dụng được nối từ manifest có quyền kế nhiệm. Receipt kế nhiệm bị từ chối từ trước .6 thiếu dữ liệu phạm vi gốc để đưa vào lịch sử, nhưng vẫn replay qua command gốc với đúng payload và quyền hiện hành. Yêu cầu chưa có receipt cần tra lại trước khi gửi cùng mã và dữ liệu gốc.
- `VerifyASuccessor` bao phủ mất ACK, context browser mới sau Stop/NextDay/Resume, replay và thay payload, sai tenant hoặc bị thu hồi quyền, bộ lọc và đổi người/hạn từ bàn phím, đối chiếu snapshot và audit SQL. `VerifyCloseReadiness` thêm ca sai hash nguồn và biên cutoff. [Kế hoạch kiểm chứng](../superpowers/plans/2026-11-26-app-05-6-work-and-recovery.md).

#### Closeout APP-05.6 — 26/11/2026

[Hồ sơ gốc và toàn bộ biên nhận chuyển tới APP-05 receipts](../receipts/app-05-receipts.md#app05-6-closeout). Các điều kiện ở roadmap không thay đổi.

Bằng chứng APP-05.6 áp dụng cho synthetic DEV. APP-05.7 sở hữu nghiệm thu ca tích hợp. Nghiệm thu tenant, Pilot và vận hành thực tế theo mốc riêng.

### APP-05.7 — Nghiệm thu ca tích hợp

Runner `VerifyDailyClose` chạy một ca qua ngày 23–24/11 trên một cặp LocalDB theo nonce, gồm đóng chuyến có việc tồn, chuyến sạch, chuyến bị chặn, zero-manifest và chuyến chưa sử dụng. Ngày hai dùng browser context mới, khôi phục lệnh cũ và ghi giao tiếp từ nguồn Confirm. SQL đối chiếu nguồn, số dư, giá vốn, phải thu, cutoff D.4, lô, quyền giữ, biên bản và tenant đối chứng.

[Manifest fixture](app-05-7-fixture.json) và [hướng dẫn, biên nhận local](app-05-7-acceptance.md) ghi các checkpoint và hash. CI thêm runner tự động vào job `app03` cùng các bài hồi quy hiện hành. F09 All và hai thao tác trên Edge có biên nhận chạy local. Owner tự thao tác toàn ca giữ trạng thái `NOT_RUN`.

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

Không chạy toàn bộ các lệnh trên trong mọi PR chỉ để tăng số PASS. Chọn test/runners theo rủi ro và file thay đổi, giữ gate repo bắt buộc; .7 chạy acceptance mới và hồi quy cuối. Playwright business integration cần launcher/API/SQL thực tương ứng, không chỉ `npm test` hoặc browser fixture mock. Lệnh `VerifyDailyClose` thuộc APP-05.7 và có hướng dẫn chạy trong biên nhận riêng.

Biên nhận mỗi increment ghi scope, command, môi trường, source/tree, run ID, expected/actual/diff, command/source IDs, artifact và SHA-256. Tách `IMPLEMENTED`, `DEV_VERIFIED_LOCAL`, `CI_VERIFIED`, `MERGED` và thao tác trực tiếp của Owner. Việc chưa chạy ghi `NOT_RUN`; không gắn PASS tiền nhiệm cho code vừa sửa. Review độc lập tập trung RF-01–05 và các nhánh thực sự bị thay đổi.

Increment **APP-05.7** ghi nghiệm thu tích hợp synthetic DEV hai ngày. Ca do Owner tự thao tác và các mốc tenant, Pilot, PREPROD hoặc vận hành thực tế theo đúng các gate ở roadmap. [Biên nhận APP-05.7](app-05-7-acceptance.md).
