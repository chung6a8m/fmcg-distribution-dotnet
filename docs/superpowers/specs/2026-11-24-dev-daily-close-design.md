# Thiết kế đề xuất — Hoàn tất ca DEV và tiếp tục việc tồn ngày sau

Ngày: 24/11/2026. Baseline khảo sát: main 29bc4bc2b4a14ad45386f57a8f6bb25a697005ee, sau PR #143.

Trạng thái: hai lựa chọn về mục tiêu và bàn giao đã được Owner xác nhận trong hội thoại. Owner tiếp tục yêu cầu “Tạo roadmap theo thiết kế và đề xuất trên, commit vào repo”; [APP-05](../../roadmap/app-05.md) là roadmap chính thức quản lý thứ tự, phụ thuộc và trạng thái, `ROADMAP_APPROVED / IMPLEMENTATION_NOT_STARTED`. Các chi tiết thiết kế dưới đây là cơ sở triển khai, không phải bằng chứng implementation hoặc nghiệm thu và không được ghi thành các quyết định kinh doanh Owner đã trả lời riêng. Phạm vi synthetic DEV trên laptop Windows, dữ liệu mẫu APP-03, SQL Server LocalDB theo tenant.

Rà soát khả năng lập kế hoạch: đã được một reviewer độc lập đọc và đối chiếu các ràng buộc nguồn. Các vấn đề về cutoff D.4, chuyến chưa nạp và fixture 40/25/15 đã được làm rõ trong bản này; vòng rà soát tiếp theo không còn câu hỏi quan trọng cần trả lời trước khi lập kế hoạch. Đây là review tài liệu, không phải kiểm chứng runtime mới.

## 1. Mục tiêu và quyết định đã có

Đầu ra mong muốn: hoàn tất một ca qua các vai trên cùng dữ liệu; biết chuyến nào đóng được, việc gì còn tồn và ai chịu trách nhiệm; mở ứng dụng hôm sau để xử lý tiếp đúng chứng từ, rồi xem lịch sử của cả hai ngày.

### Lựa chọn trực tiếp của Owner

1. Ưu tiên “Hoàn tất một ca trên dữ liệu mẫu: xử lý phần chưa giao, đóng chuyến và tiếp tục việc tồn hôm sau”. Nhánh tự chuẩn bị NPP mẫu bằng danh mục/nhập kho/giá/phân công không phải ưu tiên của đợt này.
2. Với A chưa giao, “Có người phụ trách và hạn xử lý; chọn chuyến giao tiếp vào ngày sau”. Trước khi chốt chuyến cũ chưa bắt buộc có TripId kế nhiệm. Hồ sơ phải có hành động tiếp theo và vẫn giữ trách nhiệm tới khi công việc thực sự hoàn tất.

### Các phương án đã cân nhắc

| Quyết định | Phương án được chọn | Phương án còn lại và đánh đổi |
| --- | --- | --- |
| Mốc tiếp theo | Hoàn tất ca trên dữ liệu mẫu đã có | Tự chuẩn bị toàn bộ đầu vào giúp chủ động dữ liệu nhưng mở thêm luồng master/nhập kho trước khi khép chu kỳ chuyến |
| Thời điểm xếp chuyến giao tiếp | Giao người phụ trách và hạn trước; chọn chuyến hôm sau | Bắt buộc TripId kế nhiệm trước chốt cho đích bàn giao ngay, nhưng giữ chuyến cũ mở khi lịch ngày sau chưa sẵn sàng |

### Quy tắc kế thừa

- OWN-APP02C-06: phần A chưa giao tiếp tục HELD nếu đã giữ; giải phóng hoặc đóng phần còn lại cần nguồn riêng được duyệt. Giá/terms/thuế/ShipTo đã chốt và lịch sử giao phải giữ nguyên.
- SOP Mixed: Tenant Ops chấp nhận đóng; CLOSED khi sạch, CLOSED_WITH_OPEN_CASES khi còn việc được phép chuyển tiếp, có asOf, độ đầy đủ nguồn, người phụ trách và hành động kế tiếp.
- D.3: trả tồn xe có nguồn; A về MAIN vẫn giữ quyền A; B quota được giải phóng theo nguồn; custody và đề nghị nhận tiền phải được giải quyết trước unload. Chuyến hiện dừng ở RECONCILING.
- Các sổ AR, custody/quỹ, customer credit và tạm ứng độc lập. Chọn chuyến mới hoặc chốt chuyến không sinh doanh thu, AR, COGS, thu/chi hoặc giao hàng giả.
- Offline business writes tắt; runtime và quyền theo tenant/actor/assignment vẫn được kiểm ở server. Operations debt giữ mốc riêng theo quyết định DEV-first.

Nguồn: [quyết định C](../../decisions/owner-app02c-2026-11-19.md), [SOP A](../../../business/workflows/module-a.md), [SOP Mixed](../../../business/workflows/mixed.md), [ngoại lệ kết ca](../../../business/workflows/assurance-and-exceptions.md), [APP-03.6](../../roadmap/app-03-6-settlement.md), [operations debt](../../roadmap/app-02c-operations-debt.md).

## 2. Hành trình bàn giao

### Ca chính: giao 25/40, xử lý 15 còn lại vào ngày sau

1. Sales tạo và Confirm đơn 40 đơn vị trong phiên DEV. Kho giữ/nạp, Hiện trường giao thực 25. Mọi nguồn phát sinh bằng command hiện có, giá và terms theo snapshot đã chốt.
2. Thủ quỹ nhận tiền đủ điều kiện. Kho nhận lại 15 còn trên xe. Sổ và dấu vết lô được đối chiếu; 15 vẫn được giữ cho đơn A. Trường hợp không phát sinh tiền thu vẫn phải có đối chiếu cho thấy custody hợp lệ bằng 0.
3. Màn đối soát hiển thị phần đã hoàn tất, phần còn lại và điều kiện chặn. Người có quyền tạo công việc giao tiếp cho đúng dòng đơn, lượng còn lại, người phụ trách và hạn xử lý. Chưa cần chọn chuyến mới.
4. Tenant Ops chốt chuyến cũ thành CLOSED_WITH_OPEN_CASES nếu các điều kiện khác đã đạt. Lưu biên bản và danh sách việc tồn tại thời điểm chốt; giữ định danh lệnh để tra cứu khi mất ACK.
5. Ngày sau, người phụ trách mở công việc, chọn chuyến đủ điều kiện và số lượng HELD cần chuyển tiếp. Command chuyển tiếp giữ một đơn gốc, một lần Confirm và liên kết tới quyền giữ/nguồn cũ. Không cần mở lại chuyến cũ.
6. Kho nạp trên chuyến mới, Hiện trường giao tiếp. AR và due date mới chỉ phát sinh theo giao thực và terms gốc. Kế toán xem cùng đơn với các lần giao thuộc từng chuyến.
7. Khi phần giao tiếp đã hoàn tất hoặc phần còn lại đã được đóng bằng nguồn duyệt hợp lệ, công việc hoàn thành theo nguồn. Chỉ chọn chuyến chưa được coi là đã giao xong. Công việc kế toán của nguồn ngày một hoàn tất độc lập sau báo cáo đối soát có cutoff bao phủ nguồn đó.
8. Biên bản ngày một giữ đúng danh sách công việc mở lúc chốt; màn theo dõi hiện tại cho biết việc nào đã hoàn tất. Với fixture chính bên dưới, kỳ vọng hai việc mở lúc chốt: A15 chờ giao tiếp và nguồn ngày một chờ đối soát D.4. Không viết lại lịch sử chốt.

Một ca riêng kiểm CLOSED sạch, không tạo công việc giả để ép mọi chuyến vào CLOSED_WITH_OPEN_CASES. Một ca riêng kiểm từ chối đóng và giữ trạng thái RECONCILING khi chưa đủ điều kiện.

### Fixture và đồng hồ có thể thực thi

Đây là các giá trị đề xuất phục vụ kiểm chứng synthetic, không phê duyệt chính sách tín dụng/giá thật. Bootstrap vNext bổ sung SKU có lô DAY-A-EA, Customer DEV riêng, kho hiện có và opening có nguồn 60 EA trị giá 180.000 VND cho từng tenant. Giá fixture 10.000 VND/EA, discount và tax 0, terms 7 ngày, hạn mức giả lập 500.000 VND do actor độc lập duyệt; lô còn hạn trong toàn lịch 23–30/11/2026 và không thuộc chương trình quà. Opening/master/policy được seed theo phiên bản, có định danh ổn định và kiểm chạy lại; không sửa opening, SKU, hạn mức hoặc nguồn đã có của phiên cũ. Mỗi biến động sau opening đi qua command nghiệp vụ. Tenant đối chứng giữ nguyên dữ liệu trừ pha được nêu riêng.

Phần A của ca chính: Confirm40/nạp40/giao25/trả15 vào 23/11. MAIN kết thúc 35 EA/105.000 VND, A HELD15, tồn xe của phần A bằng 0, AR_A 250.000 VND đến hạn 30/11, COGS_A 75.000 VND. Chọn chuyến ngày 24/11 không đổi các số dư đó. Nạp/giao tiếp15 để lại MAIN20 EA/60.000 VND, HELD0, thêm AR_A 150.000 VND đến hạn 01/12, tổng COGS_A 120.000 VND. Các số này là expected của SKU/dòng A riêng, không cộng lẫn các nguồn B/F khác của phiên.

APP-03 giữ clock 09:00 ICT; D.4 dùng OccurredAt < cutoff và cutoff ≤ asOf ≤ now. Thiết kế v1 giữ đồng hồ này. Nguồn ngày một có thể đã RECEIVED/POSTED nhưng chưa có báo cáo cùng ngày bao phủ chính thời điểm phát sinh. Vì vậy chốt ngày một tạo việc theo dõi đối soát có nguồn, người phụ trách và hạn, không giả RECONCILED_OK hoặc giả một hồ sơ chênh lệch khi chưa chạy báo cáo. Sau NextDay, báo cáo với cửa sổ/cutoff hợp lệ bao phủ nguồn ngày một hoàn tất việc đó. Nguồn giao mới ngày hai vẫn có vòng đối soát riêng; không gán kết quả báo cáo ngày một cho nguồn mới.

Ca CLOSED sạch dùng chuyến đã hoàn tất toàn bộ nghĩa vụ và đã có báo cáo bao phủ nguồn, có thể chốt sau NextDay. Thời điểm chốt được ghi đúng lúc thực hiện, không ghi lùi về ngày phát sinh. Ca Mixed dùng thêm nguồn B hiện có để kiểm quyền/sổ độc lập; expected của từng dòng/SKU/đơn vị phải được công bố riêng.

## 3. Điều kiện đóng chuyến và phạm vi công việc tồn

Các chi tiết trong bảng là đề xuất v1 dựa trên baseline; không phải các lựa chọn mới đã được Owner xác nhận riêng.

| Tình trạng tại lúc chốt | Xử lý đề xuất v1 |
| --- | --- |
| Thiếu quyền, sai tenant/assignment, nguồn xung đột hoặc không xác định được phạm vi tồn vật lý | Chặn đóng. Không chuyển dữ liệu không xác định thành số 0 hoặc một case chung để vượt điều kiện |
| Chưa trả xong hàng thuộc chuyến, custody chưa tất toán, CashHandover còn chờ, bằng chứng C.4 đã tiếp nhận chưa có kết quả | Chặn theo điều kiện hiện hành và đối chiếu lại tại commit; giữ RECONCILING hoặc trạng thái trước đó theo command thực tế |
| Có đơn A cần xử lý nhưng chưa nạp chuyến lần nào | Bổ sung bàn giao có nguồn chứng minh lượng trên xe của chuyến bằng 0; kiểm mọi nguồn/tiền/evidence và quyền, không tạo manifest hay movement giả. Chuyển RECONCILING để chốt với việc còn lại. Áp dụng cả đơn còn HELD và đơn Confirm chưa giữ hàng |
| Chuyến do bootstrap chuẩn bị nhưng chưa có hoạt động/nghĩa vụ nghiệp vụ | Hiển thị chưa sử dụng; không tự chốt, không tạo case rỗng và không đưa vào mẫu số chuyến đã phát sinh cần kết ca. Quy tắc xác định phạm vi phải công khai và có kiểm chứng |
| A còn phải giao | Cho chuyển tiếp với dòng nguồn, lượng còn lại, lượng thực HELD, người phụ trách, hạn và next action; chưa cần chuyến đích |
| AR đã ghi đúng, chưa đến hạn | Hiển thị số dư và lịch đến hạn; tự nó không phải chênh lệch hoặc công việc ngoại lệ buộc tất toán trước khi đóng chuyến |
| D.4 kế toán chưa khớp hoặc đang chờ xử lý | Chỉ cho chuyển tiếp khi xác định nguồn và có hồ sơ/việc theo dõi, người phụ trách, hạn, next action; trạng thái RECEIVED/POSTED/RECONCILED vẫn giữ nghĩa riêng |
| Hồ sơ trả hàng/QA/refund liên quan chuyến còn mở | Liên kết hồ sơ F hiện có để theo dõi; không tạo bản sao của quy trình và không tự biến hàng cách ly thành available hoặc đề nghị hoàn thành chi thực |
| Tạm ứng/credit đang tồn | Chỉ gắn vào chuyến nếu có liên kết nguồn hợp lệ. Sổ nhân viên hoặc sổ khách không được suy thành nghĩa vụ riêng của chuyến từ việc trùng actor/customer. Số dư bình thường không tự tạo case |
| Không còn việc phải chuyển tiếp, mọi điều kiện đạt | CLOSED |
| Còn việc được phép chuyển tiếp, mọi việc có người chịu trách nhiệm và hạn, các điều kiện bắt buộc đã đạt | CLOSED_WITH_OPEN_CASES |

V1 xử lý đối tác DEV mô phỏng hiện có. Các nghĩa vụ hóa đơn thật, quyền production, ngưỡng tiền và ngoại lệ vận hành thật không được suy ra từ phép chốt synthetic này.

### Thông tin tối thiểu của công việc tồn

- Tenant, định danh ổn định, loại công việc, Trip gốc và liên kết đúng đơn/dòng hoặc hồ sơ nghiệp vụ hiện có.
- Người nhận là danh tính hiện hành đủ quyền/phạm vi; hạn xử lý là thời điểm rõ múi giờ theo ngữ cảnh ngày DEV, do người giao việc chọn. Không tự đặt SLA kinh doanh từ fixture.
- Trạng thái, next action, lý do, lượng/đơn vị khi liên quan A và định danh của chứng từ tạo/cập nhật/hoàn tất.
- Phân biệt “còn phải giao”, “đã giữ”, “đã xếp chuyến”, “đã nạp”, “đã giao” và “đã đóng phần còn”.
- Đổi người nhận hoặc hạn phải có quyền, lý do và lịch sử. Hồ sơ gốc vẫn giữ được người chịu trách nhiệm và hạn tại lúc chốt; đồng thời đọc được người/hạn hiện tại.
- Dùng một hồ sơ nghiệp vụ gốc khi đã có ở D/F. Việc theo dõi cuối ca trỏ tới hồ sơ đó, không trở thành một sổ tiền/hàng hoặc quy trình phê duyệt trùng.

### Quyền đề xuất v1

- Tenant Ops được phân công là người chấp nhận chốt; đây không phải toàn quyền Owner mặc định.
- Kho/điều phối có quyền phù hợp xếp chuyến kế nhiệm; người nhận việc không tự có quyền nạp hoặc giao chỉ nhờ được giao việc.
- Đề nghị đóng phần A chưa giao cần lý do và nguồn; người duyệt đủ quyền theo nghiệp vụ, tách người đề nghị trong fixture v1. Tên permission/actor cụ thể do implementer ánh xạ vào cơ chế hiện có.
- Mọi đọc, sửa người/hạn, xếp chuyến, duyệt, chốt, lookup/replay đều kiểm quyền hiện hành. Quyền đọc hoặc replay nguồn cũ không tự mở quyền ghi mới trên chuyến đã đóng.

## 4. Chuyển tiếp A: hợp đồng dữ liệu và bảo toàn

### Một đơn, nhiều chuyến thực hiện có nguồn

Thiết kế đề xuất giữ nguyên OrderId, OrderLineId, ConfirmSourceId, snapshot và các Delivery đã có. Bổ sung liên kết có nguồn từ hồ sơ còn lại và quyền giữ gốc tới chuyến/quyền thực hiện kế nhiệm. Không sao chép đơn rồi Confirm lại, không đổi AOrder.TripId để làm mất nguồn gốc lịch sử.

Liên kết kế nhiệm phải tạo được từ đúng OrderLine/Confirm và hồ sơ còn lại **kể cả khi HELD=0, đơn chưa từng Reserve**. Thao tác chọn chuyến chỉ tạo binding có nguồn, không đòi StockRight chưa tồn tại, không tạo right giả hay transfer số lượng 0 và không cấp giữ. Command transfer HELD và Reserve phần chưa giữ sau đó dùng binding này theo đúng nguồn/phạm vi. Roadmap APP-05.3 gọi hợp đồng logic này là `AssignASuccessor`.

Đây là thay đổi SQL/API thực sự: các truy vấn C.3/C.4 và khóa ngoại hiện giả định chuyến của đơn cũng là chuyến nạp/giao. Một màn chọn chuyến hoặc bỏ một điều kiện JOIN không đủ để hỗ trợ giao liên ngày. Migration phải bổ sung đường ràng buộc chính xác cho nhánh kế nhiệm, giữ sự toàn vẹn nguồn, tenant, OrderLine, SKU, quyền giữ, manifest và evidence. Các đường legacy tiếp tục có điều kiện riêng và được hồi quy.

### Phân biệt lượng còn phải giao với lượng đã giữ

Ví dụ kiểm chứng: Confirm 5, giữ 4, nạp 3, giao 1, trả kho 2. Khi chốt, đơn còn phải giao 4, nhưng chỉ 3 còn HELD: 1 chưa từng nạp và 2 đã nạp rồi trả. Command chuyển tiếp chỉ được dùng 3 đã giữ. Phần 1 chưa giữ vẫn phải hiện đúng trạng thái và người phụ trách, không được nhân bản thành quyền mới chỉ bằng thao tác chọn chuyến.

V1 có hai thao tác tách biệt: chuyển tiếp lượng HELD đã có, và cấp giữ mới cho phần chưa giữ bằng một lệnh Reserve có nguồn trên liên kết thực hiện kế nhiệm. Chọn chuyến cho phần chưa giữ không cấp quyền tồn. Reserve mới phải kiểm lượng còn được phép giữ trên toàn dòng, tồn khả dụng, tenant/quyền và transaction như C.2, rồi mới cho nạp/giao. Trong ca 5/4/3/1/2, chuyển HELD3 trước; phần1 chỉ trở thành HELD sau Reserve mới thành công. Nếu thiếu tồn, phần1 giữ trạng thái chờ và có người/hạn; không được đánh dấu hoàn tất.

### Các bất biến trước và sau chuyển tiếp

1. Với dòng Confirm có lượng Q: đã giao + đã đóng phần còn hợp lệ + còn phải thực hiện = Q. Các số được đối chiếu trên toàn dòng đơn qua mọi chuyến, không chỉ Trip gốc hoặc Trip đang xem.
2. Mỗi lượng HELD chỉ có một quyền sử dụng còn hiệu lực tại một thời điểm. Hai chuyến đích tranh cùng phần giữ không được cùng nhận thành công. Chuyển tiếp một phần giữ phần chưa chuyển trong hồ sơ cũ; không đánh dấu cả công việc hoàn tất.
3. Chuyển tiếp giữ tổng lượng allocated trên MAIN cho phần đã giữ; không sinh biến động vật lý kho/xe, AR, COGS, receipt hoặc quỹ. Khi nạp thật mới có cặp chuyển kho→xe. Khi giao thật mới có COGS/AR.
4. Nếu dùng nguồn đóng quyền cũ/cấp quyền kế nhiệm, phải có liên kết và phương trình triệt tiêu chính xác phần chuyển. Không cộng mọi Granted của các thế hệ quyền thành số lượng được phép giao mới.
5. Hai loại HELD, chưa nạp và đã trả từ xe, đều phải có nguồn xác định. Công thức dùng tổng nạp suốt đời không được làm lượng trả từ xe trở thành không thể nạp lại hoặc có thể dùng hai lần.
6. Lô, UoM và phần dư giá trị được truy theo nguồn. Kiểm điều kiện lô/hạn/FEFO ở thời điểm thực hiện mới; lô không còn hợp lệ phải bị chặn hoặc đi qua quy trình có nguồn tương ứng, không bỏ guard để giữ nguyên kịch bản.
7. Giá/discount/tax/terms và snapshot nguồn giữ đúng phiên bản được phép. Due date của lần giao sau tính từ ngày giao sau theo N gốc. Mọi amendment được công bố hỗ trợ phải có nguồn/duyệt và phạm vi chưa giao; việc xếp chuyến tự nó không amendment.
8. Đóng phần chưa giao được duyệt chỉ kết thúc nghĩa vụ tương ứng và giải phóng phần giữ hợp lệ; không đảo giao hàng đã phát sinh, không giảm AR đã ghi vì lý do này. Các trường hợp hàng vẫn trên xe, còn evidence chưa xử lý hoặc xung đột chuyển tiếp phải bị chặn.

## 5. Chốt, phục hồi và lịch sử

- Bản xem trước đối soát mang source revision/digest đủ để nhận diện dữ liệu thay đổi. Close phải kiểm lại nguồn và quyền trong cùng phạm vi tuần tự hóa với các command có thể làm thay đổi điều kiện chốt; bản xem trước không phải quyền bỏ qua recheck.
- Ghi quyết định đóng, snapshot nguồn/độ phủ, danh sách việc tồn và kết quả command trong một transaction. Không có trạng thái chuyến đã đóng nhưng công việc cần bàn giao chưa được giữ bền.
- Lưu một quyết định chốt thành công cho chuyến trong v1. Duplicate với cùng identity/payload trả outcome gốc; payload đổi conflict. Gửi ID mới khi đã chốt không tạo quyết định thứ hai.
- Biên bản chốt bất biến; cập nhật công việc ghi sự kiện mới. Hiển thị riêng số việc tại lúc chốt và số còn mở hiện tại. Không tự đổi lịch sử CLOSED_WITH_OPEN_CASES thành một quá khứ CLOSED sạch.
- Sai lệch phát hiện sau chốt được ghi hồ sơ có nguồn và xử lý tiến theo cơ chế D/F. V1 không mở lại quyền bán/nạp/giao mới trên chuyến đã đóng. Tra cứu và replay lệnh cũ tuân theo quyền hiện hành và scope nguồn gốc.
- Phục hồi phải hoạt động khi mất ACK, Stop/Resume và đóng hẳn trình duyệt. Từ phiên đăng nhập mới, server cho phép người có quyền tìm công việc/nguồn/lệnh đã gửi theo phạm vi của họ. Không phụ thuộc duy nhất vào sessionStorage, không lưu bearer token lâu dài để thay bằng chứng kết quả.
- Clock DEV, asOf và hạn xử lý giữ nhất quán qua NextDay. Chuẩn bị ngày mới không tự đóng chuyến, tự chọn chuyến kế nhiệm hay tự hoàn tất công việc.

## 6. Phạm vi thay đổi kỹ thuật dự kiến

| Thành phần hiện có | Thay đổi cần xem xét |
| --- | --- |
| App01a2CommandService.cs, App02c2Commands.cs/Reads.cs | Tổng nghĩa vụ/quyền giữ theo dòng qua nhiều chuyến; scope nguồn và quyền hiện hành; giữ Confirm gốc |
| App02c3Load.cs, App02DevF03Lots.cs | Lượng HELD hợp lệ để nạp ở chuyến kế nhiệm; phân biệt chưa nạp và đã trả; tránh đếm lặp quyền/lô |
| App02c4Schema.cs, App01b1PhysicalStock.cs, App01c1PostingSchema.cs | Migration tiến cho liên kết thực hiện kế nhiệm và khóa ngoại chính xác; giữ ràng buộc exact OrderLine/Confirm/SKU/right/source/evidence |
| App02c4Delivery.cs/Reads.cs/ShipTo.cs | Nhánh giao tiếp hợp lệ, quyền trên chuyến mới và nguồn cũ; tổng giao/làm tròn theo dòng, ShipTo version và due date |
| App01dD3Unload.cs và read model cuối chuyến | Dùng lại nguồn trả xe/giải phóng B; thêm readiness và command đóng mà không tạo sổ hàng/tiền song song |
| App03HandoffReads.cs, App02c5Report.cs, App03OwnerReport.Sql.cs | Một đơn với nhiều chuyến thực hiện; tổng không nhân đôi; drill-down nguồn và việc tồn |
| Workspace, Order/Load/Delivery, MixedTripWorkspace | Đường vào đối soát/chốt, công việc được giao và chọn chuyến giao tiếp; giữ context và controls đã duyệt |
| App03DevSession/Bootstrap, runner APP-03 và scripts | Nâng phiên đã có dữ liệu bằng migration có guard; fixture actor, clock và command journey; giữ ownership/nonce |

Các file C# nêu trên thuộc src/FmcgDms.Api/Persistence trừ các thành phần launcher/bootstrap thuộc src/FmcgDms.Demo. Implementer chọn tên bảng/route/permission và cách chia file sau khi rà các FK/trigger/query đầy đủ. Không được thay công thức business, sửa business.lock.json hoặc hạ guard để khớp fixture.

## 7. Chia increment có đầu ra dùng được

[Roadmap APP-05](../../roadmap/app-05.md) đã gán mã chính thức để không dùng lại các mã APP-04 lịch sử được ánh xạ sang DEV-F trong APP-03. Thứ tự 0–7 dưới đây ánh xạ lần lượt APP-05.0–.7; bước 4 tách thành APP-05.4a (Reserve bổ sung) và APP-05.4b (nạp/giao tiếp). Bảng này giữ cấu trúc thiết kế; trạng thái thực hiện chỉ cập nhật trong roadmap APP-05 và biên nhận của increment sở hữu.

| Thứ tự | Phạm vi PR | Tiêu chí hoàn tất |
| --- | --- | --- |
| 0 | Ổn định thời gian CI nền | Chẩn đoán job APP-00A bị hủy, chọn budget/tách job theo thời lượng; giữ đủ gate và artifact đúng source; có lượt main hoàn tất |
| 1 | Readiness cuối chuyến và công việc tồn | UI→API đọc đúng điều kiện; ghi người/hạn/next action bằng command; trạng thái A phân biệt phải giao và thực HELD; mở được hồ sơ D/F gốc |
| 2 | Chốt chuyến sạch và có việc tồn | Recheck trong transaction; CLOSED hoặc CLOSED_WITH_OPEN_CASES đúng điều kiện; bàn giao nguồn bằng 0 cho chuyến có đơn nhưng chưa nạp; snapshot bất biến, một outcome, quyền/tenant và mất ACK; chưa cần TripId kế nhiệm |
| 3 | Chọn chuyến và chuyển tiếp quyền A | Migration/liên kết nguồn, quyền và conservation; nhận cả HELD chưa nạp lẫn đã trả xe; cạnh tranh, rollback, replay; hồ sơ còn theo dõi sau xếp chuyến |
| 4 | Reserve bổ sung, nạp và giao tiếp | C.2/C.3/C.4 hoạt động trên liên kết/chuyến kế nhiệm với đơn/snapshot gốc; lượng chưa giữ chỉ được cấp từ tồn hợp lệ qua Reserve; AR/due mới đúng; tổng lượng qua mọi chuyến không vượt dòng |
| 5 | Đóng phần A còn lại bằng nguồn được duyệt | Người đề nghị/duyệt độc lập, lý do và lượng rõ; chỉ giải phóng phần đúng phạm vi; đua với chuyển tiếp/giao không tạo mất hàng hoặc vượt lượng; công việc hoàn tất theo nguồn |
| 6 | Bàn công việc ngày sau và phục hồi trình duyệt mới | Danh sách theo người/hạn/trạng thái; tái giao có audit; truy lệnh/nguồn từ phiên mới; menu và context dùng chung, thông tin Compact |
| 7 | Nghiệm thu ca tích hợp | Chuyến sạch, chuyến còn A15, chuyến chưa nạp và chuyến bị chặn; ngày hai xử lý tiếp trên cùng DB; thao tác trực tiếp và tự động có expected/actual/diff độc lập |

Increment 4 được phép chia các PR Reserve và nạp/giao nếu diff vượt kích thước phù hợp solo dev; không trộn thay đổi FK/posted source lớn chỉ để giữ số PR. Increment 2 cung cấp bàn giao có người/hạn trước khi increment 3/4 mở hành trình giao tiếp; chỉ increment 7 xác nhận trọn ca xuyên ngày.

## 8. Ma trận nghiệm thu tối thiểu theo rủi ro

| Nhóm | Bằng chứng cần có |
| --- | --- |
| Chốt sạch | Chuyến đủ điều kiện vào CLOSED, không có case giả; mọi sổ ngoài phạm vi không đổi |
| Chốt có việc tồn | A40/giao25/trả15; có người/hạn, chưa có chuyến đích; CLOSED_WITH_OPEN_CASES và công việc được giữ bền cùng quyết định |
| Chuyến chưa nạp | A HELD toàn bộ tại MAIN, không manifest; hoặc Confirm chưa Reserve: chứng minh không có lượng trên xe của riêng chuyến, bàn giao nguồn bằng 0 và giữ việc phải làm. Chuyến bootstrap hoàn toàn trống được phân biệt và không tạo case |
| Chặn đúng | Thiếu người/hạn, nguồn drift, custody/offer chưa xử lý, evidence C.4 còn pending, quyền sai hoặc dữ liệu đổi sau preview đều không tạo quyết định đóng |
| Ngày hai | Cùng OrderId/Confirm/dòng; quyền/lot hợp lệ chuyển sang chuyến mới, nạp/giao15; giá/terms gốc, due theo ngày giao mới; lịch sử ngày một giữ nguyên |
| Phân biệt lượng | Ca Confirm5/giữ4/nạp3/giao1/trả2: còn phải giao4, HELD3; không cho chuyển4 bằng command chỉ chuyển HELD; phần1 cần Reserve mới thành công trước nạp/giao; thiếu tồn vẫn chờ, không biến mất khỏi màn hình |
| Chưa từng Reserve | Confirm5/Reserve0: bàn giao không manifest và chốt, chọn Trip kế nhiệm từ nguồn Confirm, Reserve5 rồi mới nạp/giao5; thiếu tồn vẫn giữ người/hạn. Không cần right giả hoặc transfer số lượng 0 để tạo binding |
| Cạnh tranh | Hai chuyến đích tranh HELD, đóng phần còn đua với chuyển tiếp, close đua nguồn thay đổi, giao trên các quyền kế nhiệm tranh cùng lượng còn của dòng: bảo toàn lượng/tiền và một kết quả hợp lệ |
| Lô/giá trị | Hai nguồn HELD đã trả/chưa nạp; lô hết hạn hoặc sai nguồn bị chặn; tổng allocated, physical quantity/cost giữ nguyên ở bước chỉ xếp chuyến |
| Phân nhiệm | Người đề nghị không tự duyệt đóng phần còn trong fixture; người nhận việc không tự có quyền Warehouse/Delivery; thu hồi quyền ảnh hưởng đọc/ghi/replay đúng phạm vi |
| Phục hồi | Bỏ ACK sau COMMIT, dừng host, đóng browser, Resume và phiên mới; tra được cùng command/source, replay không tăng quyết định/quyền/tồn/AR; payload đổi conflict |
| Hai tenant | Tenant đối chứng không đọc/ghi/replay được nguồn của tenant kia; ảnh dòng đối chứng giữ nguyên sau từng nhóm rủi ro |
| Lịch sử và báo cáo | Đúng số việc mở lúc chốt theo fixture (ca chính là hai), hiện đã xong hoặc còn mở; snapshot chốt không đổi; một đơn hiện đầy đủ mọi chuyến, tổng không nhân đôi |
| Cutoff D.4 | Ca chính có việc A và việc đối soát ngày một; NextDay dùng báo cáo bao phủ đúng nguồn ngày một để kết thúc việc kế toán. Không dùng cùng báo cáo để hoàn tất nguồn phát sinh ngày hai |
| Nâng phiên | Có đơn và giao ngày một trước migration; Resume nâng đúng hai DB, giữ hash nguồn cũ; Remove chỉ thu hồi đúng ownership/nonce |

Fixture dùng sản phẩm/giá/terms/lô giả lập có nguồn được chuẩn bị trước, nhưng đơn/chuyến giao nhận/quyết định đóng và công việc phải được tạo bằng đường nghiệp vụ tương ứng. Expected độc lập và công bố trước execution. Chỉ chạy hồi quy theo ảnh hưởng trong từng PR, giữ các gate bắt buộc và chạy hành trình tích hợp cuối.

Biên nhận ghi source/tree, môi trường, run ID, từng nhóm expected/actual/diff, command/source IDs, artifact hash và phạm vi. LOCAL/CI/MERGED/Owner sử dụng trực tiếp là các trạng thái khác nhau. Giữ giới hạn physical PREPROD/EV/P0/Pilot và ngoại lệ 125% đã được chốt.

## 9. Kết quả cần thấy sau đợt này

Owner mở một phiên DEV, làm ca ngày một, chốt chuyến có 15 đơn vị A còn giữ và giao trách nhiệm có hạn. Sau Stop/Resume và mở trình duyệt mới, người phụ trách tìm được công việc ngày hai, chọn chuyến, giao tiếp bằng đơn gốc và xem được việc đã hoàn tất. Một ca khác chốt sạch; một ca chưa đủ điều kiện vẫn bị chặn với lý do và hành động cụ thể. Cả hai tenant, các sổ và snapshot đều có đối chiếu độc lập.

Thiết kế này mở rộng chức năng DEV, không gán nhãn nghiệm thu cho những hành trình chưa chạy và không tái xây các workspace C/D/F đã có.
