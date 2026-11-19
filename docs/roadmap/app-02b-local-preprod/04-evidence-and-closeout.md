# APP-02B LAB — Evidence, CI, Drivers and Closeout

**State:** PLAN_ONLY / IMPLEMENTATION_NOT_STARTED. Baseline `16d96c71a7967c216ebfa599693807a2b54e7435`, 2026-11-19.

**Goal:** chạy các scenario thật, giữ raw evidence và đối chiếu đủ 30 case + 2 runtime probes, với trạng thái closeout đúng phạm vi.
**Architecture/stack:** [design](design.md); Python collectors/runner, PowerShell entrypoints, GitHub Actions và original B9 validator.
**Global constraints:** [design](design.md), immutable fixture/business, không tự cấp quyền hoặc ký thay reviewer.
**Review focus:** wrong target, stale authority, mất evidence, giả provenance và retry che lỗi. [Codex handoff](codex-handoff.md) quy định command registry và tiêu chí từng PR. Mọi file ghi Create là đề xuất mới.

## Baseline facts

- `tools/app02b9/oracle.py` là validator, không phải executor. `expected-v1.json` chứa chính xác 30 CaseId và required witness kinds; runtime probes RUNTIME-01/02 nằm ngoài 30 ca.
- `tests/app02b9/test_oracle.py` và workflow B.9 hiện có 12 validator tests / 11 negative mutations. Chúng dùng signed mock, không tạo physical evidence.
- `scripts/Run-App00A.ps1` chưa tương đương APP-00A CI: wrapper cũ dùng minimum 82 và không bao phủ đủ audit/PWA/Playwright. Workflow hiện tại có minimum 296; các project B.2–B.8 không được bao phủ chỉ bằng solution hiện tại.
- #75 chứa hai lỗi khác nhau: LocalDB bootstrap và HTTP 500 sau restart. #68 là một record trước đó của triệu chứng A11-09. Rerun xanh không xác lập root cause.
- #59 đã CLOSED bằng risk acceptance, không phải patched package. Scope `APP00A_SYNTHETIC_CI_BUILD_TOOLS_ONLY`; expiry `2026-12-19T00:00:00Z`; không cho phép frontend publication/physical deployment qua exception này.
- Original B.9 yêu cầu observer khác implementer; Security/DBA khác nhau và khác implementer/observer; bốn public-key pins khác nhau cho fixture/observer/security/dba. Một người giữ bốn khóa không tạo ra independent human review.

<a id="lab-e01"></a>
## LAB-E01 — Đồng bộ #73/#59 và phạm vi lab

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** Không có dependency source mới. Setup, approval và live evidence là điều kiện bổ sung.

**Dependencies:** không phụ thuộc runtime mới. **Deliverable:** một PR tài liệu có trạng thái và lineage đúng; hoặc cập nhật đúng PR #73 sau khi chủ sở hữu nhánh cho phép tích hợp, không tạo hai closeout cạnh tranh.

**Modify (existing):** các file của PR #73: `README.md`, `docs/closeout/2026-11-18-app-02b9-source-only.md` (hiện chỉ trên nhánh #73), `docs/roadmap/app-00-app-01.md`, `app-02.md`, `app-02b-preprod-foundation.md`, `app-02b7-backup-pitr-safe-restore.md`, `app-02b8-immutable-audit-operations.md`, `app-02b9-independent-preprod-oracle.md`. Không chép phiên bản nhánh cũ đè main.

**Create (proposed):** `docs/decisions/proposed-app02b-local-lab-scope.md` với trạng thái `PROPOSED / NO_PHYSICAL_ACCEPTANCE_AMENDMENT`. Tách phần thiết kế được roadmap chọn khỏi Owner approval chưa có: approved environment, identities, backup target, retention/key history, operational fault windows. Nếu sau này Owner chọn ngoại lệ solo physical thì tạo decision riêng có phạm vi và ngày, không sửa lịch sử B.1 hoặc thừa kế B.4/#59.

- [ ] Đọc live #53/#56/#59/#60/#64/#65/#67/#68/#75 và #73/#74; record exact SHAs và updated times.
- [ ] Đổi trạng thái hiện tại #59 thành CLOSED / `WITHDRAWN_ADVISORY_SCOPED_RISK_ACCEPTED / NOT_PATCHED`; giữ ngày hết hạn và scope. Giữ tất cả failures cũ theo run/attempt.
- [ ] Run `44323688504` hiện success attempt 2 trên `216ce719...`; diễn đạt attempt 1 fail, attempt 2 pass. Không sửa lịch sử thành chưa từng fail.
- [ ] Thêm #75, giữ #68 root cause unresolved; giữ 30 physical cases NOT_RUN/NOT_VERIFIED và #56 OPEN.
- [ ] Diff chỉ docs; link-check các relative links vào file tồn tại trên nhánh/main và các URL run. Kiểm tra final PR HEAD/CI riêng; không dùng CI #61 như CI #73.

**Exit:** source/docs có thể merge theo gate của PR tài liệu; không ký hoặc đóng physical acceptance. Không cần viết test unit chỉ để kiểm tra một câu văn.

<a id="lab-e02"></a>
## LAB-E02 — Diagnostics cho LocalDB và A11-09

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** Không có dependency source mới. Setup, approval và live evidence là điều kiện bổ sung.

**Modify (existing):** `tests/FmcgDms.Api.Tests/App01e4TcpRestartHarness.cs`, `App02a3WorkerTests.cs`, `App02a4OracleCases.cs`, `App02a4IndependentSqlOracle.cs`, `.github/workflows/app00a.yml`. **Create:** `tests/FmcgDms.Api.Tests/FailureDiagnosticsTests.cs`, `scripts/Collect-LocalDbDiagnostics.ps1`.

**Interfaces:** bổ sung `App01e4ApiProcess.CaptureFailureAsync(string failureId, string stage, CancellationToken ct)` trả về đường dẫn artifact đã redacted; giữ nguyên `StartAsync(...)`, `Client(...)`, PID và disposal semantics. Nhận context hiện có của A4 report (`TestRunId`, `SourceHeadSha`, `WorkflowSha`, tenant, group); process context thêm PID/start/stop UTC. `IntakeA3` vẫn bắt buộc HTTP 202 và `RECEIVED_COMMITTED`.

- [ ] Test `Failure_diagnostics_redacts_bearer_cookie_connection_secret_and_preserves_correlation`: seed sentinel secrets trong stdout/stderr/headers/body; không còn sentinel trong artifact; còn exception type/SQL error number/TraceId/PID/UTC cần chẩn đoán.
- [ ] Thay ReadToEnd-only process capture bằng bounded ring buffer được drain liên tục, có snapshot khi process còn sống. Lưu cả hai kênh mà không block child, giới hạn kích thước và record truncated=true khi cắt.
- [ ] Khi intake khác 202: chụp method/path không query secret, status, correlation, process stage và redacted server exception; sau đó FAIL assertion cũ. Không thay status, không retry business request.
- [ ] Khi startup LocalDB lỗi: thu `sqllocaldb info`, instance log metadata, executable versions, service/process/error state trong bounded timeout; không in connection secrets; giữ lỗi gốc và upload artifact bằng `if: always()`.
- [ ] Run `dotnet test --solution FmcgDms.slnx -c Release --minimum-expected-tests 296 -- --report-trx`; CI giữ mọi audit/browser/PWA stage. Thêm tests làm tăng count thì ghi baseline mới, không giảm floor.

**Exit:** lỗi được quan sát có nguồn gốc; issue #75/#68 vẫn OPEN. Diagnostics PR có thể được review độc lập với việc sửa root cause.

<a id="lab-e03"></a>
## LAB-E03 — Fix LocalDB bootstrap có root cause

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E02. Setup, approval và live evidence là điều kiện bổ sung.

**Files:** tạo `scripts/Initialize-LocalDb.ps1` chỉ khi diagnostics chứng minh orchestration là điểm lỗi; sửa đúng bootstrap step trong `.github/workflows/app00a.yml` và helper sở hữu lỗi. Regression đặt cạnh helper mới hoặc test fixture hiện có. Không đổi API/business transaction để chữa lỗi bootstrap.

**Input/output:** fault record từ LAB-E02 gồm error class, instance identity, SDK/LocalDB version, timing và original attempt. Helper nhận tên scratch instance đã qua existing guard và deadline; trả readiness/error code, attempt history và log refs đã redacted.

- [ ] Phân loại lỗi LocalDB từ original/new artifacts; tái hiện có kiểm soát. Nếu chưa tái hiện/xác định được nguyên nhân, ghi INVESTIGATION_BLOCKED với chứng cứ còn thiếu; không kết luận “fixed” vì rerun xanh.
- [ ] Viết regression thất bại trước fix. Chỉ với lỗi transient đã phân loại mới dùng tối đa 3 lần init/health trong tổng deadline 60 giây, giữ mọi attempt.
- [ ] Không retry migrations, HTTP intake hoặc ledger mutation trong helper. Không DROP/reset instance người dùng hoặc xóa log để run sạch; nonce/name/instance guards vẫn bắt buộc.
- [ ] Chạy cold/warm initialization, unavailable engine, permission/version mismatch, deadline exhausted và foreign-instance refusal. Error phải giữ nguyên cause và exit nonzero khi hết budget.
- [ ] Chạy full APP00A trên exact final HEAD theo workflow; inspect artifact của mọi attempt, không hạ minimum hoặc thêm continue-on-error.

**Exit:** root cause + deterministic regression + fix + final-source CI có liên kết. #75 chỉ xử lý xong nhánh LocalDB ở đây; HTTP 500 tiếp tục thuộc LAB-E04. Nếu bằng chứng dẫn tới dependency khác, sửa roadmap/PR scope có giải thích, không đoán file lỗi.

<a id="lab-e04"></a>
## LAB-E04 — Fix HTTP 500 sau OS restart

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E02. Setup, approval và live evidence là điều kiện bổ sung.

**Files:** `tests/FmcgDms.Api.Tests/App01e4TcpRestartHarness.cs`, `App02a3WorkerTests.cs`, `App02a4OracleCases.cs` và code sở hữu lỗi được LAB-E02 chỉ ra. Production file không thể chốt trước root cause; PR phải ghi stack/transaction/SQL evidence dẫn tới đúng file. Không gom fix LocalDB vào PR này.

**Contract:** A11-09 pending C → dừng PID cũ → PID mới → P intake → outbox recovery. Intake vẫn HTTP 202/RECEIVED_COMMITTED; không đổi expected result, không retry business request tự động, không tăng timeout để che race.

- [ ] Lập fault record tenant A/B, Worker off/on, old/new PID, SQL transaction/error, correlation và phase của HTTP 500.
- [ ] Tạo regression deterministic (barrier/latch hoặc injected fault có giới hạn) FAIL trên code cũ, PASS sau fix. Không suy ra cùng root cause với #68 chỉ từ tên scenario.
- [ ] Sửa ở component sở hữu race/state/connection; giữ same-ID semantics, pending C và toàn bộ SQL oracle equations.
- [ ] Chạy fresh scratch A/B × Worker off/on, cùng nhánh concurrent outbox recovery. Dùng 5 fresh iterations/nhánh làm mẫu kiểm chứng ban đầu, ghi counts/limits; đây không phải SLO hay chứng minh xác suất lỗi bằng 0.
- [ ] Full APP00A + relevant dedicated workflows PASS trên exact HEAD; retained failing attempt vẫn được liên kết. Cross-link #68 chỉ khi cause thực sự khớp; #75 cần disposition cả E03 lẫn E04.

**Exit:** nguyên nhân được chứng minh và regression đúng semantics, hoặc giữ BLOCKED có artifact cụ thể. Một rerun attempt thành công không đủ close #75.

<a id="lab-e05"></a>
## LAB-E05 — Run context, catalog và schema

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** Không có dependency source mới. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/context.py`, `catalog.py`, `schema.py`, `run.py`, `schemas/lab-evidence-v1.json`, `schemas/lab-run-context-v1.json`, `tests/app02b_lab/test_context.py`, `test_catalog.py`, `test_schema.py`; wrapper `scripts/lab/Invoke-LabAcceptance.ps1`.

**Inputs:** `LabRunContext(runId, environmentId, sourceHeadSha, fixtureSha256, configurationSha256, startedUtc, hostSha256, mode)`; `mode` phải `LOCAL_OPERATOR_LAB`. `runId` do operator tạo một lần dạng `b-lab-<UTC>-<random>`, không derive chỉ từ commit; SHA-256 của public/redacted config, không hash tùy tiện file có secret để công bố. Source là 40-hex SHA, images ghi immutable digest, working tree phải sạch.

**Functions:** `create_context(config_path: Path, evidence_root: Path, source_sha: str, run_id: str) -> LabRunContext`; `load_catalog(fixture: Path) -> dict[str, CaseDefinition]`; `validate_lab_bundle(root: Path) -> LabValidationReport`. Không thay đổi original `tools/app02b9/oracle.py` hoặc fixture để hỗ trợ lab.

**Lab schema:** `schema=APP02B-LAB-EVIDENCE-V1`, `evidenceClass=LOCAL_OPERATOR_LAB`, `humanReview=SOLO_OWNER_SELF_ATTESTED`; `originalGateStatus=NOT_VERIFIED`; `cases` phải đủ 30 IDs. Mỗi case có `technicalResult` PASS/FAIL/BLOCKED/NOT_RUN, `acceptanceStatus=NOT_VERIFIED`, expected symbol từ frozen fixture, actual observations, missingRequirements, positiveRefs/negativeRefs, tenant scope và peer-before/after. B05-02 giữ BLOCKED khi thiếu reviewer thật. Technical PASS không đại diện mọi original acceptance condition; mô tả rõ assertions nào đã đo và independence nào thiếu.

- [ ] Test catalog thiếu/thừa/trùng ID, fixture đổi byte, dirty checkout, invalid source, duplicate JSON property, NaN, path traversal/symlink escape, empty witness => reject.
- [ ] Freeze byte-identical original expected fixture cùng hash trước first mutation; run.json ghi trạng thái PREPARED, original commitment và config digest. Không cho sửa fixture giữa run.
- [ ] Tạo 30 dòng NOT_RUN trước execution. Runner crash/abort vẫn để lại complete coverage index; không chỉ in các case đã chạy.
- [ ] CLI mới: `python tools/app02b_lab/run.py --config <public-config> --selection case:B01-01 --run-id <id> --evidence-root <outside-repo>`; hỗ trợ `gate:G-B01` và `all`; unknown selection fail. Exit 0 chỉ = mọi technical requirement đã chọn hoàn tất, exit 2 = BLOCKED/NOT_RUN, exit 1 = FAIL; report luôn original gates NOT_VERIFIED.
- [ ] Resume chỉ dùng cùng source/config/fixture/environment và admitted DB lineage. Một I08 restore transition đã khai báo được giữ trong run với immutable old→new receipt; không sửa baseline identity. Target drift/lineage không được duyệt phải tạo run mới hoặc BLOCKED. Attempt folder mới, append history; không overwrite failed attempts hoặc tự chọn attempt xanh mà giấu lịch sử.
- [ ] Unit command: `python -m unittest discover -s tests/app02b_lab -p 'test_*.py' -v`.

**Exit:** shared contract ổn định cho mọi collector/driver; chưa cần runtime thật để kiểm chứng validator.

**Contract ownership:** thêm `schemas/lab-environment-v1.json` cho cấu trúc tối thiểu/secret references; LAB-I01 bổ sung platform validation qua extension schema versioned, không đổi tên run fields tùy ý. Schema labels chính xác: `APP02B-LAB-EVIDENCE-V1`, `LOCAL_OPERATOR_LAB`, `SOLO_OWNER_SELF_ATTESTED`. `configurationSha256` hash manifest không chứa secrets, bao gồm danh tính/version của secret references và externally retained authorization record; không công bố hash đoán được của secret values. `hostSha256` đi kèm protected original fingerprint.

<a id="lab-e06"></a>
## LAB-E06 — Collectors SQL/HTTP/network/Vault

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E05, LAB-I04. Setup, approval và live evidence là điều kiện bổ sung.

**Dependencies:** context/catalog; infrastructure/principal setup. **Create:** `tools/app02b_lab/collectors/{sql,http,network,vault}.py`, `sql/fingerprint.sql`, `scripts/lab/Collect-LabRuntime.ps1`, tests cho từng collector. **Reuse:** `scripts/Collect-App02B2PreprodEvidence.sh`; kết quả của nó chỉ là raw input, không tự nâng outcome.

**Interface:** `collect(ctx: LabRunContext, case_id: str, phase: str, target_ref: str) -> WitnessRef`. `WitnessRef` gồm ref, kind, relativePath, sha256, rawArtifactPath/hash, observedUtc, observerPrincipalRef, targetIdentity và vantagePoint. Collector ghi raw artifact trước, fsync/atomic rename rồi mới tạo witness descriptor. Chỉ nhận secret handles/paths; không nhận secret values qua command line.

- [ ] SQL observer dùng principal chỉ đọc catalog và dữ liệu cần chứng minh; credential tách runtime. Capture toàn bộ user-table schema + row count + SHA-256 canonical row multiset, marker, binding, principal/effective grant. Bảng không PK phải sort canonical row bytes và giữ multiplicity. Chuẩn hóa NULL, decimals, binary và UTC; không dùng CHECKSUM_AGG làm bằng chứng mật mã.
- [ ] Chụp snapshot SQL nhất quán với isolation được chuẩn bị; detect schema thay đổi trong capture. Mọi table mới phải xuất hiện; read error không chuyển thành 0 rows. Tiến hành cả A→B và B→A khi case cần isolation. Background writes trên peer phải được kiểm soát/ghi nhận, không bỏ table khỏi hash để hết lỗi.
- [ ] HTTP collector phân biệt transport error, TLS rejection, 401/403/409/500 và expected success; không log Authorization/Cookie/Set-Cookie. SQL deny phải được chứng minh bằng actual workload principal, không chỉ observer SELECT.
- [ ] Network witness ghi `vantagePoint=WINDOWS_HOST|OUTSIDE_APP_BRIDGE|PRIVATE_NONPROXY_PEER|EXTERNAL_INTERNET` và IP/route evidence đã redacted. Không gán Windows NAT probe là Internet observer.
- [ ] Vault collector đối chiếu policy 403, request/audit event ID, key version, outage/revocation/rotation timeline; không lưu response secret. Retain existing CI-only fixtures dưới evidence class riêng.
- [ ] Test fake API200+missingSQL, peer table thêm row, wrong SQL login, expired/untrusted TLS, forged forwarded headers, app token đọc worker path => đúng FAIL/DENY hoặc BLOCKED nếu setup thiếu.

**Exit:** RUNTIME-01/02 và security/secrets drivers có raw witnesses thật của lab; external-network/independent-person gaps vẫn hiện.

**Adapter boundary:** định nghĩa `LabAdapters`/typed protocols trong `tools/app02b_lab/adapters.py`: named HTTP command/query, operator control, SQL read/deny, Vault read-only audit, fault barrier và clock/receiver observations. Chỉ mapping operation enum → implementation được review; không chạy shell/SQL tùy ý lấy từ case JSON. Collectors không có quyền gọi privileged mutation của target đang quan sát. Adapter unavailable => BLOCKED, không tạo witness giả.

<a id="lab-e07"></a>
## LAB-E07 — Exact-source CI inventory

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E03, LAB-E04, LAB-E05. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/ci.py`, `ci-check-map.json`, `scripts/lab/Collect-LabCiEvidence.ps1`, `tests/app02b_lab/test_ci.py`, `.github/workflows/app02b-lab-source.yml`. **Modify:** workflow trigger/dispatch riêng nếu cần đảm bảo final candidate chạy được đủ checks; không nới required checks.

| Required B.9 ID | Existing workflow file |
|---|---|
| APP00A_FULL | `.github/workflows/app00a.yml` |
| APP02B2_RUNTIME | `.github/workflows/app02b2-runtime.yml` |
| APP02B3_IDENTITY | `.github/workflows/app02b3-identity.yml` |
| APP02B4_TENANTS | `.github/workflows/app02b4-tenants.yml` |
| APP02B4_WEB | `.github/workflows/app02b4-web-regressions-with-known-debt.yml` |
| APP02B5_AUTHORIZATION | `.github/workflows/app02b5-authorization.yml` |
| APP02B6_POLICY | `.github/workflows/app02b6-policy-governance.yml` |
| APP02B7_RECOVERY | `.github/workflows/app02b7-recovery.yml` |
| APP02B8_OPERATIONS | `.github/workflows/app02b8-audit-operations.yml` |
| APP02B9_ORACLE | `.github/workflows/app02b9-independent-oracle.yml` |

`security-picomatch-issue59.yml`, lab source CI và workflow B53 sau khi được adopt là **additional mandatory relevant checks**; không thay thế một trong mười ID bằng Security để đủ số 10. B.9 hiện có path-filter nên có thể không chạy trên một SHA dù mười workflow khác xanh.

**Interface:** `collect_ci(repository: str, source_sha: str, check_map: Path, output: Path) -> CiEvidenceIndex`; không ghi token vào output. Dùng GitHub API/gh JSON; đọc tất cả pagination của runs/jobs/artifacts, attempt, conclusion, failed/skipped required steps, expired artifacts và source-bound metadata trong artifact. `SourceHeadSha` khác `WorkflowSha` với synthetic merge phải được kiểm tra qua source/tree lineage theo hợp đồng hiện có; không so mỗi SHA workflow với PR SHA rồi đoán.

- [ ] Tests: missing B9+extra Security; stale SHA; duplicate check ID; PR vs main confusion; successful job nhưng required step skipped; attempt mới fail sau attempt cũ pass; artifact expired/missing/hash mismatch; advisory policy expired; count/test result mismatch. Treat `nowUtc >= 2026-12-19T00:00:00Z` as expired for the current #59 policy; test one tick before, exactly at and after the boundary. A successor policy requires a separately approved version, never an automatic extension.
- [ ] Main source CI chạy Python tests, original B9 12/11, mock adapters và missing-bundle NO_GO; không cần live lab secrets và không tự dựng physical trust roots.
- [ ] Full audit (`npm audit --audit-level=moderate`) và production audit (`npm audit --omit=dev --audit-level=low`) luôn giữ; dependency/expiry đổi cần policy assessment, không tự gia hạn.
- [ ] Chuẩn bị command registry liệt kê từng .NET project và workflow stage; wrapper local không được coi `dotnet test --solution` là toàn bộ B suites. Giữ Microsoft.Testing.Platform syntax từ workflow, không chuyển sang VSTest filter theo trí nhớ.

**Source test command:** `python -m unittest discover -s tests/app02b_lab -p test_ci.py -v`. Khác biệt provenance giữa PR source H và workflow synthetic merge M phải đúng existing contract, không là ngoại lệ mới. Nếu workflow không thể dispatch trên H, PR này bổ sung workflow_dispatch/checkout đúng nguồn và receipt lineage, giữ path/required checks. Không dùng một run của main khác H để điền chỗ trống.

<a id="lab-e08"></a>
## LAB-E08 — Driver G-B01 — security

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-S07, LAB-S08, LAB-I08, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/security.py`, `tests/app02b_lab/test_security_cases.py`. **Scope:** B01-01..06; một driver PR cho G-B01.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Triển khai valid-A → B read/export/write/metadata trên đúng typed query/command boundary; invalid JWT/refresh replay; System Admin không membership; Maker=Checker/stale source; revocation tại API/Worker/CASE COMMIT; old snapshot trước revoke. Cần chứng minh cả hai valid serializations revoke/COMMIT bằng barriers và actual transaction results. Full peer fingerprint không đổi; no-read proof phải có actual deny under workload principal. Restore scenario gọi LAB-I08, không sửa row thủ công làm bằng chứng PITR.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_security_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B01 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e09"></a>
## LAB-E09 — Driver G-B02 — secrets/policy

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-S03, LAB-S07, LAB-S08, LAB-I05, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/secrets_policy.py`, `tests/app02b_lab/test_secrets_policy_cases.py`. **Scope:** B02-01..06; một driver PR cho G-B02.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy real wrong-role Vault403/SQL denial, missing key/lease/outage, key rotation + historical fixture decrypt, L0 backdate/tamper, L1 tenant/effective-window, L2 UNSET/self-check/approved-zero. Tách key retired-for-signing khỏi revoked-for-verification. Preserve raw Vault audit request IDs; driver không đọc key bytes hoặc đổi production threshold. Quay về trạng thái an toàn sau token/key fault; không restore secret cũ để bypass revocation.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_secrets_policy_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B02 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e10"></a>
## LAB-E10 — Driver G-B03 — recovery

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-I08, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/recovery.py`, `tests/app02b_lab/test_recovery_cases.py`. **Scope:** B03-01..06; một driver PR cho G-B03.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy actual FULL/DIFF/LOG và retained-copy restore; measured PITR; wrong target/binding; old B3/B4 without B5 rewind; original MessageId lost ACK/UNKNOWN; corrupted log/keyless/unauthorized restore. Positive B03-02 phải nối RESTORE+SQL+BACKUP+OUTCOME cùng run, safe API và Worker return. Chạy lỗi ở phase cụ thể của cutover, kiểm tra SUSPENDED/no-dispatch và B data unchanged. Local second copy không được gán off-host.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_recovery_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B03 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e11"></a>
## LAB-E11 — Driver G-B04 — operations

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-O02, LAB-E06, LAB-I08. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/operations.py`, `tests/app02b_lab/test_operations_cases.py`. **Scope:** B04-01..06; một driver PR cho G-B04.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy actual audit tamper/truncation vs original anchor; trace xuyên restart/inbox/outbox; real alert delivery và operator ACK; incident overdue/escalation/review; legal hold chặn purge; eligible diagnostic archive. Chỉ dùng disposable LAB_TEST_ONLY diagnostics khi real approval chưa có. Raw journal/receipt/timestamp lấy từ receiver/SQL; không dựng ACK trong driver. Restore-hold drill gọi I08; durable APP02A/ledger/audit/legal history tuyệt đối không bị TTL purge.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_operations_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B04 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e12"></a>
## LAB-E12 — Driver G-B05 — provenance/review

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E07, LAB-E08, LAB-E09, LAB-E10, LAB-E11. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/provenance.py`, `tests/app02b_lab/test_provenance_cases.py`. **Scope:** B05-01..06; một driver PR cho G-B05.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Kiểm kê 30+2 và A/B physical identities; kiểm tra reviewer/custodian/source; exact required CI; original 11 mutations; API200 không đủ SQL/restore/alert; đổi HEAD/config/fixture khiến stale signoff invalid. B05-02 trong solo profile phải BLOCKED khi không có independent humans, dù các subchecks kỹ thuật khác xanh. Không tự sinh người, key, review signature hoặc human approval.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_provenance_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B05 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

**Additional mutation tests:** retain original B9 fixture and all 11 original negative mutation IDs; add local-class promotion, mixed-run, missing-raw, path escape, changed config, fabricated independent people and older successful CI attempt hiding latest failure. Validate original oracle returns NO_GO for a forged lab-to-physical bundle. Reference the original validator directly; do not fork a relaxed copy.

<a id="lab-e13"></a>
## LAB-E13 — Lab report và conditional closeout

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E12. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/report.py`, `physical_handoff.py`, `tests/app02b_lab/test_handoff.py`, `docs/roadmap/app02b-lab-evidence-handoff.md`. **Dependencies:** shared schema, all relevant drivers/collectors, CI index; runtime/security/recovery prerequisites từ các nhánh khác.

`build_report(bundle: Path, output: Path) -> LabReport` tạo JSON+Markdown: coverage 30+2, failures, blocked dependencies, immutable attempt history, target source SHA, risk/approval gaps và original gates NOT_VERIFIED. `prepare_physical_handoff(...)` chỉ tạo index ứng viên; không ký review, không tự gán independentlyMeasured=true, không đổi local witness class thành physical. Bản physical cần approved environment/trust và các actual witnesses/reviews đáp ứng original contract; bằng chứng lab chỉ được tham chiếu khi reviewer xác minh loại/scope của nó.

- [ ] Test lab bundle dù kỹ thuật xanh vẫn bị original `oracle.evaluate(...)` từ chối; giữ exit2/NO_GO. Test một người với nhiều key/alias không được đánh dấu independent.
- [ ] Retain original eleven mutations `EXPECTED`, `ACTUAL`, `SIGNED_DIFFERENCE`, `MATCH_FLAG`, `PEER_HASH`, `SOURCE_HEAD`, `MISSING_PHYSICAL`, `API_ONLY_FORGED_SUCCESS`, `SKIPPED_REQUIRED_CI`, `STALE_REVIEW`, `FROZEN_FIXTURE_SHA`; thêm lab-class forgery, mixed run IDs, missing raw, path escape và changed config.
- [ ] Freeze runtime source **H** (full source SHA), image digests, migration set, frozen fixture và config trước full run. Thu, hash, ký và archive evidence **ngoài Git**; không commit raw bundle hoặc private trust.
- [ ] Owner/reviewer disposition được gắn H và TestRunId qua receipt/issue evidence reference ngoài source. Nếu sau đó mở docs-only closeout commit **D**, tài liệu phải nói H là source đã chạy; không gọi D đã deployed/accepted. Triển khai D hoặc bất kỳ source mới nào cần original revalidation. Không thêm loophole tree-equivalence vào oracle để né B05-06.
- [ ] Closeout local có thể ghi local mechanism verification và solo attestation; #56 vẫn OPEN cho đến khi original all30/all5 + runtime2 + independent reviews + off-host recovery + exact-source CI đáp ứng, hoặc Owner có amendment rõ ràng và truthful disposition riêng. Roadmap hiện tại không tự ban hành amendment.
- [ ] Với actual physical run, invoke original `python tools/app02b9/oracle.py --bundle <secure> --fixture tools/app02b9/expected-v1.json --trust-roots <outside-repo-and-bundle> --expected-head <H> --output <secure-report>`. Exit0 chỉ là gate candidate; cần actual evidence inspection/Owner disposition. Production/Pilot, APP02C/D/E, real EV08/P0 giữ nguyên restrictions.

**Closeout checklist:** all 32 source increments have explicit status/PR/SHA; 30 case rows and 2 runtime rows are present; known FAIL/BLOCKED and observer limitations stay visible; #59 scope/expiry is reread at report time; #75 root-cause disposition is linked; #73/B.9 docs are synchronized before any physical closure proposal. Report actual completed subset as local mechanism verification. Do not set a suite-wide PASS when B05-02 or another required case remains BLOCKED.

## Primary project sources

- [Original 30-case contract](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/main/docs/security/app-02b1-acceptance-evidence.md)
- [Frozen fixture](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/tools/app02b9/expected-v1.json)
- [Original physical oracle](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/tools/app02b9/oracle.py)
- [B9 workflow 12 tests/11 mutations](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/.github/workflows/app02b9-independent-oracle.yml)
- [APP00A baseline](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/.github/workflows/app00a.yml)
- [Issue59 pinned risk policy](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/docs/security/issue59-conditional-risk-policy.json)
- [Issue75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75), [Issue68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68), [PR73](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/73), [Issue56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56)
