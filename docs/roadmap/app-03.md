# APP-03 — Ứng dụng DEV tích hợp, dùng lại cùng dữ liệu

**Ngày chốt roadmap:** 23/11/2026. **Trạng thái triển khai:** xem bảng tại mục 4.

Owner đã đồng ý đề xuất đánh giá lại APP-03 sau DEV-F09 và yêu cầu trực tiếp: **“Tôi đồng ý đề xuất trên, bạn commit roadmap APP-03 vào repo”**. Tài liệu này ghi nhận phạm vi và thứ tự đã thống nhất. Commit ghi roadmap chỉ thay tài liệu; chưa triển khai hoặc nghiệm thu APP-03. Các PR triển khai tiếp theo thực hiện từng increment trong phạm vi synthetic DEV này, giữ review và kiểm chứng theo thay đổi; không cần một vòng hồ sơ vận hành hoặc xác nhận lại cùng phạm vi để bắt đầu.

## 1. Mục tiêu và mốc bàn giao

Mở ứng dụng trên laptop Windows, làm một ngày bán hàng qua các vai trò trên cùng dữ liệu giả định, dừng ứng dụng, mở lại để tiếp tục chính các chứng từ đó, rồi chuẩn bị chuyến/ngày làm việc tiếp theo trên cùng database.

- **Mốc 1 — DEV dùng hằng ngày:** sau APP-03.1a, .1b, .2 và .3, tổ hợp A/F đầu tiên có điểm vào chung, phiên người dùng, dữ liệu đầu ngày và Start/Stop/Resume giữ lịch sử. Có thể tạo công việc ngày tiếp theo. Mốc này chưa xác nhận toàn bộ A/B/Mixed tích hợp.
- **Mốc 2 — Bàn giao xuyên vai:** APP-03.4–.8 nối Sales, Kho, Hiện trường, Thủ quỹ/Kế toán và Chủ NPP trên cùng nguồn chứng từ; A/B/Mixed hoạt động trong các tổ hợp đã được triển khai và kiểm chứng.
- **Mốc 3 — Integrated DEV Acceptance:** APP-03.9 chứng minh hành trình liên vai, restart và hai ngày trên cùng cặp database; duy trì hồi quy APP-02E và DEV-F09.

Kiến trúc tiếp tục dùng .NET/ASP.NET Core, Dapper với transaction tường minh, FluentMigrator, React/TypeScript/Vite và SQL Server LocalDB theo tenant. Giữ toolchain được pin trong repo, Design System đã duyệt và các command/read model C/D/F. APP-03 bổ sung cấu hình chạy chung, vòng đời dữ liệu, phiên UI, hàng đợi công việc và phần ghép nghiệp vụ còn thiếu.

## 2. Baseline đã có và cách tái sử dụng

Baseline khảo sát: `main` **`3ee47aadd36392a83bb77b92983226b9d0a015a1`**, tree **`31ec75a12d62f5bccea4a962be2fe9be57f1b64d`**, sau [PR #133](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/133). Khi triển khai, đọc lại HEAD hiện tại và tái sử dụng các phần đã merge tiếp theo; không coi snapshot khảo sát là yêu cầu quay lùi mã nguồn.

[APP-02E đã ký](../closeout/2026-11-23-app-02e-localdb.md) giữ source, mẫu số và phạm vi lịch sử. [PR #124](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/124) đã merge [E-SIM-01](app-02e-ev-p0/e-sim-01.md). [DEV-F09](app-02-dev-f09.md) có lượt cuối `724c132bf81e42b6a21e096229b51b83` PASS trên source `125ff8fe7b5821cc50e7c14007b46c05e8c8cdf4`, cùng tree với baseline main: .NET 352/352, nền A.4/C.6/D.1–D.4 175/175, Chromium 29/29, F01–F08 8/8 profile. [Biên nhận HEAD cuối](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/133#issuecomment-7095592865) · [JSON kết quả bất biến](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/19c12a5a449c8394958897b3b1edf26e19f3aec5/docs/roadmap/app-02-dev-f09/follow-up-waiver-treasury/main-integration-run.json).

F09 dùng source/tree chung, nhưng mỗi fixture có scratch và run ID riêng. Kết quả này không thay thế bằng chứng APP-03 về cùng dữ liệu qua các vai và lần khởi động. D4-12 phục hồi ACK, F07 trả thực quà sau waiver và F08 hai khoản hoàn tiền cạnh tranh quỹ đã PASS trong lượt cuối; không mở lại chúng như các ca chưa chạy chỉ từ biên nhận cũ.

### Ánh xạ backlog APP-04 từng được đề xuất

Các mã APP-04 dưới đây là đề xuất trong trao đổi trước; nay chuyển phần đã có thành nguồn tái sử dụng. Không tạo lại sáu increment xây mới cùng chức năng.

| Mục đề xuất cũ | Implementation và bằng chứng DEV hiện có | Tích hợp trong APP-03 |
| --- | --- | --- |
| APP-04.1 — Visit, AC-17 | [DEV-F01](app-02-dev-f01.md), PR #125 | .5: nhánh visit không đơn trong công việc ngoài thị trường |
| APP-04.2 — Tạm ứng/hoàn ứng, AC-20 | [DEV-F02](app-02-dev-f02.md), PR #126 | .6: công việc nhân viên/thủ quỹ; .7 và .8 dùng nguồn và số dư đúng phạm vi |
| APP-04.3 — Đơn vị/lô/FEFO, AC-05 | [DEV-F03](app-02-dev-f03.md), PR #127; phạm vi A | .4/.5: bàn giao, chọn lô và kiểm tổ hợp A/B/Mixed được hỗ trợ |
| APP-04.4 — Trả/QA/giá vốn, AC-05/06 | [DEV-F05](app-02-dev-f05.md), PR #129; [DEV-F06](app-02-dev-f06.md), PR #130 | .6: nhận trả/QA; .7: hồ sơ điều chỉnh có nguồn |
| APP-04.5 — Quà/waiver, AC-07 | [DEV-F07](app-02-dev-f07.md), PR #131 | .6/.7: truy vết và bàn giao theo vai trên hồ sơ hiện có |
| APP-04.6 — Giá/discount/thuế/credit/refund, AC-19 | [DEV-F04](app-02-dev-f04.md), PR #128; [DEV-F08](app-02-dev-f08.md), PR #132 | .4/.5: snapshot giá; .7: credit/refund đúng điều kiện nguồn |

Đây là ghi nhận phạm vi DEV từng ca, không phải xác nhận toàn bộ AC-01–21 hoặc mọi tổ hợp nghiệp vụ. Các phần mở rộng còn lại có tên và điều kiện hoàn tất tại mục 7.

## 3. Điều kiện kỹ thuật và nghiệp vụ dùng chung

1. **Runtime/schema:** [API startup](../../src/FmcgDms.Api/Program.cs) hiện yêu cầu F05 có F03+F04; F06/F07/F08 từng phần cần F05; F03/F04 bị chặn khi ghép D.1–D.4 hoặc bật worker. [ScratchMigrator](../../src/FmcgDms.Api/Persistence/ScratchMigrator.cs) chọn migration theo tag. Mỗi tổ hợp mới phải có tập migration, cấu hình và kiểm thử tương ứng. Không xóa guard hoặc bật mọi flag để giả lập khả năng chạy chung; F08 không tự chứng minh schema F02 hoặc D đã hiện diện.
2. **Phiên DEV:** [launcher C/F](../../src/FmcgDms.Demo/App02c1Dev.cs) hiện chọn một increment, tạo run mới và dọn hai database khi kết thúc. F01–F08 có chế độ tương tác; C.6/D.1–D.4 hiện yêu cầu Verify. Tái sử dụng lifecycle/manifest/lock của [demo AC-03](app-02b-local-preprod/laptop-demo.md) và [DemoRun](../../src/FmcgDms.Demo/DemoRun.cs), giữ instance/name/ownership/nonce và hai binding tenant. Launcher nghiệm thu cũ giữ nguyên hợp đồng và ý nghĩa bằng chứng.
3. **Dữ liệu:** bootstrap có phiên bản, chạy lại an toàn, chỉ tạo master, phân công, opening và policy giả lập được nêu rõ. Đơn, chuyến theo đường nghiệp vụ, delivery, trả hàng và phiếu tiền phát sinh qua command. E-SIM là nguồn mô hình/oracle; không nạp toàn bộ kết quả fixture hoặc chứng từ chèn SQL của ca biên thành dữ liệu làm việc hằng ngày.
4. **Clock và snapshot:** ngày nghiệp vụ, cutoff, kỳ mở/khóa, hiệu lực policy và hạn lô phải nhất quán. Resume giữ ngữ cảnh thời gian của phiên; chuyển ngày là thao tác có chủ đích. Đơn/nguồn cũ giữ giá, thuế, terms, quy đổi và lô đã snapshot; không sửa lịch sử để khớp ngày máy hiện tại.
5. **Tiền và cấp tổng hợp:** F04/F08 dùng VND; một số nguồn C/D dùng `SYNTHETIC_UNITLESS`. Chốt đơn vị cho bộ dữ liệu chung ở .2, ưu tiên VND trong các luồng đã hỗ trợ. Khi ghép D, triển khai đúng hợp đồng tiền hoặc trình bày riêng từng đơn vị; không cộng chéo đơn vị hoặc đặt tỷ giá giả. Tồn MAIN theo kho/SKU/lô chỉ đếm một lần khi nhiều chuyến tham chiếu. AR, customer credit, custody, quỹ và tạm ứng F02 có nguồn/sổ riêng.
6. **Danh tính và phân nhiệm:** năm nhóm điều hướng không giới hạn hệ thống vào năm quyền hoặc năm tài khoản. Server kiểm tenant, actor, grant hiện hành và phân công tại thời điểm đọc/ghi/replay. Giữ maker/checker, QA, người xác minh và thủ quỹ độc lập theo từng command. Đổi vai trong DEV phải cấp danh tính hợp lệ; không dùng superuser hoặc cờ `verified=true` phía client để bỏ bước.
7. **Trạng thái:** durable intake ACK, business outcome, receipt, posted, reconciled và UNKNOWN giữ nghĩa riêng. Mất ACK tra cứu cùng command/source ID; replay không tạo hiệu ứng mới. Mixed giữ kết quả `RECONCILING`; một trạng thái đóng chuyến cuối cùng cần increment quy định điều kiện xử lý hết phần chờ.
8. **Ranh giới:** chỉ synthetic/nonproduction, `invoiceEligible=false`, đối tác giả lập D.4; offline business writes tắt, B_NO_CHANNEL bị chặn. Giữ `business/`, `business.lock.json`, hồ sơ E v0.1 đã đóng băng và bằng chứng lịch sử. LAB/Vault/Docker, physical PREPROD và EV/P0 thực tế là nghĩa vụ tại mốc tương ứng, không là điều kiện bắt đầu APP-03. Không biến dữ liệu hoặc biên nhận giả định thành bằng chứng vận hành thật.

## 4. Các increment và phụ thuộc

Mỗi hàng là một increment triển khai độc lập có thay đổi thực chất và bằng chứng tương ứng. Bảng ghi trạng thái theo bằng chứng ở từng increment.

| Increment | Phụ thuộc | Đầu ra review được | Trạng thái |
| --- | --- | --- | --- |
| APP-03.1a — Runtime/schema tích hợp đầu tiên | C/D/F đã merge | Một tổ hợp A/F chạy chung và ma trận hỗ trợ rõ ràng | [DEV_VERIFIED_LOCAL](app-03-1a-runtime-schema.md#kết-quả-local-ngày-23112026) |
| APP-03.1b — Phiên DEV dùng lại được | .1a | Start/Stop/Resume/Remove giữ đúng dữ liệu và ownership | [DEV_VERIFIED_LOCAL](app-03-1b-dev-session.md#kết-quả-local-ngày-23112026) |
| APP-03.2 — Đầu ngày/ngày kế tiếp | .1b | Bootstrap có phiên bản, chuẩn bị chuyến mới trên lịch sử cũ | [DEV_VERIFIED_LOCAL](app-03-2-day-bootstrap.md#kết-quả-local-ngày-23112026) |
| APP-03.3 — Phiên người dùng/khung ứng dụng | .1b; hợp đồng bootstrap .2 | Điểm vào chung, danh tính/tenant, điều hướng và ngữ cảnh | [DEV_VERIFIED_LOCAL](app-03-3-workspace.md#kết-quả-local-ngày-24112026) |
| APP-03.4 — Sales bàn giao Kho | .2, .3 | Hàng đợi đơn/chuyến, Confirm/Reserve/Load và nguồn chung | [DEV_VERIFIED_LOCAL](app-03-4-handoff.md#kết-quả-local-ngày-24112026) |
| APP-03.5 — Kho bàn giao Hiện trường | .4 | Giao A, bán B, visit, Mixed và phục hồi trong tổ hợp hỗ trợ | [DEV_VERIFIED_LOCAL](app-03-5-field.md#kết-quả-local-ngày-24112026) |
| APP-03.6 — Hiện trường bàn giao Thủ quỹ/Kho | .5 | Nộp/nhận tiền, trả xe, đường vào trả hàng và hoàn ứng | NOT_STARTED |
| APP-03.7 — Bàn Kế toán/Finance | .6 | Hàng đợi D.4, correction, giá vốn, quà/waiver, credit/refund theo vai | NOT_STARTED |
| APP-03.8 — Tổng quan Chủ NPP | Nguồn/read model .4–.7 | Báo cáo chỉ đọc, đúng nguồn/đơn vị/cấp dữ liệu, drill-down | NOT_STARTED |
| APP-03.9 — Integrated DEV Acceptance | .1a–.8 | Bằng chứng cùng dữ liệu, liên vai, restart và ngày hai | NOT_STARTED |

Thứ tự ưu tiên: **.1a → .1b → .2/.3 → .4 → .5 → .6 → .7 → .8 → .9**. .2 và .3 có thể tiến hành độc lập khi hợp đồng phiên/bootstrap đã rõ; không phát sinh PR tài liệu trung gian chỉ để xin bắt đầu các hàng đã được chốt.

### APP-03.1a — Runtime/schema tích hợp đầu tiên

**Phạm vi:** bắt đầu bằng A với F03+F04+F05, ghép thêm visit F01 vào cùng cấu hình và schema. Đây là tổ hợp bàn giao đầu tiên; F02/F06–F08 và D được thêm theo nhu cầu increment sở hữu, không quảng bá là đã hỗ trợ chỉ vì route tồn tại. Ghi version của profile/schema và migration cần chạy; kiểm upgrade từ trạng thái DEV tương ứng mà không xóa chứng từ. Có một điểm khởi chạy React+API cho profile mới.

**Nguồn sửa/tái sử dụng:** `src/FmcgDms.Api/Program.cs`, `Persistence/ScratchMigrator.cs`, `src/FmcgDms.Demo/App02c1Dev.cs`, các cấu hình tenant/profile và runner liên quan. Có thể tách phần điều phối mới thành file nhỏ; không sao chép business command của C/F.

[Profile v1, launcher, migration và kịch bản kiểm chứng](app-03-1a-runtime-schema.md).

**Hoàn tất:**

- [x] Chạy được visit không đơn, đơn A có giá/lô và hồ sơ trả gắn delivery tạo thật trên cùng tenant/database; visit giữ nguyên đơn, AR và tồn.
- [x] Migration tạo đủ schema của tổ hợp; đọc được nguồn đã tạo trước thay đổi schema trong phạm vi hỗ trợ. Cấu hình thiếu phụ thuộc hoặc tổ hợp chưa hỗ trợ bị từ chối rõ ràng.
- [x] Có kiểm chứng tenant đối chứng, quyền hiện hành và một tương tác mới giữa các nhóm chức năng. Ghi cấu hình/source thực chạy cùng kết quả.
- [x] Giữ các profile/runner nghiệm thu cũ chạy đúng phạm vi. Ghép A/B/Mixed và worker nằm ở increment cần chúng; không phải điều kiện hoàn tất .1a.

### APP-03.1b — Phiên DEV dùng lại được

**Phạm vi:** tạo, khởi chạy, dừng, mở lại và thu hồi phiên theo run ID; manifest giữ hai binding, ownership/nonce, profile/schema, seed version và clock. Stop giữ database; Remove chỉ xóa đúng tài nguyên đã xác minh. Resume cấp credential hợp lệ mới mà không thay lịch sử actor, cấp lại quyền đã bị thu hồi hoặc tạo nghiệp vụ mới. Lỗi khởi động không tự xóa phiên đang có dữ liệu.

[Launcher phiên và cách kiểm chứng](app-03-1b-dev-session.md).

**Hoàn tất:**

- [x] Tạo chứng từ, dừng host/process, mở lại cùng phiên và đọc/tiếp tục đúng ID, trạng thái, số dư.
- [x] Một lệnh đã commit nhưng chưa nhận ACK được nhận diện sau restart, tra/replay cùng ID chỉ có một hiệu ứng.
- [x] Không nhân opening, policy hoặc command/source; grant đã thu hồi vẫn chặn đọc/ghi/replay theo hợp đồng.
- [x] Remove xác minh instance/name/tenant/ownership/nonce; phiên hoặc database không thuộc run giữ nguyên. Ghi hướng dẫn một lệnh khởi chạy và các thao tác lifecycle thực sự đã triển khai.

### APP-03.2 — Dữ liệu đầu ngày và chuẩn bị chuyến ngày sau

**Phạm vi:** seed tối thiểu có phiên bản cho hai tenant: customer/outlet/ship-to, SKU/UoM/lô, kho/xe, nhân sự/actor/phân công, opening và policy giả lập. Tái sử dụng giá trị/oracle phù hợp từ C/F; bổ sung đường tạo/chuẩn bị chuyến mới. Không gọi lại mọi `Evidence.Seed()` để dựng một ngày đã hoàn tất.

[Hợp đồng dữ liệu, lệnh NextDay và kịch bản kiểm chứng](app-03-2-day-bootstrap.md).

**Hoàn tất:**

- [x] Seed/chạy lại cùng phiên không tạo thêm opening, tồn, ledger hoặc bản policy trùng.
- [x] Có hai chuyến cùng SKU và một chuyến ngày tiếp theo; dữ liệu ngày sau tiếp nối số dư và lịch sử ngày trước.
- [x] Clock, hiệu lực giá/policy, kỳ và hạn lô nhất quán trong kỳ synthetic đã công bố; nguồn cũ giữ snapshot. Các giả định có trong bảng expected.
- [x] Chốt đơn vị tiền/cấp dữ liệu cho các nguồn được đưa vào phiên; phần chưa hỗ trợ có trạng thái rõ, không cộng gộp sai.

### APP-03.3 — Phiên người dùng và khung ứng dụng chung

**Phạm vi:** dùng lại UI C/D/F và UI-01–03; điểm vào nghiệp vụ, phiên chung trong các workspace, điều hướng theo vai, tenant/actor đang thao tác, giữ nguồn/chuyến khi chuyển màn. Trang gốc giới thiệu đúng ứng dụng DEV hiện hành; công cụ chọn profile, tạo lỗi và diễn tập có điểm vào riêng. Đổi actor/tenant xóa lựa chọn không còn quyền; phục hồi công việc cũ khi quay lại đúng actor bằng nguồn đã lưu, không dùng chung pending-command namespace giữa danh tính.

[Hướng dẫn dùng phiên chung và kiểm chứng APP-03.3](app-03-3-workspace.md).

**Hoàn tất:**

- [x] Mỗi actor mở phiên một lần, đi giữa các workspace được phép và reload mà không phải dán mã DEV tại từng trang.
- [x] Server vẫn kiểm quyền/tenant/phân công; token hết hạn hoặc quyền bị thu hồi không làm lộ nguồn hay cho replay trái quyền.
- [x] Giữ ngữ cảnh qua đường dẫn và danh sách; màn chưa được profile hỗ trợ không được thể hiện như một chức năng dùng được.
- [x] Desktop Compact, Noto Sans, Light/Dark/System, sidebar và adaptive list/detail theo baseline; kiểm keyboard/focus/theme trên các phần thay đổi.

### APP-03.4 — Sales bàn giao Kho

**Phạm vi:** hàng đợi đơn/chuyến cần Confirm/Reserve/Load; dùng counters, UoM/lô/giá và command C.2/C.3/F03/F04 đã có. Mở đúng đơn, chuyến và nguồn từ danh sách; giữ ngữ cảnh khi chuyển bước. Xử lý migration/read model còn thiếu cho nguồn dùng chung, không dựng lại màn đơn.

[Cách bàn giao Sales–Kho và chạy kiểm chứng APP-03.4](app-03-4-handoff.md).

**Hoàn tất:**

- [x] Kho thấy lượng yêu cầu/đã giữ/đã nạp/còn lại và lot trail trên cùng đơn/chuyến.
- [x] Hai chuyến cạnh tranh cùng SKU/lô không giữ hoặc nạp vượt lượng khả dụng; đơn vị cơ sở và snapshot bảo toàn.
- [x] Những đường reserve/pricing được mở mới kiểm thiếu/hết hạn plan và quyền phê duyệt liên quan; tự duyệt bị chặn khi hợp đồng yêu cầu phân nhiệm.
- [x] Kết quả bị từ chối không thêm nguồn hoặc hiệu ứng ngoài hợp đồng; tenant khác không đọc/ghi được.

### APP-03.5 — Kho bàn giao Hiện trường

**Phạm vi:** danh sách chuyến/công việc của nhân viên; giao A, bán B online và visit F01. Ghép runtime/schema D với A/F theo ma trận được triển khai; quy định lô, giá, thuế, đơn vị tiền, quyền A và quota B trước khi mở tổ hợp đó. Tái sử dụng D.1/D.2, không mở B_NO_CHANNEL.

[Cách bàn giao Kho–Hiện trường, ma trận hỗ trợ và kiểm chứng APP-03.5](app-03-5-field.md).

**Hoàn tất:**

- [x] Cùng chuyến thực hiện A/B theo đúng quyền và hàng đang có; giao từng phần cập nhật đúng nguồn/counters.
- [x] Mất ACK, reload hoặc mở lại phiên tìm được lệnh/chứng từ gốc; retry không tạo giao/bán/thu tiền thứ hai.
- [x] Visit kết thúc không đơn và không phát sinh đơn 0 đồng, AR hoặc biến động kho.
- [x] Hành trình Field thay đổi có kiểm chứng browser ở 320 CSS px, vùng chạm mục tiêu 48×48, focus và mất mạng chỉ chặn business writes; thiết bị vật lý giữ acceptance riêng.
- [x] Mọi tổ hợp A/B/Mixed được công bố trong UI có kiểm runtime/schema và nghiệp vụ tương ứng; FEFO A không được dùng làm bằng chứng cho mode B có lô. Phạm vi chưa hỗ trợ được giữ rõ và không được tính vào acceptance.

### APP-03.6 — Hiện trường bàn giao Thủ quỹ và Kho

**Phạm vi:** danh sách đề nghị nộp tiền, kiểm đếm/nhận tiền, trả hàng còn trên xe, đối soát chuyến; tái sử dụng D.3. Nối F02 cho tạm ứng/hoàn ứng và F05/F07 cho công việc nhận trả/QA/quà từ đúng nguồn. Các bước Finance thuộc cùng hồ sơ mở qua .7, không tạo bản sao hồ sơ cho mỗi vai.

**Hoàn tất:**

- [ ] Người đề nghị và người nhận tiền độc lập; Offer chưa chuyển quỹ, Accept mới ghi đúng các chân custody/fund.
- [ ] Phân biệt nộp tiền khách, hoàn ứng nhân viên, trả tồn xe và khách trả hàng qua QA; không dùng một command hoặc một số dư thay cho các nguồn khác nhau.
- [ ] Hàng trả, quarantine/QA, quyền A còn giữ và quota B được xử lý theo nghiệp vụ; giữ Mixed `RECONCILING` khi còn phần chờ.
- [ ] Chạy tạm ứng/hoàn ứng khi sổ khách hàng đã có dữ liệu, đối chiếu chúng không bị tác động bởi luồng tạm ứng.

### APP-03.7 — Bàn làm việc Kế toán/Finance

**Phạm vi:** hàng đợi nguồn chưa tiếp nhận/chưa ghi sổ/sai lệch, correction D.4; điều chỉnh giá vốn F06 và credit/refund F08. Các bước Finance của tạm ứng, trả hàng/QA và quà/waiver mở đúng hồ sơ F02/F05/F07 do .6 chủ trì; .7 không tạo thêm một workspace hoặc bản sao hồ sơ cho các bước đó. Dùng đối tác giả lập hiện có; chọn nguồn từ danh sách thay thao tác sao chép UUID. F08 v1 giữ điều kiện nguồn A/VND đã thanh toán đủ, delivery hoàn thành riêng một dòng hàng; các hồ sơ khác hiển thị đúng tình trạng chưa hỗ trợ/chưa đủ điều kiện.

**Hoàn tất:**

- [ ] Receipt, posted và reconciled riêng; UNKNOWN tra nguồn và phục hồi cùng định danh.
- [ ] Correction và giá vốn có nguồn, kỳ khóa giữ nguyên; maker/checker/QA theo phân công hiện hành.
- [ ] Credit, đề nghị chi, duyệt, xác minh chứng từ và thủ quỹ thực chi giữ các actor độc lập và tác động số dư đúng thời điểm.
- [ ] Hồ sơ từ delivery tạo trong phiên chung đi được hết nhánh F08 được hỗ trợ. Nếu hành trình cam kết cần refund nguồn giao nhiều đợt hoặc đơn có quà, triển khai và kiểm nhánh đó trước khi đưa vào acceptance; không đánh dấu đã phủ bằng fixture F08 v1.

### APP-03.8 — Tổng quan Chủ NPP

**Phạm vi:** read model chỉ đọc về đơn/chuyến, số lượng và giá trị tồn MAIN/TRIP, giá vốn, AR/credit, tiền ngoài thị trường, quỹ, tạm ứng và công việc chờ. Tái sử dụng C.5/D.3 cùng nguồn F phù hợp; tổng hợp theo đúng nguồn, đơn vị và cấp dữ liệu, không cộng các báo cáo chuyến một cách cơ học.

**Hoàn tất:**

- [ ] Mỗi chỉ số có asOf/cutoff, phạm vi, đơn vị và mức đầy đủ/chưa đủ; dùng n/N theo nguồn khi có mẫu số xác định. Thiếu mẫu số ghi chưa đủ dữ liệu, không biểu diễn dữ liệu chưa có thành 0 hoặc tỷ lệ hoàn tất.
- [ ] Hai chuyến cùng SKU không làm số lượng hoặc giá trị MAIN bị cộng hai lần; quantity cơ sở, lot/QA và quyền giữ được phân biệt; giá vốn đối chiếu về nguồn.
- [ ] Tạm ứng F02 lấy từ sổ F02, không suy ra từ giá trị 0 của một báo cáo chuyến D.3. AR/credit/custody/quỹ/refund giữ đúng nguồn và đơn vị.
- [ ] Drill-down về nguồn/case; Owner không có quyền ghi chỉ vì được xem tổng hợp; tenant khác không nhìn thấy dữ liệu.

### APP-03.9 — Integrated DEV Acceptance

**Phạm vi mới:** một phiên DEV, một cặp database theo tenant, một bộ dữ liệu có lịch sử, actor riêng và hai ngày làm việc. Ma trận acceptance xác định theo hành trình và điểm ghép mới; không đặt trước một tổng PASS để đạt. Kết quả là bằng chứng synthetic DEV bổ sung, không sửa biên bản E đã ký.

- [ ] Khởi tạo một lần; Sales tạo đơn, Kho giữ/nạp, Hiện trường visit/giao/bán, Thủ quỹ nhận tiền, Kho nhận trả, Finance xử lý và Owner xem nguồn chung.
- [ ] Hai chuyến cùng SKU kiểm số khả dụng và tổng tồn; hai tenant kiểm cách ly đọc/ghi/replay và số dư đối chứng.
- [ ] AR/custody khách hàng và tạm ứng nhân viên đều có dữ liệu khi kiểm tính độc lập.
- [ ] Một hồ sơ trả/QA/credit/refund đủ điều kiện truy về delivery được tạo bằng command trong phiên.
- [ ] Để lại ít nhất một công việc pending và một kết quả cần phục hồi, dừng process rồi mở lại; tiếp tục đúng ID và đúng quyền, không tạo thêm hiệu ứng.
- [ ] Tạo chuyến/ngày hai trên cùng DB; số cuối ngày trước khớp đầu ngày sau, opening và snapshot cũ giữ nguyên.
- [ ] Một lượt thao tác trực tiếp từ trình duyệt và một hành trình tự động trọng tâm có bằng chứng UI→API→SQL; đối chiếu SQL độc lập, expected/actual/diff theo nguồn và đơn vị.
- [ ] Chạy hồi quy F09 và các gate repo áp dụng trên source cuối; ghi command, môi trường, source/tree, run ID, kết quả, log/SQL/JUnit/TRX và hash artifact. Không gắn PASS của bản trước cho mã đã thay đổi.

## 5. UI-05 và quyền sở hữu màn hình

[UI-05](../ui-ux/adoption-roadmap.md) được ánh xạ vào APP-03; không mở thêm một lượt xây lại năm workspace. Mỗi màn có một increment chủ trì; increment khác dùng component/route/read model chung và ghi phần thay đổi theo đúng chủ trì.

| Màn hoặc trách nhiệm | Increment chủ trì | Quy tắc dùng lại |
| --- | --- | --- |
| Điểm vào, sidebar, phiên/tenant/actor, điều hướng | .3 | Các workspace dùng chung phiên và layout; công cụ DEV có điểm vào riêng |
| Customer/ShipTo, đơn, AR trong Sales A và hàng đợi Kho | .4 | Dùng C.1/C.2/C.3/C.5/F03/F04; Confirm/Reserve/Load và drill-down AR giữ context nguồn |
| Chuyến ngoài thị trường, giao A/bán B/visit | .5 | Dùng C.4/D.1/D.2/F01, command recovery hiện hành |
| Bàn giao tiền/trả xe; workspace tạm ứng, trả hàng/QA và quà | .6 | Dùng D.3/F02/F05/F07; .6 là chủ trì duy nhất của mỗi workspace hồ sơ, kể cả các bước phê duyệt Finance |
| Hàng đợi Finance, workspace correction/giá vốn/credit/refund | .7 | Dùng D.4/F06/F08; mở lại hồ sơ .6 cho F02/F05/F07 bằng cùng ID và đúng actor, không dựng workspace trùng |
| Tổng quan Chủ NPP và drill-down | .8 | Chỉ đọc, dùng nguồn và quyền server; không nhân đôi số liệu |

Giữ [Design Baseline đã duyệt](../decisions/owner-uiux-2026-11-20.md): Desktop Compact, Noto Sans self-hosted, Light/Dark/System, sidebar và adaptive list/detail. Field dùng body 16/24, mục tiêu chạm 48×48 CSS px, một cột từ 320 CSS px, có safe area/bàn phím ảo. Tái sử dụng controls/components, tokens và theme đã có; docking hoặc đổi thư viện không là điều kiện hoàn tất APP-03. Bằng chứng thiết bị/zoom thực tế giữ phạm vi riêng; ngoại lệ 125% APP-00B/C không bị mở lại hoặc tự mở rộng.

## 6. Hướng dẫn thực hiện và kiểm chứng cho Codex

1. Đọc roadmap này, [AGENTS](../../AGENTS.md), [APP-02](app-02.md), các spec C/D/F của phần sửa và [UI/UX Standard](../ui-ux/README.md). Kiểm tra main/PR hiện tại để tránh thực hiện lại một increment đã hoàn thành sau snapshot khảo sát.
2. Bắt đầu **APP-03.1a** bằng thay đổi runtime/schema có thể chạy, sau đó **APP-03.1b**. Gộp các quyết định kỹ thuật thường lệ, expected, hướng dẫn chạy và cập nhật trạng thái vào PR có code của increment. Không tạo chuỗi PR xin bằng chứng thật trước DEV.
3. Với mỗi increment, chốt trước hành trình, nguồn, giả định và expected của phần mới; triển khai API/SQL/UI cần thiết, chạy hành trình và đối chiếu độc lập. Dữ liệu có thể giả định; kết quả PASS phải đến từ thực thi thật. Trạng thái không chạy/không đủ điều kiện ghi `NOT_RUN`/`BLOCKED` cùng nguyên nhân cụ thể.
4. Dùng runner và test hiện có theo ảnh hưởng; thêm kiểm chứng cho rủi ro mới do ghép profile, chung dữ liệu, resume, bàn giao hoặc tổng hợp. Không tạo test chỉ lặp lại implementation, không chạy lại toàn bộ mỗi bộ fixture chỉ để tăng số PASS. Giữ các gate bắt buộc của repo và hồi quy cuối .9.
5. Mỗi PR bàn giao ghi phạm vi đã chạy, source/tree, lệnh, artifact và giới hạn; cập nhật hàng trạng thái ở đây sau kết quả thực nhận. `MERGED`, `DEV_VERIFIED` và nghiệm thu thực tế được ghi riêng. Không viết lại hồ sơ v0.1 đã đóng băng hoặc tạo chuỗi closeout chỉ để ghi nhận lần merge trước.

Các lệnh nền đã có trên Windows tại thời điểm lập roadmap:

```powershell
pwsh -NoProfile -File ./scripts/Run-App02C1.ps1 -Increment DevF05
pwsh -NoProfile -File ./scripts/Run-App02C1.ps1 -Increment DevF05 -Verify
pwsh -NoProfile -File ./scripts/Run-DevFunctionalRegression.ps1 -Suite All
```

Hai lệnh DevF05 giữ profile riêng. [Launcher App03A1](app-03-1a-runtime-schema.md) giữ vòng đời scratch dùng cho kiểm chứng .1a. [Launcher APP-03 DEV](app-03-1b-dev-session.md) giữ dữ liệu qua Stop/Resume.

## 7. Phần nghiệp vụ còn lại và điều kiện đưa vào phạm vi

| Mục còn lại | Trách nhiệm/ưu tiên | Điều kiện hoàn tất |
| --- | --- | --- |
| F03: cạnh tranh cùng SKU/lô giữa hai chuyến A | [Đã kiểm chứng DEV cục bộ ở APP-03.4](app-03-4-handoff.md#kết-quả-local-ngày-24112026) | Không over-reserve/load/issue; nguồn, số dư và tenant đối chứng khớp khi cạnh tranh |
| F04: thiếu/hết hạn plan và tự duyệt | [Confirm A ở APP-03.4](app-03-4-handoff.md#kết-quả-local-ngày-24112026) và [bán B ở APP-03.5](app-03-5-field.md#kết-quả-local-ngày-24112026) | Từ chối đúng điều kiện, không thêm nguồn/effect; snapshot của nguồn cũ giữ nguyên |
| Lô/FEFO/giá/thuế của B và tổ hợp Mixed với A/F | [APP-03.5 kiểm Mixed và B không quản lý lô](app-03-5-field.md#kết-quả-local-ngày-24112026) | Runtime/schema và policy của tổ hợp được hỗ trợ; kiểm A/B trên cùng nguồn tồn, đúng currency và quyền. B có lô ngoài phạm vi. |
| Refund nguồn giao nhiều đợt hoặc đơn có nghĩa vụ quà | Backlog mở rộng AC-19/07; APP-03.7 sở hữu nếu hành trình .9 yêu cầu | Credit/refund/residual và nghĩa vụ quà đúng theo từng nguồn, không vượt lượng/tiền hợp lệ; F08 v1 không được coi là bằng chứng cho nhánh này |
| Dùng customer credit cho hóa đơn/chứng từ mới | Backlog mở rộng AC-19/21 sau phạm vi APP-03 v1 | Allocation có nguồn, đúng quyền/số dư, không dùng trùng credit và đối chiếu AR/credit độc lập |
| Điều chỉnh giá vốn sau nhiều QA release hoặc hàng đã dịch chuyển | Backlog mở rộng AC-06; .7 hiển thị đúng case chưa tự xử lý | Có phương trình/source-cost riêng, điều chỉnh kỳ hiện hành và bảo toàn kỳ khóa; không ép case vào oracle một lần release của F06 |
| Đóng chuyến cuối cùng sau RECONCILING | Backlog nghiệp vụ riêng sau D.3/.6 | Quy định rõ điều kiện hết quyền/hàng/tiền/đối soát chờ, trạng thái kết thúc và xử lý phần còn chờ |

Các mục này là phạm vi chức năng cụ thể, không đổi nhãn thành nợ vận hành. Mốc DEV dùng hằng ngày không phải chờ mọi nhánh mở rộng; hành trình nào công bố hỗ trợ thì phải triển khai và kiểm đúng nhánh đó. EV/P0 thực tế, physical PREPROD, Pilot và Go-Live tiếp tục theo các điều kiện đã ghi trong [operations debt](app-02c-operations-debt.md) và [truy vết E](app-02e-ac-ev-p0-traceability.md).
