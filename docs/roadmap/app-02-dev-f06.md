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

## Biên nhận DEV

Ngày 23/11/2026, fixture v1 chạy Chromium và LocalDB synthetic, run `306ff8e6a71244faabbca989d132608a` tại source `16afe6635364cf1c84bed3813d5f7b601d66c7fa`, tree `10c82f39b882856bbed434ab88987c4ea302d750`. Chromium 1/1, .NET Release 351/351. SQL đối chiếu AC-06 khóa nguyên kỳ tháng 11 qua 12 nhóm digest. Giao hàng thử vào tháng 11 đã khóa bị từ chối. Điều chỉnh tháng 12 giữ một đề nghị, một quyết định, cộng 101 VND vào chi phí nguồn và phân bổ 50 VND cho lần hoàn trước. Kho nhận đủ giá vốn 3.334 + 50 + 3.384 = 6.768 VND, diff 0. MAIN 2 EA, quarantine 0, COGS 0 và AR 20.000 VND. Kiểm nhánh kỳ chưa mở, nguồn chéo tenant, maker tự duyệt, release khi đề nghị còn mở và recovery mất ACK.

| Artifact | SHA-256 |
| --- | --- |
| Locked AC-06 oracle file | `225D7253645F239363CEC143CA607BF564AFD030CA7E7BFAEEBBEF2A9A63DB5F` |
| Browser fixture | `723BDE5F4AB84027B0CA524434A74894A34939D62EB16D2D4C398C2150C6DC35` |
| JUnit | `4C8F2218DE2BEE61B30CDEF3AE490CC66012D38A40C418C51F5BF1DFD638226C` |
| SQL result | `C3EB491811151BADB155FCAF02FE356A35BDBBCC5EFE118C084AA4DC3596A236` |
| API DLL | `285C7703CF5DBCB89558FC38A32FBF62213FC1D1AB764FDFF27C3F3BE63A33AA` |

Tái chạy `./scripts/Run-App02C1.ps1 -Increment DevF06 -Verify`. SQL tại `TestResults/dev-f06/<run>/sql-result.json`; launcher tạo scratch do nonce sở hữu. Case hàng đã di chuyển sau release, nhiều lần release trước khi điều chỉnh và điều chỉnh kế toán thật chưa được chấp nhận trong lượt này.
