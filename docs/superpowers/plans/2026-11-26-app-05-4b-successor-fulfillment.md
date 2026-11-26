# APP-05.4b kế hoạch nạp và giao kế nhiệm

> **For agentic workers:** Use superpowers:executing-plans or superpowers:subagent-driven-development to verify each step.

Giữ nguyên Order, dòng Confirm và bản chụp thương mại. Mỗi lần nạp và giao thuộc chuyến đang thực hiện. Liên kết với chuyến gốc đi qua binding được kiểm chứng trong SQL.

## Reserve mới

- [ ] Mở rộng fixture `VerifyASuccessor` để nạp và giao 1 EA từ quyền Reserve mới trong ca 5/4/3/1/2. Kiểm tra quyền sai chuyến, tenant đối chứng và replay.
- [ ] Cho C.3 nhận đúng quyền `App05ASuccessorReserve` trên chuyến đích đã chuẩn bị trong ngày DEV hiện hành. Dùng lại nạp partial và FEFO.
- [ ] Ràng buộc Delivery bằng binding và đúng StockRight, manifest, dòng, SKU, chuyến gốc và chuyến thực hiện. Giữ bằng chứng C.4 và kiểm quyền của chuyến thực hiện. Tính tiền lũy kế theo dòng Confirm và hạn từ lần giao hiện hành.
- [ ] Dùng C.5 đối chiếu cùng dòng gốc trên hai chuyến. SQL độc lập đối chiếu lô giữ, nạp, xuất, ngày giao, hạn, lượng MAIN và số lần sau replay.

## HELD đã chuyển

- [ ] Thêm ca đỏ C.3 nhận cả quyền chưa từng nạp và quyền đã trả có nguồn D.3. Bao phủ lô hết hạn trước ngày nạp và hai đích cùng tranh quyền gốc.
- [ ] Khi lần nạp đầu dùng quyền chuyển, ghi một quyền vật lý kế nhiệm cùng chứng từ đối ứng với quyền gốc trong giao dịch C.3. Ràng buộc lượng chưa nạp, lượng đã trả và lô theo `App05AHeldTransfer` / `App05AHeldTransferLot`. Dùng đúng số hiệu quyền đã trả về từ lệnh chuyển. Tổng allocated trước bước nạp thật giữ nguyên.
- [ ] Đối chiếu MAIN, chủ quyền và lô theo lượng chuyển đối ứng. Dùng chung các chứng từ vật lý, COGS, AR và kế toán D.4 hiện có. Giữ phần giá trị dư theo nguồn được ghi sổ.
- [ ] Kiểm quyền hiện hành khi đọc, nạp, giao và replay. Giữ giới hạn dòng Confirm qua tất cả các chuyến, gồm race hai lần giao.

## Công việc còn lại và closeout

- [ ] Đối chiếu số còn phải giao, còn giữ, đang trên xe và đã giao từ mọi chuyến. Chỉ hoàn tất follow-up A khi dòng giao đủ theo nguồn. Biên bản chốt chuyến gốc giữ trạng thái lịch sử. D.4 hoàn tất theo nguồn và cutoff riêng.
- [ ] Chạy cả hai fixture 40/25/15 và 5/4/3/1/2, nghiệm thu trên LocalDB synthetic, rà soát độc lập và CI trên HEAD cuối.
- [ ] Sau khi CI trên `main` đạt, cập nhật closeout trong roadmap APP-05. APP-05.5 dùng trạng thái toàn dòng này để đề nghị, duyệt độc lập và giải phóng phần HELD đủ bằng chứng.
