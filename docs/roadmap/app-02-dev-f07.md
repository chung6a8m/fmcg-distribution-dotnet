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

## Biên nhận DEV

Ngày 23/11/2026, run `2e2df3898a924d1ca2655f10c0dc3465` tại source `f433737532a15eb7981711fd8f7588cee33a19c4`, tree `afbaad555f2e39e7ab2eb013173cca751e413148`. Chromium 1/1 và .NET Release 351/351. Hai nhánh SQL sau mua lại đều 20 EA đủ điều kiện. Nhánh trả quà còn giữ 0 và cần cấp 2; hai yêu cầu cấp cạnh tranh chỉ ghi một lần. Nhánh waiver còn giữ 2 và cần cấp 0. Sổ quà có ba lần xuất 2 EA và một lần nhận lại 2 EA vào quarantine. AR chỉ từ giao hàng mua, 620.000 VND. Zero business effects ở B. Browser còn kiểm mất ACK, tra cứu và replay, sai vai, kho chưa phân công, tự duyệt, vượt lượng và chéo tenant. Trả thực sau waiver đã duyệt và thay thế đề nghị hết hiệu lực chưa được chạy trong lượt này.

| Artifact | SHA-256 |
| --- | --- |
| Locked AC-07 oracle file | `225D7253645F239363CEC143CA607BF564AFD030CA7E7BFAEEBBEF2A9A63DB5F` |
| Browser fixture v1 | `5FD99F7FAABB5829EDCFA5574FAFD7F63C03665069E2AA64657B32B12FDED2C9` |
| JUnit | `EC16DFB36858FAD7B54BCE2724D097901FD92B970B9EEF93C24DA73DDE5F57E2` |
| SQL result | `28904A2F45CDD19E7F17EB4BDE1BDC0B0E29169AB10E34C97886126CE5827E82` |
| API DLL | `4F3D7A007394B8EE9C395810292DD1BAD590FC6FF8C35479890D73C869A000A2` |

Tái chạy `./scripts/Run-App02C1.ps1 -Increment DevF07 -Verify`. SQL tại `TestResults/dev-f07/<run>/sql-result.json`, browser tại `web/test-results-app02c5/<run>/junit.xml`. Scratch là DEV LocalDB do nonce sở hữu.
