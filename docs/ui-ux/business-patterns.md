# Patterns UI/UX gắn với nghiệp vụ

**Trạng thái: OWNER_APPROVED UI/UX v0.1 / DOCS_ONLY.** Hiệu lực `ADOPTED` sau review, CI HEAD cuối và merge PR #79 theo [OWN-UX-01..09](../decisions/owner-uiux-2026-11-20.md). Phạm vi: chuẩn trình bày và tương tác dùng chung cho Sales A, Kho/Điều phối, Field, Finance và Owner/Operations; phê duyệt baseline không cấp quyền triển khai các luồng nghiệp vụ này.

Baseline đã đối chiếu: [`6abafe25d21678579122c11d0c206818fb1cb0fd`](https://github.com/chung6a8m/fmcg-distribution-dotnet/tree/6abafe25d21678579122c11d0c206818fb1cb0fd).
Đây là tài liệu thiết kế; không xác nhận tính năng đã triển khai, kết quả E2E, điều kiện production hoặc quyền ghi của một tenant.

## Cách áp dụng

- **MUST / MUST NOT**: bảo toàn yêu cầu và giới hạn của hợp đồng nghiệp vụ hiện hành được dẫn nguồn; UI không được biểu diễn kết quả trái với hợp đồng.
- **SHOULD — đề xuất UX v0.1**: cách bố trí, từ ngữ hoặc tương tác được đề xuất để thực hiện yêu cầu đó; có thể điều chỉnh theo tác vụ và thiết bị.
- [Business rules](../../business/business-rules.md), [policies](../../business/policies.md) và contract của module quyết định nghĩa nghiệp vụ. Khi có khác biệt, ghi rõ và sửa mapping UI theo nguồn; không sửa business contract để khớp bản vẽ.
- Tên trạng thái tiếng Việt trong tài liệu là nhãn trình bày. Mỗi module phải mapping về trạng thái và quyền có nguồn; không tạo enum chung từ tên màn hình prototype.
- Phê duyệt, phân nhiệm và điều kiện mở nghiệp vụ theo nguồn hiện hành. Tài liệu này không bổ sung một vòng Owner approval hoặc gate triển khai mới.
- Ví dụ nghiệm thu bên dưới kiểm nghĩa của giao diện. Một hình đúng, phép cộng đúng hoặc fixture chạy được chưa chứng minh ứng dụng thực thi đúng.

## Bảng truy vết và ví dụ nghiệm thu trực quan

| ID | Pattern | Nguồn hiện hành | Ví dụ đọc được trên màn hình |
|---|---|---|---|
| UX-B01 | Nguồn, phạm vi, thời điểm, độ đầy đủ | [BR-23](../../business/business-rules.md); [Calculations §8](../../business/calculations.md) | “3 chuyến được chọn · dữ liệu 17:25 · 2/3 báo cáo chuyến”; mở được nguồn còn thiếu. |
| UX-B02 | Giá trị thiếu và số 0 | [POL §2–3](../../business/policies.md); [BR-23](../../business/business-rules.md) | CH-09 “Chưa có dữ liệu”; tổng ghi “Phần đã biết”, không điền 0. |
| UX-B03 | Các lớp trạng thái độc lập | [BR-16/18/19/23](../../business/business-rules.md); [SOP-10](../../business/workflows/assurance-and-exceptions.md) | Sale đã ghi nhận; hóa đơn chưa rõ; kế toán đã tiếp nhận nhưng chưa đối soát. |
| UX-B04 | UNKNOWN và phục hồi cùng identity | [BR-16/17](../../business/business-rules.md); [SOP-09/10](../../business/workflows/assurance-and-exceptions.md) | Mất ACK: hiện lần gửi gốc, “Kiểm tra kết quả”, hậu số dư chưa xác định. |
| UX-B05 | Tác vụ đọc và tác vụ ghi | [BR-11/16](../../business/business-rules.md); [APP-02C.0 §6, C-UI-01](../roadmap/app-02c0-a-online-e2e-contract.md) | Mở hồ sơ không gửi lệnh; preview có “Dự kiến”; kết quả có nguồn đã ghi. |
| UX-B06 | Quyền, assignment và phạm vi | [BR-01/15](../../business/business-rules.md); [Roles/AP](../../business/roles-and-approvals.md) | Driver và Trip Writer tách rõ; đọc tiền và xác nhận nhận tiền là hai quyền. |
| UX-B07 | Validation và snapshot | [BR-02/06/08/10/12](../../business/business-rules.md); [APP-02C.0 §2–4](../roadmap/app-02c0-a-online-e2e-contract.md) | Lượng sai có lý do; terms lịch sử vẫn đúng nguồn; quyền hiện hành được kiểm lại. |
| UX-B08 | Tồn vật lý và quyền dùng hàng | [BR-03/04/06/20](../../business/business-rules.md); [Calculations §2](../../business/calculations.md); [C0-R01/R04](../roadmap/app-02c0-a-online-e2e-contract.md) | Trong đúng SKU/kho: tồn 55, A giữ 15, khả dụng 40; không gọi 55 là có thể bán. |
| UX-B09 | Tiền, công nợ và nguồn thu | [BR-11/12/13/14](../../business/business-rules.md); [Calculations §6–7](../../business/calculations.md) | Handover OFFERED giữ custody; cashier accepted chuyển custody→quỹ, AR không giảm lần nữa. |
| UX-B10 | Phê duyệt và tác động theo loại | [AP-01/05/06/07](../../business/roles-and-approvals.md); [POL §2–3](../../business/policies.md); [APP-02B.6](../roadmap/app-02b6-policy-registry-approvals.md) | Refund đã duyệt/chờ chi; expense đã duyệt làm giảm outstanding tạm ứng. |
| UX-B11 | Field online-first | [AGENTS](../../AGENTS.md); [BR-11/15/22](../../business/business-rules.md); [SOP-09](../../business/workflows/assurance-and-exceptions.md) | Mất mạng trước gửi chưa là giao dịch; visit không đơn có outcome hợp lệ. |
| UX-B12 | Đối soát, kết chuyến và đóng ngày | [BR-19/23](../../business/business-rules.md); [SOP-08](../../business/workflows/operations.md); [Mixed](../../business/workflows/mixed.md) | Xe 0/custody 0 nhưng vẫn “Đang đối soát”; việc còn lại có nguồn, owner, next action. |

Các số trong ví dụ chỉ minh họa cách đọc, không xác lập policy, dữ liệu production hoặc fixture nghiệm thu.

## UX-B01 — Nguồn, scope, asOf và n/N

**MUST** gắn số liệu tổng hợp với tên đại lượng, đơn vị, phạm vi nguồn và `asOf` có nghĩa.
Phạm vi cần đủ để hiểu số: tenant/seller, khách, chuyến, kho/location, SKU/lô, kỳ hoặc bộ lọc liên quan.
`asOf` là mốc dữ liệu, không tự đổi thành giờ thiết bị, giờ mở trang hoặc giờ bấm làm mới.

- **MUST** nêu loại nguồn được đếm; chỉ viết `n/N` khi `N` đã xác định. Không suy `N=0` từ việc chưa có nguồn.
- **MUST** giữ đúng phạm vi khi lọc, phân trang và xuất báo cáo; tổng trang hiện tại không được mang nhãn tổng nghiệp vụ.
- **MUST** phân biệt “2/3 báo cáo chuyến”, “1/2 nguồn hóa đơn đã đối chiếu” và độ đầy đủ kế toán.
- **SHOULD — đề xuất UX v0.1:** đặt phạm vi/thời điểm cạnh tiêu đề hoặc cụm chỉ số; cho mở định nghĩa, nguồn và phần thiếu từ đó.

## UX-B02 — Null, số 0 và dữ liệu cũ

**MUST NOT** thay dữ liệu thiếu, kết quả chưa rõ hoặc policy chưa thiết lập bằng số 0 đã xác nhận.
Số 0 chỉ có nghĩa trong đúng nguồn, phạm vi và mốc đã biết; không mở rộng nó sang phần dữ liệu còn thiếu.

| Nghĩa | Nhãn UX đề xuất | Giới hạn |
|---|---|---|
| Đã biết bằng 0 | `0` kèm đơn vị | Có nguồn/mốc phù hợp. |
| Thiếu dữ liệu | “Chưa có dữ liệu” / “Chờ nguồn” | Không cộng thành 0. |
| Chưa biết kết quả lệnh | “Chưa rõ kết quả” | Dùng UX-B04; không đồng nghĩa bị từ chối. |
| Ngoài phạm vi | “Không áp dụng” | Không dùng thay cho thiếu dữ liệu. |
| Policy UNSET | “Chưa thiết lập” | Khác giá trị 0 được duyệt rõ ràng. |
| Bản chụp cũ | “Dữ liệu đến …; chưa làm mới được” | Giữ `asOf` cũ. |

- **MUST** ghi “tổng phần đã biết” và phần thiếu khi chỉ tổng hợp được một phần; không gọi là tổng đầy đủ.
- **MUST NOT** suy “realtime”, margin, tăng trưởng hoặc toàn bộ nợ quá hạn bằng 0 khi chưa đủ nguồn/định nghĩa.
- **SHOULD — đề xuất UX v0.1:** dùng chữ rõ nghĩa thay dấu gạch đơn độc; lỗi tải có cách thử đọc lại và giữ bản chụp cũ có nhãn nếu còn dùng được.

## UX-B03 — Trạng thái độc lập theo lớp

**MUST** bảo toàn riêng trạng thái đối tượng nghiệp vụ, kết quả command, độ mới/đầy đủ dữ liệu, hóa đơn và kế toán khi các lớp này cùng xuất hiện.
Một lần gửi thành công không tự chứng minh bên nhận đã giữ bền, đã ghi sổ hoặc đã đối soát.

- `RECEIVED/ACK` chưa là `POSTED`; `POSTED` chưa là `RECONCILED_OK`.
- Sale đã ghi nhận có thể cùng tồn tại với invoice `UNKNOWN`; tra hóa đơn gốc, không tạo sale mới.
- Approval ID đã cấp chưa chứng minh quyết định đã duyệt; cần kết quả quyết định có nguồn.
- Operational close và financial reconciliation phải có nhãn riêng; không lấy một badge “Hoàn tất” thay cho mọi lớp.
- **SHOULD — đề xuất UX v0.1:** đặt badge cạnh chính đối tượng nó mô tả; chi tiết giải thích ý nghĩa, thời điểm và tác vụ tiếp theo.

## UX-B04 — Command pending, mất ACK và phục hồi

**MUST** giữ identity, source, payload/version, dependency và kết quả gốc theo contract giao nhận bền.
Cùng identity/cùng nội dung chỉ có một hiệu ứng; cùng identity/khác nội dung là conflict.

| Tình huống | Điều giao diện phải giữ đúng | Tác vụ UX đề xuất |
|---|---|---|
| Chưa gửi | Chưa có căn cứ ghi nhận nghiệp vụ | Sửa nháp; kết nối để gửi nếu được phép. |
| Đã gửi, chưa rõ kết quả | `UNKNOWN`; không suy rejected hoặc rollback | Tra đúng lần gửi gốc. |
| Đã giữ bền, chờ dependency | Đã tiếp nhận, chưa hoàn tất | Xem phụ thuộc/người xử lý. |
| Rejected trước hiệu ứng | Bị từ chối với lý do được phép công bố | Sửa điều kiện theo contract hoặc chuyển xử lý. |
| Đã ghi nhận | Hiển thị chính kết quả lưu và nguồn của nó | Mở chứng từ/kết quả liên quan. |
| Identity conflict | Nội dung khác với lần gốc | Giữ nguồn và xử lý xung đột; không đổi ID để lách. |
| Đã trao hàng/tiền thực, chưa ghi được | Sự thật đã xảy ra và bằng chứng vẫn tồn tại | Recorded event/case theo quyền, owner và next action. |

- **MUST NOT** tạo sale, receipt, handover, invoice hay approval thay thế bằng ID mới để vượt UNKNOWN.
- **MUST** tra và đối chiếu identity gốc trước bước tiếp theo theo contract. Replay được phép vẫn giữ nguyên identity/nội dung, không phát sinh nghiệp vụ thứ hai.
- **MUST** để hậu số dư “chưa xác định” khi kết quả ảnh hưởng nó chưa rõ. Số trước gửi phải ghi “Lần xác nhận trước” với mốc riêng.
- **MUST NOT** lấy kết quả nhánh thành công của prototype làm kết quả hiện tại hoặc tự xóa sự kiện đã xảy ra ngoài đời.
- **MUST NOT** phục hồi quyền đã thu hồi hoặc phát lại hiệu ứng ngoài đã tồn tại sau restore. Thiếu dữ liệu đối chiếu quyền/ngoại hiệu ứng thì giữ BLOCKED/QUARANTINED theo contract.
- **SHOULD — đề xuất UX v0.1:** dùng CTA “Kiểm tra kết quả lần gửi này”; giữ mã tra cứu dễ đọc và lịch sử xử lý qua các lần mở màn.

## UX-B05 — Đọc, xem trước và ghi nhận

**MUST** tách mở trang, đổi tab, xem nguồn, refresh và tra cứu khỏi command làm thay đổi nghiệp vụ.
Navigation hoặc nhận một kết quả đọc không được tự chạy command kế tiếp.

- **MUST** phân biệt dữ kiện đã ghi, khai báo thực tế chưa ghi và số dự kiến sau ghi nhận; không dùng cùng nhãn “Đã …” cho cả ba.
- **MUST** chỉ thông báo hiệu ứng đã thành công khi có kết quả tương ứng có thẩm quyền; HTTP thành công hoặc ACK đơn thuần không đủ.
- **MUST** giữ tính toàn vẹn của B sale/delivery/receipt/allocation/stock/AR/credit/grant; không trình bày một phần thành công là kết quả B đã hoàn tất.
- **MUST NOT** dùng lựa chọn được đánh dấu sẵn làm bằng chứng mới rằng đã đếm/đã nhận tiền/đã giao hàng. Sự xác nhận thực tế phải do đúng người thực hiện theo contract.
- **SHOULD — đề xuất UX v0.1:** ô xác nhận sự thật mới bắt đầu chưa chọn; dữ kiện cũ đã xác nhận hiển thị dạng đọc kèm nguồn, không tái dùng như xác nhận mới.
- **SHOULD — đề xuất UX v0.1:** trước hành động ghi, cho xem đối tượng, nguồn, lượng/tiền, điều kiện và tác động dự kiến; tên nút mô tả đúng sự kiện.

Checkbox chỉ là cách tương tác; không thay bằng chứng đếm độc lập của thủ quỹ, authority hoặc kết quả server.

## UX-B06 — Quyền theo assignment và scope

**MUST** lấy tenant/authority từ nguồn server đã xác thực; không suy quyền từ menu, badge vai trò, phương tiện hoặc người trả phí.
Quyền đọc dữ liệu, tạo đề nghị, ghi nhận, duyệt và thực nhận tiền phải được phân biệt theo phạm vi.

- Driver không mặc nhiên là Trip Writer; một writer/trip và chuyển writer/thiết bị phải tuân thủ đối chiếu nguồn hiện hành.
- Owner không mặc nhiên được duyệt mọi AP, nhận tiền thay Cashier, ký hóa đơn hoặc sửa chứng từ posted.
- Một người kiêm vai vẫn cần assignment đúng từng tác vụ; yêu cầu checker độc lập không mất đi vì chức danh.
- **MUST** kiểm quyền hiện hành ở commit; quyền UI đang hiển thị không thay kiểm server. Search, drilldown và export vẫn giữ cô lập tenant.
- **SHOULD — đề xuất UX v0.1:** cho biết phạm vi đang làm việc; CTA thiếu quyền có lý do phù hợp và bước chuyển người có thẩm quyền, không lộ dữ liệu ngoài quyền.

## UX-B07 — Validation, snapshot và sửa sai có nguồn

**MUST** kiểm quantity/UoM/step/scale, source, stock/quota/credit, giá/terms và các điều kiện của chính tác vụ theo contract.
Kiểm trước khi gửi không thay kiểm tại commit; dữ liệu hoặc quyền có thể thay đổi giữa hai mốc.

- **MUST** giữ commercial snapshot hợp lệ tại Confirm: price/discount/tax/UoM/terms/ShipTo theo scope đã chốt.
- **MUST** phân biệt chứng minh approval lịch sử tại Confirm với quyền thực thi hiện hành. Policy hết hiệu lực/superseded không tự viết lại terms đã chốt; revoke actor/writer hiện hành vẫn phải được kiểm.
- **MUST** tính hạn A theo seller-local business date của từng Delivery và N ngày lịch trong terms snapshot; không lấy ngày thiết bị hoặc N×24 giờ.
- **MUST NOT** sửa posted/locked history hoặc tái tính từ master mới. Amendment/correction/reversal phải có nguồn, phiên bản và phạm vi được phép.
- **SHOULD — đề xuất UX v0.1:** lỗi gần field liên quan, giữ dữ liệu nhập có thể sửa, nêu điều kiện thiếu và bước xử lý; không đổi lỗi nghiệp vụ thành thông báo mất mạng chung.
- **SHOULD — đề xuất UX v0.1:** khi sửa lượng, tiền hoặc nguồn ảnh hưởng preflight, bỏ nhãn kiểm thành công cũ và yêu cầu kiểm lại.

Required fields của ShipTo, amendment và policy-selection cụ thể vẫn theo C0-R02/R03; bản mẫu không tự chốt chúng.

## UX-B08 — Tồn kho, allocation và thực giao

**MUST** phân biệt tồn vật lý, AReservation, BQuota, phần khả dụng và các bucket hold/quarantine/transit liên quan.
Phép cộng/trừ chỉ dùng các bucket không chồng nhau trong đúng SKU/UoM/location/lot; không trừ quota lần hai sau khi lượng đã lên xe.

- **MUST** giữ pool A/B không chồng nhau; B không dùng hàng A đang giữ.
- **MUST** tách quantity đã đặt/giữ/cấp/thực giao/còn giữ/trả xe và nguồn tương ứng khi cần quyết định.
- **MUST NOT** ghi AR/doanh thu/COGS chỉ vì Confirm hoặc load/unload; hiệu ứng theo sự kiện thực giao hợp lệ.
- **MUST** phân biệt hàng dư chưa giao/bán trả xe với hàng khách trả. Request/HELD chưa giảm AR hoặc tăng available; nhận khách trả vào quarantine, QA release riêng.
- **MUST NOT** tự release A remainder, B quota/grant hoặc đóng nguồn chỉ vì xe đã về 0; theo reconciliation và nguồn cho phép.
- **SHOULD — đề xuất UX v0.1:** đặt tồn và quyền dùng hàng cạnh nhau; mở được nguồn chiếm quyền và chứng từ thay đổi lượng.

**Ranh giới baseline:** C0-R01 và C0-R04 vẫn mở cho hành vi phụ thuộc của C.2–C.4 theo [phân công ngày 22/11](../roadmap/app-02c0-a-online-e2e-contract.md#8-contract-review-dispositions-and-blocking-gates); chúng không chặn công việc C.1 độc lập. Tài liệu UI này không tự chốt enum/state machine cho Confirm/Reserve/Load; không suy partial/repeated load đã hỗ trợ từ prototype.
Contract A mục tiêu tách Confirm và Reserve; hành vi lịch sử APP-01 có thứ tự khác. UI phải nêu phiên bản/mapping đang áp dụng và không cho load khi thiếu reservation hợp lệ.

## UX-B09 — AR, custody, quỹ, advance và CustomerCredit

**MUST** giữ riêng ba sổ AR Customer, cash custody và employee advance; quỹ và CustomerCredit có nguồn riêng.
Không cấn trừ chúng để tạo một tổng “tiền về” không rõ nghĩa.

- Cash confirmed tăng custody theo toàn bộ tiền mặt thực nhận; bank pending chưa confirmed, bank verified không tăng custody tiền mặt.
- Xác minh bank và allocation là hai sự kiện; chưa có allocation hợp lệ thì AR chưa giảm. CustomerCredit dư có source, không là AR âm.
- Handover OFFERED chưa đổi custody/quỹ; Cashier thực nhận mới chuyển trách nhiệm theo số accepted, không thu khách hoặc giảm AR lần thứ hai.
- B chỉ giảm exposure bằng receipt verified áp cho sale và CustomerCredit đã reserve riêng áp cho sale; thu nợ cũ không tự nạp lại grant B.
- **MUST** giữ giới hạn tiền nguồn, open AR, credit và grant; không phân bổ/hoàn vượt nguồn hoặc đếm held grant + AR thành hai lần dùng hạn mức.
- **SHOULD — đề xuất UX v0.1:** tách “Thực nhận”, “Áp lần này”, “Tiền dư”, “Nợ phát sinh”, “Grant sử dụng”; preview nói rõ sổ nào đổi.

[APP-01D](../roadmap/app-01d-cashier-unload.md) có exact-count và một acceptance/offer cho customer-cash handover: mismatch giữ chứng cứ/case, không tự nhận một phần hoặc sửa offer cho khớp.
Không mở rộng quy tắc này sang mọi loại tiền; advance return có nghiệp vụ nhận từng phần theo nguồn.

## UX-B10 — Policy và phê duyệt theo tác động

**MUST** truy được approval ID, source/case, maker, checker, scope, policy version, quyết định, lý do, evidence và hiệu lực.
Thiếu checker độc lập khi bắt buộc thì giữ PENDING/CASE; `UNSET` không là `APPROVED=0` và không tự cấp quyền bán chịu.

- **MUST** giữ L1 trong biên L0; L2 có case và không vô hiệu L0. Cấu hình không tự tạo authority hay nghiệp vụ.
- **MUST** mô tả tác động đúng từng AP; không áp quy tắc “mọi approval đều chưa đổi sổ”.
- AP-05: duyệt refund chưa giảm CustomerCredit; actual disbursement có nguồn mới tạo hiệu ứng hoàn.
- AP-06: Cashier thực nhận, không self-accept; đề nghị nộp chưa là đã nhận quỹ.
- **AP-07: approved expense làm giảm advance outstanding**; expense request chưa giảm. Approval chi phí không tự tạo thêm lần chi quỹ sau nguồn issued đã có.
- Return Request, commercial acceptance, physical receive và QA release phải có nghĩa và điều kiện riêng theo nguồn.
- **SHOULD — đề xuất UX v0.1:** chi tiết duyệt đặt nguồn, phạm vi, maker/checker và “Tác động của quyết định này” trước CTA; policy thiếu nêu bước cần bổ sung.

Reservation/expiry/release của refund và cạnh tranh với allocation chưa được baseline chốt; không coi “approve tự reserve credit” là quy tắc chung.

## UX-B11 — Mobile field online-first

**MUST** tuân thủ online-first hiện hành: không business write khi offline. Khả năng lưu nháp, xem cache hoặc chụp bằng chứng phải được mô tả đúng năng lực sản phẩm, không gắn nhãn giao dịch đã ghi.
Quyền offline có giới hạn được mô tả trong business source không tự cấp quyền offline cho ứng dụng; B_NO_CHANNEL vẫn blocked.

- **MUST** giữ Trip/Writer/Customer chịu nợ/ShipTo/nguồn hàng đúng tác vụ; giảm mật độ mobile không được bỏ mất các nghĩa này.
- **MUST** kiểm điều kiện B trước giao thật, xác nhận tiền theo thực nhận và ghi B toàn vẹn; sự kiện đã xảy ra nhưng chưa ghi được theo UX-B04.
- **MUST** giữ A giao một phần và phần còn lại; COD yêu cầu tiền verified theo policy, không tự chuyển thành bán chịu.
- **MUST** cho visit kết thúc không đơn với outcome/reason có nguồn; không tạo đơn 0 đồng, sale, AR hoặc movement giả.
- **SHOULD — đề xuất UX v0.1:** mỗi bước tập trung một việc, lượng và tác động tiền/nợ đọc được trước CTA; trạng thái gửi và cách phục hồi ở nơi dễ thấy.

GPS, ảnh, chữ ký, barcode/check-in bắt buộc, kích thước 430×932 và số tab của prototype là lựa chọn thiết kế, chưa là gate nghiệp vụ chung.

## UX-B12 — Đối soát, kết chuyến và đóng ngày

**MUST** phân biệt Trip, xe, người, quỹ và ngày kế toán; scope của một chuyến không tự thành scope cả ngày hoặc NPP.
Xe bằng 0, custody bằng 0 hoặc một tổng tiền khớp chưa đủ kết luận CLOSED hay RECONCILED_OK.

- **MUST** giữ việc đối A/B, hàng trả/quarantine, custody, advance, AR, invoice, accounting và nguồn chưa sync theo contract đóng tương ứng.
- **MUST** phân biệt đề nghị đóng với quyết định chấp nhận đóng theo authority.
- **MUST** thể hiện `asOf`, mức đủ/thiếu nguồn, case owner, hạn/next action khi CLOSED_WITH_OPEN_CASES được phép; trạng thái này không tự vượt P0/nghĩa vụ pháp lý.
- **MUST NOT** đổi cảnh báo thiếu báo cáo thành case đã đăng ký hoặc tăng case count khi chưa có nguồn case.
- **SHOULD — đề xuất UX v0.1:** dùng ma trận từng phần việc với trạng thái, nguồn và người xử lý; mở từ dòng chưa đạt tới hành động có thẩm quyền.

## Các quyết định giữ mở và giới hạn của prototype

| Chủ đề | Cách áp dụng v0.1 |
|---|---|
| C0-R01 / C0-R04 | Theo [APP-02C.0](../roadmap/app-02c0-a-online-e2e-contract.md): mapping Confirm/Reserve, counter và partial/repeated load chưa được tài liệu này chốt. |
| C0-R02 / C0-R03 | ShipTo/amendment, seller timezone, policy precedence và snapshot/current authority theo contract; không đặt field/default từ bản mẫu. |
| Aging, KPI, stale/SLA, margin | Giữ thiếu định nghĩa/nguồn rõ ràng; không lấy tuổi snapshot hoặc số demo làm threshold. |
| Allocation, refund reservation | Không tự áp FIFO/oldest-first, reserve/expiry/release chưa có policy. |
| Fixture và bố cục | Tên người, ID, số tiền, terms 7 ngày, quỹ mở, thứ tự màn, pixel và checkbox mẫu không tạo policy hoặc trạng thái ứng dụng. |

Khi thiết kế một màn mới, chọn các pattern liên quan, dẫn contract hiện hành và mô tả trạng thái cần kiểm trực quan.
Tối thiểu xét trạng thái có số liệu/thiếu nguồn, quyền phù hợp/thiếu quyền và kết quả ghi thành công/bị từ chối/UNKNOWN nếu màn có command; không dựng biến thể không liên quan chỉ để đủ số lượng.
