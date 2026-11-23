# Kho-01 — Danh mục writer và mẫu opening

**v0.2 · 23/11/2026 (Asia/Bangkok) · Đã cập nhật sau rà soát chỉ đọc · Cutover và opening thực `NOT_RUN`**

Áp dụng AC-04/16, EV-04/12, G-STOCK/G-CUT cho **toàn Kho-01**, gồm chuyến, điểm giữ hàng và hoạt động ngoài wave. Các dòng dưới là nhóm ứng viên để operator kiểm kê. Tất cả đường ghi thực và số mở đầu còn `PENDING`. [Baseline AC](../../../business/acceptance.md) · [SOP-11](../../../business/workflows/assurance-and-exceptions.md#sop-11--chuyển-đổi-số-dư-khóa-nguồn-cũ-forward-recovery) · [BR-05 và BR-15](../../../business/business-rules.md)

Bản này kế thừa [v0.1 đã được Owner duyệt](kho-01-writer-opening-v0.1.md).

## Kết quả rà soát chỉ đọc ngày 23/11/2026

Owner đã duyệt biểu mẫu v0.1. Đã đối chiếu cả tám nhóm với mã nguồn và runbook trên baseline `4a6f003`. Các đường ghi DEV và tài liệu kỹ thuật là căn cứ để tìm writer. Danh mục thực tế từng hệ thống, principal/job/thiết bị Kho-01 và mọi nguồn ngoài wave vẫn `PENDING`. Opening chưa có snapshot đã chốt, chưa xác định mẫu số `N` và chưa có cơ sở điền số dư bằng 0. [Hồ sơ APP-02E DEV](../../closeout/2026-11-23-app-02e-localdb.md)

## Danh sách bằng chứng cần thu chỉ đọc

Đầu mối ghi theo vai trò, người phụ trách cụ thể chờ xác nhận. Dùng hồ sơ hiện có hoặc bản xuất được phép từ hệ nguồn. Mỗi mục giữ tham chiếu, phạm vi toàn Kho-01 gồm ngoài wave, người cung cấp, thời điểm hoặc watermark và hash nếu có. Chứng từ và bản xuất chi tiết lưu tại kho được duyệt ngoài Git. Freeze, khóa writer, phép thử từ chối có ghi, chốt opening và cutover thuộc phạm vi riêng.

### Writer

| Mã | Kết quả đối chiếu mã nguồn / runbook | Bản xuất hoặc hồ sơ cần thu | Đầu mối cung cấp / đối chiếu | Hiện trạng |
| --- | --- | --- | --- | --- |
| W-01 | Tài liệu nghiệp vụ liệt kê thao tác kho và ngoài wave. | Danh sách chức năng UI cũ, kho/vị trí, tài khoản và log cho nhập/xuất/chuyển, kiểm kê, sửa tồn, QA/quarantine, write-off. | Quản lý Kho-01 và quản trị hệ cũ / IT | PENDING |
| W-02 | DEV có confirm/reserve, quota, load, giao và dỡ hàng. | Cấu hình route, quyền và roster tuyến/xe/manifest, gồm ngoài wave và hoàn trả. | Điều vận và bán hàng / Kho-01, IT ứng dụng | PENDING |
| W-03 | Có route synthetic. | Danh mục gateway, API, CRM/ERP, webhook, file connector, đối tác, principal và log ghi gần đây. | IT tích hợp / chủ hệ đối tác và Security | PENDING |
| W-04 | Có Worker/outbox synthetic LocalDB. | Danh sách import, ETL, scheduler, service accounts, checkpoint/queue, job retry và dead-letter replay. | Vận hành ứng dụng và chủ ETL / IT tích hợp | PENDING |
| W-05 | Có runbook cấp quyền. | Metadata login/grant, stored procedure, script, job SQL, quyền service và quyền khẩn cấp. | DBA / Security và quản trị hệ cũ | PENDING |
| W-06 | PWA cấu hình không ghi nghiệp vụ offline. | Roster mobile/scanner, thiết bị chưa đồng bộ/đã thay, journal sync muộn và số phiếu giao/thu còn mở. | IT thiết bị và tổ giao nhận / Kho-01, thủ quỹ | PENDING |
| W-07 | Có luồng DEV thu tiền, bàn giao quỹ và kế toán loopback. | Danh sách writer tiền/ngân hàng/credit, approval nộp quỹ, tạm ứng, posting/correction, principal và sổ đích. | Thủ quỹ và kế toán / Finance, IT tích hợp | PENDING |
| W-08 | Có mã và runbook phục hồi. | Runbook và danh sách job restore/failover, quyền mở lại, checkpoint queue và journal hiệu ứng ngoài có thể được replay. | DBA và vận hành ứng dụng / Security, chủ hệ đối tác | PENDING |

### Opening

| Mã | Bản xuất hoặc hồ sơ cần thu | Đầu mối cung cấp / đối chiếu | Hiện trạng |
| --- | --- | --- | --- |
| O-RO-01 | Phạm vi, cutoff/asOf dự kiến, snapshot/extract ID, schema/mapping, danh sách location và tập nguồn độc lập để xác định mẫu số. | Tenant Owner và IT dữ liệu / Kho-01, kế toán | PENDING |
| O-RO-02 | Phiếu kiểm kê và sổ tồn/giá vốn theo SKU, vị trí, lô, hạn, UoM, số lượng và cost version. Gồm MAIN, xe, transit, quarantine và ngoài wave. | Quản lý Kho-01 / kế toán tồn kho | PENDING |
| O-RO-03 | A hold, B quota/credit grant, manifest và chuyến mở, phần đã load/đã giao và nguồn đối chiếu Delivery. | Điều vận và bán hàng / Kho-01, Finance | PENDING |
| O-RO-04 | Sổ AR và CustomerCredit tách riêng, terms, receipt/allocation, hạn và hồ sơ pending theo khách, chứng từ và kỳ. | Kế toán công nợ / Finance | PENDING |
| O-RO-05 | Custody, quỹ, cash/bank pending, nộp quỹ và tạm ứng theo người/chuyến, currency, chứng từ đếm và bàn giao. | Thủ quỹ và kế toán / Finance | PENDING |
| O-RO-06 | Invoice/receipt/posting, giấy tờ đến muộn, job và thiết bị dở, watermark và journal theo source ID. | Kế toán và đầu mối provider, IT / Kho-01 | PENDING |
| O-RO-07 | Tập số nguồn độc lập, bản preview đích đã có nếu được phép xem, expected/actual/diff, n/N khi biết N, ngoại lệ và đầu mối xử lý. | Kế toán và IT dữ liệu / Kho-01, thủ quỹ | PENDING |

## 1. Phiếu phạm vi và danh mục writer

| Trường | Giá trị để phê duyệt |
| --- | --- |
| Tenant, seller, Kho-01 và phạm vi kiểm kê | PENDING |
| Hệ thống cũ/mới, đầu mối kho, kế toán, thủ quỹ, IT, Tenant Owner | PENDING |
| Cutover ID, phạm vi mode/wave và ngoài wave, timezone, freeze/cutoff UTC, dự kiến chuyển quyền ghi | PENDING |
| Snapshot/extract ID, schema/mapping, worker/job/device roster, kho lưu bằng chứng ngoài Git | PENDING |
| Approved operator và cửa sổ kiểm kê/chuyển quyền, phép thử, stop condition và Forward Recovery | PENDING |

Bổ sung một dòng cho từng đường ghi thực, đồng thời tách theo hệ thống, job/endpoint, principal hoặc thiết bị khi cơ chế khóa khác nhau. Đối chiếu với cấu hình triển khai, tài khoản DB, scheduler, luồng file/queue, danh sách thiết bị và witness của kho/IT; log gần đây dùng để tìm thêm đường ghi đang hoạt động hoặc ghi muộn. Giữ các đường không dùng thường xuyên trong danh mục nếu quyền ghi còn hiệu lực.

Dùng tám nhóm W-01–W-08 ở danh sách thu thập để kiểm kê chi tiết.

**Dòng chi tiết cho mỗi writer** sao chép từ mẫu sau. Trường “Dấu vết” dùng ID/hash, timestamp, authority hiện hành và hình ảnh SQL trước/sau theo phạm vi đã duyệt.

| Writer ID / nhóm | Hệ thống, route/job, principal/thiết bị | Tenant / location / lô / mode và thuộc wave hay ngoài wave | Loại hiệu ứng, state/nguồn đang dở, watermark hoặc hàng chờ | Người phụ trách, cơ chế khóa, quyền thay thế và thời điểm | Dấu vết trước/sau, phép thử từ chối, peer/fingerprint và actual | Trạng thái/case/next action |
| --- | --- | --- | --- | --- | --- | --- |
| PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING |

## 2. Mẫu opening và đối soát

Mỗi dòng cần source system + source/line ID, loại chứng từ, tenant/location và mốc `asOf`. Gắn source-of-truth, snapshot ID/version, nơi lưu bằng chứng, hash, số lượng chuẩn/đơn vị hoặc currency, expected từ nguồn đã chốt, actual ở preview đích, diff = actual − expected, reviewer và case nếu chưa khớp. Dữ liệu chưa biết để `PENDING`; số 0 cần chứng cứ riêng. Bảng mẫu để trống cho số liệu thật.

| Nhóm opening | Khóa và số liệu cần thu | Expected | Actual / diff | Người kiểm và bằng chứng |
| --- | --- | --- | --- | --- |
| Tồn vật lý theo vị trí | MAIN, xe/TRIP, transit, quarantine; SKU/lô/hạn, UoM và hệ số snapshot, base qty, stock value và cost version; đối chiếu kiểm kê thực. | PENDING | PENDING | Kho / kế toán |
| Quyền hàng và chuyến mở | A hold, B quota, quyền khác theo location/lot/trip và phần đã load, đang vận chuyển hoặc đã giao; manifest, lệnh dở, credit grant còn lại và writer hiện hành. | PENDING | PENDING | Kho / kinh doanh / Finance |
| AR và điều khoản | Customer, Delivery/invoice, receipt/allocation, AR gốc/còn phải thu, hạn, điều khoản đã chốt và kỳ ghi nhận; ngoại lệ pending. | PENDING | PENDING | Kế toán |
| CustomerCredit | Nguồn tạo, giữ/áp/hoàn và số khả dụng theo Customer; tách khỏi AR và credit grant B. | PENDING | PENDING | Kế toán |
| Tiền và nộp quỹ | Custody theo người/chuyến, quỹ theo nơi giữ, cash đã xác nhận, bank pending, handover offered/accepted và chứng cứ đếm; tổng theo currency. | PENDING | PENDING | Thủ quỹ / kế toán |
| Tạm ứng | Employee, nguồn issued, approved expense, return offered/accepted và outstanding riêng từng hồ sơ. | PENDING | PENDING | Finance / thủ quỹ |
| Hóa đơn và kế toán ngoài | Delivery, identity provider, invoice/receipt/posting ID, trạng thái/cutoff, journal chống lặp, UNKNOWN/pending và mapping kỳ. | PENDING | PENDING | Kế toán / đầu mối provider |
| Phiếu giấy, job và thiết bị | Giao dịch đã xảy ra, chưa sync, đợi ACK hoặc đang ghi; nguồn, event ID, observed/received time, owner, quyền của thiết bị và kế hoạch xử lý muộn. | PENDING | PENDING | IT / kho / kế toán |

**Tổng và quy tắc chốt.** Cân số lượng và giá trị theo SKU × location × lô/UoM; chuyển kho→xe bù nội bộ và quarantine đứng ngoài available. Đối chiếu A hold/B quota theo các bucket không chồng nhau. Tách AR, CustomerCredit, custody/quỹ và advance, so với nguồn và kỳ đã chốt. Báo số nguồn đã khớp `n/N` khi biết `N`; pending/unknown có case, owner và hạn xử lý. [Các phép đối soát](../../../business/calculations.md)

## 3. Trình tự kiểm tra và chữ ký

1. Kiểm kê writer, xác định cutoff và freeze theo quyết định được duyệt. Ghim chứng cứ source, hàng chờ, checkpoint và fingerprint trước thay đổi. Khóa từng đường cũ, kiểm tra từ chối cả ngoài wave và so protected rows trước/sau. Một writer cũ còn tự ghi hiệu ứng ⇒ `G-CUT FAIL / NO_GO`.
2. Preview opening theo nguồn snapshot. Chốt bộ diff và case, ghi rõ phần thiếu scope. Kho, kế toán, thủ quỹ, IT và Owner chấp nhận opening theo AP-08 trước khi mở writer mới. Kiểm tra lại quyền và watermark ngay tại cutover. Trong môi trường thử đã duyệt, thử abort hoặc rollback sau freeze một phần hoặc preview và trước hiệu ứng thật đầu tiên. Ghi fingerprint và watermark trước/sau, phục hồi quyền ghi theo runbook, kiểm các đường đang chờ và chứng minh writer cũ/mới không bao giờ đồng thời có quyền ghi.
3. Chứng từ muộn đối chiếu original source/ID, cutoff và journal trước khi chấp nhận. Trường hợp không giải được vào case. Sau hiệu ứng thật đầu tiên, Forward Recovery dùng adjustment/reversal có nguồn và giữ lịch sử các ngoại hiệu ứng.
4. Phiếu ký để điền cho từng vai gồm ID người ký/thẩm quyền, timestamp, cutover ID, covered scope, opening snapshot/mapping/receipt hashes, writer register version, ngoại lệ P1 hoặc case nếu có theo thẩm quyền và quyết định Go/No-Go. Các P0 giữ điều kiện bắt buộc. [AP-08 và AP-10](../../../business/roles-and-approvals.md)

**Điểm duyệt:** phê duyệt mẫu, đầu mối và phạm vi thu thập trước khi lên lịch. Freeze, khóa writer, ký opening và mở ghi thật cần quyết định cutover riêng. G-STOCK/G-CUT `NOT_VERIFIED`; PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`.
