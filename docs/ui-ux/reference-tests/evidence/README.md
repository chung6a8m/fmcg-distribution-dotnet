# Bằng chứng render UI-00 · 20/11/2026

`tested_source_sha` là `d469d24649a33e7c10dc590b26f90f2d7f3b7edf`. Input Git sạch. Lượt chạy bắt đầu lúc **11:14:30 (UTC+07:00)**. Môi trường Windows 11 x64 (build 26300), Node 26.13.1, Tailwind và compiler 4.3.5, Playwright 1.65.1, Chromium headless 159.0.8154.6. Chromium Settings báo zoom mặc định 100% với profile mới. Fixture đặt deviceScaleFactor 1 và đo DPR 1, visual viewport scale 1 cho từng case bảng.

```sh
node docs/ui-ux/reference-tests/run.mjs test-results/pr78-tested-d469d24
```

[Manifest JSON](manifest.json) là báo cáo có cấu trúc. Manifest ghi kết quả, phiên bản, vị trí và kích thước DOM, computed styles, font thực dùng, tọa độ/RGBA pixel trước và sau focus, cùng SHA-256 cho từng file bằng chứng.

| Case | Kết quả | Trước focus | Sau Tab và Shift+Tab |
|---|---|---|---|
| Light compact (REF-01) | PASS | [Ảnh](REF-01-read.png) | [Ảnh](REF-01-focus.png) |
| Dark compact (REF-02) | PASS | [Ảnh](REF-02-read.png) | [Ảnh](REF-02-focus.png) |
| Light comfortable (REF-03) | PASS | [Ảnh](REF-03-read.png) | [Ảnh](REF-03-focus.png) |
| Dark comfortable (REF-04) | PASS | [Ảnh](REF-04-read.png) | [Ảnh](REF-04-focus.png) |
| Light compact, 1024 × 768 (REF-05) | PASS | [Ảnh](REF-05-read.png) | [Ảnh](REF-05-focus.png) |
| Light compact, padding editor 0px (BASE-01) | FAIL dự kiến | [Ảnh](BASE-01-read.png) | [Ảnh](BASE-01-focus.png) |
| SVG Light tĩnh 1600 × 1280 (SVG-01) | PASS | [Ảnh render](SVG-01-light.png) | — |

Trace Playwright có thể tải riêng và mở qua lệnh trong [hướng dẫn](../README.md).

- [REF-01-trace.zip](REF-01-trace.zip), SHA-256 `02bce7985ce53fb5bc20ed7e74720629909deadfb206ae40a6a06645d8f2abed`.
- [BASE-01-trace.zip](BASE-01-trace.zip), SHA-256 `d56d424bc7493700e21e5f8fa442ca3451869fae9ec1770073a2766c804e0ea0`.

## Quan sát và review

- Trong bốn ảnh chính, viền focus hiện ở cả bốn cạnh, nằm trong ô editor, giữa hai đường phân cách hàng. Bề mặt, chữ, màu trạng thái và focus khác nhau theo Light/Dark. Thành tiền và số lượng căn phải. Tên dài và thông báo lỗi đều hiển thị đủ năm dòng, có dấu tiếng Việt. Ảnh 1024 × 768 vẫn thấy đủ bảy cột. BASE-01 cho thấy viền vượt cả hai đường phân cách hàng.
- SVG hiển thị tiêu đề và dấu tiếng Việt, các ô palette/trạng thái và dòng chú thích cuối canvas. Qua quan sát ảnh không thấy chữ chồng nhau hay bị cắt ở biên khối. Chromium ghi nhận hai subset font nhúng qua glyph thực dùng. SVG là Light static reference.
- Review kỹ thuật độc lập đối chiếu pilot, kiểm checksum, font, assertions, số đo và ảnh. Runner được sửa để ghi `BLOCKED` riêng cho trường hợp Chromium từ chối/timeout ở bước điều hướng. Lượt trên source SHA sạch chạy lại toàn ma trận và SVG. Các PNG có cùng SHA-256 với pilot cuối trước commit.
- Regression trên source SHA đã chạy token parity (101 token, 29 Dark overrides), contrast 32/32 mỗi theme, compiler 27/27 và business validator trong phạm vi document/model. Xem [lịch sử kiểm chứng và giới hạn](../../verification.md).
