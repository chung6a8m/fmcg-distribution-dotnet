# APP-02E EV/P0 — Yêu cầu bổ sung bằng chứng chỉ đọc

**v0.1 · 23/11/2026 (Asia/Bangkok) · Bản trình Owner duyệt trước khi gửi**

Gói này chuyển 31 mục còn thiếu sau rà soát thành yêu cầu bàn giao. Đầu mối được ghi theo vai trò. Owner xác nhận người nhận và nơi bàn giao theo từng nhóm. Mỗi bên nhận các dòng thuộc phạm vi mình phụ trách và mẫu bàn giao chung. [B.1/B.9 v0.2](b1-b9-readiness-v0.2.md) · [Writer/opening Kho-01 v0.2](kho-01-writer-opening-v0.2.md) · [Seller/provider/kế toán v0.2](seller-provider-accounting-evidence-v0.2.md)

## Thứ tự đề xuất

| Ưu tiên | Mục đích | Số mục |
| --- | --- | ---: |
| 1 | Xác định phạm vi và định danh nguồn. Ghim môi trường, HEAD và phân nhiệm B.1/B.9, cutoff và tập nguồn Kho-01, seller/mode và mapping kế toán. | 7 |
| 2 | Thu bản xuất gốc đã có theo phạm vi. Gồm quyền và biên nhận vận hành, danh mục writer, snapshot opening, hồ sơ pháp lý và kênh provider. | 19 |
| 3 | Ghép các nguồn và bàn giao kết quả đối chiếu. Gồm số opening, biên nhận provider, sổ đích, correction và hồ sơ phạm vi thử dự kiến. | 5 |

Các đầu mối có thể chuyển ngay tài liệu đã có sau khi gói gửi được duyệt. Khi ghép, dùng cùng environmentId/HEAD cho B.1/B.9, cùng Kho-01 và cutoff/asOf cho opening, cùng tenant/seller/mode, source key và kỳ cho EV-10/11. Phần đối chiếu không đủ phạm vi ghi rõ phần thiếu và đầu mối bổ sung.

## Yêu cầu theo đầu mối

Cột **Đối chiếu** ghi vai trò xác nhận. Với mục cần nhiều bên, đầu mối đứng trước phối hợp thu phần của các bên còn lại. Các vai độc lập của B.9 giữ nguyên theo hợp đồng nghiệm thu.

### Owner, Tenant Owner và IT dữ liệu

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| B-RO-01 | 1 | Owner | Tham chiếu quyết định nonproduction nếu đã có, environmentId, host fingerprint, operator, cửa sổ, phạm vi thử, điểm dừng và phương án khôi phục. Ghi rõ phần đang chờ quyết định. | Operator PREPROD |
| O-RO-01 | 1 | Tenant Owner, IT dữ liệu | Phạm vi toàn Kho-01 gồm ngoài wave, cutoff/asOf dự kiến, danh sách vị trí, snapshot/extract ID, schema/mapping và tập nguồn độc lập để xác định N. | Kho-01, kế toán |
| SPK-RO-01 | 1 | Tenant Owner | Ánh xạ tenant → pháp nhân seller, mode A/B_ONLINE/Mixed, thẩm quyền, hiệu lực và ID duyệt policy. | Finance, Legal |
| SPK-RO-08 | 3 | Product Owner, Tenant Owner | Hồ sơ hoặc dự thảo phạm vi lượt thử, exact build, môi trường, tenant/seller/mode, operator/observer, loại outbound, cửa sổ, điểm dừng và nơi giữ bằng chứng. Ghi rõ mục chưa duyệt. | Provider, kế toán, Security |

### Operator PREPROD, implementer, Security, DBA và Fixture Owner

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| B-RO-02 | 1 | Operator PREPROD, DBA | Sơ đồ mạng/runtime và vantage/proxy, định danh hai DB vật lý A/B, binding Registry, current epoch và grant API/Worker/Control Plane theo môi trường. | Security |
| B-RO-03 | 1 | Implementer | Exact source HEAD, image digest API/Worker, hash migration, manifest đã che bí mật, nơi giữ raw evidence, mẫu TestRunId và biên nhận đồng bộ giờ nếu có. | Observer |
| B-RO-04 | 2 | Security, vận hành | Bản xuất quyền IAM/refresh/revoke/suspend, metadata Vault/TLS/rotation, audit/retention/legal hold và hồ sơ tuyến/biên nhận cảnh báo đã có. Gắn bằng chứng revoke sau restore nếu có. | DBA |
| B-RO-05 | 1 | Operator độc lập, Fixture Owner | Tham chiếu trust file độc lập, bốn dấu vân tay khóa công khai, xác nhận phân nhiệm và fixture ký sẵn nếu có. | Observer, Security và DBA độc lập theo B.9 |
| B-RO-06 | 2 | DBA | Catalog backup mã hóa ngoài host, chuỗi LSN FULL/DIFF/LOG và biên nhận restore hiện có gồm thời gian, witness SQL/epoch, journal hiệu ứng mô phỏng, dấu vết A/B và safe re-entry để đối chiếu RPO/RTO. | Security, observer |
| B-RO-07 | 2 | Operator PREPROD, vận hành | Tham chiếu biên nhận RUNTIME-01/02, audit, cảnh báo và incident đúng môi trường nếu đã có. Giữ `NOT_RUN` cho probe vật lý chưa chạy. | Observer, Security |
| B-RO-08 | 2 | Implementer | Liên kết blocker và run/attempt/artifact của 10 check cùng APP-00A audit trên HEAD thực thi khi xác định. Đính tham chiếu review độc lập và manifest vật lý đã ký nếu có. | Security và DBA độc lập |
| W-05 | 2 | DBA | Metadata login/grant, stored procedure, script, job SQL, quyền service và quyền khẩn cấp liên quan Kho-01. | Security, quản trị hệ cũ |
| W-08 | 2 | DBA, vận hành ứng dụng | Runbook, danh sách job restore/failover, quyền mở lại, checkpoint queue và journal hiệu ứng ngoài có thể replay. | Security, chủ hệ đối tác |

### Kho-01, điều vận, bán hàng và giao nhận

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| W-01 | 2 | Quản lý Kho-01, quản trị hệ cũ | Danh sách thao tác UI cũ, kho/vị trí, tài khoản và log nhập/xuất/chuyển, kiểm kê, sửa tồn, QA/quarantine, write-off, gồm ngoài wave. | IT |
| W-02 | 2 | Điều vận, bán hàng | Route, quyền, roster tuyến/xe/manifest và đường hoàn trả, gồm ngoài wave. | Kho-01, IT ứng dụng |
| W-06 | 2 | IT thiết bị, giao nhận | Danh sách mobile/scanner, thiết bị chưa đồng bộ hoặc đã thay, journal sync muộn và mã phiếu giao/thu còn mở. | Kho-01, thủ quỹ |
| O-RO-02 | 2 | Quản lý Kho-01 | Phiếu kiểm kê và sổ tồn/giá vốn theo SKU, lô/hạn, UoM, lượng và cost version. Gồm MAIN, xe, transit, quarantine và ngoài wave. | Kế toán tồn kho |
| O-RO-03 | 2 | Điều vận, bán hàng | A hold, B quota/credit grant, manifest và chuyến mở, phần đã load/đã giao và nguồn đối chiếu Delivery. | Kho-01, Finance |

### IT tích hợp, vận hành ứng dụng và chủ ETL

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| W-03 | 2 | IT tích hợp | Catalog gateway/API, CRM/ERP, webhook, file connector, đối tác, principal và log ghi hiện có. | Chủ hệ đối tác, Security |
| W-04 | 2 | Vận hành ứng dụng, chủ ETL | Danh sách import, ETL, scheduler, service account, checkpoint/queue, retry và dead-letter replay. | IT tích hợp |

### Finance, Legal, thủ quỹ và kế toán hai phía

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| SPK-RO-02 | 2 | Legal, Finance/thuế | `LEGAL-MEMO`, `B-CASE-001..003`, căn cứ Delivery/chuyển quyền, quy tắc phát hành/điều chỉnh/hủy/hoàn tiền và phiên bản hiệu lực. | Tenant Owner |
| W-07 | 2 | Thủ quỹ, kế toán | Danh mục writer tiền/ngân hàng/credit, nộp quỹ, tạm ứng, posting/correction, principal và sổ đích. | Finance, IT tích hợp |
| O-RO-04 | 2 | Kế toán công nợ | Bản xuất AR và CustomerCredit riêng, terms, receipt/allocation, hạn và khoản pending theo khách, nguồn và kỳ. | Finance |
| O-RO-05 | 2 | Thủ quỹ, kế toán | Custody, quỹ, cash/bank pending, nộp quỹ/tạm ứng theo người/chuyến/currency và chứng từ đếm/bàn giao. | Finance |
| O-RO-06 | 2 | Kế toán, provider, IT | Danh sách invoice/receipt/posting, chứng từ muộn, job và thiết bị dở, watermark và journal theo source ID. | Kho-01 |
| O-RO-07 | 3 | Kế toán, IT dữ liệu | Tập số nguồn độc lập, preview đích đã có nếu được phép xem, expected/actual/diff, n/N khi xác định được, ngoại lệ và người xử lý. | Kho-01, thủ quỹ |
| SPK-RO-05 | 1 | Kế toán gửi và nhận | Mapping nguồn → chứng từ/dòng/sổ đích theo phiên bản, kỳ/timezone, cutoff/asOf, quy tắc AR/giá vốn và tập nguồn độc lập để xác định N. | IT tích hợp |
| SPK-RO-06 | 3 | Kế toán nhận | Bản xuất chỉ đọc từ sổ đích, receipt/posting ID và thời điểm, payload hash, expected/actual/diff theo source/line, case thiếu dòng/sai kỳ/chưa post. | Kế toán gửi |
| SPK-RO-07 | 3 | Kế toán nhận, Finance | Hồ sơ sai lệch/correction đã có, posting gốc, approval phân nhiệm, ID adjustment/reversal và snapshot trước/sau. | Kế toán gửi |

### Provider hóa đơn và IT/Security

| Mã | Ưu tiên | Đầu mối cung cấp | Bằng chứng yêu cầu | Đối chiếu |
| --- | ---: | --- | --- | --- |
| SPK-RO-03 | 2 | Provider, IT/Security | Profile/kênh, API version, seller liên kết, môi trường, metadata quyền/chứng thư, thu hồi và hợp đồng lookup/retry/chống lặp. | Finance, Legal |
| SPK-RO-04 | 3 | Provider | Biên nhận đã có từ Delivery đến provider ID và ký/gửi/giao, journal mất ACK/`UNKNOWN`, lookup theo ID gốc, dấu vết retry/restore, case thiếu kênh và điều chỉnh nếu có. Ghi các lượt chưa chạy, gồm `INV-09`. | IT tích hợp, Finance |

## Mẫu thông điệp gửi từng đầu mối

**Tiêu đề đề xuất** · APP-02E EV/P0 — Bàn giao bằng chứng chỉ đọc — [nhóm đầu mối]

Anh/chị vui lòng bàn giao bằng chứng hiện có cho các mã [mã mục] theo bảng đính kèm. Dùng mẫu bên dưới và ghi rõ hệ nguồn, phạm vi, thời điểm, tham chiếu và phần chưa có. Nếu dữ liệu cần hạn chế truy cập, vui lòng gửi tham chiếu có quyền xem cho người đối chiếu theo kênh đã duyệt. Xin xác nhận đầu mối và ngày dự kiến bàn giao cho từng mục còn thiếu.

Phạm vi bàn giao gồm hồ sơ đang lưu và bản xuất chỉ đọc được phép từ hệ nguồn. Các phép thử, triển khai, thay quyền, backup/restore, freeze/cutover, phát hành, gửi lại và ghi sổ thuộc quyết định thực thi riêng.

## Mẫu bàn giao chỉ đọc

Sao chép một phiếu cho mỗi mã mục. Khi có nhiều artefact, thêm một dòng cho từng artefact dưới cùng mã. Dùng ngày giờ với timezone hoặc UTC. Giữ bản gốc tại kho được duyệt ngoài Git. Dùng bản che dữ liệu cho người đối chiếu, chỉ chia sẻ metadata và dấu vân tay công khai được phép. Giữ credential, khóa riêng và dữ liệu cá nhân ngoài gói bàn giao.

| Trường | Giá trị điền |
| --- | --- |
| Mã mục / ưu tiên | [mã] / [1, 2 hoặc 3] |
| Người cung cấp / vai trò / ngày bàn giao | [tên, vai trò, timestamp] |
| Người đối chiếu / ngày đối chiếu | [tên, vai trò, timestamp hoặc PENDING] |
| Hệ nguồn / cách thu | [hệ thống, hồ sơ đã có hoặc bản xuất chỉ đọc, report/query ID nếu có] |
| Phạm vi và định danh | [environmentId, HEAD/build, TestRunId hoặc tenant/seller/mode/Kho-01, vị trí và wave/ngoài wave theo mục] |
| Khoảng dữ liệu và phiên bản | [từ/đến, cutoff/asOf hoặc watermark, snapshot/extract ID, schema/mapping/version] |
| Tóm tắt đối chiếu | [ID liên kết nguồn/đích, expected/actual/diff, số dòng và n/N nếu xác định được] |
| Trạng thái bàn giao | [Đã cung cấp / Một phần / Chưa có / Chưa chạy / Chờ quyền xem] |
| Phần thiếu / lý do / hành động tiếp theo | [phạm vi còn thiếu, đầu mối bổ sung, ngày dự kiến hoặc chưa xác định] |

| Artefact ID và tên | Bản gốc / bản đã che | Tham chiếu chỉ đọc và phạm vi người xem | SHA-256 nếu có / thời điểm trích xuất |
| --- | --- | --- | --- |
| [ID / tên] | [loại bản] | [tham chiếu hoặc mã lưu trữ / vai được xem] | [hash / timestamp] |

Nếu chỉ có biên nhận DEV, CI hoặc LAB, ghi rõ phạm vi đó. Với phép thử chưa thực hiện, ghi `NOT_RUN` nếu checklist dùng trạng thái này. Giữ giá trị chưa biết là `PENDING` hoặc `UNKNOWN`. Chỉ ghi số 0 khi có chứng cứ nguồn. Trạng thái bàn giao ghi việc nhận hồ sơ. Kết quả nghiệm thu theo cổng vẫn theo hợp đồng hiện hành.

## Nội dung trình Owner duyệt

- Danh sách 31 mã, thứ tự ưu tiên và nhóm người nhận. Tên người cụ thể và ngày dự kiến điền khi xác nhận từng đầu mối.
- Mẫu thông điệp, mẫu bàn giao, kênh lưu đã duyệt ngoài Git và phạm vi người xem.
- Quyết định cho gửi gói yêu cầu sau khi duyệt nội dung. Các hồ sơ thực thi và nghiệm thu tiếp tục theo cổng riêng.

PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, B.1/B.9 vật lý `NOT_RUN / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`. EV-10/11 `EVIDENCE_PENDING`, năm P0 `NOT_VERIFIED`, `B_NO_CHANNEL` `BLOCKED`, Pilot và Go-Live `NOT_AUTHORIZED`. [Hồ sơ APP-02E DEV](../../closeout/2026-11-23-app-02e-localdb.md) · [Hợp đồng B.9](../app-02b9-independent-preprod-oracle.md)
