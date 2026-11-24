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

## Tham chiếu

- [Microsoft: explicit create và start](https://learn.microsoft.com/en-us/sql/connect/php/php-driver-for-sql-server-support-for-localdb).
- [Microsoft: first-use automatic instance và connection timeout](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb).
- [Microsoft: SqlLocalDB utility](https://learn.microsoft.com/en-us/sql/tools/sqllocaldb-utility).
- [Microsoft: sqlcmd timeout/error options](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility).
- [Microsoft: SQL error 5173](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-5000-to-5999?view=sql-server-ver15).
- [Kế hoạch triển khai](../superpowers/plans/2026-11-25-localdb-bootstrap.md), [CI bốn worker](app-00a-parallel-ci.md), [hủy PR bị thay thế](pr-ci-concurrency.md).
