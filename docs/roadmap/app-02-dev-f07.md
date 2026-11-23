# DEV-F07 — Trả quà và waiver

AC-07, BR-09, POL-09, SOP-06 và AP-04. Scope một phiên bản chương trình DEV đã định sẵn cho SKU mua chuẩn EA và một SKU quà, cùng kho nguồn. Cộng lượng mua từ SaleDelivery của SKU và trừ lượng đã RELEASE của F05. Giao quà là nguồn riêng, giảm tồn của kho quà. Nhận lại quà sau kiểm đếm vào quarantine. Mỗi waiver gắn lần giao quà, phạm vi số lượng, lý do/chứng từ và checker độc lập. Waiver còn giữ khách hàng vẫn tính vào giftHeld. Quà cùng đợt giao không phân biệt chiếc. Khi trả, trừ phần chưa miễn trước, sau đó giảm waivedHeld theo lượng còn giữ, đồng thời giữ lịch sử quyết định. Đề nghị vượt lượng còn giữ tự hết hiệu lực; Sales có thể lập đề nghị mới cho phần còn giữ. Kho quà chuyên dụng có sổ lượng và giá vốn tồn. Kỳ này chưa hạch toán chi phí khuyến mại vào kế toán.  Các bước thực hiện online với tenant và vai hiện hành.

Fixture v1 cho hai khách A tách biệt, có tenant B đối chứng. Chương trình 10 EA mua ròng nhận 1 quà, hai nguồn mua ban đầu mỗi khách 20 EA, trả 11 EA mỗi khách từ đúng Delivery/lô đã xuất rồi mua tiếp 11 EA. Quà lấy từ kho chuyên dụng, VND nguyên đồng. Finance duyệt phạm vi waiver, người lập thuộc vai Sales. Kho nhận quà chỉ có quyền được phân công.

| Ca | Expected trước lượt kiểm |
| --- | --- |
| F07-01 · AC-07 | Mua 20 EA, quyền 2. Cấp 2 quà một lần, giftHeld 2, không cấp lại khi gửi trùng. |
| F07-02 · AC-07 | Trả 11 EA hàng mua được release, trả hai quà; sau mua tiếp 11, net 20/held 0/toDeliver 2. Hai quà mới có nguồn khác. |
| F07-03 · AC-07/AP-04 | Sau trả 11 hàng mua, giữ hai quà có waiver đã duyệt. Sau mua tiếp 11, net 20/held 2/toDeliver 0. |
| F07-04 · BR-09 | Mất ACK, cạnh tranh, sai tenant, sai vai, tự duyệt, cấp vượt quyền. Chỉ quay lại quyền giao khi đã nhận quà thực. Kiểm SQL tồn và không có đơn 0 đồng hoặc AR từ nguồn quà. |

UI `/sales/gifts`, API DEV và nguồn ledger riêng trên LocalDB. Fixture đặt rule chương trình; quản trị master chương trình và vận hành vật lý nằm ngoài phạm vi.
