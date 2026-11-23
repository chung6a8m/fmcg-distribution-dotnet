# Seller, provider hóa đơn và kế toán — Yêu cầu bằng chứng EV-10/11

**v0.1 · 23/11/2026 (Asia/Bangkok) · Chờ Owner duyệt hồ sơ · EV-10/11 `EVIDENCE_PENDING`**

Áp dụng từng tenant/seller, mode, build và môi trường thực thi đã phê duyệt. D.4 loopback cung cấp cấu trúc dữ liệu và kịch bản đối chiếu. Bằng chứng ở đây cần bên có thẩm quyền cung cấp từ hệ đích. `G-INV` và `G-LEDGER` giữ `NOT_VERIFIED`; `B_NO_CHANNEL` giữ `BLOCKED`. [SOP-10](../../../business/workflows/assurance-and-exceptions.md#sop-10--hóa-đơn-và-ghi-sổ-kế-toán-bên-ngoài) · [AC-13/14/15/19](../../../business/acceptance.md) · [Truy vết APP-02E](../app-02e-ac-ev-p0-traceability.md)

## 1. Gói hồ sơ và trách nhiệm

| Gói | Bằng chứng để duyệt phạm vi trước lượt thử | Người cung cấp và xác nhận | Hiện trạng |
| --- | --- | --- | --- |
| Seller và thẩm quyền | Liên kết tenant → pháp nhân seller, mã hồ sơ, người chịu trách nhiệm, phạm vi mặt hàng, mode A/B_ONLINE/Mixed, chứng từ và quy tắc áp dụng, hiệu lực từ/đến. Legal/thuế ghi văn bản và phiên bản áp dụng, xác nhận thời điểm nghĩa vụ theo Delivery/chuyển quyền, điều kiện phát hành, điều chỉnh và hủy. Ghi ID duyệt policy; dùng tham chiếu đã che dữ liệu nhạy cảm. | Tenant Owner, Finance/Legal | PENDING |
| Kênh và quyền provider | Provider và profile/phiên bản API, tenant/seller liên kết, môi trường đích, phạm vi quyền và vai giữ credential/chứng thư, hiệu lực và thủ tục thu hồi. Xác nhận semantics tạo/ký/gửi/giao và truy vấn cùng định danh sau timeout, cùng cơ chế chống phát hành lặp. Chốt endpoint, hợp đồng log, cửa sổ và loại chứng từ được phép thử. | Đầu mối provider, IT/Security, Finance/Legal | PENDING |
| Hệ kế toán và mapping | Adapter/API/file/manual thực tế, kỳ và timezone, quy tắc cutoff/asOf, mapping phiên bản nguồn → external document/line và sổ đích, các trường lượng, tiền, AR, giá vốn, phương án correction và kỳ khóa. Xác định mẫu số nguồn kỳ vọng độc lập, owner phía nhận, cách truy vấn receipt/posting và báo cáo đối soát. | Accounting Owner phía gửi và phía nhận | PENDING |
| Custody và lượt thử | Exact source/build/digest, môi trường đích và chứng thực cấu hình, mode, tenant/seller, TestRunId, số liệu kỳ vọng, actor/operator/observer, điểm dừng, phạm vi credential, loại outbound được phép, hạn mức nếu có, dữ liệu và nơi giữ artifact. Owner phê duyệt lần chạy theo từng bên. | Product Owner, Tenant Owner, operator, người chứng kiến | PENDING |

`B-CASE-001..003` và `LEGAL-MEMO` giữ `EVIDENCE_PENDING`. `INV-09` giữ `NOT_RUN`. Gắn mỗi ID với tài liệu gốc, quy định/kịch bản phiên bản đã duyệt, người chịu trách nhiệm và chứng cứ thật. Các ID bên dưới chỉ dùng để tham chiếu checklist của lần duyệt này. [Sổ trạng thái nguồn](../../../business/acceptance.md#3-evidence-cần-giữ-độc-lập)

## 2. Ma trận yêu cầu chứng cứ

| Mục | Phạm vi quan sát và điều kiện đối chiếu | Artefact từ hệ đích | Trạng thái |
| --- | --- | --- | --- |
| HĐ-01 · nguồn Delivery | Hai đợt giao A có source line riêng. Với B_ONLINE chỉ dùng seller/kênh có hiệu lực theo hồ sơ đã duyệt. Truy từng Delivery → định danh hóa đơn → trạng thái đã xác nhận; loại trừ xuất kho→xe và thu nợ cũ khỏi nguồn phát hành. | SQL nguồn, phiên policy, request hash, provider ID, receipt ký/gửi/giao theo áp dụng, timestamp và link người mua đã che. | NOT_RUN |
| HĐ-02 · UNKNOWN và gửi lại | Ghi thời điểm mất ACK, giữ định danh gốc, tra cứu lại và ghi raw outcome. Nếu đã tồn tại thì gắn chứng từ gốc. Chỉ thử lại cùng identity khi hợp đồng chiều nhận và đối chiếu authoritative cho phép; ambiguous/pending giữ case. Retry, replay và restore được duyệt phải bảo toàn một hiệu ứng. Với AC-13, chạy cùng phạm vi restore EV-09 đã duyệt, thử quyền cũ sau thu hồi và đối chiếu privacy action còn hiệu lực. Giữ writer BLOCKED/QUARANTINED cho tới khi kiểm quyền hiện hành và đối chiếu journal với provider theo identity gốc trước khi mở lại. | Attempt/source key, provider query/response, journal, số chứng từ trước/sau, case và người quyết định. | NOT_RUN |
| HĐ-03 · thiếu kênh | Lệnh mới B thiếu kênh bị chặn. SQL và provider xác nhận 0 Delivery, 0 phát sinh AR/tồn và 0 invoice mới từ lệnh bị chặn. Nếu đã có phát sinh thật trong hồ sơ ngoại lệ, lưu bằng chứng sự việc, case và trách nhiệm xử lý theo thẩm quyền. | Audit denial, ledger fingerprint trước/sau, hồ sơ case và quyết định kiểm soát. | NOT_RUN |
| HĐ-04 · điều chỉnh và kỳ | Khi mode áp dụng, đối chiếu điều chỉnh/hủy/hoàn tiền theo hồ sơ pháp lý và policy đã duyệt, chỉ tiêu net/tax/gross, residual, liên kết nguồn gốc và kỳ. Duyệt quyền và chứng cứ chi thực ở nhánh refund cần giải ngân. | Chứng từ điều chỉnh, receipt của provider, approval, bằng chứng chi và sổ liên quan. | NOT_RUN |
| KT-01 · tiếp nhận và post | Gửi một tập nguồn đã chốt. Xác nhận receipt bền, posting ID theo kỳ và mapping mỗi dòng. Gửi trùng cùng source và thử nội dung xung đột theo scope đã duyệt để xác định mỗi nguồn chỉ sinh một hiệu ứng hợp lệ. | Payload hash, receipt ID/time, posting ID/time, kỳ, mapping, query/receipt từ hệ nhận, số lượng hiệu ứng. | NOT_RUN |
| KT-02 · đối soát và scope | Đối chiếu source/line với chứng từ đích theo [from, cutoff) và asOf đã chốt. Xác minh khoản thiếu dòng, sai kỳ hoặc chưa post được thể hiện đúng. Dùng nguồn kỳ vọng riêng để xác định N và ghi n/N nếu biết N. | Export ledger hoặc SQL chỉ đọc, CSV expected/actual/diff, trạng thái từng dòng, snapshot hash và xác nhận kế toán. | NOT_RUN |
| KT-03 · sai lệch và correction | Quan sát `RECEIVED → POSTED → RECONCILED_DIFF` ở ca lệch. Mở case, dùng adjustment/reversal có nguồn và approval theo phân nhiệm, bảo toàn posting/snapshot gốc. Chạy snapshot mới để xác nhận `RECONCILED_OK` và ghi quyết định đóng case. | Case ID, posting gốc, approval, correction ID, snapshot trước/sau, bằng chứng liên kết và xác nhận phía nhận. | NOT_RUN |

**Mẫu một dòng đối soát** dùng chung theo từng source/line, mở rộng trường thuế khi scope áp dụng.

| Tenant / seller / mode | Source type, source ID, line ID, Delivery ID | Provider ID / external document, line ID | Mapping, kỳ, from/cutoff/asOf, currency | Expected và actual số lượng / tiền / AR / cost | Diff, trạng thái, pending/unknown, case và correction ref | Chứng cứ ID/hash và reviewer |
| --- | --- | --- | --- | --- | --- | --- |
| PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING |

Chụp timestamp nghiệp vụ, receipt, posting và đối soát riêng. Số thiếu hoặc chưa biết giữ `PENDING` / `UNKNOWN`, ghi owner và next action. Bên nhận xác nhận bằng receipt và sổ đích. Kho chứng cứ thật nằm ngoài Git, báo cáo chia sẻ chỉ lưu tham chiếu và hash của bản chứng cứ được phép xem.

**Điểm duyệt:** Owner xác nhận seller/mode, đầu mối Legal/Finance/provider/kế toán, hồ sơ kênh và mapping, môi trường, cửa sổ, kịch bản thử, phạm vi được cấp quyền và cách review trước khi chạy. Phát hành hoặc ghi sổ thật cần thẩm quyền nghiệp vụ riêng. PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`. Pilot và Go-Live `NOT_AUTHORIZED`.
