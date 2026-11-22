# APP-02D.3 — Ma trận nghiệm thu DEV

**v0.1 · 22/11/2026 (Asia/Bangkok) · `OWNER_APPROVAL_PENDING / NOT_RUN`**

Theo [hợp đồng D.3](app-02d3-dev-contract.md). Chạy đủ **18 ca trên hai tenant**, tổng **36 lượt ca–tenant**. D3-16 gồm A→B và B→A. Chữ A/B trong cột quyền là loại quyền bán. Chữ A/B trong phạm vi tenant là hai DB khác nhau.

## Fixture đề xuất

- Windows `(localdb)\MSSQLLocalDB`, hai DB `Fmcg_App00_<nonce>_[AB]`. Bảo vệ tạo/migrate/ghi fixture/thu hồi theo instance, tên, marker sở hữu, nonce và persisted tenant binding. API/web loopback. Tạo ID, chứng cứ và chủ thể riêng theo tenant; các ca phá lỗi, cạnh tranh và số dư đặc biệt dùng trip riêng, có ảnh SQL mở và ảnh SQL sau xử lý.
- Đồng hồ cố định `2026-11-22T04:00:00Z`, ngày nghiệp vụ 22/11/2026 (Asia/Bangkok). Proposal: phiên bản giá D.3 B15 và B grant100 có hiệu lực `[03:00Z, 05:00Z)`, duyệt lúc `02:55Z`, đơn vị synthetic. A15, terms N=7 chốt lúc Confirm, due date 29/11/2026. Witness cash và kiểm đếm hợp lệ tại `03:55Z` và `03:58Z`, theo tenant/trip/Customer hoặc cashier thích hợp. Seed maker/checker, Sales writer, Warehouse writer, Cashier được gán, reviewer cash, các actor cùng role thiếu assignment và SQL reader. Sales và Cashier dùng hai danh tính khác nhau.
- Khởi tạo MAIN 100 EA/1.000, TRIP 0/0, stock pool allocated 0. Qua nguồn thật của C/D, reserve A40, B quota30, B grant100, nạp A40 + B10 + B20. Chọn B20 cho bán B20 và dùng phần dư B10 lúc trả xe. A giao 25 từ lô A40. Mọi trường hợp có giao dịch giữ `InvoiceEligible=false`. Khóa [oracle AC-03](../../business/reference-data/expected-results/golden-oracles.json), fixture và expected-results bằng SHA-256 trước lượt nghiệm thu. Dùng dữ liệu SQL chỉ đọc để lấy actual.

## Checkpoint AC-03 trên mỗi tenant

Các số dưới đây theo oracle đã khóa. So 13 trường (ID bước và 12 số), **91 ô/tenant, 182 ô tổng**, kèm so nguồn và các delta. Số dư cột tồn hiển thị lượng/giá trị.

| Bước | MAIN | TRIP | Đã bán/giao | COGS | A hold | B quota còn | AR A | AR B | Custody | Quỹ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | 100 / 1.000 | 0 / 0 | 0 | 0 | 40 | 30 | 0 | 0 | 0 | 0 |
| T1_LOAD | 30 / 300 | 70 / 700 | 0 | 0 | 40 | 30 | 0 | 0 | 0 | 0 |
| T2_REJECT | 30 / 300 | 70 / 700 | 0 | 0 | 40 | 30 | 0 | 0 | 0 | 0 |
| T3_B20 | 30 / 300 | 50 / 500 | 20 | 200 | 40 | 10 | 0 | 100 | 200 | 0 |
| T4_A25 | 30 / 300 | 25 / 250 | 45 | 450 | 15 | 10 | 375 | 100 | 200 | 0 |
| T5_HANDOVER | 30 / 300 | 25 / 250 | 45 | 450 | 15 | 10 | 375 | 100 | 0 | 200 |
| T6_RETURN | 55 / 550 | 0 / 0 | 45 | 450 | 15 | 0 | 375 | 100 | 0 | 200 |

Bổ sung so stock pool `onHand/allocated/free` tại T0 `100/70/30`, T1–T5 `30/0/30`, T6 `55/15/40`. B grant dùng 100 từ T3, còn 0, đã release 0. A right consumed 25, còn held 15, released 0; B right consumed 20, released 10. T6 trả A15/150 và B10/100 theo đúng dòng, giữ nguyên tổng AR 475, COGS450 và trạng thái chứng từ receipt/allocation.

## Ca bắt buộc

| ID | Thao tác | Kỳ vọng SQL độc lập |
| --- | --- | --- |
| D3-01 | Kiểm tạo mới, nâng cấp từ D.2 có dữ liệu legacy, C.3 và Mixed D.1/D.2; chạy lại migration nếu có, thử sai cấu hình scratch. | Migration áp một lần, dữ liệu lịch sử và provenance giữ nguyên. Sai instance/tên/marker/nonce/binding bị từ chối. D.3 giữ rào legacy, legacy unload từ chối manifest hiện đại. |
| D3-02 | Qua UI tạo T0 và nạp A40, B10, B20 qua luồng C.3/D.1 đến T1. | T0/T1 khớp oracle. Ba dòng nguồn và các cặp MAIN OUT/TRIP IN có lượng/giá trị chính xác. A và B không dùng chung right, B10 và B20 cùng B right hợp lệ. Không có sale/AR/COGS. |
| D3-03 | Thử B31 tại T2 với giá15 và witness cash465 hợp lệ, thử B11 từ lô B10 và chọn dòng A cho B. | Mốc T2 giữ nguyên T1. Các lệnh bị từ chối, không source hay issue/AR/custody. Đối chiếu status nghiệp vụ và allowlist đúng terminal command nếu có. |
| D3-04 | Qua UI bán B20 cash200 rồi giao A25, quan sát T3/T4. Thử cờ giá hoặc giá B profile thiếu/sai scope trên nhánh riêng. | B payable300, AR100, grant dùng100, COGS200. A payable/AR375, due 29/11, COGS thêm250. Price provenance đúng và không thay vì payload; D.2 baseline giá10 hồi quy riêng. |
| D3-05 | Từ T4 qua UI gửi offer200, thử offer vượt custody và offer thứ hai khi offer đầu còn pending. Ở hai scope riêng, thử offer15 sau B1/giá15 chỉ có bank pending15, và offer130 sau B6/giá15 đã thu cash110 và có CustomerCredit dư20. | Một `OFFERED` và một nguồn hợp lệ, 0 ledger effect cho offer. Custody200/quỹ0, AR375/100 giữ nguyên. Bank pending giữ custody0. Branch cash110 có custody110 và CustomerCredit20 từ cùng receipt, offer130 bị từ chối. Offer thứ hai bị chặn khi offer đầu đang chờ. |
| D3-06 | Cashier được gán dùng chứng cứ đếm200 từ fixture, nhận offer T5. | Một acceptance đúng offer, khác actor, proof duy nhất; hai leg `−200/+200`, custody0/quỹ200. Receipt, allocation, AR, CustomerCredit, advance và COGS giữ nguyên. |
| D3-07 | Sai amount 199/201, proof thiếu, tham chiếu pending chưa có trong kho VERIFIED, proof có giờ tương lai hoặc sai actor/trip, tự nhận dù có cả hai role, và tái dùng proof trên offer khác. | Từ chối từng biến thể, không đổi offer/protected rows. Chứng cứ đúng sau một lần từ chối vẫn có thể được dùng đúng một lần. |
| D3-08 | Warehouse trả xe T6 qua UI sau T5. So sánh nguồn nạp, issue, trả và group theo right. | TRIP0, MAIN55/550, A15/150 và B10/100 về đúng MAIN. B release10 duy nhất, A held15, pool55/15/40. COGS, receipt, AR và quỹ giữ nguyên. Trip `RECONCILING`, không có trạng thái closed. |
| D3-09 | Trip riêng A40, B quota30, nạp B5+B7. Bán B6 từ B7 và quyết toán custody90 trước khi trả. Biến thể bán hết B12 qua hai lô và quyết toán custody180. Biến thể thứ ba nạp A40 và để nguyên B30 chưa từng nạp. | Nhánh 1 trả A40 + B6, B release24 gồm 6 trên xe và 18 chưa nạp, MAIN94/940/allocated40/free54, COGS60. Nhánh 2 trả A40, release-only B18, MAIN88/880/allocated40/free48, COGS120. Nhánh 3 trả A40, release-only B30 theo source quota, MAIN100/1.000/allocated40/free60, COGS0. Cả ba TRIP0, một release mỗi B right và 0 bút toán vật lý cho phần chưa nạp. Grant tài chính chưa dùng vẫn còn100. |
| D3-10 | Thử trả khi custody còn, offer vẫn chờ, manifest/trip/kho không khớp, thiếu nguồn hoặc balance lượng/giá trị lệch. Dùng fault trước khi post A để giữ một bằng chứng C.4 thật ở `RECEIVED`. | Từ chối từng biến thể, không tạo unload/return/release hoặc đổi trip. Không trộn lô legacy với lô hiện đại. Bằng chứng `RECEIVED` và stock giữ nguyên cho đến khi kết quả C.4 được xử lý qua cùng command. Proof drift SQL ghi rõ phần cố ý làm lệch và bảo toàn phần còn lại. |
| D3-11 | Ba dòng trả cùng SKU, mỗi dòng 1 EA, giá trị còn lại đã đối chiếu là 1.0000 đơn vị synthetic. Giữ thứ tự nguồn ổn định giữa các lần đọc và replay. | Các cặp giá trị trả là 0.3333, 0.3333, 0.3334; tổng đúng 1.0000. Giá trị còn trên xe bằng 0. Ledger và SQL balances khớp từng dấu, không đổi sau replay. |
| D3-12 | Hai command cạnh tranh gửi offer từ cùng trip, rồi hai command nhận cùng offer/chứng cứ theo role đúng. | Một offer pending. Tại accept, đúng một thắng, một nguồn và hai leg, không chi vượt custody, không tái áp AR. |
| D3-13 | Từ T5 cho hai lệnh unload cạnh tranh. Nhánh riêng cho B1 từ B10 chạy đua với unload, P15 và cash15 đã xác minh. | Hai unload có đúng một bộ return/release. Nếu unload thắng B1 bị chặn và T6 chuẩn. Nếu B1 thắng, có TRIP24/240, COGS460, B còn9, custody15 và unload bị chặn. Hai nhánh giữ AR375/100 và quỹ200, không để dư âm hay có hiệu ứng nửa vời. |
| D3-14 | Fault SQL trước commit sau leg custody đầu tiên và sau cặp trả xe đầu tiên trước các cập nhật còn lại. Bỏ fault rồi retry cùng ID ở từng nhánh. | Xác nhận đã tới đúng điểm lỗi. SQL không còn command/source/leg nửa vời, protected rows khớp trước thử. Retry mỗi nhánh ghi một lần và ra T5/T6 tương ứng. |
| D3-15 | Làm mất ACK sau commit offer, accept và unload trong scope độc lập. Restart API, tra cứu và replay cùng ID, thử payload đổi và ID mới dùng source cũ. | Phục hồi đúng nguồn/kết quả. Replay giữ fingerprint SQL, payload đổi conflict, source cũ không ghi lại. Cho phép tra cứu outcome unload sau `RECONCILING` khi còn quyền. |
| D3-16 | Thay trip, offer, cashier proof, manifest/command ID bằng của peer tenant, theo A→B và B→A. | Từ chối hoặc không tìm thấy, không trả outcome/source bên kia. Hai protected fingerprints không đổi, ngoài terminal/audit được allowlist có định danh. |
| D3-17 | Trên UI thử offline và thu hồi role hoặc assignment riêng cho Sales, Cashier, Warehouse lúc phiên đang mở. Thử nạp/bán/giao/trả mới sau T6. | Offline không có POST nghiệp vụ hoặc queued write; quyền hiện hành được kiểm lại lúc ghi/đọc/replay. Các lệnh mới sau T6 bị chặn cả ở bước tiếp nhận bằng chứng C.4. Quyền A15 và credit theo nguồn giữ nguyên, tra cứu command đã commit vẫn theo quyền hiện hành. |
| D3-18 | Hai checkpoint độc lập AC-10/21 đã phân bổ, offer và thủ quỹ nhận 50.000. Đọc báo cáo theo trip trước offer, trước accept và sau accept. | AC-10: AR120.000 giữ nguyên, custody80.000→30.000, quỹ+50.000. AC-21: AR105.000, CustomerCredit25.000 và đã phân bổ35.000 giữ nguyên, custody60.000→10.000, quỹ+50.000. Offer giữ nguyên toàn bộ số dư. Advance mở 10.000 ở mỗi scope, có nguồn riêng và giữ nguyên; mỗi lần nhận có đúng hai leg và báo cáo khớp SQL. |

D3-18 đóng băng mốc AC-10 **sau** thu/phân bổ20.000 từ AR140.000 và custody60.000 ban đầu; mốc AC-21 **sau** hai phân bổ20.000 và15.000 từ receipt60.000 đã xác minh. Dùng fixture có receipt/AR/allocation và ledger đã phát sinh theo nguồn, với thước đo tiền chỉ dùng trong DEV synthetic. Phép thử chỉ nhận offer/accept và đối soát trên các checkpoint đó. Nghiệp vụ phân bổ mới, hoàn tiền và tạm ứng nằm ngoài phạm vi D.3.

## Bằng chứng trước khi ký

1. Sau phê duyệt, chuẩn bị runner, oracle SQL read-only, browser Chromium, database fixture và cơ chế fault/restart loopback. Đóng băng hash và danh sách expected trước khi chạy. Lưu phiên bản .NET, Node, LocalDB, run ID, source HEAD, Git tree của checkout CI, tên DB/tenant binding và clock. Artifact tránh token và bí mật.
2. Thực thi T0–T6 qua browser → API → LocalDB. Các ca fault và race đi qua API và SQL thật. So sánh expected/actual/diff có dấu, từ số dư vật lý, rights, stock pool, ledger theo từng source, AR/receipt/custody/quỹ, scope của chứng cứ và fingerprint chéo DB. Đối chiếu số lượng dòng và các phép cân bằng, cùng đơn vị và rounding. Giữ bộ comparator APP-01E.5 như hồi quy độc lập.
3. Chấp nhận khi đủ 36 lượt, 182 ô AC-03 và các phép so nguồn khớp, không bỏ ca. Chạy hồi quy C.1–C.6, D.1, D.2, APP-01D/E.5, .NET và browser/PWA; CI trên HEAD cuối. Kiểm tra thu hồi đúng hai DB nonce-owned. Owner ký sau khi xem SQL và CI.

Mọi ca hiện `NOT_RUN`. C-PREPROD `BLOCKED / NOT_VERIFIED`, nghiệm thu vật lý C-E2E `NOT_RUN` và PREPROD `FOUNDATION_ONLY / NO_GO`.
