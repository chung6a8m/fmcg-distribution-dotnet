# APP-02E — Bảng truy vết AC–EV–P0

**v0.1 · 23/11/2026 · Chờ Owner duyệt**

Cột EV giữ mapping của [AC-01–21](../../business/acceptance.md). Cột P0 là mối liên hệ đề xuất để lập hồ sơ xác minh, dựa trên bất biến và cổng trong nguồn. “Một phần” giới hạn được nêu ở cột DEV hoặc phần thiếu. E-02–E-07 là các suite đề nghị chạy, hiện `NOT_RUN`. Phần còn thiếu áp dụng cho xác minh thực tế theo mode. [Ràng buộc P0](../../business/constraints.md) · [Ma trận DEV](app-02e-dev-acceptance-matrix.md)

| AC | EV | P0 liên quan | Bằng chứng DEV có thể dùng hoặc chạy lại | Phần còn thiếu cho xác minh thực tế |
| --- | --- | --- | --- | --- |
| AC-01 | EV-01, EV-09 | G-IDEMP (restore) | E-03, E-05–E-07 có ca chéo tenant và quyền hiện hành | Danh tính/quyền thật, audit đọc/ghi/xuất và restore trước revoke trên runtime đích. |
| AC-02 | EV-02 | G-LEDGER | E-03 Customer không Outlet, ShipTo bất biến, AR đúng nguồn | Customer, terms/price và chứng từ thực tế của mode A. |
| AC-03 | EV-03, EV-04, EV-06, EV-07, EV-08, EV-11 | G-STOCK, G-IDEMP, G-LEDGER | E-06 toàn bộ 182 ô T0–T6 của D.3 | Mixed thực tế, tồn/giá trị và sổ đích với actor/chứng từ thật. |
| AC-04 | EV-04, EV-12 | G-STOCK, G-CUT | Rà hồ sơ writer, rehearsal trên giấy | Khóa từng writer cũ và kiểm toàn Kho-01, gồm ngoài wave. |
| AC-05 | EV-04, EV-05 | G-STOCK, G-LEDGER | Oracle nghiệp vụ tham chiếu; đủ lot/FEFO/quarantine chưa chạy | Base Qty nhiều UoM, snapshot lô/hạn, return/QA, hàng held và AR. |
| AC-06 | EV-07 | G-STOCK, G-LEDGER | E-06 đối soát giá vốn và trả xe một phần phạm vi | Hoàn từng phần gắn nguồn, adjustment qua kỳ khóa và giá vốn kỳ trên hệ đích. |
| AC-07 | EV-05 | G-STOCK, G-LEDGER | Oracle nghiệp vụ tham chiếu; ca quà chưa chạy | Hai nhánh trả quà và waiver có checker, provenance và hậu quả khi mua tiếp. |
| AC-08 | EV-06 | G-LEDGER | E-03 due date theo snapshot và cutoff báo cáo | Policy/terms thật, từng Delivery và báo cáo AR thực tế. |
| AC-09 | EV-06, EV-08 | G-STOCK, G-IDEMP, G-LEDGER | E-04/E-05 quota, grant, cash, credit, bank pending, race/replay | Chứng cứ xác minh thanh toán, chính sách grant và actor thật. |
| AC-10 | EV-06, EV-12 | G-LEDGER, G-IDEMP | E-06 allocation/custody, offer và acceptance theo scope D.3 | Nộp quỹ thật, phân nhiệm, sổ riêng biệt và chứng từ bàn giao. |
| AC-11 | EV-08 | G-IDEMP | E-02 C→P, dependency, mất ACK, restart, payload conflict | Chứng cứ worker/runtime đích qua sự cố, effect duy nhất và khôi phục pending. |
| AC-12 | EV-08, EV-09 | G-IDEMP, G-STOCK | E-03, E-06/E-07 chặn ghi offline và kiểm quyền, phạm vi một phần | Offline/phiếu giấy, writer hết hạn, observed-event/case và sync muộn theo quyền hiện hành. |
| AC-13 | EV-09, EV-10 | G-IDEMP, G-INV | E-06/E-07 revoke hiện hành, mô phỏng tra cứu ngoài, phạm vi một phần | Restore trước revoke/privacy và trạng thái invoice thực, đối chiếu trước mở lại. |
| AC-14 | EV-10 | G-INV, G-IDEMP | E-07 intent theo Delivery, lookup cùng ID, B_NO_CHANNEL và case giả lập | Seller/kênh hợp lệ, phát hành/lookup của provider thực và hồ sơ pháp lý. |
| AC-15 | EV-11 | G-LEDGER, G-IDEMP | E-07 receipt/post/diff/correction theo source, đối tác giả lập | Hệ kế toán đích, mapping/kỳ/chứng từ thật và xác nhận kế toán. |
| AC-16 | EV-04, EV-12 | G-STOCK, G-CUT, G-IDEMP, G-LEDGER, G-INV | Rà checklist opening và Forward Recovery trên giấy | Số opening đã ký, khóa writer, late-event và Forward Recovery sau hiệu ứng thật. |
| AC-17 | EV-13 | — | Ca visit không nằm trong suite v0.1 | Visit hợp lệ ngoài hiện trường và 0 sale/AR/stock. |
| AC-18 | EV-14, EV-15 | Cả 5 P0 là điều kiện vào Pilot | Checklist và chỉ số đề nghị; Pilot chưa được phép | Ngưỡng ký trước, đo n/N và Go/No-Go theo mode có thẩm quyền Tenant. |
| AC-19 | EV-05, EV-06, EV-10, EV-11 | G-LEDGER, G-INV | Oracle synthetic tham chiếu; E-07 chỉ kiểm giao tiếp mẫu | Tax/discount/return/refund, chứng cứ chi, quyền và residual sổ/thuế thực. |
| AC-20 | EV-06 | G-LEDGER, G-IDEMP | E-06 chỉ có bất biến advance không đổi | Chuỗi đầy đủ issued, expense, offer và hai lần accept hoàn ứng. |
| AC-21 | EV-06 | G-LEDGER, G-IDEMP | E-06 checkpoint phân bổ và quỹ theo phạm vi D.3 | Chứng từ thu/allocate/accept thật, đối soát AR/credit/custody/fund. |

## Tình trạng EV và điều kiện P0

EV-01–04 và EV-10–15 giữ `EVIDENCE_PENDING`. EV-05–09 giữ `MODEL_ONLY` với chứng cứ thực tế còn thiếu. `B-CASE-001..003` và `LEGAL-MEMO` giữ `EVIDENCE_PENDING`; `INV-09` giữ `NOT_RUN`. [Trạng thái nguồn](../../business/acceptance.md#3-evidence-cần-giữ-độc-lập)

| P0 | Trạng thái | Chứng cứ thực tế phải có |
| --- | --- | --- |
| G-STOCK | NOT_VERIFIED | Kiểm kê và đối soát số lượng, giá trị, nguồn, quyền A/B và mọi biến động toàn Kho-01, cả ngoài wave. |
| G-CUT | NOT_VERIFIED | Opening đã ký, freeze và khóa từng writer cũ; late event được đối chiếu và Forward Recovery sau hiệu ứng thật. |
| G-IDEMP | NOT_VERIFIED | Một nguồn một effect trên runtime đích qua retry, race, mất ACK, restart/restore; quyền đã thu hồi và pending được bảo toàn đúng quy tắc. |
| G-LEDGER | NOT_VERIFIED | Đối soát AR, receipt/custody, quỹ, advance, stock/cost và posting theo chứng từ, kỳ và dữ liệu đích. |
| G-INV | NOT_VERIFIED | Hồ sơ seller/kênh, quy trình pháp lý, chứng từ theo Delivery và lookup cùng định danh khi UNKNOWN với provider thực tế. |

P0 không miễn trừ. PREPROD `FOUNDATION_ONLY / NO_GO`, C-PREPROD `BLOCKED / NOT_VERIFIED`, C-E2E vật lý `NOT_RUN`; Pilot và Go-Live `NOT_AUTHORIZED`. [Cổng APP-02](app-02.md#4-app-02b-preprod-obligations-and-app-02ce-scope)
