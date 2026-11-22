# APP-02E — Nghiệm thu DEV đã ký trên Windows LocalDB

**Ký ngày 23/11/2026 (Asia/Bangkok) · `E_DEV_ACCEPTANCE_VERIFIED` · Lượt `e-mws74htv`**

Owner duyệt bộ APP-02E DEV v0.1 trước lượt chạy, xem đối soát SQL và ký nghiệm thu DEV trên `fb2bb1f69425bf30e8dbefc613b6f83c8c0e0704` (Git tree `73b64295b70de7993805ac03f6ac62f2ad8614b1`). Phạm vi ký là sáu suite hồi quy trên Windows LocalDB với hai tenant giả lập, theo các scope gốc của mỗi suite. Bốn tệp v0.1 được giữ đúng nội dung và SHA-256 của bản đã duyệt. Các nhãn chờ duyệt và chưa chạy trong những tệp đó là ảnh chụp trước lượt chạy. [Hợp đồng](../roadmap/app-02e-dev-contract.md) · [Ma trận](../roadmap/app-02e-dev-acceptance-matrix.md) · [Fixture](../../scripts/fixtures/app02e/fixture-v0.1.json) · [Truy vết AC–EV–P0](../roadmap/app-02e-ac-ev-p0-traceability.md)

## Kết quả đã ký

Lượt chạy từ 01:55:11 đến 02:01:33 ngày 23/11/2026 (Asia/Bangkok) đạt **6/6 suite, 91 mã ca và 175/175 lượt**. Chromium đạt **21/21 kịch bản**. Catalog LocalDB trước và sau khớp. Hồ sơ ghi nhận 14 DB scratch đã quan sát, gồm các DB kiểm tra guard tạm thời, và số DB còn lại do lượt này tạo bằng 0.

| Suite | Mã ca | Lượt đạt | TestRunId | Chứng cứ tiêu biểu |
| --- | ---: | ---: | --- | --- |
| A.4 | 12 | 24/24 | `app02a4-e-mws74htv-1` | 1.440/1.440 ô SQL, 60 checkpoint và từ chối 5/5 mẫu evidence sai. |
| C.6 | 11 | 15/15 | `25ec235dacd04afb9af9a69f80c2e4e4` | ShipTo và điều khoản giữ snapshot. Giao A25 cho AR 375 và COGS 250 theo fixture. |
| D.1 | 14 | 28/28 | `0a3f8fab946e4a20b986e1893cc68e2a` | Tách quyền A40 và B30, MAIN30/TRIP70 sau nạp, AR và COGS trước giao bằng 0. |
| D.2 | 18 | 36/36 | `304429f0d57c4774a767928a4196475c` | Cash/credit/bank pending, tranh grant, rollback, replay và chặn thiếu kênh. |
| D.3 | 18 | 36/36 | `3d1e0f746b7a4a919d079197d741a786` | 182/182 ô AC-03 gồm 168 ô số và 14 nhãn mốc. Cuối T6 MAIN55/550, TRIP0, COGS450, AR A375/B100, custody0, quỹ200 và A giữ15. |
| D.4 | 18 | 36/36 | `54e9beb49fc74628bb30acdbe68d844a` | Cả hai tenant khớp 2/3 rồi 3/3 dòng sau correction. Giữ snapshot sai lệch và liên kết SQL của case đóng. |

CI baseline đạt 20/20 checks. Commit kiểm thử `972fc518940ddf460ef5e5a0cfba0ded38e68c9b` dùng cùng Git tree với nguồn LocalDB. CI hosted chạy các ma trận hồi quy đến D.3. D.4 thuộc bằng chứng Windows LocalDB của lượt APP-02E. [CI baseline](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45054720761) · [Checks của nguồn baseline](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/119/checks)

Bộ bằng chứng 276 tệp nằm dưới `TestResults/app02e/e-mws74htv/` trong checkout chạy của operator.

| Tệp | SHA-256 |
| --- | --- |
| `dev-sql-signoff.md` | `3A1B086E89F55584C514E596CC19C45D6889B74987ADDE2D38229B668E909B93` |
| `case-scope-index.csv` | `F8D8EAE286E14D7B8747F68E9759749F13DDC6C0539C016D83E57C05250778BB` |
| `sql-summary.json` | `72D00F6CC0A3BDD55F7FFDB240341306F6F1CAD90D9B7F9525A798E1A207BAF4` |
| `SHA256SUMS.txt` | `0012CE6871EFF2D61D110E1AB31CD0408FD980F6B49AB02F867CC2EE5245903C` |

## Bằng chứng EV còn thiếu

| EV | Trạng thái thực tế | Chứng cứ còn thiếu |
| --- | --- | --- |
| EV-01–04 | EVIDENCE_PENDING | Danh tính, quyền và audit từ chối chéo tenant trên môi trường đích. Hồ sơ Customer/ShipTo và snapshot thương mại với dữ liệu thật. E2E Mixed có chứng từ đối chiếu. Kiểm kê, đối soát tồn/giá trị và quyền ghi toàn Kho-01. |
| EV-05–07 | MODEL_ONLY, real pending | Chứng từ giá/VAT/quà/trả, lot/FEFO và QA quarantine. Đối soát AR, receipt/custody, quỹ và tạm ứng trên chứng từ thật. Giá vốn bình quân, hàng trả và điều chỉnh có nguồn qua kỳ khóa. |
| EV-08–09 | MODEL_ONLY, real pending | Retry, mất ACK, cạnh tranh và hiệu ứng duy nhất trên runtime đích. Bằng chứng offline/phiếu giấy nếu được duyệt. Restore thật với quyền hiện hành, privacy, đối chiếu ngoại hiệu ứng và số đo RPO/RTO. |
| EV-10–11 | EVIDENCE_PENDING | Hồ sơ seller/kênh, invoice theo từng Delivery và tra cứu UNKNOWN với provider thật. Receipt, posting, đối soát đúng kỳ và correction có xác nhận trên hệ kế toán đích. |
| EV-12–13 | EVIDENCE_PENDING | Chữ ký opening, khóa mọi writer cũ kể cả ngoài wave, chứng từ đến muộn và Forward Recovery. UX trên thiết bị thực tế và visit hợp lệ không phát sinh sale/AR/stock. |
| EV-14–15 | EVIDENCE_PENDING | Pilot profile theo mode với mẫu số và ngưỡng UX/SLO được duyệt trước. Kết quả thực đo n/N, sai lệch, case, chi phí và Go/No-Go của Product Owner và Tenant. |

`B-CASE-001..003` và `LEGAL-MEMO` giữ `EVIDENCE_PENDING`; `INV-09` giữ `NOT_RUN`. Chi tiết liên hệ từng AC nằm trong bảng truy vết v0.1. [Baseline EV](../../business/acceptance.md#3-evidence-cần-giữ-độc-lập)

## Năm P0 còn thiếu

| P0 | Trạng thái | Chứng cứ bắt buộc |
| --- | --- | --- |
| G-STOCK | NOT_VERIFIED | Kiểm kê, trích xuất đối soát số lượng/giá trị và lineage của mọi biến động toàn Kho-01, gồm phần ngoài pilot/wave và kiểm tra quyền A/B. |
| G-CUT | NOT_VERIFIED | Opening có chữ ký, freeze, xác minh từng writer cũ bị khóa, xử lý chứng từ đến muộn và Forward Recovery sau hiệu ứng thật. |
| G-IDEMP | NOT_VERIFIED | Chứng minh một nguồn chỉ sinh một hiệu ứng qua retry, tranh chấp, mất ACK, restart/restore trên runtime đích. Có bằng chứng quyền đã thu hồi, pending và đối chiếu hiệu ứng ngoài. |
| G-LEDGER | NOT_VERIFIED | Đối soát AR, tiền thu/custody, quỹ, tạm ứng, stock/cost và posting kế toán với chứng từ, mapping và kỳ thật. |
| G-INV | NOT_VERIFIED | Hồ sơ seller, kênh hợp lệ và bằng chứng tra cứu UNKNOWN cùng định danh với provider thực tế. |

Năm P0 không miễn trừ. PREPROD giữ `FOUNDATION_ONLY / NO_GO`, C-PREPROD giữ `BLOCKED / NOT_VERIFIED`, nghiệm thu vật lý C-E2E giữ `NOT_RUN`. B.1/B.9, RUNTIME-01/02, Product Verification theo mode, Pilot và Go-Live tiếp tục áp dụng điều kiện hiện hành. [Ràng buộc P0](../../business/constraints.md) · [Cổng APP-02](../roadmap/app-02.md#4-app-02b-preprod-obligations-and-app-02ce-scope)
