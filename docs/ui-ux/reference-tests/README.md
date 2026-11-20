# Kiểm render reference UI-00

Fixture riêng cho bảng HTML native, dùng token và adapter Tailwind v4 của repository. Dữ liệu 24 mặt hàng tổng hợp cố định gồm 7 cột. Các nút chỉ làm điểm dừng bàn phím. Hai input minh họa trạng thái đang sửa và số lượng không hợp lệ.

## Chạy lại

Tại repository root, dùng Node 26 (baseline từ 26.13.1) và quyền chạy Chromium/HTTP loopback.

```sh
npm --prefix web ci --no-audit --no-fund
node web/node_modules/@playwright/test/cli.js install chromium
node docs/ui-ux/reference-tests/run.mjs
```

Runner resolve Playwright và Tailwind từ `web/`, biên dịch import theo [hướng dẫn Tailwind](../tailwind-v4-and-themes.md), rồi mở fixture trên `127.0.0.1` với cổng tự chọn. Có thể truyền thư mục output mới làm đối số. Mặc định dùng `test-results/ui00-reference-<UTC timestamp>/`. Dùng checkout sạch khi thu bằng chứng. `manifest.json` ghi source SHA, trạng thái input Git, môi trường, phép đo, trạng thái từng case và SHA-256 của các file đầu ra. Runner giữ trace cho REF-01 và các case lỗi. BASE-01 là đối chứng dự kiến FAIL vì outline vượt hàng; các case chính, SVG và phép chẩn đoán đối chứng phải cho kết quả dự kiến thì runner mới thoát 0.

Mở trace bằng lệnh sau, thay `<output>` bằng thư mục chứa trace.

```sh
node web/node_modules/@playwright/test/cli.js show-trace "<output>/REF-01-trace.zip"
```

## Các phần style bổ sung

- Recipe dùng `height` của native row làm mức sàn, cell padding ngang 8px, dọc 0px và border cuối ô 1px. Editor block/border-box kế thừa chữ 13px/18px và giữ mức sàn 32px hoặc 40px.
- Fixture thêm bề mặt, hàng xen kẽ, cặp màu trạng thái, viền invalid và vòng focus từ semantic token. Giới hạn bề rộng đoạn tên/lỗi để kiểm xuống dòng; cột số căn phải. Padding dọc 4px ở mỗi đầu của ô editor tạo chỗ cho outline ngoài 2px/offset 2px ngay cả trước focus. Các nút minh họa dùng outline đặt vào trong (offset −4px) để giữ hàng đọc ở mức sàn. Phép kiểm focus chính đo outline ngoài của editor.
- `BASE-01` tắt riêng padding bổ sung, giữ nguyên các style và chuỗi Tab. So sánh vùng vẽ với hàng/ô và pixel PNG trước/sau focus. Dung sai khi so cạnh layout là 1/64 CSS px theo đơn vị layout của Chromium. Lấy hai pixel ở giữa mỗi cạnh thẳng của outline tại DPR 1, bên cạnh quan sát toàn viền trong ảnh.
- Root Light/Dark được đặt từ HTML fixture, với preference mô phỏng đối nghịch. Browser zoom đọc từ Settings của Chromium trên profile tạm mới. `deviceScaleFactor`, DPR và visual viewport scale được ghi riêng. Số hàng hữu ích đếm các hàng dữ liệu nằm trọn trong viewport đầu tiên. Số cột đếm header nằm trọn trong viewport; cả bảy cột có nội dung trong các hàng được đếm.

## Font

Fixture phục vụ Noto Sans 2.015 variable TTF gốc qua loopback, dùng weight 400/700. Tệp theo SIL OFL 1.1, kèm [giấy phép và copyright](fonts/OFL.txt). Chrome DevTools Protocol báo font thực dùng từ glyph của tiêu đề, mã hàng, tên dài, lỗi, badge và số. SVG dùng hai subset Noto Sans Regular/Bold nhúng riêng; tài nguyên và SHA-256 được ghi trong manifest. [Nguồn Noto Sans đã pin](https://github.com/google/fonts/tree/48133440178622e215912d34d386c5ee1682c677/ofl/notosans).

Xem [lịch sử kiểm chứng](../verification.md).
