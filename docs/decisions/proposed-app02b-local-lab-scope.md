# Đề xuất phạm vi APP-02B Windows local lab

**Trạng thái** `PROPOSED / NO_PHYSICAL_ACCEPTANCE_AMENDMENT` (2026-11-21).

[Thiết kế Windows local lab](../roadmap/app-02b-local-preprod/design.md) chọn môi trường cô lập do operator quản lý để thu bằng chứng kỹ thuật SQL/HTTP/Vault, fault và recovery. Báo cáo dùng `LOCAL_OPERATOR_LAB`; khi cùng một Owner thực hiện và tự xác nhận, ghi `SOLO_OWNER_SELF_ATTESTED`. Physical acceptance vẫn theo [B.1](../security/app-02b1-acceptance-evidence.md) và [B.9](../roadmap/app-02b9-independent-preprod-oracle.md).

| Nội dung cần Owner phê duyệt trước khi chạy | Phạm vi cần ghi nhận |
|---|---|
| Môi trường | Host, VM/container, network boundary và danh tính SQL/Control Plane của hai tenant dành riêng cho lab. |
| Danh tính | Operator, API, Worker, SQL/Vault principals, observer, Security và DBA. Ghi rõ người chịu trách nhiệm và quyền được cấp. |
| Đích backup | Kho backup riêng, đích restore đã duyệt, quyền truy cập và bằng chứng lưu giữ ngoài host. |
| Retention và lịch sử khóa | Thời hạn lưu, legal hold, người giữ khóa, phiên bản, rotation/revocation và cách đọc lại bản sao lịch sử. |
| Cửa sổ fault | Khoảng thời gian, mục tiêu, người điều phối, điều kiện dừng và khôi phục khi thử outage, revoke, restart và PITR. |

Các mục trên đang chờ phê duyệt cho môi trường cụ thể. Kết quả lab chỉ mô tả các assertion thực sự đo được. B05-02 vẫn bị chặn nếu thiếu người review độc lập. Nếu Owner chọn ngoại lệ solo cho physical acceptance sau này, cần quyết định riêng có ngày, phiên bản và phạm vi rõ ràng. Các ngoại lệ B.4 và #59 chỉ áp dụng theo quyết định gốc.
