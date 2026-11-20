# Mapping và provenance của prototype

**PROPOSED UI/UX v0.1.** Audit 20/11/2026 đối chiếu cả năm bộ, gồm **52 bản màn hình**. Các bộ mô tả trải nghiệm đích bằng artifact tĩnh; không là prototype tương tác hoặc evidence ứng dụng đã chạy.

## Bộ nguồn thiết kế

| Vai trò | Màn | Tài liệu nguồn | SHA-256 của PDF được đối chiếu |
|---|---|---|---|
| Sales A | 8 · WF-01…08 | [FMCG-Sales-A-UX-Design-v0.1.pdf](https://chatgpt.com/library?file_id=libfile_fde3_f-ecdb579b71208191be3d4fbd83d199e1_e77baaadc4e48191ac7d0ec7102fff02&account_id=personal) | `0bd74f8023808889d996b08ed6d2da0c7de052db12d728d8d4c6259aeddc42b2` |
| Kho và điều phối | 8 · WH-01…08 | [FMCG-Warehouse-Dispatch-UX-v0.1.pdf](https://chatgpt.com/library?file_id=libfile_fde3_f-379e5c569a6c8191b22979f9b8d8436e_e77baaadc4e48191ac7d0ec7102fff02&account_id=personal) | `b2f16c080c8ca284968da01b67b8a9e59392926ed56600a48f0fa5e088761193` |
| Hiện trường | 12 · FT-01…12 | [FMCG-Field-Delivery-Sales-UX-v0.1.pdf](https://chatgpt.com/library?file_id=libfile_fde3_f-05f60aeb86048191a89dd0fc7a80e92c_e77baaadc4e48191ac7d0ec7102fff02&account_id=personal) | `c116807cdbc57d73cf52f91e3035d8eb94d506e2bfcc819d264eeb2b9f7f8e89` |
| Thủ quỹ và kế toán | 12 · FA-01…12 | [FMCG-Cashier-Accounting-UX-v0.1.pdf](https://chatgpt.com/library?file_id=libfile_fde3_f-3e7dd5778f8881918debb76d41e116a0_e77baaadc4e48191ac7d0ec7102fff02&account_id=personal) | `0d7b40f0b6d94cb67193310b3229c097fbade2463ba7d9e743edf8b2a2d4f7c5` |
| Chủ NPP và vận hành | 12 · OM-01…12 | [FMCG-Owner-Operations-UX-v0.1.pdf](https://chatgpt.com/library?file_id=libfile_fde3_f-286f604bbe848191b9e638cbb9c40bde_e77baaadc4e48191ac7d0ec7102fff02&account_id=personal) | `eb1df81949b4a07f27fd3f36f60e84592c371b246424a8d5af834c7cbfe7481f` |

Các link artifact cần quyền truy cập của chủ file. Quy chuẩn trong repository tự chứa nội dung cần triển khai; CI và ứng dụng không phụ thuộc việc tải những file này. PDF minh họa được giữ ở nơi bàn giao, không đưa bản sao lớn vào codebase.

## Phần kế thừa và phần được chuẩn hóa

| Chủ đề | Bằng chứng trong các bộ | Đề xuất v0.1 |
|---|---|---|
| Màu | Năm palette giống nhau: teal/navy, nền sáng, các tone thông tin/cảnh báo/lỗi | Giữ primitive, tách token theo nghĩa; thêm viền control |
| Font | Noto Sans 400/700 trong artifact | Đề xuất cùng font cho UI; runtime Segoe UI hiện tại cần migration riêng |
| Desktop | Canvas 1440×900; Sales sidebar208/topbar64, các bộ sau188/52 | Sidebar200/topbar52 là đề xuất mới, không gọi là giá trị đã đồng nhất |
| Bảng | Sales row51–58; Kho26–44; Finance36–44; Owner36–51 theo từng bảng | Compact row min36, chữ13; không sao chép hàng26/chữ11,2 làm mức bắt buộc |
| Field | Canvas430×932, nút48, input45; task cards và bốn tab | Body16, vùng chạm/control48, một cột, chiều cao theo nội dung |
| Trạng thái | Source/asOf, thiếu dữ liệu, pending, UNKNOWN, quyền và phục hồi | Pattern dùng chung; mapping enum theo module contract |
| Component | Nhiều helper bị override ở make_wireframes.py | Không lấy default ui.py riêng lẻ làm kích thước chuẩn |
| Hành động | Sách mô tả một ưu tiên hành động; vài hình còn lặp primary | Chuẩn hóa một primary theo vùng tác vụ, giải thích ngoại lệ |

## Những chi tiết không được sao chép thành policy

- Giá 150.000 đ, điều khoản 7 ngày, grant 1 triệu, refund 200.000 đ, quỹ đầu 5 triệu, SKU/lô/tên người/mốc giờ là fixture.
- Checkbox đã tích minh họa một thời điểm sau thao tác; xác nhận sự thật của tác vụ mới phải bắt đầu chưa tích.
- JSON số mẫu là sổ đối chiếu, không phải API schema và chưa là đầu vào trực tiếp của các script vẽ.
- PNG desktop 1800×1125 và mobile 860×1864 là tỷ lệ xuất; viewport thiết kế là 1440×900 và 430×932.
- Tiến trình Confirm/Reserve, partial/repeated load và policy validity phải theo contract module; các blocker C0-R01…06 không được giải quyết bằng màn hình.
- Cảnh báo thiếu báo cáo không tự là case đã đăng ký. Gán người xử lý không tự đóng case/chuyến.
- Duyệt hoàn AP-05 chưa thực chi; approved expense AP-07 có tác động giảm outstanding. Không tạo quy tắc mọi approval đều không đổi sổ.

## Mapping theo nhóm tác vụ

| Bộ màn | Patterns để tái sử dụng | Contract cần đối chiếu |
|---|---|---|
| WF | List/detail đơn, lookup khách/SKU, số đã đặt/đã giao/còn giữ | A_ONLINE contract và C0-R01…06 |
| WH | Nguồn hàng, manifest, phân bổ A/B, nhận hàng về xe/kho | BR-03/04/06 và operations/mixed |
| FT | Visit/task cards, preflight, giao/thu thực tế, review, UNKNOWN | BR-11…16/22, module-b-online, module-a |
| FA | Hàng đợi nguồn, receipt/allocation, cashier acceptance, refund/advance, đối soát | BR-13/14/18/19, AP-05/06/07 |
| OM | Scope/KPI, nguồn tổng, tồn khả dụng, approvals, case ownership và kết chuyến | BR-23, calculations, policy/roles |

## Baseline nghiệp vụ và ứng dụng

Các artifact được đối chiếu với commit `6abafe25d21678579122c11d0c206818fb1cb0fd`; kiểm main trong lượt chuẩn hóa vẫn trả commit này. Nguồn liên quan: [business rules](../../business/business-rules.md), [calculations](../../business/calculations.md), [roles](../../business/roles-and-approvals.md), [APP-02C.0](../roadmap/app-02c0-a-online-e2e-contract.md), [AGENTS](../../AGENTS.md).

Khi hợp đồng được chấp nhận thay đổi, cập nhật mapping và screen spec trước khi triển khai; không thay artifact lịch sử để làm mất provenance.
