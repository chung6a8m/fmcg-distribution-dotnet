# Kiểm tra bộ quy chuẩn v0.1

**Phạm vi:** tài liệu và reference tokens ngày 20/11/2026; review PR #78 vòng 1 từ HEAD `f3aeb7b6445c842e682025d23bf3dba59dfbc4da`, vòng 2 từ `c31b7acd2b2926ba755eafcfe4c10c147338714e`, baseline `6abafe25d21678579122c11d0c206818fb1cb0fd`. Không là runtime/E2E/usability/accessibility acceptance của ứng dụng.

## Kết luận review và bổ sung

Review vòng 1 không tìm thấy material defect trong phạm vi Light/DOCS_ONLY đã công bố của bản gốc: 101 token và 15 cặp contrast đều khớp. Không gọi việc chưa có Dark runtime là regression khi PR chưa triển khai UI.

Theo trọng tâm Tailwind v4 và Light/Dark của người dùng, bổ sung reference có thể kiểm: 29 semantic override Dark, adapter CSS-first, generator/checker và [hợp đồng Light/Dark/System](tailwind-v4-and-themes.md). Dark là DESIGNER_PROPOSED; chưa là Owner adoption. SVG hiện hữu vẫn là **Light static reference**, không phải ảnh Dark hoặc ảnh ứng dụng.

## Review vòng 2 — compact

**R2-F01 / P2 — hướng dẫn sizing cho bảng native:** UX-A06 trước đó yêu cầu “Row/control dùng min-height”, trong khi UX-I03 ưu tiên HTML table. `min-height` trên `tr`/`td` không bảo đảm mức sàn hàng như block/grid; áp dụng máy móc có thể không đạt row 36px dù token đúng. Finding nằm trong hướng dẫn triển khai, chưa là lỗi đã quan sát trên ứng dụng.

Đã sửa UX-A06, foundations, adapter guide, template và chú thích SVG; thêm [recipe mật độ bảng](components.md#compact-table-sizing) dùng `height` của native row như mức sàn, giữ nội dung tăng chiều cao. Recipe nêu rõ border-box, cell padding, inline-editor baseline và vùng vẽ focus; không áp cho rowspan hoặc coi virtualization có cùng hành vi.

Giữ các số đề xuất compact 14/20, table 13/18, control32/row36, comfortable40/44 và Field48. Không thêm token hoặc chuyển đề xuất thành Owner decision. Hai template yêu cầu ghi số hàng/cột hữu ích và đo trạng thái đọc/sửa/lỗi theo màn; không tạo quota toàn dự án hoặc gate zoom mới.

Compiler checker bổ sung bảy utility contracts của recipe, từ 20 lên **27**. Kiểm tra sau sửa gồm Node 26.13.1/Tailwind 4.3.5, parity/contrast, liên kết và SVG XML/glyph coverage. Phép tính editor `32 + 8 + 8 + 1 = 49px` minh họa rủi ro padding, không được ghi thành số đo browser.

**Browser reference vòng 2: NOT_RUN/BLOCKED.** Browser kết nối và Cloud Browser đều không truy cập được HTTP fixture cục bộ (`ERR_CONNECTION_REFUSED`); bước đọc trang lỗi trên Cloud Browser tiếp tục bị URL policy từ chối. Không dùng workaround, không báo rendered row/focus/Light-Dark PASS. Recipe và tài liệu được đối chiếu đặc tả CSS; browser/device acceptance vẫn thuộc increment triển khai được cấp phạm vi.

## Kiểm tra có thể chạy lại

Chạy tại repository root, dùng Node 26.13.1+ trong major 26 theo baseline project:

```sh
node docs/ui-ux/tools/sync-tokens.mjs --check
npm --prefix web ci --no-audit --no-fund
node docs/ui-ux/tools/check-tailwind.mjs
```

Khi thay JSON, chạy `node docs/ui-ux/tools/sync-tokens.mjs --write`, review cả hai CSS sinh ra rồi chạy `--check`. Script không được import vào ứng dụng và không thêm CI gate.

Compiler checker dùng dependency đã pin trong `web/`; không thêm package. Nó biên dịch đúng thứ tự import tham chiếu, kiểm 27 utility contracts cùng font/line-height/focus/density/native-row sizing, selector Dark theo root và breakpoint literal.

Kết quả kiểm reference:

- **101 token nền**: CSS variable duy nhất, type/value hợp lệ, alias resolve không vòng lặp.
- **29/29 semantic color override Dark**: phải đủ đúng cùng tập khóa, có foreground/background, hover/pressed, disabled, selection, control border, focus và status; không ghi đè primitive.
- **130 khai báo token CSS** theo đúng selector (101 nền + 29 Dark); `color-scheme` Light/Dark nhất quán. `tokens.css` khớp toàn bộ output generator.
- **61 ánh xạ `@theme inline`** và **3 breakpoint literal** ở adapter; `tailwind-v4.css` khớp generator. Không expose primitive qua utility hoặc reset mặc định Tailwind.
- **32/32 cặp Light và 32/32 cặp Dark PASS**; so ngưỡng bằng giá trị chưa làm tròn. Bộ mở rộng gồm text trên page/subtle/alternate/selected, control border, focus, link, selection và disabled. Mức đọc disabled là mục tiêu sản phẩm, không tuyên bố đó là yêu cầu contrast WCAG cho control inactive.
- **27/27 compiler contracts PASS** với Node **26.13.1**, Tailwind và compiler **4.3.5**. Không còn directive chưa biên dịch hoặc `var()` trong điều kiện media query. Các ca compiler tham chiếu không được đưa vào app và không xác nhận kích thước render của bảng.
- Review business/keyboard/a11y và đường dẫn tương đối độc lập không phát hiện mâu thuẫn material. Các nguồn và giới hạn APP/Owner vẫn giữ nguyên.
- SVG gốc: XML hợp lệ, 17 màu thuộc palette, font Noto Sans 400/700 bao phủ ký tự của 84 text element; kiểm font metrics không thấy chữ ra ngoài canvas/vùng nền. Đây là kiểm artifact, không là browser acceptance.

## Contrast của các cặp chính

Tính trực tiếp từ sRGB token. Bảng 15 cặp ban đầu được bổ sung cột Dark; checker còn kiểm 17 cặp bổ sung mỗi theme. Surface khác, opacity/overlay, ảnh nền và trạng thái render thực phải kiểm lại khi triển khai.

| Foreground | Background | Light | Dark | Ngưỡng | Kết quả palette |
|---|---|---:|---:|---:|---|
| `text.primary` | `surface.card` | 14.102:1 | 13.224:1 | 4.5:1 | PASS / PASS |
| `text.secondary` | `surface.card` | 5.130:1 | 7.565:1 | 4.5:1 | PASS / PASS |
| `text.primary` | `surface.page` | 13.255:1 | 15.085:1 | 4.5:1 | PASS / PASS |
| `action.primary.foreground` | `action.primary.background` | 5.473:1 | 10.838:1 | 4.5:1 | PASS / PASS |
| `action.primary.foreground` | `action.primary.hover` | 6.725:1 | 12.718:1 | 4.5:1 | PASS / PASS |
| `action.primary.foreground` | `action.primary.pressed` | 8.552:1 | 8.613:1 | 4.5:1 | PASS / PASS |
| `action.destructive.foreground` | `action.destructive.background` | 5.505:1 | 8.529:1 | 4.5:1 | PASS / PASS |
| `border.control` | `surface.card` | 3.549:1 | 4.845:1 | 3:1 | PASS / PASS |
| `border.control` | `surface.page` | 3.336:1 | 5.527:1 | 3:1 | PASS / PASS |
| `focus.ring` | `surface.card` | 6.438:1 | 8.637:1 | 3:1 | PASS / PASS |
| `focus.ring` | `surface.page` | 6.051:1 | 9.853:1 | 3:1 | PASS / PASS |
| `status.success.foreground` | `status.success.background` | 4.881:1 | 7.956:1 | 4.5:1 | PASS / PASS |
| `status.info.foreground` | `status.info.background` | 5.631:1 | 6.868:1 | 4.5:1 | PASS / PASS |
| `status.warning.foreground` | `status.warning.background` | 4.952:1 | 8.224:1 | 4.5:1 | PASS / PASS |
| `status.danger.foreground` | `status.danger.background` | 4.829:1 | 6.886:1 | 4.5:1 | PASS / PASS |

Viền nhẹ chỉ để trang trí; không dùng làm dấu hiệu duy nhất nhận biết input. Focus 2px/offset 2px cần được kiểm trên component thật. `text.secondary` trên `status.info.background` Light chỉ đạt **4,487:1**, nên notice/badge phải dùng đúng foreground của status; không suy mọi tổ hợp semantic token đều đạt 4,5:1.

## Regression và giới hạn

- `web/`, dependency/lockfile, business snapshot và workflow không đổi trong phần bổ sung này. Build TypeScript/Vite/PWA, bốn health tests và ba PWA-build tests đã chạy với Node 26.13.1; PASS chỉ áp dụng ứng dụng hiện hành.
- **Browser/visual reference: NOT_RUN/BLOCKED.** Vòng 1: Chromium cục bộ không khởi chạy do môi trường từ chối `socket()`; Browser kết nối từ chối URL `data:` của fixture. Vòng 2: HTTP fixture không truy cập được, như ghi ở trên. Không thử vượt giới hạn hoặc coi compile/contrast là browser PASS.
- UI component/theme controller chưa được triển khai; bootstrap trước paint, System/OS listener, storage failure, PWA launch/cache và acceptance thiết bị vẫn phải kiểm ở UI-01.
- Chưa chạy luồng nghiệp vụ thật, lost-ACK/recovery trên UI, hoặc receipt/stock/invoice/accounting integration.
- Năm PDF prototype và SHA-256 trong mapping là provenance của lần lập tài liệu; review này không tải lại và không tuyên bố đã tái kiểm hash PDF.
- Trạng thái GitHub Actions phải xem ở exact final source HEAD của PR. Palette/compiler/build check không thay kết quả CI hoặc nâng PREPROD/EV/P0.
- Giữ ngoại lệ 125% APP-00B/C đúng phạm vi và trạng thái; không đưa lại vào gate qua tên kiểm mới.

Xem [governance](governance-and-review.md) để kiểm các increment triển khai sau.
