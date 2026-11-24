# APP-03.8 — Tổng quan Chủ NPP

## Mở báo cáo trong phiên DEV

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Start
```

Mở URL do launcher in ra. Nhập `owner` tại launcher để sao chép mã tenant A, hoặc `peer-owner` cho tenant B. Dán mã vào trường **Mã truy cập DEV** và mở **Tổng quan Chủ NPP**. Bấm giá trị của từng chỉ số để xem dòng nguồn, mã hồ sơ hoặc lần giao liên quan. Có thể mở chi tiết bằng Enter và đóng để quay về chỉ số đã chọn.

Báo cáo chỉ đọc trong profile tích hợp `APP-03.7-v1`. Migration `202611240006` thêm quyền `owner` vào danh mục grant. `Resume` áp dụng migration và cấp grant bootstrap một lần ở từng database đã xác minh. Grant đã thu hồi giữ nguyên trạng thái qua bootstrap. Mỗi lần tải báo cáo đều kiểm vai đã ký và grant hiện hành trong tenant. Các quyền tác nghiệp vẫn theo hợp đồng của từng command.

## Cách đọc số liệu

Báo cáo là ảnh chụp hiện trạng tích lũy của tenant. **Đọc lúc** là thời điểm SQL phục vụ lần đọc. **Mốc nghiệp vụ** là clock của phiên dùng cho hạn lô và tuổi nợ. Chỉ số kế toán dùng trạng thái đối soát D.4 đã ghi ở từng nguồn. Chọn **Tải lại số liệu** để lấy ảnh chụp mới.

Mỗi dòng chỉ số ghi phạm vi, đơn vị và số nguồn có giá trị trên tổng số nguồn xác định được. Khi thiếu nguồn để lập mẫu số hoặc một phần nguồn chưa có giá trị, báo cáo ghi **Chưa đủ dữ liệu**. Số 0 từ một nguồn đã có, như khoản phải thu đã tất toán hay vị trí có số dư vật lý bằng 0, vẫn hiện rõ.

| Chỉ số | Nguồn và cấp dữ liệu |
| --- | --- |
| Đơn và chuyến | Nháp C.2, trạng thái AOrder và Trip. Mỗi đơn hoặc chuyến một dòng. |
| MAIN, xe và QA | PhysicalStockBalance theo vị trí và SKU. Giá trị trong profile bootstrap là VND. Dòng lượng dùng đơn vị cơ sở của SKU. Tồn MAIN không đi qua danh sách chuyến. |
| TRIP | Mẫu số gồm quyền giữ, manifest và hiệu ứng vật lý theo chuyến và SKU. Nguồn chưa nạp có lượng và giá trị bằng 0. Nguồn đã nạp cần khớp manifest, hiệu ứng và số dư theo xe/SKU. Phân bổ giá trị theo chênh lệch giữa các mốc lũy kế đã làm tròn, theo thứ tự ngày và mã chuyến. Tổng giá trị phân bổ khớp với giá trị xe khi lượng đối chiếu đủ. |
| Lô và quyền | Lô theo vị trí, SKU và hạn dùng từ opening, nạp, xuất, trả xe và các bước F05. Quyền giữ A và quota B theo StockRight, độc lập với số lượng vật lý. |
| AR và giá vốn | AR theo ReceivableOpenItem và lần giao A/B, dùng snapshot F04 hoặc B pricing để xác định đơn vị. C.4 đang chờ kết quả nằm trong độ phủ AR A. Phần đã biết đối chiếu số phải thu gốc và số tiền đã phân bổ hoặc tất toán F08. Nguồn thiếu snapshot tiền nằm trong nhóm `SYNTHETIC_UNITLESS`. Giá vốn là tổng hiệu ứng giao, trả QA và chênh lệch F06. |
| Tiền khách và quỹ | Hiệu ứng credit tiền khách, custody và fund theo hợp đồng D.3 của profile VND. Credit trả hàng, thực chi và số dư quỹ hoàn tiền thuộc F08. |
| Tạm ứng | Sổ F02 theo chứng từ và nhân viên, lấy tiền đã cấp trừ khoản chi được duyệt và tiền hoàn đã nhận. |
| Công việc chờ | Nháp C.2, đề nghị bàn giao tiền D.3, F02/F05/F06/F08 và hàng đợi hoặc hồ sơ chênh lệch D.4. Mỗi nhóm có mẫu số riêng. Khoản chi F02 chờ duyệt dùng phần tiền còn lại của từng đề nghị sau các lần duyệt được liên kết. |

## Kiểm chứng

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyFinance
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifySettlement
dotnet test --solution FmcgDms.slnx -- --report-trx
```

Runner dùng cùng phiên .7 với hai LocalDB synthetic được bảo vệ bằng nonce. Hai chuyến cùng SKU kiểm tồn MAIN và TRIP. Chromium mở báo cáo bằng grant Owner, mở chi tiết F08/F02, kiểm focus và bố cục hẹp. SQL đối chiếu tiền, giá vốn, tồn và dữ liệu tenant đối chứng. Runner kiểm truy cập thiếu grant, Owner bị từ chối khi ghi F02, thu hồi grant và ảnh dòng trước/sau lần đọc. Kết quả nằm trong `TestResults/app03-7/<run-id>/owner-overview.xml` và `owner-review.json`.

`VerifySettlement` nâng phiên .6 đã ghi bán B và nhận bàn giao tiền lên schema báo cáo. Oracle lấy chứng từ tiền mặt, chấp nhận bàn giao và khoản phải thu B để đối chiếu chỉ số Owner. Kết quả nằm trong `TestResults/app03-6/<run-id>/owner-settlement-review.json`.

Bằng chứng thuộc phạm vi synthetic DEV. Kiểm chứng toàn hành trình APP-03.9 và nghiệm thu vận hành có mốc riêng.

## Kết quả local ngày 24/11/2026

Môi trường Windows LocalDB `MSSQLLocalDB`, .NET SDK `10.0.404`, Node `26.13.1` và Chromium qua Playwright. Source runtime là tree `aaf3710f28137859784a0c214015f0c58ecdd61b` trên baseline `3b483bb0b0fd6a89ba0d48c36c757e412822e764`, trước phần cập nhật kết quả tài liệu.

- `VerifyFinance` run `5332666b10f24c03962341a2d9557184` đạt 6/6 bài Chromium và 16/16 đối chiếu SQL của báo cáo trên hai tenant. Tenant A có 27 EA và 62.767 VND tại MAIN, với một dòng MAIN cho `R-05` và hai dòng TRIP riêng. Dư nợ A 20.000 VND, giá vốn thuần 9.767 VND, tạm ứng 300.000 VND, credit F08 còn 6.000 VND và quỹ hoàn tiền còn 96.000 VND. Tenant B giữ 32 EA và 79.001 VND tại MAIN. Các lần đọc giữ nguyên ảnh dòng và thu hồi grant chặn lần đọc tiếp theo.
- `owner-overview.xml` SHA-256 `5D6384E9C3782BE30D7AEEB9C7CCA8C9B8FDDCD37BC5BEC1F4A899ED4A8EE842`. `owner-review.json` SHA-256 `3BC88FF42B6804A666D5070BFEA1E1F187758D02876D51D0E613D56A5099DDF3`.
- `VerifySettlement` run `116a805a830041c8afd4ee2057417a50` đạt 1/1 bài Chromium và 3/3 đối chiếu số liệu Owner sau khi nối dữ liệu .6 vào báo cáo. Tiền ngoài thị trường còn 0 VND sau bàn giao, fund tích lũy 40.000 VND và AR B đã tất toán về 0 VND. `owner-settlement-review.json` SHA-256 `DC35427BD1E1D869B228D8552B65A48B9A4B910DF659989BAF122A4C87EEA920`.
- .NET đạt 352/352. `FmcgDms.Api.Tests_net10.0_x64.trx` SHA-256 `FCCE89AB9512FB1D2AEB78C208B39A53D5512031D8178C83C3C249B117B88AB9`. Web build, web tests 15/15, kiểm PWA 5/5, dependency 3/3 và `Verify-Business.ps1` đều qua.

## Kiểm chứng sau review PR #142

Source runtime tree `ce886ef003e12535f308715e6223d2726bd8d642` trên commit `430696a93707775e512f6aa2102db08fe7226b10`, trước khi bổ sung biên nhận.

- `VerifyFinance` run `60c066f093a64b7f82a85f9143f78fc3` đạt 6/6 bài Chromium và 18/18 đối chiếu SQL. Đề nghị 100.000 VND đã duyệt 40.000 VND còn chờ 60.000 VND. Đề nghị 20.000 VND đã duyệt đủ còn 0. Tạm ứng còn 240.000 VND sau các khoản đã nhận hoàn và đã duyệt. Tenant B không có đề nghị của tenant A. Mẫu số TRIP 4/4 gồm cả dòng giữ hàng chưa nạp có lượng và giá trị bằng 0.
- `owner-review.json` SHA-256 `018CE4C53BEAC64EBA0A9DD827BBA9CA65C5ED0C3B9B49EB3861E4CD395156D2`. `owner-overview.xml` SHA-256 `6EB1B0DE493755A1AA15497520B5C88F81E1EDA29A68C2DC599715F3CE60D55D`.
- `VerifySettlement` run `cab9c8957e6440ec832eff8e35b7a1c4` đạt 1/1 Chromium và 3/3 đối chiếu tiền của Owner. Hai chuyến cùng xe có giá trị 50,0001 và 50,0000 VND, khớp số dư 100,0001 VND. Mẫu số giữ 5 nguồn. Độ phủ giảm từ 5/5 xuống 2/5 khi số dư xe bị thiếu hoặc lệch theo mỗi chiều. Fixture khôi phục số dư trước khi trả xe. `owner-trip-coverage.json` SHA-256 `D673C9F85D3F222BD97EF1ACB0BC6CEA78AB0DD7E12F85BAD77EC6022461EB3B`.
- .NET đạt 352/352. `app03-8-findings-final.trx` SHA-256 `DC3ADDF42E1FFC24767837BF8530284B2E55EC10AD669B9AFD2BD8DABFB90B38`. Web build, 23/23 bài kiểm tra web và `Verify-Business.ps1` đều qua.
