# APP-03.6 — Hiện trường bàn giao Thủ quỹ và Kho

## Làm việc trong phiên DEV

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Start
```

| Danh tính DEV | Công việc |
| --- | --- |
| `source` | Mở **Đối soát chuyến** từ **Chuyến và công việc**, kiểm tiền thực giữ và gửi đề nghị nộp tiền. Trong **Trả hàng**, chọn lần giao A và lô khách trả. Trong **Tạm ứng và hoàn ứng**, chọn khoản ứng của mình và ghi đề nghị hoàn. |
| `cashier` | Mở **Đối soát chuyến và nộp tiền**, kiểm tra mã đề nghị đang chờ và nhập số tiền đã kiểm đếm. Khi mã đề nghị thay đổi, nhập lại số tiền cho đề nghị mới. Dùng **Tạm ứng và hoàn ứng** khi nhận tiền hoàn ứng của nhân viên. |
| `receiver` | Sau khi chuyến hết tiền thực giữ và đề nghị chờ nhận, trả tồn xe. Dùng **Trả hàng** để nhận hàng khách trả vào cách ly và **Quà khuyến mại** để ghi thực nhận quà theo lần giao quà gốc. |
| `finance`, `qa` | Mở cùng hồ sơ trả hàng để duyệt và giải phóng hàng theo phân công. Kế toán có thể phát ứng, duyệt chi và duyệt đề nghị miễn thu hồi quà trong sổ nguồn tương ứng. |

Màn hình hiển thị VND cho các khoản của chuyến. Sổ tạm ứng F02 theo nhân viên và chứng từ, tách khỏi tiền khách, tiền thực giữ, quỹ đã nhận và công nợ. Khi mất phản hồi, dùng **Tra cứu yêu cầu** hoặc **Gửi lại cùng yêu cầu**. **Tải lại danh sách chuyến** cập nhật toàn bộ đề nghị chờ nhận và giữ chuyến đang xem. Khi kết nối lại, màn hình khóa lệnh mới cho đến khi tải thành công danh sách và số liệu chuyến đang chọn. Nếu tải lỗi, có thể tải lại danh sách hoặc tra cứu và gửi lại lệnh đã lưu.

## Tổ hợp DEV và nguồn dữ liệu

- `APP-03.6-v1` ghép C.1–C.5, D.1–D.3 và F01/F02/F03/F04/F05/F07 trên hai database LocalDB của cùng phiên. Start dùng profile này. Resume và NextDay nâng phiên dùng bootstrap .2–.5 sau khi xác minh instance, tên, tenant binding, ownership marker và nonce của cả hai database. Phiên seed .1b tiếp tục dùng A/F. Migration `202611240002` ghi nguồn lô trả xe và mốc cài bootstrap phân công. Bootstrap chỉ bổ sung danh tính, phân công, chương trình quà và tồn mở giả lập. Chương trình DEV cấp 1 quà cho mỗi 2 EA mua ròng của SKU R-05.
- Offer ghi đề nghị, giữ nguyên custody và fund. Accept dùng số tiền do thủ quỹ được phân công kiểm đếm, kiểm khớp đề nghị và người gửi độc lập, rồi ghi chứng cứ, trừ custody và tăng fund trong cùng transaction. D.3 giữ đúng tiền VND của A/F04 và B/F04 trong profile này.
- Trả tồn xe đọc nguồn nạp, giao và quyền giữ của chuyến. Hàng A trả về kho vẫn thuộc quyền giữ A. Lô trả gắn với dòng nạp và được hiển thị trong dấu vết lô của đơn. Quota B chưa tiêu thụ được giải phóng, kể cả quota chưa nạp. Xe dùng lại giữ lượng của chuyến khác. Warehouse return dùng cùng kho nguồn trong một chuyến và giữ chuyến ở `RECONCILING`. Chuyến đã giao hết vẫn có thể bàn giao để đối soát khi tiền đã nộp. Điều kiện đóng chuyến cuối nằm ở mốc sau.
- Hồ sơ khách trả gắn delivery và lô F05. Hàng nhận vào quarantine qua các bước kiểm đếm, duyệt và QA của đúng hồ sơ. F07 theo dõi quà cấp/nhận từ nguồn mua A đủ điều kiện và đề nghị miễn thu hồi. Danh mục và lệnh tạo/xác nhận đơn A cùng loại SKU quà khỏi hàng bán. F02 giữ riêng khoản ứng, chứng từ chi và hoàn ứng.
- Tổ hợp B kế thừa giá, quota, tiền mặt VND và SKU không quản lý lô của .5. B_NO_CHANNEL, ghi offline, F06, F08, D.4 và worker giữ ranh giới increment tương ứng. Nghiệm thu thiết bị và vận hành thuộc các mốc riêng.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifySettlement
```

Runner nâng phiên A/F đã có đơn và lần giao lên .5 rồi .6, đối chiếu nguồn trước/sau nâng và chạy Chromium qua các vai. SQL kiểm tra Accept, chân custody/fund, lượng theo lô, quyền A, quota B, sổ ứng, hồ sơ QA và quà ở tenant chính và tenant đối chứng. Sau Resume và NextDay, runner đọc lại cùng chứng từ và gửi lại cùng lệnh Accept. Các ngày kế tiếp kiểm xe dùng chung và giải phóng quota chưa nạp.

Artifact nằm trong `TestResults/app03-6/<RunId>/`. Workflow Windows chạy cùng lệnh và tải artifact.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** trên working tree của `codex/app-03-6` từ base `f39d5c4005433199ac78af63154e425061faf316`. Run `2c82c3f3e3f04589ab5d07ede0fd08a5` qua 1/1 kịch bản Chromium và đối chiếu SQL trên Windows LocalDB với hai tenant.

- Chuyến Mixed nhận 20.000 VND từ thủ quỹ độc lập. Offer có 0 hiệu ứng sổ. Accept có một chứng cứ kiểm đếm, giảm custody 20.000 VND và tăng fund 20.000 VND. Kiểm chặn kiểm đếm lệch, sai vai và truy cập chéo tenant. Sau mất ACK, reload, Stop, NextDay và Resume, lệnh Accept tra và replay về cùng mã nguồn.
- Kho trả 4 EA, gồm 1 EA có lô, giữ quyền A 3 EA tại kho và giải phóng quota B 1 EA. Chuyến Mixed và chuyến đã giao hết đều vào `RECONCILING`. Trên xe dùng chung, đối chiếu lượng tồn qua hai lệnh trả của hai chuyến lần lượt là 2 → 1 → 0 EA. Chuyến có quota chưa nạp giải phóng 1 EA và không ghi dòng trả vật lý.
- Hồ sơ trả khách qua đủ ba bước nhận vào cách ly, duyệt và QA. Sổ quà có một lần giao và một lần thực nhận trả. F02 phát 500.000 VND, nhận hoàn 200.000 VND và còn 300.000 VND, với AR và báo cáo tiền khách giữ nguyên sau các lệnh tạm ứng. AR của chuyến Mixed là 40.000 VND. Tenant đối chứng có 0 chứng từ trong các luồng được đối chiếu.

Hồi quy local qua APP-03.5 (run `3e4f62d87c2948948bd8efe0ed69af0b`) và D.3 độc lập (run `d37f5bbef1344dcfb7c20152b8f9a448`). Bộ .NET đạt 352/352, kiểm tra web đạt 23/23 và Playwright tổng đạt 56 bài. Một bài cài PWA standalone được bỏ qua theo cấu hình local.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `junit.xml` | `E487B6131254A2DD5328CF72D602903CFE438341DBCF3C62891CB5B74F8A9506` |
| `sql-review.json` | `B41863D101722773D3E18D2A8248A779C7B0496AEAED94D9709968DC717EE100` |
| `vehicle-reuse.json` | `8B931A45A65B0F7E43D2F8DE79B686BE1060AEC2878FC1775788A8425F17920B` |
| `quota-only.json` | `1458A3914E2DC0DE25EBCB82934D456834A2EF5A77002DC47F8AA7FB43416D0A` |

### Kiểm chứng sau review PR #140

- Run `ee34e5079ae84728a6ebf66d71511c70` qua Chromium và LocalDB hai tenant. Thủ quỹ giữ chuyến đang xem khi tải lại, thấy đề nghị của chuyến khác xuất hiện sau Offer và biến mất sau Accept. Bài hồi quy trình duyệt mô phỏng tải chậm, lỗi tải danh sách và lỗi tải chi tiết khi xác minh lại. Màn hình khóa lệnh mới, giữ mã lệnh chờ cho tra cứu và mở lại thao tác khi số liệu mới đã tải thành công.
- Gọi API tạo đơn trực tiếp bằng `GiftSkuId` thực của chương trình F07 trả `C2_SKU_INVALID`. Ảnh dòng SQL trước và sau ở hai tenant giữ nguyên cho bản nháp, đơn, quyền giữ, nguồn và sổ. Luồng SKU bán A và giao/nhận quà vẫn qua trong cùng kịch bản.

Hồi quy APP-03.2 và vòng đời .1b qua với profile .6 (run `9d418641ca95418aa18feb583896f94d`). Bài thử lại NextDay qua launcher dùng cùng profile với phiên đang kiểm, và ảnh dòng bao gồm phân công thủ quỹ cùng mốc bootstrap. D.3 độc lập qua (run `1f5eb3146bca47e59cb61a057bd8f49d`). Bộ .NET đạt 352/352. Playwright tổng đạt 57 bài, với một bài cài PWA standalone bỏ qua theo cấu hình local.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `junit.xml` | `384AB397B603B0A4F97AAB0C2F351C26065F334F2485E47205EE81ABB00961A9` |
| `gift-only-draft.json` | `187A4472F0DED9EF69FB509F1F4C331B9970777FF0799ACFCEA8EBE56A3BD6DE` |

### Kiểm chứng vòng review 2

Run `1e517805cc34439fa264759849ed8ab7` qua Chromium và LocalDB. Hai đề nghị 10.000 VND của cùng chuyến được kiểm ở hai phiên thao tác. Tải lại cùng mã giữ số đã nhập. Khi đề nghị sau thay đề nghị trước, màn hình hiện mã mới và để trống ô kiểm đếm. Bấm xác nhận khi ô trống không gửi Accept. Sau khi nhập lại, Accept ghi đúng mã đề nghị mới. Ca mất ACK và tra cứu lệnh gốc vẫn qua trong cùng run.

SHA-256 của `junit.xml` là `D22304386E463FD0780878F685CB0B0A163BEB66A8CDB541168A35BB5BFB2317`. Bộ kiểm tra web đạt 23/23, Playwright tổng đạt 57 bài với một bài cài PWA standalone bỏ qua theo cấu hình local.

APP-02C.6 qua lượt chạy lại local (run `a960755370a34bfdb6ba23355cbb9e71`, 15/15 ca theo tenant) sau khi lượt CI của HEAD trước bị timeout ở nhóm kiểm chứng nâng cao.

### Điều tra timeout HTTP sau NextDay (28/11/2026)

**INVESTIGATION / ROOT_CAUSE_UNCONFIRMED.** Main `686be2dadd38a4dfb8367621fb12e14e7682697a`, [APP-00A attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46136550283/attempts/2), [job app03](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46136550283/job/140964478734): Chromium PASS, SQL review ban đầu đã ghi; lần PrepareB sau ngày 25/11, profile APP-03.7-v1, thất bại tại GET /api/synthetic/app02c2/catalog với HttpClient.Timeout 3 giây khi đang đọc phản hồi. LocalDB READY. Bốn SQL cancellation trước đó trong browser phase không chứng minh nguyên nhân của request này. Không quy kết REL-75.6C hoặc bootstrap nếu thiếu witness.

Khoảng trống xác định từ source: observer APP-03.4/.5 chỉ theo dõi browser GET và SQL trong các suite đó, không theo dõi C# PrepareB sau NextDay. PR riêng bổ sung witness tại cả ba lần PrepareB: HTTP start/end với correlation ID, checkpoint, source/run, API/Vite PID, UTC/elapsed, timeout và phân loại lỗi; API opt-in IntegrationTest log cùng correlation ID trước/sau middleware; SQL observer đã có, phạm vi tenant-a, ownership/nonce, samples 250ms và các family C2Catalog/C2Units/C2RoleCheck. Observer được chạy sau Chromium và dừng trước cleanup, giới hạn startup 8s và giữ nguyên deadline tổng 4 phút. Các receipt ở TestResults/app03-6/<RunId>; API marker APP03_CATALOG_WITNESS trong job log. Không ghi token, raw SQL, request/response body, query string hoặc exception message vào witness mới. Không suy diễn HTTP-to-SPID duy nhất từ các sample đồng thời.

Giữ nguyên client 3 giây, không retry GET hoặc business command, không đổi proxy, API endpoint semantics, SQL/isolation, assertions, fixtures, business snapshot, LocalDB bootstrap hoặc CI gates. Đây là sửa thiếu observability, không phải xác nhận đã khắc phục nguyên nhân timeout. Khi có kết quả mới: đối chiếu API start/end và client ID; nếu API đến nhưng chậm, kiểm tra SQL family/wait/blocker; nếu không thấy API start, kiểm tra transport/proxy và độ đầy đủ log. Không dùng thiếu sample để khẳng định không có contention. Giữ lịch sử failure; chưa unblock PR #180 trước Full CI trên main.

#### Review #184 — sửa diagnostic harness, không sửa runtime

Attempt 1 HEAD `48e5866a`: [APP-00A](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46142081576) thất bại CS7036 ở bốn worker do hai caller APP-05 chưa có tham số mới; APP-03.6 và regression chưa chạy. [B.5](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46142081478) có cold LocalDB InstanceStartFailed riêng. Giữ hai failure receipts.

Khôi phục overload bốn tham số PrepareB cho APP-05. Context diagnostic chỉ tạo cho VerifySettlement khi đủ điều kiện Windows hosted CI opt-in và evidence directory hợp lệ; truyền tường minh tới ba post-NextDay hosts/requests. Context null không tạo GUID, header, HTTP receipt, SQL observer hoặc API marker. Bổ sung regression disabled và coverage. `startedWithoutSample` chỉ xác nhận observer đã bắt đầu.

Receipt `catalog-witness-coverage.json` kiểm tra chẩn đoán độc lập: ba cặp HTTP start/end, identity/source/run/PID/3s, observer identity và terminal reason, sample hợp lệ trước/sau từng request; gap tối đa 1s (ngưỡng review bảo thủ so với cadence 250ms, không phải SLA), sampleUnavailable/truncated trong khoảng bao phủ khiến request INCOMPLETE. BRACKETED không chứng minh không có contention. CLIENT_SQL_READY_FOR_REVIEW vẫn cần đối chiếu sáu API markers trong job log, thời điểm/PID/outcome, checkout/source/run/attempt và raw SQL samples. Nếu timeout làm dừng suite chỉ phân tích witness thu được, không suy diễn ca chưa thực thi. DIAGNOSTIC_EVIDENCE_INCOMPLETE không thay đổi assertion gốc hoặc cho phép chốt root cause. Giữ nguyên deadline, isolation, fixtures và required Full CI. Chưa có runtime remediation hoặc closeout.

### Mở rộng điều tra HTTP chung sau NextDay/Resume — PR #184

**ROOT_CAUSE_UNCONFIRMED / DIAGNOSTIC_SOURCE_CANDIDATE.** [APP-00A attempt 1 trên HEAD 2a253f78](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46148319383/attempts/1) đạt 435/435 .NET tests (13 diagnostic cases) nhưng APP-03.5 thất bại trước .6: GET C1 /session sau NextDay/Resume, 3s, tenant chưa rõ. SQL observer browser dừng trước lần timeout; lock RoleCheck 40ms trong browser phase không chứng minh nguyên nhân. Không coi 9/10 workflows là Full PASS.

Phạm vi chung: hai endpoint read đã có recurrence (C1 session của cả hai tenant, C2 catalog ba checkpoint hiện hành); session probes sau rollover .5 và .6, kể cả Resume cuối .6. Dùng cùng GUID header, client start/end với tenant, checkpoint, PID, elapsed, timeout và status nếu có; API marker allowlisted trước authentication; Vite marker ingress/upstream-request/headers/end/aborted/error/client-finish/close với cùng ID. Không đổi proxy target, readiness, deadline, request count, assertion hoặc endpoint semantics. Chỉ opt-in Windows hosted synthetic verification; APP-05 và DEV tương tác giữ đường cũ. Không log token, URL/query thô, payload, exception message, SQL text.

SQL observer sau browser chạy từ trước Stop/NextDay qua Resume/probes, hai tenant scratch có ownership/nonce độc lập. .5 dùng sql-resume-a.ndjson và sql-resume-b.ndjson, không ghi đè browser sql-reads.ndjson. .6 giữ tên legacy tenant-a và thêm sql-resume-b.ndjson. Các startup receipts chứa tenant/profile/run/source/PID/status. Catalog coverage legacy tiếp tục kiểm riêng; resume-witness-coverage.json xác minh C1 pairs theo checkpoint/tenant, provenance, PID, timeout, SQL observer identity/terminal và sample bracket với gap tối đa 1s. Cả hai receipt chỉ READY_FOR_REVIEW khi đủ witness; tiếp tục đối chiếu raw artifact/log theo ID, thời điểm, PID, source/run/attempt. Thiếu cặp hoặc thiếu sample bracket là INCOMPLETE, không suy diễn không có lock. C1 403 có thể hợp lệ theo assertion cũ, không đồng nghĩa timeout. SQL sample chỉ là temporal evidence, không unique HTTP-to-SPID mapping.

Workflow APP-03 HTTP investigation chạy .6 độc lập ngay cả khi .5 chặn APP-00A; chỉ evidence chẩn đoán, không thay thế Full CI hoặc closeout. Giữ failure artifacts mọi attempt. Kiểm chứng mới: unit tests C1 tenant/status, API route filter và fail-closed coverage; Node proxy marker/filter/sink/error-code regression; independent review và hosted Full CI trên HEAD cuối. Không tự retry, tăng timeout, hạ isolation, sửa REL-75.6C, business, fixtures hoặc required gates. Root cause chỉ kết luận khi timeline đủ định vị tầng lỗi; nếu không tái diễn giữ UNCONFIRMED. PR #180 tiếp tục BLOCKED cho tới Full CI trên main.

#### Review #184 — khôi phục diagnostic harness, không đổi security policy

[Review độc lập trên 228e405e](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/184#issuecomment-7240155935): Full run 46150603794 và independent run 46150603884 cùng dừng build với CS0103; security run 46150603824 phát hiện C59-B02. Không có request/SQL post-Resume được thực thi. Giữ nguyên tất cả failure receipts.

- P1: liên kết đúng App03ResumeWitnessCoverage.cs trong test csproj. Khôi phục vite.config.ts byte-identical với main; không thay đổi C59-B02, security policy, lockfile hay audit gates. Vite witness chỉ dùng launcher programmatic DEV riêng trong web/ci, có đủ opt-in Windows hosted CI, đúng một tham số cổng loopback, configFile cố định, mode development, host 127.0.0.1, strictPort và proxy /api cùng target literal localhost:5116 và changeOrigin:true tương đương string shorthand của Vite; /health giữ nguyên. Không dùng launcher để build, không input glob, không thêm plugin vào canonical config; kiểm tra resolved boundary trước listen. Real HTTP/Vite regression trên Windows kiểm chứng success, client abort, proxy error và loại trừ route ngoài allowlist; unit regression kiểm opt-in và CLI. Đây là bổ sung dev-only cần independent review, không phải mở rộng risk acceptance.
- P2: khi C# observer nhận EOF bình thường từ parent còn sống, thực hiện đúng một sample cuối có giới hạn trước stop (cùng command timeout 1s, 240s loop, 2MB cap và cleanup budget); browser observers không đổi terminal sampling. Ghi finalSample/finalSampleStatus; nếu không có sample hợp lệ sau HTTP end, giữ coverage INCOMPLETE. Regression decision matrix và last-timeout coverage; không resend, không kéo dài deadline business. Secondary cleanup failure không thay thế lỗi gốc.
- P2: C1 transport RESPONSE_RECEIVED tách khỏi assertion PASSED / EXPECTED_FORBIDDEN / HTTP_STATUS_REJECTED / ASSERTION_FAILED. Giữ nguyên status/tenant/nonce checks và exception khi witness disabled; ghi thời điểm nhận HTTP và assertion riêng, không đổi 3s timeout. Coverage xác minh cả outcome/status/assertion/response time; timeout không bị đổi thành PASS bởi sample cuối.

Trạng thái SOURCE_CANDIDATE / FINAL_HEAD_REVIEW_AND_HOSTED_CI_PENDING. Không suy diễn nguyên nhân HTTP, không thay đổi REL-75.6C, không merge hoặc unblock PR #180.

#### Review `f2239f06` — SQL observer startup witness

[Review raw evidence](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/184#issuecomment-7240619722): Full APP-03 and independent .6 completed, 24/24 HTTP/Vite/API correlations, but independent both observers timed out before NDJSON was created (0/11 SQL brackets). Full SQL brackets 13/13. D2-16/B-to-A separately timed out in distribution; Full CI FAILED. No common runtime cause established.

Startup receipt retains only exact, bounded child milestones (script entered, guards passed, parent/pipe/file open, SQL connection, ownership, visibility, sample loop), stderr presence flag, parent-side allowlisted failure category, pre-cleanup exit code when observed, bounded cleanup result and exit code when observed. No raw stream content or secrets. Missing stages remain missing; READY still requires the original NDJSON proof, not stdout. Keep startup 8s, cleanup limits, SQL timeouts, tenant guards and all business assertions unchanged. Regression covers duplicate/unrecognized marker filtering and absent stages. New source requires hosted verification; no runtime remediation or issue closeout.

#### Same-checkout baseline/instrumented comparison

Owner-directed initial matrix: VerifyField and VerifySettlement × baseline/instrumented × two fresh hosted Windows runner iterations (8 independent jobs, fail-fast false). This is a bounded initial sample, not a statistical claim of reliability or equivalence. Each job runs its original scenario once; no rerun of a failed business case. VerifySettlement is independent of VerifyField. All jobs build the same merge checkout with pinned SDK, npm and LocalDB setup. Both arms run the same diagnostic boundary self-tests; only the actual scenario receives explicit witness flag 0 or 1. Baseline uses the ordinary Vite launcher and has no post-browser HTTP/SQL witness; no witness is expected in baseline. Instrumented arm uses existing opt-in alternate launcher and observers, so a difference measures the combined harness, not a specific component. Browser/SQL business assertions and 3s client limit are identical.

Each job records comparison receipt (source HEAD, checkout, workflow run/attempt, scenario, arm, iteration, witnessEnabled and original outcome), uploads raw business/diagnostic evidence even on failure with unique artifact name. Verify receipt, exact job checkout, startup/phase/elapsed/HTTP outcomes, compare same scenario across arms, and verify absence of diagnostic client/API markers in baseline plus actual markers/brackets in instrumented arms. A setup failure is NOT_EXECUTED rather than absence of recurrence. Retain every attempt. A green diagnostic workflow or 0 recurrences in a small sample does not justify runtime changes, satisfy Full CI, or unblock #180. D2-16 is investigated separately in PR #185.

#### Managed post-browser SQL observer candidate

Review on 4a490815: all eight comparison jobs passed business assertions, but eight instrumented observers stopped at entered before guarded with no NDJSON; Full 4/4 READY. Replace only opt-in post-browser child with managed Demo command, using the proven D2 read-only DMV approach, existing SQL statement/lock allowlists, fixed LocalDB instance/database names, same-connection nonce and tenant checks plus LocalApp03Session for APP-03. Child waits for an explicit handshake after job assignment before opening any file or DB, no raw stdout/stderr or secret environment. Ready requires flushed start/available/first sample proof and ordered marker. Existing artifact names/schema, 8s startup, 2s connect, 1s command, 250ms sample cadence, 240s/2MB caps and final sample retained. Cleanup 4s graceful then 2s job close, 2s final kill fallback; explicit cleanup receipt; if child exit cannot be confirmed retain owned scratch rather than racing DROP. Legacy browser PowerShell observer unchanged. Re-run comparison on exact new checkout; no root cause or runtime remediation claimed.
