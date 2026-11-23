# DEV-F03 — Đơn vị, lô và FEFO

AC-05 (luồng giao), BR-06/08, POL-06 và stock SOP-02. Áp dụng vào luồng A online Draft → Confirm → Reserve → Load → Delivery trong profile DEV-F03. Giữ nguồn, quyền A, manifest, tồn vật lý, AR và COGS trên mô hình C.2–C.4. Snapshot quy đổi theo dòng draft và nguồn đơn xác nhận. Chi tiết lô bắt nguồn từ opening/StockRight → manifest line → source giao. Kho lot/expiry dùng ngày nghiệp vụ Asia/Ho_Chi_Minh. Đơn vị cơ sở không thay đổi trong vòng đời nguồn.

Giả định fixture v1, SKU C2-SKU có EA và BOX, 1 BOX = 24 EA, bước nhập 1 đơn vị mỗi UoM, scale tồn 3. Tồn C2-MAIN 100 EA, giá vốn 10 SYNTHETIC_UNITLESS mỗi EA. Lô EARLY 30 hết hạn 30/06/2027, LATE 50 hết hạn 31/12/2027, EXPIRED 20 hết hạn 30/06/2026. QA-HELD 40 vào vị trí quarantine riêng, hết hạn 31/12/2026. Đồng hồ fixture pin 2026-11-23T02:00:00Z trong phiên LocalDB DEV. Giá thương mại fixture 15 mỗi EA, invoiceEligible=false. Cắt scope tới ngày DEV do host ghi nhận. Hàng trả và QA release thành luồng DEV-F05.

| Ca | Expected độc lập |
| --- | --- |
| F03-01 | Nhập 0.5 BOX bị từ chối. Hai BOX được snapshot thành 48 EA, hệ số 24, bước 1. Giữ baseline khi khách dùng đơn vị mặc định. |
| F03-02 | Giữ 48 EA từ MAIN lấy EARLY 30 rồi LATE 18. Lệnh giữ từ QA bị từ chối. EXPIRED và QA-HELD có 0 allocation, load và issue. |
| F03-03 | Load 48 qua hai đợt, giữ lot trail. Từ chối giao LATE khi còn EARLY khả dụng của cùng dòng đơn/chuyến. Giao 35 qua hai nguồn từ EARLY 30 và LATE 5. MAIN 52, xe 13, QA 40. Delivery 35, AR 525, COGS 350. Replay không thêm lot hay nghiệp vụ. |
| F03-04 | Tenant B chỉ có opening riêng và 0 giao. Cạnh tranh dùng khóa của stock/right đang có. B ngoài lane DEV-F03 và hàng trả/QA release thuộc phạm vi khác. |

UI tại `/sales` dùng form đơn A và chọn UoM theo SKU. Test dùng API, SQL và Chromium trong LocalDB nonce. Có snapshot và đối chiếu theo nguồn C2, C3, C4.
