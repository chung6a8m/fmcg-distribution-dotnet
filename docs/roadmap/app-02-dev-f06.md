# DEV-F06 — Điều chỉnh giá vốn sau kỳ khóa

AC-06, BR-07/20, AP-02 và SOP-05. Giữ Delivery cost gốc và các hàng đã ghi ở kỳ khóa. Finance lập đề nghị điều chỉnh giá vốn của Delivery có nguồn. Checker QA được phân công tại kho nguồn, độc lập với maker, xác nhận lý do/chứng từ và ghi sự kiện điều chỉnh vào kỳ hiện hành OPEN. Ghi phần chênh đã hoàn và thay đổi giá vốn nguồn theo sổ hiện hành. Phần trả về sau dùng phiên chi phí được duyệt mới. AR và tiền khách giữ nguyên. Trả qua kỳ mở vẫn dùng giá vốn gốc khi chưa có đề nghị điều chỉnh, chờ xử lý khi đề nghị đang mở.

Fixture v1 kế thừa SKU R-05, 2 EA, lô RET-ORIGIN và cost 6667 VND. Clock fixture tháng 11 để giao và hoàn 1 EA giá vốn 3334. Khóa 2026-11 với close ref synthetic rồi chuyển clock sang tháng 12 OPEN để đề nghị 6768. Giả định thử mỗi Delivery một điều chỉnh được duyệt. Chỉ tự ghi phần chênh chi phí của hàng đã hoàn khi fingerprint tồn kho nguồn vẫn khớp sau đúng một lần release. Phạm vi nhiều lần release đã ghi trước khi sửa cần hồ sơ độc lập. Nếu hàng đã tiếp tục dịch chuyển, giữ case cho xử lý nguồn độc lập. Giá trị điều chỉnh nguyên VND.

| Ca | Expected độc lập |
| --- | --- |
| F06-01 · AC-06 | Giao 6667 và trả 3334 kỳ tháng 11. Kỳ khóa và các snapshot Shipment/Release/Ledger cũ giữ nguyên. Không ghi trực tiếp vào kỳ khóa. |
| F06-02 · AC-06 | Checker độc lập ghi chênh nguồn +101 và chênh phần đã hoàn +50 vào tháng 12. Lần hoàn sau dùng 3384. 3334 + 50 + 3384 = 6768, sổ giá vốn nguồn và tồn khớp. |
| F06-03 · AP-02 | Từ chối sai tenant/sai vai, tự duyệt, thiếu kỳ mở, hoặc sửa lặp cùng nguồn. Mất ACK có thể tra cứu nguồn, payload khác cùng ID bị chặn. |
| F06-04 · regression | AR unchanged, lot/QA stages giữ đúng oracle F05. So sánh digest nguồn đã khóa trước/sau và thời điểm ghi từng effect kỳ mới. |

Màn hình `/sales/cost-adjustments`. API và SQL dùng source và command hiện hành trong scratch. Kết quả phạm vi DEV.
