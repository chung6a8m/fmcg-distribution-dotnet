# APP-03.2 — Dữ liệu đầu ngày và chuyến ngày kế tiếp

## Khởi chạy

Trên Windows, dùng .NET 10.0.404, Node 26 LTS, LocalDB `MSSQLLocalDB` và cài dependency bằng `npm --prefix web ci`.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1
```

Lưu `RunId` từ terminal. Mở URL `/sales` và dùng mã DEV `source` theo [hướng dẫn phiên](app-03-1b-dev-session.md). Trong hồ sơ khách hàng APP-03 DEV, chọn địa chỉ đã đăng ký để tạo một lựa chọn giao hàng cho mỗi đơn. Màn đơn hiển thị các chuyến của ngày đang chạy. Ngày 23/11 có `DEV-20261123-A` và `DEV-20261123-B`.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Stop -RunId $runId
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action NextDay -RunId $runId -BusinessDate 2026-11-24
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Resume -RunId $runId
```

`NextDay` yêu cầu phiên đã dừng và khóa độc quyền theo run ID. Chỉ định ngày hiện tại để thử lại cùng yêu cầu, hoặc ngày kế tiếp liền kề để tiến ngày. Ngày mới có một chuyến dùng xe A. Nếu LocalDB bị gián đoạn sau khi manifest ghi ngày đích, `NextDay` cùng ngày hoặc `Resume` hoàn tất chuẩn bị cả hai tenant trước khi API phục vụ lại. `Resume` giữ nguyên ngày đã chọn và dùng lại URL trước đó. Các chuyến, phân công và chứng từ cũ được giữ lại. Tạo nháp và xác nhận lần đầu đều yêu cầu chuyến của ngày hiện tại. Đơn đã xác nhận tiếp tục theo giá và terms đã chốt; C.4 ghi ngày giao thực tế theo clock của phiên. Tra cứu và replay nguồn cũ vẫn theo grant, phân công và hợp đồng của từng lệnh.

Các phiên .1b đã tạo với seed `APP-03-C2-F05-v1` tiếp tục dùng Stop/Resume/Remove với clock và dữ liệu cũ. `NextDay` áp dụng cho phiên Start mới có seed APP-03.2.

## Hợp đồng dữ liệu

| Phạm vi | Dữ liệu/giả định |
| --- | --- |
| Bootstrap `APP-03.2-VND-v1` | Mỗi tenant có các ID độc lập cho một khách hàng, một điểm bán, một địa chỉ đăng ký, một SKU `R-05` (EA, hệ số 1), một lô `RET-ORIGIN`, kho `C2-MAIN`, hai xe và khu QA. Chỉ tạo master, receipt C.1 bắt buộc cho revision địa chỉ, quyền/phân công mẫu, opening và policy; các lệnh C/F tạo chứng từ. |
| Opening | MAIN có 6 EA và giá vốn 20.001 VND trên cùng một lô. Hai xe và khu QA ban đầu bằng 0. Mỗi kho/SKU/lô có một opening tương ứng, dùng chung giữa các chuyến trong tenant. Marker bootstrap và opening nằm trong cùng giao dịch SQL; gọi lại seed giữ nguyên các dòng đã ghi. |
| Clock và kỳ | 09:00 ICT (02:00 UTC) từ 23 đến 30/11/2026. Lô có hạn đến hết 30/11. Kịch bản nằm trong một tháng nghiệp vụ; đóng/khóa kỳ và chuyển tháng thuộc phạm vi F06, ngoài profile này. Các chuyến mới được chuẩn bị bằng giao dịch theo tenant, với mã ổn định theo ngày và xe. |
| Policy | V1 dùng ngày 23/11 với 10.000 VND/EA và terms 7 ngày. V2 có hiệu lực lúc 00:00 ICT ngày 24/11, dùng 12.000 VND/EA và terms 10 ngày, hết hiệu lực lúc 00:00 ICT ngày 01/12. Hai phiên bản có VAT và chiết khấu bằng 0, hạn mức 100.000 VND, maker/checker riêng. Giá, terms, đơn vị và lô đã chốt theo nguồn giữ nguyên khi đổi ngày. |
| Đơn vị và cấp dữ liệu | Giá trị F04, AR và giá vốn dùng VND. AR theo nguồn giao hàng; xuất lô theo delivery/lô; tồn vật lý theo tenant/kho hoặc xe/SKU, lô giữ chi tiết riêng. Hai chuyến cùng tham chiếu tồn MAIN, nên không cộng tồn MAIN theo chuyến. `invoiceEligible=false`. |
| Profile | A cùng F01/F03/F04/F05. B/Mixed, D và đối tác D.4, quỹ, tạm ứng, F06–F08 và cộng gộp với các nguồn `SYNTHETIC_UNITLESS` nằm ngoài profile. |

## Kịch bản và kết quả dự kiến

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Verify
```

Runner gửi lệnh qua Vite → API và đối chiếu SQL trên cặp LocalDB sở hữu theo nonce. Mỗi chuyến của tenant A dùng luồng chọn địa chỉ → nháp → confirm → reserve → load → giao 2 EA của cùng SKU/lô.

| Chuyến | Ngày giao | Policy | AR mới (VND) | Giá vốn (VND) | Đến hạn |
| --- | --- | --- | ---: | ---: | --- |
| DEV-20261123-A | 23/11 | 1 | 20.000 | 6.667 | 30/11 |
| DEV-20261123-B | 23/11 | 1 | 20.000 | 6.667 | 30/11 |
| DEV-20261124-A | 24/11 | 2 | 24.000 | 6.667 | 04/12 |

Dự kiến tổng AR của tenant A là 64.000 VND, MAIN còn 0 EA và 0 VND. Tenant B có ba chuyến, MAIN còn 6 EA và 20.001 VND, không có giao hàng hoặc AR. So khớp row image sau khi chạy lại seed, sau khi chuẩn bị ngày mới và khi lặp `NextDay`. Runner mô phỏng lỗi chuẩn bị tenant B sau khi tenant A đã commit, kiểm tra Resume tiếp tục đúng ngày, từ chối tạo và xác nhận nháp trên chuyến cũ, và replay được giao hàng ngày trước. Các ca lifecycle .1b gồm mất ACK, thu hồi quyền và Remove có kiểm tra ownership cũng chạy lại.

Bằng chứng nằm tại `TestResults/app03-2/<RunId>/session-review.json`.
