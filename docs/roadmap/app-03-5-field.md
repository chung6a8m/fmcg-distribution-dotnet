# APP-03.5 — Kho bàn giao Hiện trường

## Làm việc trong phiên DEV

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Start
```

| Vai trò | Thao tác |
| --- | --- |
| Sales A | Dùng mã `source` để tạo và Confirm đơn. Chọn chuyến sẽ bàn giao cho Kho. |
| Kho | Dùng mã `receiver`. Nạp đơn A theo quy trình .4. Trong **Quota và nạp hàng B**, chọn cùng chuyến, cấp quota theo EA và nạp từ quota đã được ghi. |
| Hiện trường | Dùng mã `source`. Mở **Chuyến và công việc**, chọn chuyến được phân công rồi mở **Giao A** hoặc **Bán B**. Trong B, chọn dòng đã nạp, khách hàng, ShipTo đã chốt và chứng cứ tiền mặt DEV. Xem trước số tiền VND rồi ghi giao dịch. |
| Viếng thăm | Chọn khách hàng được phân công, ghi lý do và hoàn tất với **Không có đơn**. |

Nếu mạng ngắt trong lúc ghi, dùng **Tra cứu yêu cầu** hoặc gửi lại đúng yêu cầu đã lưu. Màn hình A truy xuất nguồn giao và AR đã ghi. Màn hình B hiển thị mã nguồn và mã lệnh gần nhất trên chuyến theo quyền hiện hành, kể cả khi đăng nhập lại. Sau **NextDay**, chuyến B ngày trước tiếp tục hỗ trợ đọc và phục hồi lệnh cũ. Ghi B mới chỉ mở trên ngày nghiệp vụ hiện tại.

Khi chuyến trong liên kết hết khả dụng, màn hình yêu cầu chọn chuyến khác. **Mở lại kết quả** dùng kết quả của chuyến đang xem. Yêu cầu mất ACK có nhãn chuyến nguồn, và tra cứu yêu cầu của chuyến khác giữ nguyên chuyến đang xem.

## Tổ hợp và quy tắc DEV

| Tổ hợp | Hợp đồng |
| --- | --- |
| A | Giữ C.1–C.5/F03/F04, gồm ShipTo, FEFO và giá đã chốt. Giao từng phần cập nhật cùng đơn, quyền giữ và nguồn nạp. |
| B | D.1 quota/nạp, D.2 bán và ghi biên nhận tiền mặt đã xác minh một lần. SKU không quản lý lô, EA nguyên, giá kế hoạch F04 có hiệu lực do người độc lập duyệt, không chiết khấu và thuế suất fixture 0%. Tiền mặt VND đúng bằng phải trả. Snapshot B giữ plan, quote, đơn giá và tiền thuế trên nguồn giao. |
| Mixed | Các dòng A và B có quyền giữ riêng, dùng chung nguồn tồn, location và sổ trong một tenant. Có thể giao A từng phần trước và sau khi bán B trên cùng chuyến. |

`APP-03.5-v1` yêu cầu workspace/đồng hồ APP-03, runtime C.1–C.5, D.1/D.2 và schema F01/F03/F04/F05. Cơ sở C.5 đã chứa migration D.1/D.2. Migration `202611240001` chỉ bổ sung snapshot giá B trong LocalDB có marker/nonce. Resume hoặc NextDay nâng phiên ngày nghiệp vụ .2–.4 sau khi xác minh cả hai database. Manifest cập nhật profile sau khi chuẩn bị xong cả hai tenant. Phiên .1b dùng seed cũ tiếp tục chạy A/F.

B có lô, ngân hàng chờ xác minh, B bán chịu, CustomerCredit, tiền dư, D.3/D.4 và phát hành hóa đơn nằm ngoài tổ hợp này. Luồng B độc lập APP-02D tiếp tục chạy theo profile và fixture tương ứng. Giao diện Field dùng controls và theme hiện có, bố cục một cột với vùng chạm 48 CSS px. Thiết bị thật và vận hành vẫn theo các mốc nghiệm thu riêng.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyField
```

Runner mở một phiên A/F tiền nhiệm, dừng và nâng hai tenant lên profile Field. Chromium kiểm Kho → Hiện trường trên chuyến Mixed và chuyến chỉ bán B. Hai lần giao A đi trước và sau một lần bán B trên cùng SKU và xe. Kịch bản kiểm lệnh mất ACK, reload, tra cứu sau khi đăng nhập lại, các nhánh giá không hợp lệ, chặn SKU có lô và đọc/ghi chéo tenant. Sau Stop, NextDay và Resume, API kiểm cùng mã nguồn bán và từ chối ghi B mới vào ngày cũ. SQL đối chiếu chứng từ, quota, tồn xe/kho, giá VND, AR, tiền thu và visit không có đơn.

Artifact nằm trong `TestResults/app03-5/<RunId>/`. Workflow Windows chạy `VerifyField` và tải artifact.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** trên working tree của `codex/app-03-5` từ base `ec3ba3db1069bd40c783ba7b19a53a37a87058ec`. Windows LocalDB, .NET 10.0.404, Node 26.13.1, Playwright Chromium.

- Run `32ad09a5e9bb42afb5195d6a4c585f15` qua 1/1 browser và đối chiếu SQL trên hai tenant. Chuyến Mixed giao A hai lần, mỗi lần 1 EA, với lần bán B 2 EA ở giữa. Chuyến chỉ B bán thêm 1 EA. Tồn cuối của SKU chung là 1 EA ở kho, 3 EA trên xe Mixed và 1 EA trên xe B.
- SQL ghi đúng hai biên nhận B, tổng custody B 30.000 VND, công nợ A còn 20.000 VND và một visit `NO_ORDER`. Tenant đối chứng có 0 chứng từ và còn đủ tồn mở. Lệnh bị từ chối không có nguồn phát sinh. Trình duyệt xác minh mất ACK ở cả A và B, khôi phục B sau đăng nhập lại, focus bàn phím, màn hình 320 CSS px và nút ghi tối thiểu 48×48 CSS px khi bật/tắt mạng.
- Phiên tiền nhiệm được nâng lên profile Field. API từ chối tổ hợp thiếu D.2 và tổ hợp bật D.3. Sau Stop, NextDay và Resume, cùng yêu cầu bán B trả lại nguồn cũ, chuyến ngày trước giữ lịch sử, từ chối giao dịch B mới và chuyến hiện hành đứng đầu danh sách.
- Hồi quy APP-03.1a, vòng đời .1b/.2, workspace .3, bàn giao .4 và APP-02D.1/D.2 đều qua. Bộ .NET đạt 352/352 với TRX. Web build và 23/23 kiểm tra unit, dependency và PWA build đều qua. Playwright web đạt 55 bài, với một bài cài PWA standalone bỏ qua theo cấu hình local.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `sql-review.json` | `1597E86E83226932D2B19C35E3247B1ABCB35AFB6CD3A31EBD1FCBC9EA46B0A9` |
| `junit.xml` | `6E530CC9355EFB302B2C93151D22614AD51515FDC5E3C598CA473A86B135F89E` |

### Kiểm chứng sau review PR #139

- Run `96732f37d5df4e788b552a5cc91353da` qua Chromium và đối chiếu SQL. Liên kết đến chuyến ngoài danh mục giữ nguyên URL và để trống chuyến bán. Màn công việc chỉ đưa liên kết B cho chuyến có trong danh mục D.2. Khi đổi chuyến rồi tải lại, kết quả B vẫn gắn với chuyến nguồn.
- Trước khi nâng phiên, Sales và Kho ghi đơn A, giữ, nạp và giao 2 EA có lô. SQL đối chiếu ảnh dòng ở hai tenant trước và sau khi nâng, gồm mã nguồn, quyền giữ, tồn, giá và AR 20.000 VND. Tra cứu và gửi lại cùng lệnh giao trả nguồn cũ. Ảnh dòng giữ nguyên sau replay.
- Sau NextDay, API từ chối cả ba lệnh ghi mới gồm bán B, cấp quota và nạp B trên chuyến ngày cũ. Ảnh dòng nghiệp vụ ở hai tenant giữ nguyên. Với chuyến đã đóng trong fixture, danh mục B vẫn giữ lịch sử giao dịch và chỉ cho đọc.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `sql-review.json` | `A73D2F32EC40503FCA1E8048AA7AD6FBFED923651CA25BD7CDC005BEE8594EB8` |
| `junit.xml` | `85264268611F241DC24E3FC743375B4DD8CB5C935E87D99AE372D16F4E7F84BB` |
