# CI — Bootstrap LocalDB theo diagnostics

Ngày triển khai: **25/11/2026**. Owner yêu cầu mục 4 của kế hoạch giảm thời gian chờ CI, trên `main` sau PR #146: `ac2ca0d5f0f28b14cb7bc186ae3b8628b7a9bc59`.

**Phạm vi:** cải tiến điều phối và diagnostics của LocalDB trên GitHub-hosted `windows-2022`. Source ở trạng thái `SOURCE_REVIEW`; biên nhận CI của HEAD cuối nằm trong PR triển khai. Nguyên nhân bên trong engine/Windows của lỗi lịch sử vẫn `INVESTIGATION_BLOCKED`. Đây không phải closeout toàn bộ LAB-E03 hoặc #75.

## 1. Bằng chứng đã đọc

| Nguồn | Bằng chứng | Điều đã quan sát |
| --- | --- | --- |
| `main` sau #146, `ac2ca0d` | [B4 run 45499265441, attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45499265441/job/138817869787) | Lỗi tạo automatic instance, rồi login timeout; chưa chạy build/test SQL |
| `main` sau #145, `dfa5937` | [B5 attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45445278577/job/138627219072), [attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45445278577/job/138628992748) | Attempt 1 lỗi bootstrap; worker mới chạy cùng source thành công ở attempt 2 |
| APP00A `6acdc4c` | [Run 45150486380](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45150486380), [artifact 14805903355](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45150486380/artifacts/14805903355) | `AutomaticInstanceNotCreated` → `AutomaticInstanceCreationFailed` → `LoginTimeout`; sau đó instance tồn tại nhưng `Stopped` |
| APP00A `3ab276b` | [Run 45137989550](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45137989550), [artifact 14800342549](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45137989550/artifacts/14800342549) | Cùng chuỗi lỗi; metadata version `15.0.4382.1`, không có process SQL, nhiều Event 528 và error log mới |

Hai ZIP diagnostics đã được tải và đối chiếu SHA-256 với metadata GitHub:

- `14805903355`: `a959fa68944c94e28611af0f252d1905b60f05a67ae45137fcfcb9274347ad26`.
- `14800342549`: `d5ccab71cf0c3fadc3d2dabc445ed162e5868d1d5c64528e2572fcb3237051aa`.

Các lượt trên dùng image `20261116.370.1`. Thông báo “automatic instance chưa được tạo” cũng xuất hiện trước một lượt thành công, nên đó là trạng thái cold hợp lệ. Ngược lại, Event 528, exit code được wrapper ghi là 0 và một lần rerun xanh không chứng minh lỗi tạo/start là transient. Artifact cũ chỉ có metadata log/event; không thể suy ra lỗi quyền, cấu hình, disk hoặc hỏng model database từ những dữ liệu này.

Trong PR #147, các control heads `77adf81`, `0ff932b2` và `f02d82f0` xác định lời gọi explicit-version bị từ chối với exit 0; dùng version đầy đủ không sửa được lỗi. Head `24ec4b1` chuyển sang lệnh automatic-instance chuẩn, sau khi xác minh default runtime/tool: cả bốn APP00A worker và B3/B5/B6/B8 qua bootstrap, core_sql qua warm check. Hai thất bại còn lại được giữ nguyên để phân biệt:

- [B4 run 45508702927](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45508702927/job/138850508144): create bị cắt ở cap 15 giây; collector còn thấy một process SQL, không có numeric SQL error/event. Cap riêng của cold creation được điều chỉnh lên 45 giây trong deadline tổng 60 giây, không thêm lần create.
- [B7 run 45508702885](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45508702885/job/138850508243): `InstanceStartFailed` trong create, native exit 0; log có **5173 / severity 16 / state 1** hai lần, cùng Event 528. Microsoft mô tả 5173 là bất nhất giữa các file database; signature này chưa xác định file/cause vật lý. Giữ FAIL và investigation mở, không coi đây là login timeout để retry hoặc tự sửa system database.

## 2. Luồng bootstrap chung

`scripts/Initialize-LocalDb.ps1` được gọi ở cả bốn worker APP00A và sáu job SQL B3–B8. Helper chỉ chấp nhận đúng `MSSQLLocalDB` trên GitHub-hosted Windows. Database scratch, migration, ownership và nonce tiếp tục do các script/test hiện có bảo vệ.

1. Xác nhận môi trường và hai executable có sẵn; kiểm tra instance.
2. Chỉ khi nhận đúng toàn bộ câu thông báo instance `MSSQLLocalDB` chưa được tạo, đọc `versions` để xác nhận chỉ có một runtime đã đăng ký và thuộc family 15.0. File version của tool phải tương ứng đúng runtime này. Sau đó gọi lệnh automatic-instance được Microsoft hướng dẫn, `create MSSQLLocalDB`, một lần; không tự chọn family khác hoặc cài thêm runtime.
3. Đọc lại metadata duy nhất và hợp lệ. Giữ runtime family `15.0` đang dùng, không cài hoặc tự chuyển engine version.
4. Nếu `Stopped`, gọi `start` một lần. Native exit và thông báo thành công đều phải hợp lệ; output lỗi/không nhận diện không được một probe sau đó che đi.
5. Xác nhận lại `Running`, rồi truy vấn chỉ đọc trong `master`, kiểm `IsLocalDB` và version trước khi trả `READY`.

Đây là cách khởi tạo tường minh được Microsoft hỗ trợ. Nó bỏ sự phụ thuộc vào implicit creation trong lệnh start và làm rõ nơi thất bại; chưa phải bằng chứng khắc phục được nguyên nhân native lịch sử.

| Điều kiện | Hành vi |
| --- | --- |
| Instance đã `Running` | Chỉ kiểm metadata và readiness |
| Warm verification nhưng instance thiếu hoặc `Stopped` | FAIL trước create/start; không coi việc khởi động lại là warm PASS |
| Create/start lỗi, kể cả text lỗi với exit 0 | FAIL, giữ nguyên stage/cause và history |
| Sai quyền, sai version, metadata lạ/trùng, sai target hoặc output bị cắt | FAIL; không thử lại |
| Chỉ có thông báo login timeout đã cho phép, sau khi start/Running được xác minh | Kiểm Running, đợi 2 giây có giới hạn, kiểm lại Running ngay trước probe kế tiếp |
| Timeout kèm lỗi tạo instance, quyền, auth hoặc lỗi thứ hai chưa nhận diện | FAIL; không coi cả đoạn output là transient |
| Hết ba readiness probes hoặc deadline tổng | FAIL; không chuyển sang test |

Deadline đơn điệu tối đa **60 giây** bao gồm các bước init/readiness: cold create tối đa 45 giây, start 15 giây, info/versions 3 giây và readiness process 12 giây, mỗi bước đều bị chặn bởi thời gian còn lại; SQL login/query có timeout riêng. Kiểm lại budget ngay trước khi tạo process, kể cả khi tìm executable chậm. Khi hết giờ chỉ dừng launcher thuộc helper, không kill cả cây có thể chứa engine SQL. Các bước ghi evidence/collector chạy sau kết quả bootstrap với timeout riêng; không được diễn giải 60 giây thành giới hạn cho cả job.

Không retry migration, lệnh HTTP, ledger hoặc test suite. Không xóa/reset/stop instance, thay system database, đổi quyền thư mục hoặc xóa log.

## 3. Evidence và tương thích collector

Mỗi invocation ghi JSON riêng dưới `TestResults/LocalDbBootstrap/`: schema `CI_LOCALDB_BOOTSTRAP_V1`, source HEAD, workflow SHA, run/attempt, job, image, mode cold/warm/stopped, runtime đã đăng ký, native exit/status, stage/attempt, deadline và kết luận. Một timeout được khôi phục vẫn nằm trong history. Native detail chỉ dùng các nhãn cố định để phân biệt lý do version/parameter/profile/folder/registry phía sau tiêu đề create/start thất bại, không giữ raw message.

Helper xuất basename receipt và projection `info/start/sqlcmd` qua step outputs. Failure collector nhận lại các giá trị này cùng source HEAD; báo cáo LAB-E02 có job và receipt identity để liên kết đúng invocation. Nếu SDK/source guard thất bại trước khi helper chạy, collector ghi không có invocation được ghi nhận, không dựng một bootstrap history giả.

`scripts/Collect-LocalDbDiagnostics.ps1` tiếp tục giữ metadata event, tool, instance và process. Phần mới chỉ đọc tối đa **64 KiB cuối mỗi file** của ba log đã biết, giữ tối đa **16 bộ số error/severity/state** mỗi file. Không đưa dòng log, payload event, tên owner, đường dẫn riêng hoặc chuỗi kết nối vào artifact. File/ancestor reparse points bị từ chối; thiếu file, đọc bị từ chối và encoding sai có status riêng.

Artifact hiện có dùng `if: always()` để giữ cả receipt/diagnostics khi lỗi xảy ra trước .NET restore hoặc trước khi test tạo TRX. Tên artifact và mọi evidence nghiệp vụ cũ được giữ nguyên.

## 4. Kiểm chứng và giới hạn kết luận

- Regression PowerShell thực thi state machine thật, thay đúng boundary native Windows bằng fixture có trạng thái. Bao phủ cold/warm, success text và native exit, instance/engine/tool không hợp lệ, timeout có lỗi đi kèm, retry/deadline và history.
- Tests native-probe dùng subprocess thật do test sở hữu: nonzero exit, drain output, timeout, child process còn sống khi chỉ kill launcher và lookup dùng hết deadline.
- Tests numeric-log dùng file thật có sentinel, UTF-8/UTF-16LE, tail/record bounds, lỗi quyền và reparse points. Event metadata cũ vẫn có đúng ba field id/level/utc.
- CI thực trên HEAD cuối phải chạy đủ workflow được kích hoạt, giữ mọi gate/test/witness. `core_sql` kiểm cold rồi `-RequireWarm`; tất cả mười job LocalDB đều có receipt. Kiểm source/run/job, history, SQL tests và artifact trước khi kết luận CI đạt.
- Không dùng một lượt xanh để tuyên bố mọi bootstrap flake đã biến mất hoặc đóng nguyên nhân Event 528. Nếu native failure tái diễn, đọc receipt và numeric error codes mới; giữ lỗi đầu và sửa đúng cause, không rerun để che.

Giữ [LAB-E03](app-02b-local-preprod/04-evidence-and-closeout.md#lab-e03) cùng master register ở `SOURCE_REVIEW / NATIVE_CAUSE_INVESTIGATION_BLOCKED`; #75 và nhánh HTTP 500 LAB-E04 có disposition riêng. Kết quả CI, SHA và artifact cụ thể được ghi trong PR triển khai, tránh tạo một commit mới chỉ để xác nhận lượt CI vừa chạy.

## 5. REL-75.1 — Diagnostics test handshake (27/11/2026)

**Scope / status:** Owner authorized REL-75.1; `CLOSED / MAIN_CI_VERIFIED` via [PR #175](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/175), reviewed source `8094fb4ff99266ef383592e594f1096f7171e0c3`, merge `a60dec6462ef0e0b958c17b0373710d576ba8d0b`. [PR Full attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45928037242/attempts/2) and [main Full attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45971418727/attempts/2) each 10/10 workflows, 53 success / 3 policy skips; 46 bootstrap + 30 diagnostics, 398/398 .NET and 1440/1440 SQL cells PASS. Retain failed attempts (#176 and main C.1/APP-03.5); no root-cause disposition for those failures. [Closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7216376756). Base `4b2cbdffb0411d27e93784265a37ff9088258204`. This is a test-harness-only correction for R75-04 under [Issue #75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75), not a change to LocalDB initialization or runtime recovery.

- [Observed failure: PR #170, core_sql attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45797421286/job/139831779931): the parent read a heartbeat file while its child rewrote it every 100 ms. A later PASS does not eliminate that sharing race. Both real subprocess fixtures had the same pattern.
- Replace shared mutable heartbeat with one-shot request/ack markers in a unique test-owned directory. The parent publishes its request only after `Invoke-LocalDbProbe` returns; the real child acknowledges within a bounded 2-second wait. No marker content is read, no shared text rewrite occurs. Stale/absent ack fails; the acknowledged child PID and start time must match both ownership records and a live process. Ownership JSON is published atomically; cleanup remains PID/start-time guarded, with independent child ownership as a fallback. The independent review also requires proof that the original launcher exited before the post-probe handshake (read-only check, at most 1.5 seconds for termination, PID/start-time matching); a still-running launcher is a test failure before cleanup, not a success masked by cleanup.
- Preserve both original native subprocess scenarios, including redirected streams for the running launcher, inherited pipes and exact exit 37 for the completed launcher, 4-second probe timeout, and `-KillEntireProcessTree:$false`. The running launcher must also report no exit code. Keep the collector's intentional locked-file negative regression. No retries, skipped tests or production code changes.
- Verify on exact final source: `pwsh -NoProfile -File tests/ci/Test-LocalDbDiagnostics.ps1` (30 cases: 27 retained, 2 negative handshake cases, 1 negative live-launcher case); `pwsh -NoProfile -File tests/ci/Test-LocalDbBootstrap.ps1` (46 bootstrap cases plus diagnostics); hosted Full APP-00A and all companion gates. APP-00A already invokes the diagnostics suite via the bootstrap test before SQL execution. Independent review and final-source/main receipts are separate from this candidate; record them in the PR. Preserve the original failed job and any further failed attempts.

**Non-claims:** G75-01 is verified within REL-75.1; no claim that other failure signatures have been fixed. R75-01 (engine startup), R75-02 (HTTP 500) and R75-03 (recovery assertion) remain unresolved; LAB-E03/E04, #68/#75, physical PREPROD, EV/P0 and production/Pilot status are not advanced.

## 6. REL-75.3 — LocalDB investigation matrix (27/11/2026)

**Scope / status:** `SOURCE_CANDIDATE / REVIEW_AND_CI_PENDING / MATRIX_NOT_RUN`. Owner authorized REL-75.3 after [REL-75.2 closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7219038626). Base `10176d1316b2c6a6e441ebc89f655990f60566be`. R75-01 / LAB-E03 only: gather discriminating evidence; no remediation, production, PREPROD or laptop changes. R75-02 direct 1205 evidence from main attempt 1 is tracked separately in REL-75.4/.6.

### Matrix and controls

| Arm | Runner / instance | Engine policy | Official sample size |
| --- | --- | --- | ---: |
| win22-auto | windows-2022 / MSSQLLocalDB | existing 15.0 control helper, sole registered version and corresponding tool | 20 |
| win22-named | windows-2022 / fresh REL75_<run>_<attempt>_<iteration>_<random> | same 15.0 family, verified default tool, isolated named path | 20 |
| win25-auto | windows-2025 / MSSQLLocalDB | actual sole installed 15/16/17.0 version and matching tool, use existing control if 15.0 | 20 |

Each sample owns one new GitHub-hosted Windows VM; no reuse, no self-hosted, no laptop. Verify source, event, run, OS build (20348/26100), target directory absence, exact automatic-not-created result or named-list absence before creation; reject ambiguous inventory and foreign/pre-existing/reparse targets. No version installation, delete, stop, reset, DROP, migrations, system-file changes or business SQL. Named create uses verified default utility version, not an explicit potentially incompatible version. Existing `Initialize-LocalDb.ps1` and required-CI gates stay byte-identical. The experimental alternative uses the same create/start/SQL readiness budget: 60s bootstrap; create max 45s, start 15s, info 3s, readiness 12s; only pure login timeout after exact Running verification may retry (max three readiness probes). Read-only master checks IsLocalDB and expected runtime. Separate preflight (15s) and collection are not included in bootstrap timing. Native error or unexpected success text fails even if exit code is zero.

### Usage and evidence

- `.github/workflows/rel75-localdb-matrix.yml` is independent of the ten required workflows. PR changes to harness run 3 smoke samples (one per arm), contract tests and a summary; no push trigger. After merge use Actions → **REL-75.3 LocalDB investigation (non-gating)** → Run workflow → main, `samples_per_arm=20`. Default 20, only 1 or 20 accepted. Max 6 concurrent, 6-minute job timeout. A new manual dispatch is a new experiment; do not replace a failed observation by rerun. Rerun attempts cannot mix older receipts; missing exact-attempt cells are INCOMPLETE.
- `scripts/Invoke-Rel75LocalDbSample.ps1` writes one `REL75_LOCALDB_SAMPLE_V1` receipt under `TestResults/REL75-Matrix/sample.json` per fresh runner. Source/workflow SHA, run/attempt, arm/iteration, image and OS, tool/registered/selected/engine versions, stages with timings/exit/classification, readiness count, file metadata (fixed system database/log basenames, size and UTC timestamps only), disk format/free/total bytes, bounded SQL numeric signatures (existing 64KiB/16 records per log) and event metadata (max 8) before/after as applicable. No raw native output, event payload, user paths, contents or hashes of system database files. Event records are runner-level correlation, not proof they belong to the target; metadata cannot identify a mismatched file pair by itself.
- `scripts/rel75_localdb_matrix.py` plans a finite matrix and validates exact per-cell identity/attempt, source, target, version, stage and diagnostic evidence. Missing, invalid, not-cold, unsupported inventory, missing/partial metadata or unexpected artifacts produce INCOMPLETE; no invented denominator. Count READY and BOOTSTRAP_FAILED separately, and include image/version strata and numeric signatures. `COMPLETE_WITH_BOOTSTRAP_FAILURES` is valid investigation evidence but remains a failed workflow, not a gate waiver. `COMPLETE_ALL_READY` reports sample observations, not a claim of zero flake. Artifact names include run/attempt/arm/iteration; always upload receipts, including failures; summary always attempts aggregation; retain original negative history. Export/link relevant artifacts before expiry (30 days).
- Regressions: Python schema/plan/aggregation tests and PowerShell native-boundary mocks (foreign/non-hosted/collision, valid cold path, zero-exit failure, wrong metadata, pure timeout versus fatal mixed failure). Required Full CI must still pass on reviewed final source and main; smoke and matrix are independent of required workflow success.
- Acceptance for REL-75.3: reviewed harness + source/main Full receipts + 60 scheduled cold observations with valid exact-attempt summary, per-arm actual counts and stratification, reviewed logs/signatures and evidence-backed root-cause disposition. If not cold, unavailable runner/engine, missing diagnostics, or no reproduced/isolated root cause, report the exact gap and keep native root cause INVESTIGATION_BLOCKED; no automatic repair or claim of fix. A different engine on 2025 confounds OS/engine; do not attribute differences to OS alone. Additional pinned engine comparison needs evidence and a separately scoped decision. Any 5173/528 recurrence is a lead, not proof of which files mismatched.

[Microsoft: SqlLocalDB utility](https://learn.microsoft.com/en-us/sql/tools/sqllocaldb-utility) · [GitHub-hosted runner images](https://github.com/actions/runner-images).

## Tham chiếu

- [Microsoft: explicit create và start](https://learn.microsoft.com/en-us/sql/connect/php/php-driver-for-sql-server-support-for-localdb).
- [Microsoft: first-use automatic instance và connection timeout](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb).
- [Microsoft: SqlLocalDB utility](https://learn.microsoft.com/en-us/sql/tools/sqllocaldb-utility).
- [Microsoft: sqlcmd timeout/error options](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility).
- [Microsoft: SQL error 5173](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-5000-to-5999?view=sql-server-ver15).
- [Kế hoạch triển khai](../superpowers/plans/2026-11-25-localdb-bootstrap.md), [CI bốn worker](app-00a-parallel-ci.md), [hủy PR bị thay thế](pr-ci-concurrency.md).
