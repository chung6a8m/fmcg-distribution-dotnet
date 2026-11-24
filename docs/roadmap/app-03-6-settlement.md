# APP-03.6 — Hiện trường bàn giao Thủ quỹ và Kho

## Làm việc trong phiên DEV

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Start
```

| Danh tính DEV | Công việc |
| --- | --- |
| `source` | Mở **Đối soát chuyến** từ **Chuyến và công việc**, kiểm tiền thực giữ và gửi đề nghị nộp tiền. Trong **Trả hàng**, chọn lần giao A và lô khách trả. Trong **Tạm ứng và hoàn ứng**, chọn khoản ứng của mình và ghi đề nghị hoàn. |
| `cashier` | Mở **Đối soát chuyến và nộp tiền**, kiểm tra mã đề nghị đang chờ và nhập số tiền đã kiểm đếm. Khi mã đề nghị thay đổi, nhập lại số tiền cho đề nghị mới. Dùng **Tạm ứng và hoàn ứng** khi nhận tiền hoàn ứng của nhân viên. |
| `receiver` | Sau khi chuyến hết tiền thực giữ và đề nghị chờ nhận, trả tồn xe. Dùng **Trả hàng** để nhận hàng khách trả vào cách ly và **Quà khuyến mại** để ghi thực nhận quà theo lần giao quà gốc. |
| `finance`, `qa` | Mở cùng hồ sơ trả hàng để duyệt và giải phóng hàng theo phân công. Kế toán có thể phát ứng, duyệt chi và duyệt đề nghị miễn thu hồi quà trong sổ nguồn tương ứng. |

Màn hình hiển thị VND cho các khoản của chuyến. Sổ tạm ứng F02 theo nhân viên và chứng từ, tách khỏi tiền khách, tiền thực giữ, quỹ đã nhận và công nợ. Khi mất phản hồi, dùng **Tra cứu yêu cầu** hoặc **Gửi lại cùng yêu cầu**. **Tải lại danh sách chuyến** cập nhật toàn bộ đề nghị chờ nhận và giữ chuyến đang xem. Khi kết nối lại, màn hình khóa lệnh mới cho đến khi tải thành công danh sách và số liệu chuyến đang chọn. Nếu tải lỗi, có thể tải lại danh sách hoặc tra cứu và gửi lại lệnh đã lưu.

## Tổ hợp DEV và nguồn dữ liệu

- `APP-03.6-v1` ghép C.1–C.5, D.1–D.3 và F01/F02/F03/F04/F05/F07 trên hai database LocalDB của cùng phiên. Start dùng profile này. Resume và NextDay nâng phiên dùng bootstrap .2–.5 sau khi xác minh instance, tên, tenant binding, ownership marker và nonce của cả hai database. Phiên seed .1b tiếp tục dùng A/F. Migration `202611240002` ghi nguồn lô trả xe và mốc cài bootstrap phân công. Bootstrap chỉ bổ sung danh tính, phân công, chương trình quà và tồn mở giả lập. Chương trình DEV cấp 1 quà cho mỗi 2 EA mua ròng của SKU R-05.
- Offer ghi đề nghị, giữ nguyên custody và fund. Accept dùng số tiền do thủ quỹ được phân công kiểm đếm, kiểm khớp đề nghị và người gửi độc lập, rồi ghi chứng cứ, trừ custody và tăng fund trong cùng transaction. D.3 giữ đúng tiền VND của A/F04 và B/F04 trong profile này.
- Trả tồn xe đọc nguồn nạp, giao và quyền giữ của chuyến. Hàng A trả về kho vẫn thuộc quyền giữ A. Lô trả gắn với dòng nạp và được hiển thị trong dấu vết lô của đơn. Quota B chưa tiêu thụ được giải phóng, kể cả quota chưa nạp. Xe dùng lại giữ lượng của chuyến khác. Warehouse return dùng cùng kho nguồn trong một chuyến và giữ chuyến ở `RECONCILING`. Chuyến đã giao hết vẫn có thể bàn giao để đối soát khi tiền đã nộp. Điều kiện đóng chuyến cuối nằm ở mốc sau.
- Hồ sơ khách trả gắn delivery và lô F05. Hàng nhận vào quarantine qua các bước kiểm đếm, duyệt và QA của đúng hồ sơ. F07 theo dõi quà cấp/nhận từ nguồn mua A đủ điều kiện và đề nghị miễn thu hồi. Danh mục và lệnh tạo/xác nhận đơn A cùng loại SKU quà khỏi hàng bán. F02 giữ riêng khoản ứng, chứng từ chi và hoàn ứng.
- Tổ hợp B kế thừa giá, quota, tiền mặt VND và SKU không quản lý lô của .5. B_NO_CHANNEL, ghi offline, F06, F08, D.4 và worker giữ ranh giới increment tương ứng. Nghiệm thu thiết bị và vận hành thuộc các mốc riêng.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifySettlement
```

Runner nâng phiên A/F đã có đơn và lần giao lên .5 rồi .6, đối chiếu nguồn trước/sau nâng và chạy Chromium qua các vai. SQL kiểm tra Accept, chân custody/fund, lượng theo lô, quyền A, quota B, sổ ứng, hồ sơ QA và quà ở tenant chính và tenant đối chứng. Sau Resume và NextDay, runner đọc lại cùng chứng từ và gửi lại cùng lệnh Accept. Các ngày kế tiếp kiểm xe dùng chung và giải phóng quota chưa nạp.

Artifact nằm trong `TestResults/app03-6/<RunId>/`. Workflow Windows chạy cùng lệnh và tải artifact.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** trên working tree của `codex/app-03-6` từ base `f39d5c4005433199ac78af63154e425061faf316`. Run `2c82c3f3e3f04589ab5d07ede0fd08a5` qua 1/1 kịch bản Chromium và đối chiếu SQL trên Windows LocalDB với hai tenant.

- Chuyến Mixed nhận 20.000 VND từ thủ quỹ độc lập. Offer có 0 hiệu ứng sổ. Accept có một chứng cứ kiểm đếm, giảm custody 20.000 VND và tăng fund 20.000 VND. Kiểm chặn kiểm đếm lệch, sai vai và truy cập chéo tenant. Sau mất ACK, reload, Stop, NextDay và Resume, lệnh Accept tra và replay về cùng mã nguồn.
- Kho trả 4 EA, gồm 1 EA có lô, giữ quyền A 3 EA tại kho và giải phóng quota B 1 EA. Chuyến Mixed và chuyến đã giao hết đều vào `RECONCILING`. Trên xe dùng chung, đối chiếu lượng tồn qua hai lệnh trả của hai chuyến lần lượt là 2 → 1 → 0 EA. Chuyến có quota chưa nạp giải phóng 1 EA và không ghi dòng trả vật lý.
- Hồ sơ trả khách qua đủ ba bước nhận vào cách ly, duyệt và QA. Sổ quà có một lần giao và một lần thực nhận trả. F02 phát 500.000 VND, nhận hoàn 200.000 VND và còn 300.000 VND, với AR và báo cáo tiền khách giữ nguyên sau các lệnh tạm ứng. AR của chuyến Mixed là 40.000 VND. Tenant đối chứng có 0 chứng từ trong các luồng được đối chiếu.

Hồi quy local qua APP-03.5 (run `3e4f62d87c2948948bd8efe0ed69af0b`) và D.3 độc lập (run `d37f5bbef1344dcfb7c20152b8f9a448`). Bộ .NET đạt 352/352, kiểm tra web đạt 23/23 và Playwright tổng đạt 56 bài. Một bài cài PWA standalone được bỏ qua theo cấu hình local.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `junit.xml` | `E487B6131254A2DD5328CF72D602903CFE438341DBCF3C62891CB5B74F8A9506` |
| `sql-review.json` | `B41863D101722773D3E18D2A8248A779C7B0496AEAED94D9709968DC717EE100` |
| `vehicle-reuse.json` | `8B931A45A65B0F7E43D2F8DE79B686BE1060AEC2878FC1775788A8425F17920B` |
| `quota-only.json` | `1458A3914E2DC0DE25EBCB82934D456834A2EF5A77002DC47F8AA7FB43416D0A` |

### Kiểm chứng sau review PR #140

- Run `ee34e5079ae84728a6ebf66d71511c70` qua Chromium và LocalDB hai tenant. Thủ quỹ giữ chuyến đang xem khi tải lại, thấy đề nghị của chuyến khác xuất hiện sau Offer và biến mất sau Accept. Bài hồi quy trình duyệt mô phỏng tải chậm, lỗi tải danh sách và lỗi tải chi tiết khi xác minh lại. Màn hình khóa lệnh mới, giữ mã lệnh chờ cho tra cứu và mở lại thao tác khi số liệu mới đã tải thành công.
- Gọi API tạo đơn trực tiếp bằng `GiftSkuId` thực của chương trình F07 trả `C2_SKU_INVALID`. Ảnh dòng SQL trước và sau ở hai tenant giữ nguyên cho bản nháp, đơn, quyền giữ, nguồn và sổ. Luồng SKU bán A và giao/nhận quà vẫn qua trong cùng kịch bản.

Hồi quy APP-03.2 và vòng đời .1b qua với profile .6 (run `9d418641ca95418aa18feb583896f94d`). Bài thử lại NextDay qua launcher dùng cùng profile với phiên đang kiểm, và ảnh dòng bao gồm phân công thủ quỹ cùng mốc bootstrap. D.3 độc lập qua (run `1f5eb3146bca47e59cb61a057bd8f49d`). Bộ .NET đạt 352/352. Playwright tổng đạt 57 bài, với một bài cài PWA standalone bỏ qua theo cấu hình local.

| Artifact trong thư mục run | SHA-256 |
| --- | --- |
| `junit.xml` | `384AB397B603B0A4F97AAB0C2F351C26065F334F2485E47205EE81ABB00961A9` |
| `gift-only-draft.json` | `187A4472F0DED9EF69FB509F1F4C331B9970777FF0799ACFCEA8EBE56A3BD6DE` |

### Kiểm chứng vòng review 2

Run `1e517805cc34439fa264759849ed8ab7` qua Chromium và LocalDB. Hai đề nghị 10.000 VND của cùng chuyến được kiểm ở hai phiên thao tác. Tải lại cùng mã giữ số đã nhập. Khi đề nghị sau thay đề nghị trước, màn hình hiện mã mới và để trống ô kiểm đếm. Bấm xác nhận khi ô trống không gửi Accept. Sau khi nhập lại, Accept ghi đúng mã đề nghị mới. Ca mất ACK và tra cứu lệnh gốc vẫn qua trong cùng run.

SHA-256 của `junit.xml` là `D22304386E463FD0780878F685CB0B0A163BEB66A8CDB541168A35BB5BFB2317`. Bộ kiểm tra web đạt 23/23, Playwright tổng đạt 57 bài với một bài cài PWA standalone bỏ qua theo cấu hình local.

APP-02C.6 qua lượt chạy lại local (run `a960755370a34bfdb6ba23355cbb9e71`, 15/15 ca theo tenant) sau khi lượt CI của HEAD trước bị timeout ở nhóm kiểm chứng nâng cao.
