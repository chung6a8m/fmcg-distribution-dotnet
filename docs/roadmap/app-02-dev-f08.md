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

| F08-05 · quỹ hoàn chi | Từ quỹ 80.000 VND, duyệt hai khoản độc lập 60.000 VND từ hai credit. Chỉ một khoản giữ quỹ và được ghi thực chi. Khoản kia thiếu quỹ khi cạnh tranh và sau khi quỹ còn 20.000 VND. Các lệnh bị từ chối có 0 nguồn và 0 hiệu ứng ledger. Tổng credit còn 128.800 VND. |

UI `/sales/refunds`, endpoint synthetic DEV, LocalDB nonce-owned và ledger AR, customer credit, quỹ thử.

## Biên nhận DEV

Ngày 23/11/2026, Chromium 1/1 và SQL 9/9 checkpoint khớp trong run `c5d20c815cef4d92a9f7f3c4e0aba843` tại source `bf55edc427513d6bcb6f12cab27696867ec82d9d`, tree `a4cd22894aafb825b34e7a67230cf35b3cdcb56d`. .NET Release 351/351. Chứng từ được verifier ghi trước khi Finance tất toán AR 208.800 → 0. Lần trả đầu tiên chỉ ghi credit sau khi QA release và đã tất toán.

| Lần trả | Net | Thuế | Gross | Discount |
| --- | ---: | ---: | ---: | ---: |
| 1 | 64.444 | 5.156 | 69.600 | 2.222 |
| 2 | 64.445 | 5.155 | 69.600 | 2.223 |
| 3 | 64.444 | 5.156 | 69.600 | 2.222 |
| Tổng | 193.333 | 15.467 | 208.800 | 6.667 |

Credit đầu giữ 69.600 VND qua đề nghị, duyệt và xác minh chứng từ thực chi. Hai lệnh cạnh tranh chỉ ghi một chi 20.000 VND. Credit đầu còn 49.600, quỹ còn 80.000, credit tổng sau ba case còn 188.800. Sai lệch oracle bằng 0. Snapshot của nguồn giao giữ nguyên; B có zero effect. Lượt đầu bị lỗi collation giữa role của chương trình và grant hiện hành; migration đã dùng cùng kiểu/collation. Lượt đạt ghi một GET bị hủy khi browser chuyển trang trong workspace F05; các bước ghi nghiệp vụ và SQL đều đạt. Cạn quỹ qua hai khoản chi đã duyệt khác nhau chưa chạy. Nguồn giao từng phần của dòng đơn, đơn có nghĩa vụ quà, áp credit cho hóa đơn mới, ngân hàng và thuế thật ngoài phạm vi fixture.

| Artifact | SHA-256 |
| --- | --- |
| Locked AC-19 oracle file | `6850EA6F4C6AF4F4CDD65A4506BA7035D0A7771536FB54FD4F4809C918FB90E7` |
| Browser fixture v1 | `B0FFF06AD0ADE4E364927ACD777447C87DCECF8637EA410462CB9905E17B8234` |
| JUnit | `1E7B0940DF7AD32B7EADC8366C1173007955B609FDBF85B4F87F6E41E1B0C447` |
| SQL result | `88502E695DEA86875AE7B5633B054F2C15675C8D216076948DA02A08D2590E20` |
| API DLL | `8E8E3669FEE96BC098BE6C9493CE70947F6124D0B6F3815519B5FBAE0E72F7E8` |

Tái chạy `./scripts/Run-App02C1.ps1 -Increment DevF08 -Verify` trên LocalDB scratch do nonce sở hữu. SQL tại `TestResults/dev-f08/<run>/sql-result.json`, browser tại `web/test-results-app02c5/<run>/junit.xml`.
