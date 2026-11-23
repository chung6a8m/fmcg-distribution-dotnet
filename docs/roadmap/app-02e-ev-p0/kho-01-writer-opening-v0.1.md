# Kho-01 — Danh mục writer và mẫu opening

**v0.1 · 23/11/2026 (Asia/Bangkok) · Chờ duyệt biểu mẫu · Cutover và opening thực `NOT_RUN`**

Áp dụng AC-04/16, EV-04/12, G-STOCK/G-CUT cho **toàn Kho-01**, gồm chuyến, điểm giữ hàng và hoạt động ngoài wave. Các dòng dưới là nhóm ứng viên để operator kiểm kê. Tất cả đường ghi thực và số mở đầu còn `PENDING`. [Baseline AC](../../../business/acceptance.md) · [SOP-11](../../../business/workflows/assurance-and-exceptions.md#sop-11--chuyển-đổi-số-dư-khóa-nguồn-cũ-forward-recovery) · [BR-05 và BR-15](../../../business/business-rules.md)

## 1. Phiếu phạm vi và danh mục writer

| Trường | Giá trị để phê duyệt |
| --- | --- |
| Tenant, seller, Kho-01 và phạm vi kiểm kê | PENDING |
| Hệ thống cũ/mới, đầu mối kho, kế toán, thủ quỹ, IT, Tenant Owner | PENDING |
| Cutover ID, phạm vi mode/wave và ngoài wave, timezone, freeze/cutoff UTC, dự kiến chuyển quyền ghi | PENDING |
| Snapshot/extract ID, schema/mapping, worker/job/device roster, kho lưu bằng chứng ngoài Git | PENDING |
| Approved operator và cửa sổ kiểm kê/chuyển quyền, phép thử, stop condition và Forward Recovery | PENDING |

Bổ sung một dòng cho từng đường ghi thực, đồng thời tách theo hệ thống, job/endpoint, principal hoặc thiết bị khi cơ chế khóa khác nhau. Đối chiếu với cấu hình triển khai, tài khoản DB, scheduler, luồng file/queue, danh sách thiết bị và witness của kho/IT; log gần đây dùng để tìm thêm đường ghi đang hoạt động hoặc ghi muộn. Giữ các đường không dùng thường xuyên trong danh mục nếu quyền ghi còn hiệu lực.

| Nhóm cần kiểm kê | Ví dụ đường ghi cần đối chiếu | Xác nhận hiện tại |
| --- | --- | --- |
| W-01 UI cũ và thao tác kho | Nhập/xuất/chuyển, kiểm kê, sửa tồn, write-off, QA/quarantine, kho phụ và hoạt động ngoài wave. | PENDING |
| W-02 Luồng bán, xe và giao | Confirm/reserve, A hold/B quota, manifest/load, delivery, trả xe, hàng trả; mở rộng đến tuyến ngoài Pilot. | PENDING |
| W-03 API và tích hợp bên ngoài | API v1/v2, gateway, CRM/ERP, webhook, file/manual connector, đối tác ghi lại trạng thái. | PENDING |
| W-04 Import và automation | Batch CSV/Excel, ETL, scheduled job, service task, Worker/queue, retry và dead-letter replay. | PENDING |
| W-05 SQL trực tiếp và công cụ quản trị | Tài khoản vận hành, scripts, stored procedure, quyền service, break-glass và correction. | PENDING |
| W-06 Thiết bị và nguồn giấy đến muộn | PWA/cache, mobile/scanner, thiết bị chưa sync/đã thay, phiếu giao/thu tiền và chứng từ legacy còn mở. | PENDING |
| W-07 Luồng tiền và kế toán | Nhận receipt/bank/credit, phân bổ, quỹ/custody, tạm ứng, posting và điều chỉnh có tác động trên sổ liên quan. | PENDING |
| W-08 Restore, failover và replay | Các đường có thể hồi sinh writer cũ, batch hoặc ngoại hiệu ứng sau restore, failover hoặc chạy lại. | PENDING |

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
