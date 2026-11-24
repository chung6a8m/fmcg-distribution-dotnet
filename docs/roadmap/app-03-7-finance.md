# APP-03.7 — Bàn làm việc Kế toán/Finance

## Làm việc trong phiên DEV

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Start
```

| Danh tính DEV | Công việc |
| --- | --- |
| `accountant` | Mở **Hàng đợi kế toán**, chọn lần giao theo chuyến, khách và SKU. Tiếp nhận nguồn rồi ghi sổ. Nếu mất ACK, tra yêu cầu gốc theo lần giao, thao tác và mã gửi. Khi chưa có kết quả, gửi lại cùng mã và nội dung. |
| `reconciler` | Chạy đối soát với khoảng thời gian có múi giờ và mốc chốt dữ liệu. Tra hoặc gửi lại báo cáo theo cùng mã yêu cầu. Chọn báo cáo và hồ sơ để đóng sai lệch sau lần đối soát khớp. |
| `maker`, `checker` | Chọn hồ sơ sai lệch tiền hàng/công nợ để lập đề nghị và chọn đề nghị để duyệt. Đây là hai danh tính riêng. D.4 giữ bản ghi gốc và ghi một dòng điều chỉnh theo kỳ cho phép. |
| `finance` | Mở **Điều chỉnh giá vốn** để lập đề nghị cho lần giao kỳ đã khóa. Trong **Credit và hoàn tiền**, đối soát tiền vào, ghi credit và lập đề nghị chi theo nguồn. Tiếp tục dùng **Tạm ứng và hoàn ứng**, **Trả hàng** và **Quà khuyến mại** của .6. |
| `qa`, `refund-checker`, `cashier` | QA thực hiện phần kiểm tra nguồn kho của F06 và xác minh chứng từ F08. Finance checker độc lập duyệt chi. Thủ quỹ đối chiếu chứng từ đã xác minh và ghi thực chi. |

Trong profile `APP-03.7-v1`, danh sách ghi rõ **tiếp nhận**, **ghi sổ** và trạng thái **đối soát** riêng. Báo cáo D.4 dùng giá trị gốc lúc giao. Tiền xác minh và tất toán của F08 đi qua nguồn riêng; F06 ghi chênh lệch ở kỳ hiện hành và giữ giá vốn gốc trên lần giao. Trạng thái ghi sổ này thuộc đối tác DEV mô phỏng trên loopback.

## Phạm vi dữ liệu

- Profile .7 bổ sung D.4, F06, F08 và read model Finance vào profile .6. Resume nâng dữ liệu theo lịch nghiệp vụ sau khi xác minh hai database theo tenant, instance, tên, ownership và nonce. Phiên legacy .1b tiếp tục dùng A/F. Bootstrap bổ sung danh tính DEV, khoản quỹ hoàn tiền F08 100.000 VND mỗi tenant và SKU có lô `Z-REFUND-EA` không kèm quà. Quỹ F08 và các sổ tạm ứng, tiền khách, custody/fund của D.3 có nguồn riêng.
- F08 v1 dùng lần giao A/VND hoàn thành riêng một dòng, có xác minh và tất toán đủ tiền, với hồ sơ đã QA release. Danh sách hồ sơ phân biệt chờ QA, chờ tiền và nguồn ngoài phạm vi. Đơn có SKU tham gia chương trình quà và nguồn giao từng phần nằm ngoài nhánh này. Mở hồ sơ trả từ danh sách credit dùng cùng mã F05 và quyền hiện hành.
- F06 chỉ điều chỉnh nguồn gốc có dấu vết xuất lô. Nếu có nhiều lần QA release hoặc thiếu chứng cứ tồn kho còn khớp, màn hình nêu giới hạn của phần giá vốn đã hoàn. Server tiếp tục kiểm điều kiện tại thời điểm duyệt.
- `NextDay` đi từng ngày. Ngày DEV 01/12/2026 mở riêng để kiểm F06 qua kỳ khóa. Bootstrap khóa tháng 11, mở tháng 12 và giữ lịch sử. Ngày này dành cho Finance, với chính sách bán hàng và dữ liệu chuyến DEV được chuẩn bị đến hết 30/11. Đối soát nguồn ghi cùng thời điểm mô phỏng cần mốc chốt bao phủ nguồn, có thể thực hiện sau `NextDay`.

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyFinance
```

Runner nâng phiên .6 đã có lần giao lên .7. Một lần giao mới được tạo qua C.1–C.4 và theo cùng mã qua F05, F08, D.4 và F06. Chromium kiểm các vai, cách ly tenant, từ chối tự duyệt, mất ACK và tra lại đúng mã. Các truy vấn SQL độc lập đối chiếu hai tenant, quỹ/credit, tổng các chân đối tác trước và sau điều chỉnh và dấu vết nguồn qua kỳ khóa. Artifact nằm trong `TestResults/app03-7/<RunId>/`. Workflow Windows chạy cùng lệnh và tải artifact.

## Kết quả local ngày 24/11/2026

**DEV_VERIFIED_LOCAL** với Windows LocalDB, .NET 10.0.404, Node 26.13.1 và Chromium. Run `c42389ed50fe464d8ab2190344fe7ba3` chạy 3/3 chặng. Base `3c6ebf5d161c841a5030c86d9024507efc15877f`, tree mã nguồn, kiểm thử, script và workflow `8ea291971079fb9cc7ea42ef25a9a358a83cf481` trên base đó.

- D.4 có hai receipt và hai base posting. Mất ACK ở receipt và báo cáo đều tra lại đúng mã. Gửi lại giữ nguyên nguồn, đổi tham số cửa sổ cùng mã báo cáo bị từ chối. Điều chỉnh của hai actor riêng đưa đối soát về 2/2 nguồn khớp, chênh lệch 0, và đóng một hồ sơ. Tenant đối chứng không có receipt hoặc posting.
- F08 tất toán AR nguồn 20.000 VND về 0. Credit 10.000 VND giữ nguyên ở bước đề nghị, duyệt và xác minh chi. Bốn actor độc lập hoàn tất thực chi 4.000 VND, credit còn 6.000 VND và quỹ còn 96.000 VND. Tenant đối chứng giữ quỹ 100.000 VND. Hồ sơ của nguồn ngoài phạm vi được hiển thị trong danh sách.
- F06 giữ giá vốn lần giao gốc 6.000 VND và giá vốn hoàn tháng 11 là 3.000 VND. Finance đề nghị 6.200 VND, QA duyệt và ghi thêm 100 VND vào giá vốn phần hàng đã hoàn trong tháng 12. Digest của lần giao, công nợ, giá bán và dòng QA tháng 11 khớp nguyên trạng qua rollover và duyệt.

| Artifact | SHA-256 |
| --- | --- |
| `refund.xml` | `E4156F3184567FE214E908349A446FE80A53D336BF1A2DEE773D618C991FBBBC` |
| `accounting.xml` | `F3822F711CA9109477A52C8D5F8C3225CB08DBC9AEF9A867696D3C55D5FF9F9A` |
| `cost.xml` | `8AA71884FECE155F1E669B277C8345B6CC72059AEEA97F7672C53CF6AC743447` |
| `sql-review.json` | `A31F29F585E730E4D50372F90EAD220EDA92FDA9CA21DF09ED6E6BEB60E9D9A8` |

Hồi quy APP-03.1b/.2 qua trên profile .7 (run `3b3f1561b8cc4727a8349a7869b07623`). D.4 độc lập qua 36/36 ca theo tenant và 2/2 Chromium (run `481613c03b0448a3a6dd9850b3031499`). F06 và F08 độc lập qua Chromium và đối chiếu SQL (run `dba761ca2e9340c58cc7dd8af4b9f5ec` và `6e8c9cd56ab74846b2bcb0fac32feeb4`). Lượt F08 ghi một GET F05 bị hủy khi chuyển màn. Bộ .NET đạt 352/352, kiểm tra web đạt 23/23 và Playwright tổng đạt 58 bài, với một bài cài PWA standalone bỏ qua theo cấu hình local.

Bằng chứng thuộc phạm vi synthetic DEV trên LocalDB. Nghiệm thu .9, CI hosted và vận hành giữ mốc riêng.

## Sửa sau review PR #141

- Hàng đợi dùng một hồ sơ sai lệch cho cùng bút toán, dòng nguồn, hash và lý do. Mỗi báo cáo giữ kết quả lịch sử và trỏ đến hồ sơ chung. Hồ sơ trùng từ runtime cũ được nhóm khi đọc. Việc đóng cả hồ sơ trùng chỉ áp dụng khi báo cáo mới đáp ứng cùng cửa sổ đối soát.
- Đề nghị, duyệt và đóng hồ sơ giữ định danh nguồn để tra cứu hoặc gửi lại sau reload. Khi đối tác đã ghi bút toán trước khi khóa kỳ, API đối chiếu lại chứng cứ ghi sổ và khôi phục mã posting của ứng dụng. Kỳ đã khóa chặn ghi bút toán điều chỉnh mới.
- Dòng hàng đợi hiển thị mã báo cáo, khoảng thời gian và mốc AsOf riêng. Lần đối soát cũ được đánh dấu khi thông tin tiếp nhận hoặc ghi sổ thay đổi. Reconciler có thể mở báo cáo từ dòng nguồn.

Run `15946ed7585b4300815bf7a136a40485` đạt 5/5 chặng Chromium → API → SQL trên hai tenant LocalDB. Tree mã và kiểm thử `eb6ed1f867cf683551de6fcb22a21e2beebad729` trên base `a161d82a03d31328aeb6276da76249e9fba64e4c`.

- Hai báo cáo sai lệch giữ hai mốc AsOf và dùng một hồ sơ. Runner bổ sung một hồ sơ trùng theo cấu trúc cũ trong dữ liệu giả lập để kiểm nâng cấp. Hàng đợi vẫn chỉ hiện hồ sơ chung. Báo cáo khớp đóng cả hai bản ghi cùng cửa sổ.
- Đối tác ghi đúng một bút toán khi mất ACK. Sau Stop, NextDay và Resume qua ngày 01/12, mã posting trong ứng dụng khớp mã đối tác, và gửi lại đề nghị trả cùng mã gốc. Mất ACK khi đóng hồ sơ vẫn tra được đúng mã hồ sơ và báo cáo đóng sau reload.
- Hồi quy D.4 đạt 36/36 và Chromium 2/2 (run `1d47b897ed4946998b6bb8374502ed8f`). F06 (run `9eee53a894214a109a8b70ed55c7f0a0`) và F08 (run `fdc2e46b1ae84a8fa378615b8cb6f97a`) đều qua Chromium và đối chiếu SQL. F08 ghi một GET F05 bị hủy khi chuyển màn. .NET đạt 352/352, các kiểm tra web đạt 23/23, Playwright đạt 58 bài và bỏ qua một bài PWA standalone theo cấu hình local.

| Artifact | SHA-256 |
| --- | --- |
| `refund.xml` | `7212C7DB1FEA13233D61E5D6A7CDD9E5286267E615BE0E4117CFC8BA865FD1E7` |
| `accounting.xml` | `9F64B867C684060C81E4C18EE404808F8FFA9DC76CA1D6955554B1F27692AFE2` |
| `approval.xml` | `481D94426B8D11457998B818EF49A72393F1315CE2A4E23418685F3BCDA64F1E` |
| `recovery.xml` | `A045354F7276976CB584911C3BE53A5BBCC98A13C3CBB1EC55FFF8B53A914A99` |
| `cost.xml` | `09943824CFB2481ABA0613BD0D2704E73C6A679FA5D1616A238A3F78E2C28D89` |
| `sql-review.json` | `7949015C6B12E3251D29BCD38D562AC85B988EEEE066A4DAE30C02617D6136CB` |
| `app03-7-review-final.trx` | `8620349B9FD4EF1DF2593B158A07EC846A7E530001334DA4C9C195637A8F936B` |

## Sửa cửa sổ đối soát của hồ sơ lịch sử

Hồ sơ trùng từ runtime cũ tiếp tục xuất hiện cho người đối soát theo mã và báo cáo gốc của từng hồ sơ. Những hồ sơ của cùng dòng nguồn, hash, bút toán và lý do dùng bút toán điều chỉnh của hồ sơ chính. Maker chỉ chọn hồ sơ chính để lập điều chỉnh. Đóng hồ sơ vẫn yêu cầu báo cáo mới khớp với chính cửa sổ của hồ sơ đang đóng và có kết quả OK ở dòng nguồn đó.

Runner dùng các báo cáo tạo qua trình duyệt rồi bổ sung hai bản ghi hồ sơ theo cấu trúc cũ khi phiên đã dừng. Sau Resume, báo cáo mới cửa sổ 23–24/11 đóng hồ sơ chính và hồ sơ cùng cửa sổ. API từ chối dùng báo cáo đó để đóng hồ sơ cửa sổ 22–24/11. Hồ sơ 22–24/11 đóng với báo cáo của cửa sổ này và cùng bút toán điều chỉnh. SQL đối chiếu mã đóng, dòng nguồn, cửa sổ và digest các báo cáo lịch sử.

Run `cd65e64e54db467290c62cdeb0cfa910` đạt 5/5 chặng Chromium → API → SQL trên hai tenant LocalDB. Tree mã và kiểm thử `c5532254eecccd749ed0bfe9a2d49fe2a4f8b256` trên base `3f9de477e4b12443055b6f63be12202610f66c42`. SQL xác nhận 3/3 hồ sơ đóng với báo cáo đúng cửa sổ, một bút toán điều chỉnh và digest báo cáo lịch sử khớp trước và sau.

| Artifact | SHA-256 |
| --- | --- |
| `refund.xml` | `4DBD5B440DA540472235E07BBF8D31C08A76F275F5F26E47D89A54B9FF76CA78` |
| `accounting.xml` | `7E8937FC905D7F427BE22F5813454F0468A10A48EC4D54513B9FF33D633418DC` |
| `approval.xml` | `749594805E3EE366F8B1CA92E12A7BDCA4D84E99BA0C71E17B5A3F5E46F5261C` |
| `recovery.xml` | `3111B23400AE4DE83626551E241284AEC39A2F6FB48FD37F3EB35417E41179ED` |
| `cost.xml` | `556B79CFEA17ABFDBF3D3BAC32DA41003CBDFCC17570D237E56222452A735C0C` |
| `sql-review.json` | `F7286FF51E4FA10E79AC67836C208A62F857FA7D1AAC571AF0C4B01E4721803B` |
| `app03-7-review-cross-window.trx` | `D51CF401D141A6DD8156B25DCF65AED04236C3D825767749D979FE961F4A5CA8` |

Hồi quy local D.4 đạt 36/36 ca theo tenant và 2/2 Chromium (run `a2d06fa49b694c7c892ba987db7229c6`). F06 (run `9b02ffd831ca4af7b6ff1a3eb9258492`) và F08 (run `939b724fe8c7467f8898c8ae3af3d0c3`) đều qua Chromium và đối chiếu SQL. F08 đạt khi chạy lại sau lỗi chia sẻ file checkpoint của lượt đầu. Bộ .NET đạt 352/352. Các kiểm tra web đạt 23/23.
Playwright tổng đạt 58 bài. Một bài cài PWA standalone bỏ qua theo cấu hình local.
