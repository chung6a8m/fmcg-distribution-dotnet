# APP-02E — Hợp đồng xác minh DEV

**v0.1 · 23/11/2026 (Asia/Bangkok) · Phạm vi DEV đã duyệt · Bộ tài liệu chờ duyệt · `NOT_RUN`**

## Nguồn và quyền thực hiện

Baseline đề nghị là `main` tại `fb2bb1f69425bf30e8dbefc613b6f83c8c0e0704`, Git tree `73b64295b70de7993805ac03f6ac62f2ad8614b1`. C.6 và D.1–D.4 đã ký DEV. Owner duyệt phạm vi APP-02E DEV trên Windows LocalDB, hai tenant giả lập, và yêu cầu duyệt bộ tài liệu trước khi chạy. Chữ ký trước đó giữ phạm vi của từng increment. [Roadmap APP-02](app-02.md) · [C.6 đã ký](../closeout/2026-11-22-app-02c6-localdb.md) · [D.4 đã ký](../closeout/2026-11-23-app-02d4-localdb.md)

Đề xuất chạy lại nguyên bộ A.4, C.6 và D.1–D.4 trên một commit, rồi tổng hợp theo [bảng truy vết AC–EV–P0](app-02e-ac-ev-p0-traceability.md). Cách này kiểm lại hành vi thực của các luồng đã có và giữ cách đếm, fixture, ranh giới từng suite. Lập hồ sơ chỉ từ các lượt cũ có thể kiểm được truy vết nhưng thiếu bằng chứng chạy lại trên một nguồn. Một hành trình dùng chung DB qua mọi suite cần fixture và runner mới, ngoài v0.1. Hồ sơ này giới hạn ở hồi quy hợp nhất theo bằng chứng từ các lượt chạy độc lập.

## Môi trường và fixture

- Windows, `(localdb)\MSSQLLocalDB`, .NET SDK 10.0.404, Node 26, Chromium từ lockfile. Lưu phiên bản thực, máy và operator. Khóa Git SHA/tree và build Release từ checkout sạch. Ghi hash assembly API hoặc binary test thực thi theo từng suite.
- Hai tenant giả lập `tenant-a` và `tenant-b` trong mỗi suite. Chạy các suite tuần tự, mỗi suite có cặp DB scratch và run ID riêng cho hai tenant. Một số ca guard, như D4-01, còn tạo DB scratch probe tạm thời thuộc nonce riêng. Ghi các DB probe ở phần hạ tầng và teardown, ngoài mẫu số lượt ca. Giữ guard instance, tên DB, ownership marker, nonce và tenant binding cho create/migration/reset/cleanup. Tenant lấy từ danh tính được server xác thực. Lưu receipt thu hồi DB theo nonce và kiểm tồn dư.
- Giữ clock và nguồn số liệu do suite gốc quy định. Dữ liệu dùng `SYNTHETIC_UNITLESS`, delivery giữ `InvoiceEligible=false`, `B_NO_CHANNEL` giữ `BLOCKED` và offline business writes tiếp tục tắt. Hai đối tác D.4 giới hạn ở loopback. [Fixture v0.1](../../scripts/fixtures/app02e/fixture-v0.1.json)
- Dùng API/UI và oracle SQL của suite gốc. So sánh từ số liệu kỳ vọng cố định với dữ liệu đã commit, kiểm provenance và hai tenant ở các điểm suite quy định. Giữ nguyên expected, guard, oracle và nghĩa nghiệp vụ từ baseline. Khi thiếu chứng cứ, ghi rõ outcome quan sát được và phạm vi còn thiếu.

## Điều kiện duyệt và chốt DEV

Owner duyệt revision v0.1, baseline commit, 6 suite với 91 mã và 175 lượt theo scope gốc, quyền chạy trên host Windows, scratch DB, fault/restart/cleanup, operator và người đối chiếu. Khóa SHA-256 của bốn tệp trình duyệt theo UTF-8 chuẩn hóa LF trước khi chạy và ghi hash bytes của artifact thực tế khi xuất. Trên cùng nguồn, lưu 175 khóa suite/ca/scope, expected/actual/diff, mã nguồn và thời gian thực thi, log fault/retry, fingerprint, JUnit/TRX khi suite có sinh, cùng hash và vị trí artifact. [Ma trận v0.1](app-02e-dev-acceptance-matrix.md)

Chỉ trình kết quả hồi quy DEV khi đủ 6 suite đạt oracle, 175/175 lượt khớp, gate vào/ra đạt, bằng chứng nguồn và teardown khớp, hồi quy CI bắt buộc của repo trên source/tree tương ứng đã được xác minh và Owner đã xem báo cáo. CI hosted hiện hành chạy các ma trận đến D.3. D.4 trong v0.1 lấy từ lượt Windows LocalDB chạy lại. Lỗi hoặc thiếu dấu vết giữ trạng thái `FAIL`, `BLOCKED` hoặc `NOT_RUN` theo nguyên nhân. Mọi thay đổi baseline, expected hoặc phạm vi cần đưa lại vào bản duyệt phù hợp trước lượt bị ảnh hưởng.

Chứng cứ EV thật vẫn `EVIDENCE_PENDING` hoặc `MODEL_ONLY` theo baseline; cả năm P0 vẫn `NOT_VERIFIED`. C-PREPROD giữ `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`, PREPROD `FOUNDATION_ONLY / NO_GO`; B.1/B.9, RUNTIME-01/02, Product Verification thực tế, Pilot, Go-Live và quyết định theo mode giữ điều kiện hiện hành.
