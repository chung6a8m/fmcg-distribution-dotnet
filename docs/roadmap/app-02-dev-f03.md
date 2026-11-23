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

## Chạy và biên bản DEV

`npm --prefix web ci` rồi `./scripts/Run-App02C1.ps1 -Increment DevF03`. Mở `/sales` từ launcher, nhập mã DEV và dùng Đơn A, Nạp hàng, Giao hàng. Chọn BOX hoặc EA cho C2-SKU. Hai màn hình thao tác kho hiển thị lô đủ điều kiện và hạn dùng. Kiểm bằng cùng lệnh với `-Verify`.

Windows LocalDB + Chromium run `cc032f81efc742c5ae6b32f8e74cc9ea` trên source `a8f42da06439ba87c85bf79983e8131459fdfd91`, tree `81a2e64a1dfbd7eb1f81aa8f84569f34efdfdfe1`. Chromium 1/1. SQL 24/24 giá trị số khớp, 6/6 dòng trail giữ nguồn, 8 opening và 6 dòng tồn trước/sau khớp. V1 file browser SHA-256 `83D332B9D090D300CB8071A97EF9B6C89D2949B46969981BD42356611DC726A6`; SQL receipt SHA-256 `717D0E1B902762C993A4D9FA0F97384D542B07D0412854CDF64ECB013EA95303`.

| Ca | Kết quả quan sát trên Windows LocalDB | Trạng thái |
| --- | --- | --- |
| F03-01 · AC-05 | 0.5 BOX bị từ chối. Draft, AOrderLine và nguồn Confirm giữ 2 BOX × 24 = 48 EA. Diff 0. | PASS |
| F03-02 · AC-05 | Giữ EARLY30/LATE18. QA bị từ chối. EXPIRED/QA-HELD có 0 dòng giữ, nạp và giao. Diff 0. | PASS |
| F03-03 · AC-05 | Hai lần nạp 30/18. Từ chối giao LATE trước EARLY. Hai lần giao 30/5. Replay không thêm hàng. MAIN52, TRUCK13, QA40; AR525, COGS350, invoiceEligible=false. Diff 0. | PASS |
| F03-04 · AC-05 | Tenant B có stock opening riêng, 0 effect. Race cùng SKU chưa chạy riêng. Hồi quy Release 346/346. | PASS cho tenant B; NOT_RUN cho race mới |

`TestResults/dev-f03/<run-id>/sql-result.json` chứa expected/actual/diff và nguồn, JUnit tại `web/test-results-app02c5/<run-id>/junit.xml`. Workflow APP-00A lưu cả hai. Hồi quy dùng `dotnet test --solution FmcgDms.slnx -c Release -- --report-trx`. Lượt browser đầu dừng do locator label dù combobox đã render. Ca sau dùng role và tên từ giao diện. Review bổ sung ca giao lô muộn giữa hai manifest và tách lượng khả dụng khỏi lượng còn giữ.

Phạm vi A online synthetic, không có thay đổi EV thực tế. Chưa thực thi luồng nhận trả, release QA hoặc mode B lotted trong profile này.
