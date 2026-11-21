# APP-02B LAB — Evidence, CI, Drivers and Closeout

**State:** LAB-E01 `SOURCE_MERGED (DOCS_ONLY)`, LAB-E02 `SOURCE_REVIEW`, LAB-E03..E13 `PLANNED`. Baseline `16d96c71a7967c216ebfa599693807a2b54e7435`, 2026-11-19.

**Goal:** chạy các scenario thật, giữ raw evidence và đối chiếu đủ 30 case + 2 runtime probes, với trạng thái closeout đúng phạm vi.
**Architecture/stack:** [design](design.md); Python collectors/runner, PowerShell entrypoints, GitHub Actions và original B9 validator.
**Global constraints:** [design](design.md), immutable fixture/business, không tự cấp quyền hoặc ký thay reviewer.
**Review focus:** wrong target, stale authority, mất evidence, giả provenance và retry che lỗi. [Codex handoff](codex-handoff.md) quy định command registry và tiêu chí từng PR. Mọi file ghi Create là đề xuất mới.

## Baseline facts

- `tools/app02b9/oracle.py` là validator, không phải executor. `expected-v1.json` chứa chính xác 30 CaseId và required witness kinds; runtime probes RUNTIME-01/02 nằm ngoài 30 ca.
- `tests/app02b9/test_oracle.py` và workflow B.9 hiện có 12 validator tests / 11 negative mutations. Chúng dùng signed mock, không tạo physical evidence.
- `scripts/Run-App00A.ps1` chưa tương đương APP-00A CI: wrapper cũ dùng minimum 82 và không bao phủ đủ audit/PWA/Playwright. Workflow hiện tại có minimum 296; các project B.2–B.8 không được bao phủ chỉ bằng solution hiện tại.
- #75 chứa hai lỗi khác nhau: LocalDB bootstrap và HTTP 500 sau restart. #68 là một record trước đó của triệu chứng A11-09. Rerun xanh không xác lập root cause.
- #59 đã CLOSED bằng risk acceptance, không phải patched package. Scope `APP00A_SYNTHETIC_CI_BUILD_TOOLS_ONLY`; expiry `2026-12-19T00:00:00Z`; không cho phép frontend publication/physical deployment qua exception này.
- Original B.9 yêu cầu observer khác implementer; Security/DBA khác nhau và khác implementer/observer; bốn public-key pins khác nhau cho fixture/observer/security/dba. Một người giữ bốn khóa không tạo ra independent human review.

<a id="preprod-pr91-merge"></a>
## Ghi nhận sau merge PR #91

- **Merge vào `main`** ngày 21/11/2026 lúc 14:01 ICT, SHA `a9d5be047c8218766ebe9af3be43ed34e32c1950`. [PR #91](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/91) · [Merge commit](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/a9d5be047c8218766ebe9af3be43ed34e32c1950).
- **CI** đạt **20/20 checks** trên HEAD của PR `d305118ed0d3956d6ead4cbc7da75a86378226c1` trước khi merge. [Kết quả CI](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/91/checks).
- **Gói ZIP LocalDB** `PREPROD-bo-bang-chung-AC09-AC21-checkout-sach.zip`, SHA-256 `34E84654948398E7ABA04D237FFB9CA87F7CE003326A266CCCF8AABDBABADAFF`. [Hồ sơ lượt chạy và đường dẫn ZIP](#preprod-ac09-ac21-clean-checkout).
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="preprod-pr90-merge"></a>
## Ghi nhận sau merge PR #90

- **Merge vào `main`** ngày 21/11/2026 lúc 13:46 ICT, SHA `f4f941eed616af4083ed64d25951be5bab4ac78c`. [PR #90](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/90) · [Merge commit](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/f4f941eed616af4083ed64d25951be5bab4ac78c).
- **CI** đạt **24/24 checks** trên HEAD của PR `3fa9dc9e8f78986e3afe75ee50d26cd8f425f606` trước khi merge. [Kết quả CI](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/90/checks).
- **Gói ZIP LocalDB** `PREPROD-bo-bang-chung-AC09-AC21-checkout-sach.zip`, SHA-256 `34E84654948398E7ABA04D237FFB9CA87F7CE003326A266CCCF8AABDBABADAFF`. [Hồ sơ lượt chạy và đường dẫn ZIP](#preprod-ac09-ac21-clean-checkout).
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="preprod-ac09-ac21-clean-checkout"></a>
## Tái lập AC-09 và AC-21 từ source đã chốt

Ngày 21/11/2026, lượt `ac09-ac21-repro-20261121-062524Z-9d1eb42b` chạy từ commit `f1ee1c8ff71ff77ad3b41db9209a70796a31c6e8` trong checkout riêng ở detached HEAD. Git xác nhận checkout sạch trước và sau khi chạy. Môi trường Windows SQL Server LocalDB 15.0.4382.1, .NET SDK 10.0.404, `SYNTHETIC_NONPRODUCTION`.

| Phạm vi | `tenant-a` | `tenant-b` |
|---|---|---|
| AC-09 `CASH_60` và `CASH_100` | **10/10 mỗi ca** | **10/10 mỗi ca** |
| AC-09 `CASH_60_CREDIT_20`, `BANK_PENDING`, `CASH_OVERPAY_120` | **15/15 mỗi ca** | **15/15 mỗi ca** |
| AC-09 tranh cùng hạn mức | **18/18** | **18/18** |
| AC-21 qua APP-01 | **5/5 mốc, 25/25 chỉ số**, gửi lại **4/4** | **5/5 mốc, 25/25 chỉ số**, gửi lại **4/4** |

Các chỉ số SQL khớp gói trước ở cả hai tenant. Ca tranh hạn mức vẫn có một lệnh ghi sổ và một lệnh bị từ chối. Gửi lại các lệnh được kiểm tra giữ nguyên dấu vân tay SQL, và tenant đối chứng giữ nguyên dữ liệu trong từng lượt. AC-09 dùng fixture cho chứng cứ chuyển khoản chờ và giữ tín dụng. AC-21 dùng fixture cho mốc khoản thu đã xác nhận, sau đó chạy bốn lệnh qua HTTP. Kiểm thử Release đạt **12/12 AC-09 và 2/2 AC-21**.

- **Gói PREPROD cập nhật** [PREPROD-bo-bang-chung-AC09-AC21-checkout-sach.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-ac21-repro-20261121-062524Z-9d1eb42b/PREPROD-bo-bang-chung-AC09-AC21-checkout-sach.zip). SHA-256 `34E84654948398E7ABA04D237FFB9CA87F7CE003326A266CCCF8AABDBABADAFF`. Gồm sáu gói thành phần, trong đó có các lượt AC-03 và khôi phục đã lưu trước đây.
- **Gói chi tiết lượt tái lập** [PREPROD-AC09-AC21-clean-checkout.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-ac21-repro-20261121-062524Z-9d1eb42b/PREPROD-AC09-AC21-clean-checkout.zip). SHA-256 `A7B4D98EE67BB8D3063EA7F200C52166437613773D2681DB8D3698C3B704D056`. Gồm JSON hai tenant, TRX, thông tin checkout và đối chiếu với gói trước.
- **Mốc so sánh** [Bộ bằng chứng PREPROD cập nhật AC-09](#preprod-updated-ac09-bundle), SHA-256 `CA1981DCDA8D37AE7F39F46E133A7A9D0E1576E5B92D6092D5B746E907EFDC12`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="preprod-updated-ac09-bundle"></a>
## Bộ bằng chứng PREPROD cập nhật đủ các nhánh AC-09 trên LocalDB

Ngày 21/11/2026, bổ sung lượt `ac09-expanded-20261121-062029Z-c750534a` trên Windows SQL Server LocalDB 15.0.4382.1 (`SYNTHETIC_NONPRODUCTION`). Gói này giữ nguyên bốn ZIP từ lượt tổng hợp trước và bổ sung [bằng chứng APP-01 cho đủ năm nhánh AC-09 và ca tranh hạn mức](#ac09-full-localdb-evidence).

| Phạm vi | `tenant-a` | `tenant-b` |
|---|---|---|
| Tái lập AC-03 | **84/84** | **84/84** |
| AC-09 tranh hạn mức | **18/18** | **18/18** |
| AC-09 `CASH_60`, `CASH_100` | **10/10 mỗi ca** | **10/10 mỗi ca** |
| AC-09 `CASH_60_CREDIT_20`, `BANK_PENDING`, `CASH_OVERPAY_120` | **15/15 mỗi ca** | **15/15 mỗi ca** |
| AC-21 qua APP-01, từ khoản thu xác nhận trong fixture | **25/25**, gửi lại **4/4** | **25/25**, gửi lại **4/4** |
| Khôi phục AC-03 | **84/84**, đối soát xong sau 441,1 ms | Đối chứng, **61 bảng** giữ nguyên dấu vân tay |

- **Gói ZIP cập nhật** [PREPROD-bo-bang-chung-cap-nhat-AC09.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-expanded-20261121-062029Z-c750534a/PREPROD-bo-bang-chung-cap-nhat-AC09.zip).
- **SHA-256 của ZIP** `CA1981DCDA8D37AE7F39F46E133A7A9D0E1576E5B92D6092D5B746E907EFDC12`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="preprod-four-steps-bundle"></a>
## Bộ bằng chứng PREPROD cập nhật sau bốn bước

Ghi nhận ngày 21/11/2026. ZIP gồm bốn gói thành phần nguyên bản, bản tóm tắt `tong-hop.md` và `manifest.json` chứa SHA-256 từng gói. Kết quả được ghi nhận trên Windows SQL Server LocalDB 15.0.4382.1, môi trường `SYNTHETIC_NONPRODUCTION`.

| Bước | `tenant-a` | `tenant-b` |
|---|---|---|
| [1. Tái lập từ checkout sạch](#ac03-ac09-clean-checkout) | AC-03 **84/84**, AC-09 tranh hạn mức **18/18** | AC-03 **84/84**, AC-09 tranh hạn mức **18/18** |
| [2. AC-09 thu tiền mặt](#ac09-cash-localdb-evidence) | `CASH_60` **10/10**, `CASH_100` **10/10** | `CASH_60` **10/10**, `CASH_100` **10/10** |
| [3. AC-21 qua APP-01](#ac21-app-localdb-evidence) | **5/5 mốc**, **25/25 chỉ số**, **4/4 lệnh gửi lại** giữ nguyên SQL | **5/5 mốc**, **25/25 chỉ số**, **4/4 lệnh gửi lại** giữ nguyên SQL |
| [4. Khôi phục AC-03](#ac03-localdb-restore-evidence) | Bản khôi phục riêng khớp **7/7 mốc**, **84/84 chỉ số** | Tenant đối chứng, dấu vân tay **61 bảng** giống nhau trước và sau |

Ở cả hai tenant, ca AC-09 bán 100 thu 60 cho công nợ 40, hạn mức còn 60 và tiền giữ hộ tăng 60. Thu 100 cho kết quả lần lượt 0, 100 và 100. AC-21 bắt đầu từ fixture khoản thu đã xác nhận và thực hiện bốn lệnh tiếp theo qua HTTP. Câu lệnh khôi phục AC-03 mất 172,5 ms, đến khi hoàn tất đối soát mất 441,1 ms.

**Phạm vi lịch sử của gói này** còn thiếu `CASH_60_CREDIT_20`, `BANK_PENDING` và `CASH_OVERPAY_120`. [Lượt bổ sung AC-09](#ac09-full-localdb-evidence) ghi kết quả ba nhánh qua APP-01 trên LocalDB.

- **Gói ZIP cập nhật** [PREPROD-bo-bang-chung-4-buoc-LocalDB.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/preprod-four-steps-20261121/PREPROD-bo-bang-chung-4-buoc-LocalDB.zip).
- **SHA-256 của ZIP** `CA75BF21EF48AB7808978D2257707056A973B5DBE6EFCC64C9B2F17932DB84EC`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="preprod-ac03-ac09-ac21-bundle"></a>
## Bộ bằng chứng tổng hợp AC-03, AC-09 và AC-21

Ghi nhận ngày 21/11/2026. Bộ gồm một workbook tổng hợp và ba gói bằng chứng gốc. Các kết quả dưới đây được quan sát trên **Windows SQL Server LocalDB** (`SYNTHETIC_NONPRODUCTION`).

| Ca | `tenant-a` | `tenant-b` | Phạm vi kiểm chứng |
|---|---:|---:|---|
| [AC-03](#ac03-localdb-evidence) | **84/84** | **84/84** | Luồng APP-01 qua 7 mốc, đối soát chứng từ và bút toán cấu thành |
| [AC-09](#ac09-localdb-evidence) | **18/18** | **18/18** | Hai lệnh tranh cùng hạn mức và gửi lại lệnh trong APP-01 |
| [AC-21](#ac21-localdb-evidence) | **25/25** | **25/25** | Gói tổng hợp chứa lượt chạy đặc tả SQL-03B. [Lượt kiểm chứng bổ sung qua APP-01](#ac21-app-localdb-evidence) có gói riêng. |

- **Gói ZIP trên laptop** [PREPROD-bo-bang-chung-AC03-AC09-AC21-20261121-053811Z.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/preprod-ac03-ac09-ac21-20261121-053811Z/PREPROD-bo-bang-chung-AC03-AC09-AC21-20261121-053811Z.zip)
- **SHA-256 của ZIP** `9944D6DDF421D79F99B560008A3CE2FA9D602C43B96BB3BEADB1B47B85934663`
- **PREPROD vật lý** `NOT_VERIFIED` cho cả ba ca.

<a id="ac03-ac09-clean-checkout"></a>
## Tái lập AC-03 và AC-09 từ checkout sạch

Ngày 21/11/2026, chạy từ commit `bed914860c1e023d21d582589665c9cc02b30ac2` trong một bản clone local riêng, ở detached HEAD. Git xác nhận checkout sạch trước khi build và sau lượt đối soát. Môi trường `SYNTHETIC_NONPRODUCTION` sử dụng .NET SDK 10.0.404 và Windows SQL Server LocalDB 15.0.4382.1. Mã lượt chạy `repro-20261121-054558Z-87882212`.

| Ca | `tenant-a` | `tenant-b` | So với gói gốc |
|---|---:|---:|---|
| AC-03 | **84/84** | **84/84** | Khớp số thực tế SQL, số kỳ vọng và tổng dòng cấu thành ở cả 7 mốc. Mỗi tenant khớp thêm 174 dòng cấu thành và 27 dòng trong sheet chứng từ phát sinh. |
| AC-09 | **18/18** | **18/18** | Mỗi tenant có 2 lệnh cùng chờ SQL, 1 ghi sổ, 1 bị từ chối `B_CREDIT_INSUFFICIENT` với 0 chứng từ và bút toán. Khớp 7 bút toán của lệnh được ghi. Hai lần gửi lại cùng ID và dữ liệu tenant đối chứng giữ nguyên dấu vân tay SQL trong từng lượt. |

AC-03 dùng seed `AC03-v1` và RunId mới `0811d8e7de284efebc677d85cebf3188`. AC-09 giữ phạm vi `concurrencyNegative` và đạt 2/2 ca Release. Phép đối chiếu dùng các giá trị nghiệp vụ, loại và mã chứng từ ổn định, dòng sổ và kết quả lệnh. GUID, thời điểm chạy và bên thắng trong cuộc tranh hạn mức có thể khác giữa hai lượt.

- **Gói tái lập trên laptop** [PREPROD-AC03-AC09-clean-checkout.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/repro-20261121-054558Z-87882212/PREPROD-AC03-AC09-clean-checkout.zip). Gồm Excel và JSON AC-03, JSON và TRX AC-09, thông tin checkout, mã đối chiếu và kết quả `comparison.json`.
- **SHA-256 của ZIP** `69C7BC521504CEDB88E76BCE1179563B2DDB8335C8940CCCFD547451B9F92793`.
- **Hồi quy .NET Release** 306/306 đạt, 0 lỗi và 0 bỏ qua trên cùng checkout. [TRX hồi quy](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/repro-20261121-054558Z-87882212/regression/FmcgDms.Api.Tests_net10.0_x64.trx).
- **Mốc so sánh** [Bộ bằng chứng tổng hợp gốc](#preprod-ac03-ac09-ac21-bundle), SHA-256 `9944D6DDF421D79F99B560008A3CE2FA9D602C43B96BB3BEADB1B47B85934663`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="ac03-localdb-evidence"></a>
## Bằng chứng đối soát AC-03 trên LocalDB

Ghi nhận ngày 21/11/2026 cho phiên `AC03Complete`, RunId `0fb94c54cd0047698b7306697f8f63b4`, seed `AC03-v1`. Phiên chạy trên **Windows SQL Server LocalDB** trong môi trường `SYNTHETIC_NONPRODUCTION`. Source ghi trong workbook là `22e70256b99a759bd8c27d296206c0a41960da49+working-tree`.

| Tenant | Mốc đã đối soát | Kết quả trên LocalDB |
|---|---:|---|
| `tenant-a` | 7/7 | **84/84 chỉ số khớp** |
| `tenant-b` | 7/7 | **84/84 chỉ số khớp** |

Ở cả bảy mốc từ `T0` đến `T6_RETURN`, số đọc từ SQL bằng số kỳ vọng AC-03 và bằng tổng các dòng cấu thành. Gói chứa hai workbook và bản tóm tắt bảy mốc, kèm SHA-256 của từng workbook.

- **Gói ZIP trên laptop** [AC03-goi-bang-chung-LocalDB-20261121-050644Z.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/0fb94c54cd0047698b7306697f8f63b4/AC03-goi-bang-chung-LocalDB-20261121-050644Z.zip)
- **SHA-256 của ZIP** `2A84DA8DD761AF7D04CD32F227227780631C616B4A69FADE33B67429635C99BC`
- **PREPROD vật lý** `NOT_VERIFIED`. Phạm vi bằng chứng là đối soát nghiệp vụ AC-03 trên LocalDB. Nghiệm thu B.1/B.9 vẫn cần bằng chứng từ môi trường PREPROD vật lý.

<a id="ac03-localdb-restore-evidence"></a>
## Diễn tập sao lưu và khôi phục AC-03 trên LocalDB

Ngày 21/11/2026, dựng phiên mới `AC03Complete` với RunId `204243739fa54a03bb1c50176de0fa3b`, seed `AC03-v1` và source `bed914860c1e023d21d582589665c9cc02b30ac2` từ checkout sạch. Môi trường `SYNTHETIC_NONPRODUCTION`, Windows SQL Server LocalDB 15.0.4382.1. Sao lưu database `tenant-a` của phiên bằng `FULL COPY_ONLY, CHECKSUM`, kiểm tra `RESTORE VERIFYONLY WITH CHECKSUM`, rồi khôi phục sang database riêng `Fmcg_Ac03Restore_204243739fa54a03bb1c50176de0fa3b` với tệp dữ liệu và nhật ký riêng.

| Mốc đo | Thời gian thực đo |
|---|---:|
| Câu lệnh sao lưu FULL | 93,6 ms |
| Câu lệnh `RESTORE DATABASE` | 172,5 ms |
| Từ lúc bắt đầu restore đến khi mở được kết nối và xác minh quyền sở hữu | 181,1 ms |
| Từ lúc bắt đầu restore đến khi hoàn tất đối soát và kiểm tra tenant đối chứng | 441,1 ms |

- **`tenant-a`** khớp **84/84 chỉ số tại 7/7 mốc** giữa báo cáo trước sao lưu, snapshot khôi phục, số tính lại từ sổ theo từng mốc và oracle. Cuối chuyến, kho 55, xe 0, giá vốn 450, công nợ A/B 375/100 và quỹ 200. Toàn bộ 61 bảng người dùng với 124 dòng khớp dấu vân tay dữ liệu giữa nguồn và bản khôi phục.
- **`tenant-b`** giữ nguyên dấu vân tay từ trước sao lưu đến sau đối soát, trên 61 bảng và 124 dòng. SHA-256 `ADD92B2359772A9BAF8272AE4913B15BEC6007F6F5B1571C770DC9D0C1584F3D`.
- **Gói bằng chứng** [PREPROD-AC03-LocalDB-restore.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac03-restore-20261121-060703Z-7c2b4fc6/PREPROD-AC03-LocalDB-restore.zip). Gồm tệp `.bak`, báo cáo đối soát 84 ô, dấu vân tay chi tiết, báo cáo nguồn và kịch bản diễn tập. SHA-256 của ZIP `82FCDF8E89FD2366ED00171AF5E5F65A430646B124C266F91D7E6787EAF37D23`. SHA-256 của `.bak` `B97EC6231B3030EBF4D73FCF1DC3BB399D3EA7F4EA5E73C9F72D071CC83C0009`.
- **Bàn giao** Bản khôi phục ở trạng thái `ONLINE`. Hai database nguồn thử nghiệm đã được dọn qua lệnh có kiểm tra quyền sở hữu và nonce sau khi lưu bằng chứng.
- **PREPROD vật lý** `NOT_VERIFIED`. Các số đo thuộc lần khôi phục bản FULL trên cùng laptop. PITR, khôi phục ngoài máy và RPO/RTO PREPROD chưa được đo trong lượt này.

<a id="ac09-localdb-evidence"></a>
## Bằng chứng AC-09 trên LocalDB — hai lệnh tranh cùng hạn mức

Ghi nhận ngày 21/11/2026 cho Run ID `ac09-20261121-051210Z-0d9ced4b`, source `22e70256b99a759bd8c27d296206c0a41960da49+working-tree`. Bài test Release chạy trên hai database Windows SQL Server LocalDB, môi trường `SYNTHETIC_NONPRODUCTION`. Ca này đối chiếu nhánh `concurrencyNegative` trong [oracle AC-09](../../../business/reference-data/expected-results/additional-oracles.json).

| Tenant | Lệnh cùng chờ SQL | Ghi sổ / Từ chối | Chỉ số SQL khớp |
|---|---:|---:|---:|
| `tenant-a` | 2 | 1 / 1 | **18/18** |
| `tenant-b` | 2 | 1 / 1 | **18/18** |

Mỗi lệnh cần 80 từ cùng hạn mức 100. Trên mỗi tenant, lệnh được ghi dùng 80, còn 20, tăng công nợ B 80 và tăng tiền giữ hộ 20. Lệnh còn lại trả `B_CREDIT_INSUFFICIENT`, có 0 chứng từ, 0 dòng chứng từ và 0 bút toán. Gửi lại từng ID giữ nguyên dữ liệu SQL, kể cả database tenant đối chứng.

- **Gói ZIP trên laptop** [AC09-goi-bang-chung-LocalDB-20261121-051210Z-0d9ced4b.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-20261121-051210Z-0d9ced4b/AC09-goi-bang-chung-LocalDB-20261121-051210Z-0d9ced4b.zip)
- **SHA-256 của ZIP** `1939833BECE719DE6CF528B0C8BD7C8B043DF01FFB3C06738AACD0370F446B06`
- **Nội dung** Hai workbook, bản tóm tắt, hai báo cáo JSON đọc từ SQL và TRX Release 2/2 ca đạt.
- **PREPROD vật lý** `NOT_VERIFIED`. Gói ghi nhận ca tranh chấp và gửi lại lệnh của AC-09 trên LocalDB.

<a id="ac09-cash-localdb-evidence"></a>
## AC-09 bổ sung hai ca thu tiền mặt trên LocalDB

Ngày 21/11/2026, lượt `ac09-cash-20261121-055136Z-871c98dc` chạy lệnh bán hàng B của APP-01 qua HTTP trên hai database Windows SQL Server LocalDB 15.0.4382.1, môi trường `SYNTHETIC_NONPRODUCTION`. Source `bed914860c1e023d21d582589665c9cc02b30ac2+working-tree`; ZIP lưu bản source bài test đã chạy. Mỗi ca bán 100, có hạn mức đầu kỳ 100 và chứng cứ tiền mặt đã xác minh. Đơn vị `SYNTHETIC_UNITLESS`. [Bộ số kỳ vọng AC-09](../../../business/reference-data/expected-results/additional-oracles.json).

| Tenant | Ca | Công nợ B cuối | Hạn mức đã dùng | Hạn mức còn | Tăng tiền giữ hộ | Chỉ số SQL khớp |
|---|---|---:|---:|---:|---:|---:|
| `tenant-a` | `CASH_60` | 40 | 40 | 60 | 60 | 10/10 |
| `tenant-a` | `CASH_100` | 0 | 0 | 100 | 100 | 10/10 |
| `tenant-b` | `CASH_60` | 40 | 40 | 60 | 60 | 10/10 |
| `tenant-b` | `CASH_100` | 0 | 0 | 100 | 100 | 10/10 |

Cả bốn ca trả `B_SALE_DELIVERY_POSTED`, tiền vào quỹ tăng 0 và SQL ở tenant đối chứng giữ nguyên dấu vân tay. Bộ test Release đạt 6/6, gồm bốn ca thu tiền mặt và hai ca tranh cùng hạn mức chạy lại.

| Nhánh ngoài phạm vi lượt chạy hai ca tiền mặt | Kỳ vọng nghiệp vụ | Khoảng trống tại thời điểm chạy |
|---|---|---|
| `CASH_60_CREDIT_20` | Công nợ 20, hạn mức còn 80, tiền giữ hộ tăng 60 | Lệnh bán B cần thêm khả năng áp dụng 20 tín dụng khách hàng đã giữ trước. |
| `BANK_PENDING` | Công nợ 100, hạn mức còn 0, tiền giữ hộ tăng 0 | Lệnh bán B hiện yêu cầu chứng cứ `CASH` ở trạng thái `VERIFIED`. Cần đường xử lý khoản chuyển khoản đang chờ với giá trị áp dụng bằng 0. |
| `CASH_OVERPAY_120` | Công nợ 0, hạn mức còn 100, tín dụng khách hàng mới 20, tiền giữ hộ tăng 120 | Lệnh bán B hiện chặn tiền nhận vượt giá trị bán. Cần ghi riêng số thực thu, số áp dụng và phần thu thừa. |

- **Gói bằng chứng** [PREPROD-AC09-cash-LocalDB.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-cash-20261121-055136Z-871c98dc/PREPROD-AC09-cash-LocalDB.zip). Gồm 6 báo cáo SQL JSON, TRX, nhật ký và source bài test.
- **SHA-256 của ZIP** `CD38922FAFD80F90B71774EEBFBC279D2367FCE3BDCC4B631DEF966EFE7297B0`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="ac09-full-localdb-evidence"></a>
## AC-09 qua APP-01 trên LocalDB — tín dụng đã giữ, chuyển khoản chờ và thu thừa

Lượt `ac09-expanded-20261121-062029Z-c750534a` chạy từ `bed914860c1e023d21d582589665c9cc02b30ac2+working-tree`. ZIP lưu source đã thay đổi trong lượt này. Lệnh bán B, ghi sổ và tra audit chạy qua API APP-01 synthetic. Tiền mặt có chứng cứ xác minh, còn chứng cứ chuyển khoản đang chờ và lệnh giữ tín dụng được dựng bằng fixture. Khoản tín dụng dùng ở nhánh hỗn hợp có nguồn từ một giao dịch thu thừa đã ghi trước đó qua APP-01.

| Nhánh | Công nợ B cuối | Hạn mức còn | Tăng tiền giữ hộ | Tín dụng khách hàng | `tenant-a` | `tenant-b` |
|---|---:|---:|---:|---|---:|---:|
| `CASH_60_CREDIT_20` | 20 | 80 | 60 | Dùng 20 đã giữ | **15/15** | **15/15** |
| `BANK_PENDING` | 100 | 0 | 0 | 0 | **15/15** | **15/15** |
| `CASH_OVERPAY_120` | 0 | 100 | 120 | Sinh mới 20 | **15/15** | **15/15** |

Các giá trị dùng đơn vị giả lập. Chuyển khoản chờ dùng 0 tiền thu, không tạo phiếu thu. Thu 120 áp dụng 100 vào công nợ và ghi riêng 20 tín dụng. Cả sáu lượt gửi lại cùng ID giữ nguyên SQL, tenant đối chứng giữ nguyên dấu vân tay trong từng ca. Gửi lệnh mới với chứng cứ đã dùng hoặc khoản giữ đã tiêu đều bị từ chối. Bộ test Release đạt **318/318**, 0 lỗi và 0 bỏ qua.

- **Gói chi tiết** [PREPROD-AC09-full-local-payment.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac09-expanded-20261121-062029Z-c750534a/PREPROD-AC09-full-local-payment.zip). Gồm 12 báo cáo SQL cho hai tenant, đối chiếu với oracle, TRX và source.
- **SHA-256 của ZIP** `70D7F017637E699F6B21EE54F354C59254AA2CA400E6DA839F898CEAAD5B5583`.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="ac21-localdb-evidence"></a>
## Bằng chứng AC-21 trên LocalDB — phân bổ tiền thu và bàn giao quỹ

Ghi nhận ngày 21/11/2026 cho Run ID `ac21-20261121-052225Z`. Hai database riêng chạy [đặc tả SQL thực thi SQL-03B](https://github.com/chung6a8m/fmcg-distribution-sql/tree/7c61ac495aed6220b7d234efa910b377e92f70d4) trên **Windows SQL Server LocalDB 15.0.4382.1**, môi trường `SYNTHETIC_NONPRODUCTION`. Các checkpoint được so với [golden oracle AC-21](../../../business/reference-data/expected-results/golden-oracles.json).

| Tenant | Mốc khớp | Chỉ số tiền khớp | Gửi lại cùng ID |
|---|---:|---:|---:|
| `tenant-a` | 5/5 | **25/25** | 4/4 giữ nguyên dữ liệu SQL |
| `tenant-b` | 5/5 | **25/25** | 4/4 giữ nguyên dữ liệu SQL |

Hai lần phân bổ 20.000 và 15.000 đưa công nợ từ 140.000 xuống 105.000 và phần tiền thu chưa phân bổ từ 60.000 xuống 25.000. Đề nghị bàn giao 50.000 có 0 bút toán tiền. Xác nhận chuyển 50.000 từ sổ tiền giữ hộ sang quỹ, để lại lần lượt 10.000 và 50.000. Các số là VND giả lập. Dấu vân tay SQL của tenant đối chứng giữ nguyên trong từng lượt chạy.

- **Gói ZIP trên laptop** [AC21-goi-bang-chung-LocalDB-20261121-052225Z.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac21-20261121-052225Z/AC21-goi-bang-chung-LocalDB-20261121-052225Z.zip)
- **SHA-256 của ZIP** `8228EA83BB610CE65A31BF871471427738E6EB7F7CFAA5C25833E68AB6DA81C3`
- **Nội dung** Hai workbook, bản tóm tắt năm mốc, hai báo cáo SQL JSON và bản SQL của từng ca đã chạy.
- **Phạm vi gói gốc** Hai lượt chạy thực thi thủ tục của SQL-03B trên scratch LocalDB. Phạm vi ứng dụng được ghi ở lượt bổ sung dưới đây. PREPROD vật lý `NOT_VERIFIED`.

<a id="ac21-app-localdb-evidence"></a>
## AC-21 E2E qua APP-01 trên LocalDB

Ngày 21/11/2026, lượt `ac21-app-20261121-060323Z-b778ac93` chạy trên hai database scratch riêng của Windows SQL Server LocalDB 15.0.4382.1 (`SYNTHETIC_NONPRODUCTION`). Source `bed914860c1e023d21d582589665c9cc02b30ac2+working-tree`; ZIP lưu các tệp source thay đổi đã dùng. Lệnh mới `POST /api/synthetic/app01d/commands/allocate-cash-receipt` dùng danh tính sales thuộc tenant do máy chủ xác thực. Mỗi lần phân bổ ghi chứng từ và một bút toán giảm công nợ, đồng thời cập nhật số áp dụng của khoản thu và khoản phải thu trong cùng giao dịch.

Mốc `CONFIRMED` lấy khoản thu đã xác minh và công nợ mở từ fixture SQL. Hai lệnh phân bổ, đề nghị bàn giao và xác nhận của thủ quỹ đều đi qua HTTP của ứng dụng. Số dưới đây đọc từ SQL và khớp cả [oracle AC-21](../../../business/reference-data/expected-results/golden-oracles.json) lẫn gói SQL-03B gốc. Đơn vị VND giả lập.

| Mốc | Công nợ | Tiền thu chưa phân bổ | Tiền giữ hộ | Quỹ | Đã phân bổ |
|---|---:|---:|---:|---:|---:|
| `CONFIRMED` | 140.000 | 60.000 | 60.000 | 0 | 0 |
| `ALLOC_20K` | 120.000 | 40.000 | 60.000 | 0 | 20.000 |
| `ALLOC_15K` | 105.000 | 25.000 | 60.000 | 0 | 35.000 |
| `OFFER_50K` | 105.000 | 25.000 | 60.000 | 0 | 35.000 |
| `ACCEPT_50K` | 105.000 | 25.000 | 10.000 | 50.000 | 35.000 |

| Tenant | Mốc khớp | Chỉ số tiền khớp | Gửi lại cùng ID |
|---|---:|---:|---:|
| `tenant-a` | 5/5 | 25/25 | 4/4 trả kết quả gốc, SQL giữ nguyên |
| `tenant-b` | 5/5 | 25/25 | 4/4 trả kết quả gốc, SQL giữ nguyên |

Hai tenant đều ghi đúng hai bút toán phân bổ, 0 bút toán khi đề nghị và 2 bút toán khi xác nhận. Kiểm tra thêm cùng ID khác số tiền trả HTTP 409, sai vai trò trả 403, phân bổ vượt số dư và tham chiếu sang tenant khác đều bị từ chối. Lượt nghiệp vụ hợp lệ giữ nguyên dấu vân tay SQL ở tenant đối chứng.

- **Gói bằng chứng** [PREPROD-AC21-app-LocalDB.zip](C:/Users/pdchung/AppData/Local/FmcgDms/outputs/ac21-app-20261121-060323Z-b778ac93/PREPROD-AC21-app-LocalDB.zip). Gồm hai báo cáo SQL JSON, đối chiếu gói gốc, TRX, nhật ký và source đã chạy.
- **SHA-256 của ZIP** `869915FAC008417A045379FF77E34E4A22323E01A9891A4A0EAB472D99501F6D`.
- **Hồi quy .NET Release** 312/312 đạt, 0 lỗi và 0 bỏ qua.
- **PREPROD vật lý** `NOT_VERIFIED`.

<a id="lab-e01"></a>
## LAB-E01 — Đồng bộ #73/#59 và phạm vi lab

**Status:** `SOURCE_MERGED (DOCS_ONLY)` qua [PR #87](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/87), final source [9bd8099](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/9bd8099f0ce6b0cb953cbdea7e30e6566a319709), merge [f736d61](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/f736d618fd4d86a3324c19117ff7b5b3ec415bc9). `LAB_EXECUTED` và physical PREPROD vẫn `NOT_VERIFIED`. **Source/API dependencies:** không có.

**Dependencies:** không phụ thuộc runtime mới. **Deliverable:** một PR tài liệu có trạng thái và lineage đúng; hoặc cập nhật đúng PR #73 sau khi chủ sở hữu nhánh cho phép tích hợp, không tạo hai closeout cạnh tranh.

**Modify (existing):** các file của PR #73: `README.md`, `docs/closeout/2026-11-18-app-02b9-source-only.md` (dùng lại đường dẫn được đề xuất trong #73), `docs/roadmap/app-00-app-01.md`, `app-02.md`, `app-02b-preprod-foundation.md`, `app-02b7-backup-pitr-safe-restore.md`, `app-02b8-immutable-audit-operations.md`, `app-02b9-independent-preprod-oracle.md`. Không chép phiên bản nhánh cũ đè main.

**Scope proposal:** [proposed-app02b-local-lab-scope.md](../../decisions/proposed-app02b-local-lab-scope.md) với trạng thái `PROPOSED / NO_PHYSICAL_ACCEPTANCE_AMENDMENT`. Tách phần thiết kế được roadmap chọn khỏi Owner approval chưa có: approved environment, identities, backup target, retention/key history, operational fault windows. Nếu sau này Owner chọn ngoại lệ solo physical thì tạo decision riêng có phạm vi và ngày, không sửa lịch sử B.1 hoặc thừa kế B.4/#59.

- [x] Đối chiếu live #53/#56/#59/#60/#64/#65/#67/#68/#75 và #73/#74. Ghi exact SHAs, trạng thái và updated times trong [B.9 source closeout](../../closeout/2026-11-18-app-02b9-source-only.md).
- [x] Đổi trạng thái hiện tại #59 thành CLOSED / `WITHDRAWN_ADVISORY_SCOPED_RISK_ACCEPTED / NOT_PATCHED`; giữ ngày hết hạn và scope. Giữ tất cả failures cũ theo run/attempt.
- [x] Run `44323688504` hiện success attempt 2 trên `216ce719...`; diễn đạt attempt 1 fail, attempt 2 pass. Không sửa lịch sử thành chưa từng fail.
- [x] Ghi #75 và #68 OPEN, root causes unresolved. Giữ 30 physical cases NOT_RUN/NOT_VERIFIED. GitHub #56 thực tế CLOSED; physical acceptance vẫn NO_GO.
- [x] Diff docs, 325 relative links và 27 URL evidence đã được kiểm tra trên final HEAD theo mục Verification của PR #87. [CI final HEAD](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/87/checks) đạt 11/11 workflow và 21/21 job. Evidence của #61 và #73 giữ theo source riêng.

**Exit:** đã hoàn tất gate merge tài liệu. Trạng thái physical acceptance vẫn `NOT_VERIFIED`.

<a id="lab-e02"></a>
## LAB-E02 — Diagnostics cho LocalDB và A11-09

**Status:** SOURCE_REVIEW trong [PR #88](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/88). PR ghi final HEAD, CI và giới hạn evidence. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** Không có dependency source mới. Setup, approval và live evidence là điều kiện bổ sung.

**Modify (existing):** `tests/FmcgDms.Api.Tests/App01e4TcpRestartHarness.cs`, `App02a3WorkerTests.cs`, `App02a4OracleCases.cs`, `App02a4IndependentSqlOracle.cs`, `.github/workflows/app00a.yml`. **Create:** `tests/FmcgDms.Api.Tests/FailureDiagnosticsTests.cs`, `scripts/Collect-LocalDbDiagnostics.ps1`.

**Interfaces:** bổ sung `App01e4ApiProcess.CaptureFailureAsync(string failureId, string stage, CancellationToken ct)` trả về đường dẫn artifact đã redacted; giữ nguyên `StartAsync(...)`, `Client(...)`, PID và disposal semantics. Nhận context hiện có của A4 report (`TestRunId`, `SourceHeadSha`, `WorkflowSha`, tenant, group); process context thêm PID/start/stop UTC. `IntakeA3` vẫn bắt buộc HTTP 202 và `RECEIVED_COMMITTED`.

- [ ] Test `Failure_diagnostics_redacts_bearer_cookie_connection_secret_and_preserves_correlation`: seed sentinel secrets trong stdout/stderr/headers/body; không còn sentinel trong artifact; còn exception type/SQL error number/TraceId/PID/UTC cần chẩn đoán.
- [ ] Thay ReadToEnd-only process capture bằng bounded ring buffer được drain liên tục, có snapshot khi process còn sống. Lưu cả hai kênh mà không block child, giới hạn kích thước và record truncated=true khi cắt.
- [ ] Khi intake khác 202: chụp method/path không query secret, status, correlation, process stage và redacted server exception; sau đó FAIL assertion cũ. Không thay status, không retry business request.
- [ ] Khi startup LocalDB lỗi: thu `sqllocaldb info`, instance log metadata, executable versions, service/process/error state trong bounded timeout; không in connection secrets; giữ lỗi gốc và upload artifact bằng `if: always()`.
- [ ] Run `dotnet test --solution FmcgDms.slnx -c Release --minimum-expected-tests 296 -- --report-trx`; CI giữ mọi audit/browser/PWA stage. Thêm tests làm tăng count thì ghi baseline mới, không giảm floor.

**Exit:** lỗi được quan sát có nguồn gốc; issue #75/#68 vẫn OPEN. Diagnostics PR có thể được review độc lập với việc sửa root cause.

<a id="lab-e03"></a>
## LAB-E03 — Fix LocalDB bootstrap có root cause

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E02. Setup, approval và live evidence là điều kiện bổ sung.

**Files:** tạo `scripts/Initialize-LocalDb.ps1` chỉ khi diagnostics chứng minh orchestration là điểm lỗi; sửa đúng bootstrap step trong `.github/workflows/app00a.yml` và helper sở hữu lỗi. Regression đặt cạnh helper mới hoặc test fixture hiện có. Không đổi API/business transaction để chữa lỗi bootstrap.

**Input/output:** fault record từ LAB-E02 gồm error class, instance identity, SDK/LocalDB version, timing và original attempt. Helper nhận tên scratch instance đã qua existing guard và deadline; trả readiness/error code, attempt history và log refs đã redacted.

- [ ] Phân loại lỗi LocalDB từ original/new artifacts; tái hiện có kiểm soát. Nếu chưa tái hiện/xác định được nguyên nhân, ghi INVESTIGATION_BLOCKED với chứng cứ còn thiếu; không kết luận “fixed” vì rerun xanh.
- [ ] Viết regression thất bại trước fix. Chỉ với lỗi transient đã phân loại mới dùng tối đa 3 lần init/health trong tổng deadline 60 giây, giữ mọi attempt.
- [ ] Không retry migrations, HTTP intake hoặc ledger mutation trong helper. Không DROP/reset instance người dùng hoặc xóa log để run sạch; nonce/name/instance guards vẫn bắt buộc.
- [ ] Chạy cold/warm initialization, unavailable engine, permission/version mismatch, deadline exhausted và foreign-instance refusal. Error phải giữ nguyên cause và exit nonzero khi hết budget.
- [ ] Chạy full APP00A trên exact final HEAD theo workflow; inspect artifact của mọi attempt, không hạ minimum hoặc thêm continue-on-error.

**Exit:** root cause + deterministic regression + fix + final-source CI có liên kết. #75 chỉ xử lý xong nhánh LocalDB ở đây; HTTP 500 tiếp tục thuộc LAB-E04. Nếu bằng chứng dẫn tới dependency khác, sửa roadmap/PR scope có giải thích, không đoán file lỗi.

<a id="lab-e04"></a>
## LAB-E04 — Fix HTTP 500 sau OS restart

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E02. Setup, approval và live evidence là điều kiện bổ sung.

**Files:** `tests/FmcgDms.Api.Tests/App01e4TcpRestartHarness.cs`, `App02a3WorkerTests.cs`, `App02a4OracleCases.cs` và code sở hữu lỗi được LAB-E02 chỉ ra. Production file không thể chốt trước root cause; PR phải ghi stack/transaction/SQL evidence dẫn tới đúng file. Không gom fix LocalDB vào PR này.

**Contract:** A11-09 pending C → dừng PID cũ → PID mới → P intake → outbox recovery. Intake vẫn HTTP 202/RECEIVED_COMMITTED; không đổi expected result, không retry business request tự động, không tăng timeout để che race.

- [ ] Lập fault record tenant A/B, Worker off/on, old/new PID, SQL transaction/error, correlation và phase của HTTP 500.
- [ ] Tạo regression deterministic (barrier/latch hoặc injected fault có giới hạn) FAIL trên code cũ, PASS sau fix. Không suy ra cùng root cause với #68 chỉ từ tên scenario.
- [ ] Sửa ở component sở hữu race/state/connection; giữ same-ID semantics, pending C và toàn bộ SQL oracle equations.
- [ ] Chạy fresh scratch A/B × Worker off/on, cùng nhánh concurrent outbox recovery. Dùng 5 fresh iterations/nhánh làm mẫu kiểm chứng ban đầu, ghi counts/limits; đây không phải SLO hay chứng minh xác suất lỗi bằng 0.
- [ ] Full APP00A + relevant dedicated workflows PASS trên exact HEAD; retained failing attempt vẫn được liên kết. Cross-link #68 chỉ khi cause thực sự khớp; #75 cần disposition cả E03 lẫn E04.

**Exit:** nguyên nhân được chứng minh và regression đúng semantics, hoặc giữ BLOCKED có artifact cụ thể. Một rerun attempt thành công không đủ close #75.

<a id="lab-e05"></a>
## LAB-E05 — Run context, catalog và schema

**Status:** SOURCE_REVIEW. [V1 contract and usage](lab-e05-contract.md). **PR boundary:** một PR độc lập để review. **Source/API dependencies:** Không có dependency source mới. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/context.py`, `catalog.py`, `schema.py`, `run.py`, `schemas/lab-evidence-v1.json`, `schemas/lab-run-context-v1.json`, `tests/app02b_lab/test_context.py`, `test_catalog.py`, `test_schema.py`; wrapper `scripts/lab/Invoke-LabAcceptance.ps1`.

**Inputs:** `LabRunContext(runId, environmentId, sourceHeadSha, fixtureSha256, configurationSha256, startedUtc, hostSha256, mode)`; `mode` phải `LOCAL_OPERATOR_LAB`. `runId` do operator tạo một lần dạng `b-lab-<UTC>-<random>`, không derive chỉ từ commit; SHA-256 của public/redacted config, không hash tùy tiện file có secret để công bố. Source là 40-hex SHA, images ghi immutable digest, working tree phải sạch.

**Functions:** `create_context(config_path: Path, evidence_root: Path, source_sha: str, run_id: str) -> LabRunContext`; `load_catalog(fixture: Path) -> dict[str, CaseDefinition]`; `validate_lab_bundle(root: Path) -> LabValidationReport`. Không thay đổi original `tools/app02b9/oracle.py` hoặc fixture để hỗ trợ lab.

**Lab schema:** `schema=APP02B-LAB-EVIDENCE-V1`, `evidenceClass=LOCAL_OPERATOR_LAB`, `humanReview=SOLO_OWNER_SELF_ATTESTED`; `originalGateStatus=NOT_VERIFIED`; `cases` phải đủ 30 IDs. Mỗi case có `technicalResult` PASS/FAIL/BLOCKED/NOT_RUN, `acceptanceStatus=NOT_VERIFIED`, expected symbol từ frozen fixture, actual observations, missingRequirements, positiveRefs/negativeRefs, tenant scope và peer-before/after. B05-02 giữ BLOCKED khi thiếu reviewer thật. Technical PASS không đại diện mọi original acceptance condition; mô tả rõ assertions nào đã đo và independence nào thiếu.

- [ ] Test catalog thiếu/thừa/trùng ID, fixture đổi byte, dirty checkout, invalid source, duplicate JSON property, NaN, path traversal/symlink escape, empty witness => reject.
- [ ] Freeze byte-identical original expected fixture cùng hash trước first mutation; run.json ghi trạng thái PREPARED, original commitment và config digest. Không cho sửa fixture giữa run.
- [ ] Tạo 30 dòng NOT_RUN trước execution. Runner crash/abort vẫn để lại complete coverage index; không chỉ in các case đã chạy.
- [ ] CLI mới: `python tools/app02b_lab/run.py --config <public-config> --selection case:B01-01 --run-id <id> --evidence-root <outside-repo>`; hỗ trợ `gate:G-B01` và `all`; unknown selection fail. Exit 0 chỉ = mọi technical requirement đã chọn hoàn tất, exit 2 = BLOCKED/NOT_RUN, exit 1 = FAIL; report luôn original gates NOT_VERIFIED.
- [ ] Resume chỉ dùng cùng source/config/fixture/environment và admitted DB lineage. Một I08 restore transition đã khai báo được giữ trong run với immutable old→new receipt; không sửa baseline identity. Target drift/lineage không được duyệt phải tạo run mới hoặc BLOCKED. Attempt folder mới, append history; không overwrite failed attempts hoặc tự chọn attempt xanh mà giấu lịch sử.
- [ ] Unit command: `python -m unittest discover -s tests/app02b_lab -p 'test_*.py' -v`.

**Exit:** shared contract ổn định cho mọi collector/driver; chưa cần runtime thật để kiểm chứng validator.

**Contract ownership:** thêm `schemas/lab-environment-v1.json` cho cấu trúc tối thiểu/secret references; LAB-I01 bổ sung platform validation qua extension schema versioned, không đổi tên run fields tùy ý. Schema labels chính xác: `APP02B-LAB-EVIDENCE-V1`, `LOCAL_OPERATOR_LAB`, `SOLO_OWNER_SELF_ATTESTED`. `configurationSha256` hash manifest không chứa secrets, bao gồm danh tính/version của secret references và externally retained authorization record; không công bố hash đoán được của secret values. `hostSha256` đi kèm protected original fingerprint.

<a id="lab-e06"></a>
## LAB-E06 — Collectors SQL/HTTP/network/Vault

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E05, LAB-I04. Setup, approval và live evidence là điều kiện bổ sung.

**Dependencies:** context/catalog; infrastructure/principal setup. **Create:** `tools/app02b_lab/collectors/{sql,http,network,vault}.py`, `sql/fingerprint.sql`, `scripts/lab/Collect-LabRuntime.ps1`, tests cho từng collector. **Reuse:** `scripts/Collect-App02B2PreprodEvidence.sh`; kết quả của nó chỉ là raw input, không tự nâng outcome.

**Interface:** `collect(ctx: LabRunContext, case_id: str, phase: str, target_ref: str) -> WitnessRef`. `WitnessRef` gồm ref, kind, relativePath, sha256, rawArtifactPath/hash, observedUtc, observerPrincipalRef, targetIdentity và vantagePoint. Collector ghi raw artifact trước, fsync/atomic rename rồi mới tạo witness descriptor. Chỉ nhận secret handles/paths; không nhận secret values qua command line.

- [ ] SQL observer dùng principal chỉ đọc catalog và dữ liệu cần chứng minh; credential tách runtime. Capture toàn bộ user-table schema + row count + SHA-256 canonical row multiset, marker, binding, principal/effective grant. Bảng không PK phải sort canonical row bytes và giữ multiplicity. Chuẩn hóa NULL, decimals, binary và UTC; không dùng CHECKSUM_AGG làm bằng chứng mật mã.
- [ ] Chụp snapshot SQL nhất quán với isolation được chuẩn bị; detect schema thay đổi trong capture. Mọi table mới phải xuất hiện; read error không chuyển thành 0 rows. Tiến hành cả A→B và B→A khi case cần isolation. Background writes trên peer phải được kiểm soát/ghi nhận, không bỏ table khỏi hash để hết lỗi.
- [ ] HTTP collector phân biệt transport error, TLS rejection, 401/403/409/500 và expected success; không log Authorization/Cookie/Set-Cookie. SQL deny phải được chứng minh bằng actual workload principal, không chỉ observer SELECT.
- [ ] Network witness ghi `vantagePoint=WINDOWS_HOST|OUTSIDE_APP_BRIDGE|PRIVATE_NONPROXY_PEER|EXTERNAL_INTERNET` và IP/route evidence đã redacted. Không gán Windows NAT probe là Internet observer.
- [ ] Vault collector đối chiếu policy 403, request/audit event ID, key version, outage/revocation/rotation timeline; không lưu response secret. Retain existing CI-only fixtures dưới evidence class riêng.
- [ ] Test fake API200+missingSQL, peer table thêm row, wrong SQL login, expired/untrusted TLS, forged forwarded headers, app token đọc worker path => đúng FAIL/DENY hoặc BLOCKED nếu setup thiếu.

**Exit:** RUNTIME-01/02 và security/secrets drivers có raw witnesses thật của lab; external-network/independent-person gaps vẫn hiện.

**Adapter boundary:** định nghĩa `LabAdapters`/typed protocols trong `tools/app02b_lab/adapters.py`: named HTTP command/query, operator control, SQL read/deny, Vault read-only audit, fault barrier và clock/receiver observations. Chỉ mapping operation enum → implementation được review; không chạy shell/SQL tùy ý lấy từ case JSON. Collectors không có quyền gọi privileged mutation của target đang quan sát. Adapter unavailable => BLOCKED, không tạo witness giả.

<a id="lab-e07"></a>
## LAB-E07 — Exact-source CI inventory

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E03, LAB-E04, LAB-E05. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/ci.py`, `ci-check-map.json`, `scripts/lab/Collect-LabCiEvidence.ps1`, `tests/app02b_lab/test_ci.py`, `.github/workflows/app02b-lab-source.yml`. **Modify:** workflow trigger/dispatch riêng nếu cần đảm bảo final candidate chạy được đủ checks; không nới required checks.

| Required B.9 ID | Existing workflow file |
|---|---|
| APP00A_FULL | `.github/workflows/app00a.yml` |
| APP02B2_RUNTIME | `.github/workflows/app02b2-runtime.yml` |
| APP02B3_IDENTITY | `.github/workflows/app02b3-identity.yml` |
| APP02B4_TENANTS | `.github/workflows/app02b4-tenants.yml` |
| APP02B4_WEB | `.github/workflows/app02b4-web-regressions-with-known-debt.yml` |
| APP02B5_AUTHORIZATION | `.github/workflows/app02b5-authorization.yml` |
| APP02B6_POLICY | `.github/workflows/app02b6-policy-governance.yml` |
| APP02B7_RECOVERY | `.github/workflows/app02b7-recovery.yml` |
| APP02B8_OPERATIONS | `.github/workflows/app02b8-audit-operations.yml` |
| APP02B9_ORACLE | `.github/workflows/app02b9-independent-oracle.yml` |

`security-picomatch-issue59.yml`, lab source CI và workflow B53 sau khi được adopt là **additional mandatory relevant checks**; không thay thế một trong mười ID bằng Security để đủ số 10. B.9 hiện có path-filter nên có thể không chạy trên một SHA dù mười workflow khác xanh.

**Interface:** `collect_ci(repository: str, source_sha: str, check_map: Path, output: Path) -> CiEvidenceIndex`; không ghi token vào output. Dùng GitHub API/gh JSON; đọc tất cả pagination của runs/jobs/artifacts, attempt, conclusion, failed/skipped required steps, expired artifacts và source-bound metadata trong artifact. `SourceHeadSha` khác `WorkflowSha` với synthetic merge phải được kiểm tra qua source/tree lineage theo hợp đồng hiện có; không so mỗi SHA workflow với PR SHA rồi đoán.

- [ ] Tests: missing B9+extra Security; stale SHA; duplicate check ID; PR vs main confusion; successful job nhưng required step skipped; attempt mới fail sau attempt cũ pass; artifact expired/missing/hash mismatch; advisory policy expired; count/test result mismatch. Treat `nowUtc >= 2026-12-19T00:00:00Z` as expired for the current #59 policy; test one tick before, exactly at and after the boundary. A successor policy requires a separately approved version, never an automatic extension.
- [ ] Main source CI chạy Python tests, original B9 12/11, mock adapters và missing-bundle NO_GO; không cần live lab secrets và không tự dựng physical trust roots.
- [ ] Full audit (`npm audit --audit-level=moderate`) và production audit (`npm audit --omit=dev --audit-level=low`) luôn giữ; dependency/expiry đổi cần policy assessment, không tự gia hạn.
- [ ] Chuẩn bị command registry liệt kê từng .NET project và workflow stage; wrapper local không được coi `dotnet test --solution` là toàn bộ B suites. Giữ Microsoft.Testing.Platform syntax từ workflow, không chuyển sang VSTest filter theo trí nhớ.

**Source test command:** `python -m unittest discover -s tests/app02b_lab -p test_ci.py -v`. Khác biệt provenance giữa PR source H và workflow synthetic merge M phải đúng existing contract, không là ngoại lệ mới. Nếu workflow không thể dispatch trên H, PR này bổ sung workflow_dispatch/checkout đúng nguồn và receipt lineage, giữ path/required checks. Không dùng một run của main khác H để điền chỗ trống.

<a id="lab-e08"></a>
## LAB-E08 — Driver G-B01 — security

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-S07, LAB-S08, LAB-I08, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/security.py`, `tests/app02b_lab/test_security_cases.py`. **Scope:** B01-01..06; một driver PR cho G-B01.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Triển khai valid-A → B read/export/write/metadata trên đúng typed query/command boundary; invalid JWT/refresh replay; System Admin không membership; Maker=Checker/stale source; revocation tại API/Worker/CASE COMMIT; old snapshot trước revoke. Cần chứng minh cả hai valid serializations revoke/COMMIT bằng barriers và actual transaction results. Full peer fingerprint không đổi; no-read proof phải có actual deny under workload principal. Restore scenario gọi LAB-I08, không sửa row thủ công làm bằng chứng PITR.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_security_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B01 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e09"></a>
## LAB-E09 — Driver G-B02 — secrets/policy

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-S03, LAB-S07, LAB-S08, LAB-I05, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/secrets_policy.py`, `tests/app02b_lab/test_secrets_policy_cases.py`. **Scope:** B02-01..06; một driver PR cho G-B02.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy real wrong-role Vault403/SQL denial, missing key/lease/outage, key rotation + historical fixture decrypt, L0 backdate/tamper, L1 tenant/effective-window, L2 UNSET/self-check/approved-zero. Tách key retired-for-signing khỏi revoked-for-verification. Preserve raw Vault audit request IDs; driver không đọc key bytes hoặc đổi production threshold. Quay về trạng thái an toàn sau token/key fault; không restore secret cũ để bypass revocation.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_secrets_policy_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B02 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e10"></a>
## LAB-E10 — Driver G-B03 — recovery

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-I08, LAB-E06. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/recovery.py`, `tests/app02b_lab/test_recovery_cases.py`. **Scope:** B03-01..06; một driver PR cho G-B03.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy actual FULL/DIFF/LOG và retained-copy restore; measured PITR; wrong target/binding; old B3/B4 without B5 rewind; original MessageId lost ACK/UNKNOWN; corrupted log/keyless/unauthorized restore. Positive B03-02 phải nối RESTORE+SQL+BACKUP+OUTCOME cùng run, safe API và Worker return. Chạy lỗi ở phase cụ thể của cutover, kiểm tra SUSPENDED/no-dispatch và B data unchanged. Local second copy không được gán off-host.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_recovery_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B03 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e11"></a>
## LAB-E11 — Driver G-B04 — operations

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-O02, LAB-E06, LAB-I08. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/operations.py`, `tests/app02b_lab/test_operations_cases.py`. **Scope:** B04-01..06; một driver PR cho G-B04.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Chạy actual audit tamper/truncation vs original anchor; trace xuyên restart/inbox/outbox; real alert delivery và operator ACK; incident overdue/escalation/review; legal hold chặn purge; eligible diagnostic archive. Chỉ dùng disposable LAB_TEST_ONLY diagnostics khi real approval chưa có. Raw journal/receipt/timestamp lấy từ receiver/SQL; không dựng ACK trong driver. Restore-hold drill gọi I08; durable APP02A/ledger/audit/legal history tuyệt đối không bị TTL purge.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_operations_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B04 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

<a id="lab-e12"></a>
## LAB-E12 — Driver G-B05 — provenance/review

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E07, LAB-E08, LAB-E09, LAB-E10, LAB-E11. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/cases/provenance.py`, `tests/app02b_lab/test_provenance_cases.py`. **Scope:** B05-01..06; một driver PR cho G-B05.

**Driver contract:** `run_case(case_id: str, ctx: LabRunContext, adapters: LabAdapters) -> CaseResult`. Exact expected/negativeInvariant/requiredKinds lấy từ frozen catalog; không hardcode expected theo response vừa nhận. Đọc [case matrix](case-matrix.md). Không đủ dependency thì BLOCKED với missingRequirements; safety invariant sai thì FAIL.

- [ ] Viết negative tests cho thiếu raw witness, wrong run/source/target, fake success và cleanup fault sau exception; driver không được tự điền PASS.
- [ ] Fault dùng barrier/signal có deadline và fault ID, luôn giữ failed attempts. Cleanup chỉ gỡ fault do run này sở hữu; không xóa DB/evidence hoặc sửa expected.
- [ ] Chạy sáu ca của gate với positive và negative witnesses theo case; lấy actual từ collector dưới observer principal khác workload. Cấm privileged SQL trực tiếp tạo application success rồi gọi đó là HTTP/Worker proof.

**Six-case implementation:** Kiểm kê 30+2 và A/B physical identities; kiểm tra reviewer/custodian/source; exact required CI; original 11 mutations; API200 không đủ SQL/restore/alert; đổi HEAD/config/fixture khiến stale signoff invalid. B05-02 trong solo profile phải BLOCKED khi không có independent humans, dù các subchecks kỹ thuật khác xanh. Không tự sinh người, key, review signature hoặc human approval.

**Verification:** `python -m unittest discover -s tests/app02b_lab -p test_provenance_cases.py -v`; sau khi có approved lab và source freeze, `python tools/app02b_lab/run.py --config <public-config> --selection gate:G-B05 --run-id <id> --evidence-root <outside-repo>`. Local run không được gọi là physical PASS. Test mocks chỉ chứng minh driver/validator logic; cần actual lab run để ghi LAB_EXECUTED.

**Additional mutation tests:** retain original B9 fixture and all 11 original negative mutation IDs; add local-class promotion, mixed-run, missing-raw, path escape, changed config, fabricated independent people and older successful CI attempt hiding latest failure. Validate original oracle returns NO_GO for a forged lab-to-physical bundle. Reference the original validator directly; do not fork a relaxed copy.

<a id="lab-e13"></a>
## LAB-E13 — Lab report và conditional closeout

**Status:** PLANNED. **PR boundary:** một PR độc lập để review. **Source/API dependencies:** LAB-E12. Setup, approval và live evidence là điều kiện bổ sung.

**Create:** `tools/app02b_lab/report.py`, `physical_handoff.py`, `tests/app02b_lab/test_handoff.py`, `docs/roadmap/app02b-lab-evidence-handoff.md`. **Dependencies:** shared schema, all relevant drivers/collectors, CI index; runtime/security/recovery prerequisites từ các nhánh khác.

`build_report(bundle: Path, output: Path) -> LabReport` tạo JSON+Markdown: coverage 30+2, failures, blocked dependencies, immutable attempt history, target source SHA, risk/approval gaps và original gates NOT_VERIFIED. `prepare_physical_handoff(...)` chỉ tạo index ứng viên; không ký review, không tự gán independentlyMeasured=true, không đổi local witness class thành physical. Bản physical cần approved environment/trust và các actual witnesses/reviews đáp ứng original contract; bằng chứng lab chỉ được tham chiếu khi reviewer xác minh loại/scope của nó.

- [ ] Test lab bundle dù kỹ thuật xanh vẫn bị original `oracle.evaluate(...)` từ chối; giữ exit2/NO_GO. Test một người với nhiều key/alias không được đánh dấu independent.
- [ ] Retain original eleven mutations `EXPECTED`, `ACTUAL`, `SIGNED_DIFFERENCE`, `MATCH_FLAG`, `PEER_HASH`, `SOURCE_HEAD`, `MISSING_PHYSICAL`, `API_ONLY_FORGED_SUCCESS`, `SKIPPED_REQUIRED_CI`, `STALE_REVIEW`, `FROZEN_FIXTURE_SHA`; thêm lab-class forgery, mixed run IDs, missing raw, path escape và changed config.
- [ ] Freeze runtime source **H** (full source SHA), image digests, migration set, frozen fixture và config trước full run. Thu, hash, ký và archive evidence **ngoài Git**; không commit raw bundle hoặc private trust.
- [ ] Owner/reviewer disposition được gắn H và TestRunId qua receipt/issue evidence reference ngoài source. Nếu sau đó mở docs-only closeout commit **D**, tài liệu phải nói H là source đã chạy; không gọi D đã deployed/accepted. Triển khai D hoặc bất kỳ source mới nào cần original revalidation. Không thêm loophole tree-equivalence vào oracle để né B05-06.
- [ ] Closeout local có thể ghi local mechanism verification và solo attestation; physical acceptance của #56 vẫn NOT_VERIFIED cho đến khi original all30/all5 + runtime2 + independent reviews + off-host recovery + exact-source CI đáp ứng, hoặc Owner có amendment rõ ràng và truthful disposition riêng. Roadmap hiện tại không tự ban hành amendment.
- [ ] Với actual physical run, invoke original `python tools/app02b9/oracle.py --bundle <secure> --fixture tools/app02b9/expected-v1.json --trust-roots <outside-repo-and-bundle> --expected-head <H> --output <secure-report>`. Exit0 chỉ là gate candidate; cần actual evidence inspection/Owner disposition. Production/Pilot, APP02C/D/E, real EV08/P0 giữ nguyên restrictions.

**Closeout checklist:** all 32 source increments have explicit status/PR/SHA; 30 case rows and 2 runtime rows are present; known FAIL/BLOCKED and observer limitations stay visible; #59 scope/expiry is reread at report time; #75 root-cause disposition is linked; #73/B.9 docs are synchronized before any physical closure proposal. Report actual completed subset as local mechanism verification. Do not set a suite-wide PASS when B05-02 or another required case remains BLOCKED.

## Primary project sources

- [Original 30-case contract](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/main/docs/security/app-02b1-acceptance-evidence.md)
- [Frozen fixture](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/tools/app02b9/expected-v1.json)
- [Original physical oracle](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/tools/app02b9/oracle.py)
- [B9 workflow 12 tests/11 mutations](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/.github/workflows/app02b9-independent-oracle.yml)
- [APP00A baseline](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/.github/workflows/app00a.yml)
- [Issue59 pinned risk policy](https://github.com/chung6a8m/fmcg-distribution-dotnet/blob/16d96c71a7967c216ebfa599693807a2b54e7435/docs/security/issue59-conditional-risk-policy.json)
- [Issue75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75), [Issue68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68), [PR73](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/73), [Issue56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56)
