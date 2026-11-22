# APP-02D.4 — Hợp đồng DEV mô phỏng hóa đơn và đối soát kế toán

**v0.1 · 23/11/2026 (Asia/Bangkok) · `DOCS_ONLY / OWNER_APPROVAL_PENDING / NOT_RUN`**

## Điều kiện bắt đầu

| Điều kiện | Kết quả rà soát |
| --- | --- |
| Tiền nhiệm | D.3 đã ký trên `3e61d44`, merge PR #116 tại `808b6d6`; mốc `main` là `7597dda0b7f4a479b8b17155d35299fc17c1cfd2`. C.6, D.1–D.3 cung cấp nguồn giao A, bán–giao B và Mixed đã nghiệm thu DEV. |
| Windows LocalDB | Có `(localdb)\MSSQLLocalDB` 15.0.4382.1, .NET 10.0.404 và Node 26.13.1. Instance đang dừng khi rà soát. Bằng chứng chạy hai tenant gần nhất thuộc D.3. |
| Nền tảng kỹ thuật | APP-02A.3/A.4 có mẫu ghi receipt bền, khóa định danh, retry cùng khóa, xử lý ACK và đối soát SQL. Outbox C/P hiện ràng buộc chặt với cashier acceptance. D.4 dùng logic phù hợp và hợp đồng lưu trữ thử nghiệm riêng cho hai đối tác. |
| Quyền thực hiện | Đang chờ Owner duyệt hợp đồng và [ma trận, fixture v0.1](app-02d4-dev-acceptance-matrix.md). Mã nguồn, migration và lượt chạy D.4 theo phạm vi đã duyệt sau đó. |

[D.3 đã ký](../closeout/2026-11-22-app-02d3-localdb.md) · [Roadmap D.0–D.4](app-02d0-dev-contract.md) · [APP-02A.3](app-02a3-worker-transactional-recovery.md) · [APP-02A.4](app-02a4-independent-sql-oracle.md)

## Phạm vi và cách chạy đề xuất

Mô phỏng các nhánh AC-14 về định danh, mất ACK và thiếu kênh, cùng AC-15 về tiếp nhận, ghi sổ, đối soát và sửa sai. Chạy ứng dụng và hai đối tác giả lập qua loopback trong phiên IntegrationTest có hai DB thuộc nonce. Mỗi tenant dùng cùng DB scratch của mình cho dữ liệu ứng dụng và vùng SQL mô phỏng đối tác, với quyền SQL, kết nối và giao dịch riêng. Bootstrap dùng quyền owner của DB scratch. Để kiểm quyền trong LocalDB, các phiên nghiệp vụ dùng user cấp DB qua ngữ cảnh thực thi có giới hạn, gồm user app, từng stub và oracle chỉ đọc. Kiểm thử phải ghi nhận user hiệu lực và chứng minh SQL từ chối ghi ngoài vùng được cấp. Đối tác mô phỏng ghi bền trước khi trả ACK, và không ghi vào bảng nghiệp vụ. API đối tác dùng danh tính kỹ thuật riêng, tenant ràng buộc server-side. Giới hạn địa chỉ loopback và cấu hình phiên giúp phép thử kiểm chứng được cả điểm lỗi phía gửi lẫn phía nhận. Giả lập trực tiếp trong một giao dịch sẽ bỏ mất ranh giới commit cần kiểm chứng. Các cửa gọi bên ngoài, khóa/chứng thư thật, chứng từ thật và sổ pháp lý thuộc kiểm chứng độc lập.

Nạp fixture có nguồn commit qua các luồng DEV đã nghiệm thu. Chép dữ liệu cần thiết sang chứng từ thử nghiệm có liên kết ID/hash bất biến tới delivery, source line và snapshot nghiệp vụ. `SYNTHETIC_UNITLESS`, `InvoiceEligible=false`, source/ledger, trạng thái trip và outbox C/P giữ nguyên. Route thử nghiệm có guard môi trường, profile D.4, nonce và tenant binding; mọi thao tác online. UI tối thiểu dùng workspace và controls đã duyệt, hiển thị rõ nhãn mô phỏng, định danh gốc, trạng thái, cutoff và case. Đọc, gửi, tra cứu và phê duyệt đều kiểm quyền hiện hành tại server. [UI baseline](../ui-ux/README.md)

## Hóa đơn mô phỏng

- Tạo một intent thử nghiệm cho **mỗi delivery đã commit**, cùng tenant, seller alias, channel profile và phiên bản fixture do server xác nhận. Định danh ổn định theo tenant + seller + delivery + loại intent; đóng băng channel và payload tại intent đầu. Ràng buộc duy nhất theo tenant + delivery + loại intent ngăn tạo khóa thứ hai nếu seller đổi. Cùng khóa và hash trả cùng intent; thay payload hoặc đổi seller/channel trên delivery đã có intent bị từ chối. Chỉ delivery đủ điều kiện **trong profile mô phỏng** mới vào đường tạo intent. Các đợt giao A có ID độc lập; load/trả xe, receipt và handover không tạo intent.
- Intent có trạng thái `PREPARED`, `UNKNOWN` và `MOCK_RECORDED`. Lượt gửi chỉ thành công khi có đối tác giả lập chỉ định; fact trả về phải khớp khóa, profile và SHA-256. Một khóa ổn định có tối đa một fact phía đối tác. `MOCK_RECORDED` ghi fact thử nghiệm, không thể dùng để phát hành hoặc làm thay đổi `InvoiceEligible`.
- Ghi lượt dispatch bền trước lời gọi. Khi mất ACK, timeout hoặc khởi động lại với lượt dispatch chưa có kết luận, ghi hoặc khôi phục `UNKNOWN` trước khi tra cứu. Tra cứu cùng định danh gốc qua đúng profile. `FOUND` hợp lệ gắn lại fact cũ và dừng gửi. Chỉ khi có result `ABSENT` có thẩm quyền từ stub trên đúng khóa và không còn xử lý đang dở, mới gửi lại **cùng định danh**. Timeout tra cứu, trạng thái đang xử lý, identity hoặc payload xung đột giữ `UNKNOWN` hoặc mở case có owner/next action; không tự đổi ID, seller hay channel. Ranh giới trước commit và sau commit được kiểm riêng.
- B thiếu kênh giữ `B_NO_CHANNEL`. Ý định chưa giao bị chặn trước post. Nhánh kiểm chứng sự kiện đã xảy ra dùng fact quan sát **giả lập riêng**, với thời điểm khai báo và thời điểm tiếp nhận riêng, lập case có nguồn và người xử lý. Nhập lại cùng fact chỉ trả case cũ. Giữ nguyên delivery, AR, stock, invoice intent và thời điểm lịch sử.

## Kế toán mô phỏng và correction

- Lấy một snapshot có version/hash theo tenant, source line đã commit và loại ghi nhận. Server tra lại source, phân loại, quy đổi fixture và đóng băng mapping kỳ ghi sổ trong payload. Kho đối tác dedup theo tenant + source line + loại ghi nhận dù message vận chuyển khác. Cutoff/asOf chỉ thuộc báo cáo, không tham gia khóa receipt/posting. Cùng định danh và hash trả receipt/posting gốc; đổi nội dung gọi là conflict. ACK vận chuyển chỉ xác nhận receipt đã commit. Lệnh post riêng, với quyền kế toán mô phỏng hiện hành, tạo một posting bất biến và tham chiếu chứng từ thử nghiệm từng dòng.
- Lưu các mốc `RECEIVED`, `POSTED`, `RECONCILED_OK` và `RECONCILED_DIFF` với lịch sử riêng. Reconciliation so từ tập source kỳ vọng được xác định độc lập với inbox và bảng posting phía stub. Khoảng kỳ `[from, cutoff)` dùng thời điểm nghiệp vụ đã khóa của source và khai rõ timezone. Báo cáo lưu asOf, N kỳ vọng, số RECEIVED, POSTED, OK, DIFF, pending, nguồn ngoài tập và thiếu. Từng dòng đối chiếu ID, mapping, kỳ, lượng, tiền, AR, giá vốn và dấu `actual − expected`. Thiếu receipt/posting thì dòng dự kiến ở trạng thái pending; posting đã có nhưng thiếu dòng hay sai/thiếu mapping thì ghi DIFF. Khi nguồn kỳ vọng chưa xác định được, báo cáo ghi lý do và để N chưa biết. Các trường hợp này chưa xác nhận toàn bộ scope đạt. Mốc chỉ kết luận aggregate đạt khi biết N và mọi dòng trong phạm vi khớp.
- Sai lệch đã post mở một case theo source/phiên bản đối soát có ảnh SQL bất biến, owner và next action. Maker đề nghị đúng delta khóa vào kỳ cho phép, Checker hiện hành khác Maker duyệt. Append một posting correction mô phỏng mới tham chiếu posting sai, report và approval; lưu chứng từ sai, ảnh DIFF cùng lịch sử. Sau correction, yêu cầu một lần đối soát mới độc lập để xác nhận OK và kiểm tra điều kiện đóng case. Replay correction giữ nguyên ID/hiệu ứng; kỳ khóa hoặc sai scope từ chối. D.4 chỉ điều chỉnh sổ mô phỏng, dữ liệu ứng dụng và các bút toán thật giữ nguyên.

[AC-14/15](../../business/acceptance.md) · [BR-18/19](../../business/business-rules.md) · [SOP-10](../../business/workflows/assurance-and-exceptions.md)

## Owner cần duyệt

1. Phạm vi mô phỏng hai đối tác loopback, hai tenant, quyền và ranh giới commit giữa ứng dụng và đối tác.
2. Quy tắc một intent thử nghiệm mỗi delivery, định danh cố định, `UNKNOWN` xử lý bằng tra cứu, và case giả lập khi B mất kênh.
3. Quy tắc dedup kế toán theo source line, cutoff, dữ liệu thiếu, Maker/Checker và correction nối nguồn có đối soát lại.
4. Fixture số, đồng hồ, và **18 ca / 36 lượt ca–tenant** trong ma trận. Sau phê duyệt đóng băng SHA-256 cho hợp đồng, ma trận, fixture và bảng expected-results trước lượt chạy.

Kết quả DEV cần đủ ca, SQL độc lập, CI trên nguồn cuối và chữ ký sau xem bằng chứng. EV-10/11 thật, P0, B_ONLINE/Mixed vận hành, Pilot và Go-Live tiếp tục có cổng riêng. C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`, PREPROD `FOUNDATION_ONLY / NO_GO`. B.1/B.9 và RUNTIME-01/02 giữ nguyên.
