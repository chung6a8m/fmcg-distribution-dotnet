# DEV-F08 — Credit và thực chi theo giá nguồn

AC-19, BR-08/20, POL-08/20, SOP-06, AP-05 và các nhánh EV tương ứng. Chỉ dùng lô đã release của F05 và F04 Delivery có snapshot giá VND. Verifier DEV độc lập ghi đối soát tiền vào theo đúng Delivery, số tiền và mã chứng từ. Finance xác nhận đúng mã đã được verifier ghi để tất toán khoản phải thu. Khi đã thu đủ, Finance đăng credit từ lần trả được duyệt. Tính net, thuế, discount và residual theo số lượng lũy kế của Delivery, dùng tỷ trọng số nguyên và quy tắc đã chốt trong F04. Mỗi case chỉ ghi một credit. F08 v1 yêu cầu nguồn đã thanh toán đủ, snapshot của một Delivery hoàn thành riêng một dòng hàng và một kỳ đang mở khi F06 bật. Hàng đã release nhưng chưa đủ điều kiện giữ quyền tiếp tục tra cứu để xử lý khi đủ dữ liệu.

Finance lập yêu cầu chi riêng cho một credit. Finance checker độc lập duyệt theo AP-05. Sau duyệt, verifier độc lập ghi phiếu thực nhận tiền DEV gắn yêu cầu, lượng tiền và mã chứng từ. Thủ quỹ riêng đối chiếu cùng phiếu và lượng thực chi. Phiếu kiểm chứng độc lập giữ riêng nguồn đã duyệt và quỹ khả dụng.  Đề nghị và duyệt không ghi giảm credit. Mỗi chứng từ thực chi và yêu cầu chỉ ghi một lần. Nguồn vào và ra quỹ thử theo chứng từ synthetic; không gọi dịch vụ thanh toán hay hóa đơn. Thuế ở đây là số nội bộ, invoiceEligible=false. Credit riêng theo nguồn được tổng hợp theo khách; luồng dùng credit trên hóa đơn mới chưa thuộc fixture này.

Fixture v1 dùng P của EXCLUSIVE, số lượng 3 EA, net 193333, tax 15467, gross 208800, allocated discount 6667 từ AC-19. Lộ trình trả ba lần mỗi lần 1 EA. A được kiểm ba vai và B là đối chứng tenant. Số dư quỹ thử 100000 VND.

| Ca | Expected trước lượt chạy |
| --- | --- |
| F08-01 · AC-19 | Thanh toán 208800 theo chứng từ, AR mở về 0. Case trong quarantine hoặc chưa release, nguồn giả và bản ghi chéo tenant không tạo credit. |
| F08-02 · AC-19 | Ba phần tương ứng 64444/5156/69600, 64445/5155/69600, 64444/5156/69600. Discount lần lượt 2222/2223/2222 và về đúng 6667. |
| F08-03 · AP-05 | Credit 69600 qua đề nghị và duyệt. Chi 20000 với chứng từ hợp lệ thì phần credit còn 49600, quỹ 80000. Thiếu bằng chứng tiền thực chi, tự duyệt, chéo vai và cùng chứng từ chi hai lần bị từ chối. |
| F08-04 · regression | Retry/lost acknowledgement bảo toàn source rows; đồng thời không vượt credit hoặc quỹ thử. Snapshot của giao hàng, thuế, lượng và COGS đã ghi giữ nguyên. |

UI `/sales/refunds`, endpoint synthetic DEV, LocalDB nonce-owned và ledger AR, customer credit, quỹ thử.
