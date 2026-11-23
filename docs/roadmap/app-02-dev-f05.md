# DEV-F05 — Trả hàng từng phần và QA

AC-05/06, BR-06/07/20, POL-06/20, SOP-05/06, AP-02/05. DEV-F05 tiếp nối luồng A của F03/F04. Customer trả từ đúng Delivery và lô đã xuất. Mỗi đề nghị giữ phần lượng của lô nguồn. Kho nhập lượng thực đếm độc lập. V1 nhận khi lượng đếm khớp hồ sơ, sai lệch giữ bước nhận chờ xử lý. Hàng đã nhận vào QUARANTINE và giữ trạng thái không khả dụng. Finance xác nhận hồ sơ thương mại, QA độc lập được phân công ở kho nguồn quyết định release về kho nguồn nếu lô còn hạn. Chỉ release khi đã có hai kiểm tra. Ghi giảm chi phí nguồn lũy kế khi release. Giá trị sổ quarantine ở trạng thái đang nhận chờ được ghi khi có xác nhận release; lượng và nguồn nhận được ghi ngay. AR, credit và refund của khách giữ nguyên qua phạm vi này. DEV-F08 xử lý lượng đã được chấp nhận về tiền và thuế.

Fixture v1 gồm A source SKU R-05, base EA bước 1, 2 EA tại MAIN cost 6667 VND, lô RET-ORIGIN hạn 31/12/2027. B có opening scratch riêng. Quarantine F05-QA bắt đầu 0. Giá bán thử 10000 mỗi EA, VAT và discount 0, invoiceEligible=false. Clock 2026-11-23 UTC, giao và hoàn trong kỳ đang mở của fixture. Finance, người kho nhận và QA là các actor DEV khác nhau. AP-02/05 có nguồn, lý do và chứng từ. Mô hình này giữ đúng số dư chứng từ khi yêu cầu bị lặp, đồng thời, hoặc bị từ chối. Phạm vi nguồn có snapshot lô, VND và giá vốn xuất nguyên đồng.

| Ca | Expected trước lượt thử |
| --- | --- |
| F05-01 | 2 EA giao, cost 6667, AR 20000. Tạo case trả 1 EA gắn nguồn; không đổi AR/tồn. Trả vượt lô và chéo tenant bị từ chối. |
| F05-02 | Kho nhận 1 EA vào QA. MAIN 0 EA, QA 1 EA; không cấp từ QA. Chưa có COGS reversal hoặc AR giảm. QA không duyệt trước Finance. |
| F05-03 | Finance và QA độc lập, release lần 1 trả 3334, lần 2 trả 3333. MAIN 2 EA / 6667 VND, QA 0; COGS net 0, AR 20000. Replay giữ source. Kiểm tenant B và sai vai. |
| F05-04 | Đồng thời với cùng hoặc hai identity không thể vượt Delivery, lot, nhận, hay release hai lần. Partial qua hai case giữ cùng source cost. |

UI `/sales/returns` và synthetic API `/api/synthetic/dev-f05`. SQL lưu cạnh nguồn hiện hành và stock ledger. Mỗi case hiện hành có đường đọc lại trạng thái. Hồ sơ kỳ khóa và điều chỉnh thuộc DEV-F06. Quà và waiver, refund thực thuộc F07/F08.
