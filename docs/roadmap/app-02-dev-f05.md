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

## Biên nhận DEV

Ngày 23/11/2026, fixture v1 chạy Chromium vào UI, API và LocalDB synthetic. Run `66323b88cca44cfda86ae3e0b1a58034` tại source `ef9bb8895e8f203d720fc49e57c8f839a51f5b72`, tree `1c455bf92f2610afe0dd9963b45d2c66b27d80d2`. Chromium 1/1, .NET Release 351/351. SQL khớp 6 checkpoint, ảnh tồn và giá vốn cuối, 2 bộ stage/ledger và tenant đối chứng. Sau hai release, MAIN 2 EA / 6.667 VND, QA 0 EA, COGS 0 VND, AR 20.000 VND. Mỗi case giữ chứng từ của Sales, kho nhận, Finance và QA. Browser còn kiểm lượng đếm sai, QA chưa được phân công, tranh chấp lượng trả cuối, chéo tenant và replay.

| Artifact | Giá trị |
| --- | --- |
| Browser fixture SHA-256 | `A0A47CB37C0089E173E64D58A71FFD26B2A4A4AED80B3A7EF8F0AABA67D4B26B` |
| JUnit SHA-256 | `4DC362B1497BA2AACF8EBA99BF55FBD202785879E04FB2EB772313CB31959757` |
| SQL result SHA-256 | `A89F567A42FEC45510FF0868CD3F561FCA9A46F84BEB2943DF172DA6A3720553` |
| API DLL SHA-256 | `4F24AB05C3524D462F04FBD1C3112DF13ED6677046BAB975551D538B0EBD5884` |

Tái chạy bằng `./scripts/Run-App02C1.ps1 -Increment DevF05 -Verify`. Kết quả nằm tại `TestResults/dev-f05/<run>/sql-result.json` và `web/test-results-app02c5/<run>/junit.xml`. Fixture sử dụng SQL LocalDB do nonce sở hữu. Kho với hàng thật, chấp nhận vật lý và tenant go-live chưa kiểm.
