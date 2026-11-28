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

**Scope / status:** `CLOSED / MAIN_CI_VERIFIED / MATRIX_COMPLETE_WITH_BOOTSTRAP_FAILURES` (investigation only). Owner authorized REL-75.3 after [REL-75.2 closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7219038626). Base `10176d1316b2c6a6e441ebc89f655990f60566be`. R75-01 / LAB-E03 only: gather discriminating evidence; no remediation, production, PREPROD or laptop changes. R75-02 direct 1205 evidence from main attempt 1 is tracked separately in REL-75.4/.6.

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

### Official closeout and remediation handoff

**REL-75.3 closeout:** `CLOSED / MAIN_CI_VERIFIED / MATRIX_COMPLETE_WITH_BOOTSTRAP_FAILURES` — investigation scope only, 27/11/2026. [PR #178](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/178), reviewed source `e105f328594365dbe2a595fab4af29816e8f6ee5`, merge `fcd914247c52ec05d69e3111d15a1cfb02101a0c`. [Source receipt](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/178#issuecomment-7220259611), [main Full receipt](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7220883809), [official closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7221415264). Source/main required 10/10 workflows, 53 success/3 policy skips, 406/406 .NET and 1440/1440 oracle cells; main attempt 1. 19 Python + 11 PowerShell matrix regressions, 3/3 smoke READY. Official [run 46016885607 attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46016885607/attempts/1): 60/60 fresh cells, 52 READY, 8 failed; all 61 ZIPs independently CRC/SHA-256 verified, no missing/invalid/partial artifacts or replacement. [Summary 15206354389](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46016885607/artifacts/15206354389), SHA-256 `db5b35b070639b27cb28b6300cca2514f4e7cb3287149105d7929c4b9e77efd0`. Export archive SHA-256 `acbcb33a8f3e7d294f77e3b6d0dbf7a8252b6d4935d904f2f62d39baae1e735f`; retention 30 days, preserve Owner export.

| Arm | Actual engine | READY / FAILED | READY median ms |
| --- | --- | --- | ---: |
| win22-auto | 15.0.4382.1 | 17 / 3 | 14394 |
| win22-named | 15.0.4382.1 | 19 / 1 | 20870 |
| win25-auto | 17.0.4025.3 | 16 / 4 | 13016 |

All eight failures occurred before readiness: two create InstanceStartFailed / SqlServerStartupFailed with SQL 5178/state 1/severity 16 and Event 528 (including named instance), six NativeProcessTimeout (five create, one start), no recorded SQL/event in timeout family. No evidence proving earlier 5173 and 5178 identical, exact affected filename/sector geometry or native timeout mechanism. Windows 2025 comparison confounds OS/engine; no default runner/engine, timeout, storage or registry change. R75-01 `REPRODUCED / FAILURE_SIGNATURES_CLASSIFIED / NATIVE_ROOT_CAUSE_INVESTIGATION_BLOCKED`, G75-02 OPEN. REL-75.5 must collect read-only exact-volume sectorinfo and original/actual sector size if reliably available, separately classify create/start native progress, then select evidence-backed remediation. Retain the separate two SQL 1205 HTTP recurrences for REL-75.4/.6; no claim that LocalDB startup and A11-09 share a root cause.

Next runtime investigation: [REL-75.4](rel75-a11-reproduction.md), independent of REL-75.5.

## 7. REL-75.5 — Native LocalDB evidence and disposition (28/11/2026)

**Status:** `SOURCE_CANDIDATE / INDEPENDENT_REVIEW_AND_CI_PENDING / NATIVE_ROOT_CAUSE_UNDETERMINED`. Owner selected option 1 on main `ccfd7b7df533f08e284e0b73e86a8ba7fc46592c`: prioritize infrastructure without closing REL-75.4. **REL-75.4 source/main and official disposition (28/11/2026):** PR #179 final source `b0c3068de50f8abe4280c6036671c5b436dc6227`, main merge `ccfd7b7df533f08e284e0b73e86a8ba7fc46592c`. [Final review](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/179#pullrequestreview-6095000096), [main Full verification](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7233970732), [official matrix disposition](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75#issuecomment-7234372400): main Full 10/10 workflows, 53 jobs/3 expected skips, 407/407 .NET, 1440/1440 SQL; [official run 46107249575 attempt 1](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46107249575/attempts/1) 59 valid PASS and 1 INCOMPLETE, 2832/2832 SQL cells, 61/61 ZIP CRC/SHA-256 verified, no new 1205 or graph. All 10 C/P and nine executed P-commit/sweep cells observed directed blocking and recovered. Missing p-commit-sweep/tenant-b/2 cold create: 5537ms, SQL5173/16/1 twice, Event528, selected engine 15.0.4382.1, image 20261123.376.1, zero readiness probes. [Artifact 15248900247](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46107249575/artifacts/15248900247), SHA-256 `501713d0acfd982a26c4f3bb16f0c28e1fcda89cf35b9bc48220de34227c86e1`; [summary 15248388765](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/46107249575/artifacts/15248388765), SHA-256 `96ec2da26d79c06a09d225ac412931cff9ad524ce7697ada756075f0eb9391af`. Owner explicitly selected option 1: prioritize REL-75.5; **REL-75.4 remains open and blocked**, no Owner Accepted, no replacement or combination of failed cells, no remediation/root-cause claim.

[Implementation/acceptance contract](rel755-native-evidence.md). Phase A: read-only sector geometry and source-bound original/actual pair when supported; separate create/start timeout timeline and exit evidence. Same three hosted arms as REL-75.3, 3-cell PR smoke, 60-cell official experiment after merge/main Full. Phase B: choose separately scoped evidence-backed remediation only if the observation establishes the applicable cause. Existing bootstrap, budgets, required workflows, runner defaults and business contract unchanged. R75-01/G75-02 remain OPEN; R75-02/03 are separate.

## Tham chiếu

- [Microsoft: explicit create và start](https://learn.microsoft.com/en-us/sql/connect/php/php-driver-for-sql-server-support-for-localdb).
- [Microsoft: first-use automatic instance và connection timeout](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb).
- [Microsoft: SqlLocalDB utility](https://learn.microsoft.com/en-us/sql/tools/sqllocaldb-utility).
- [Microsoft: sqlcmd timeout/error options](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility).
- [Microsoft: SQL error 5173](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-5000-to-5999?view=sql-server-ver15).
- [Kế hoạch triển khai](../superpowers/plans/2026-11-25-localdb-bootstrap.md), [CI bốn worker](app-00a-parallel-ci.md), [hủy PR bị thay thế](pr-ci-concurrency.md).
