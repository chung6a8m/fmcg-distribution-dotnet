# Tailwind CSS v4 và hợp đồng Light/Dark/System

Design Baseline v0.1 `ADOPTED` qua PR #79. Light, 29 Dark overrides và Light/Dark/System được chuẩn hóa trong OWN-UX-02/06. UI-01 `MERGED / CI_VERIFIED` (synthetic/nonproduction) qua [PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80); [closeout](../closeout/2026-11-20-ui-01.md). [Phạm vi kiểm chứng runtime](ui-01-verification.md).

## 1. Baseline và nguồn token

[Package](../../web/package.json) pin `tailwindcss` và `@tailwindcss/vite` **4.3.5**. [CSS entry](../../web/src/style.css) import Tailwind, token layer và adapter. Body Compact dùng 14/20px.

[tokens.json](tokens/tokens.json) khai báo 101 token cơ sở và 29 màu ngữ nghĩa Dark. [tokens.css](tokens/tokens.css) xuất custom properties; [tailwind-v4.css](tokens/tailwind-v4.css) ánh xạ utility/variant.

Custom properties trong `:root` không tự tạo utility Tailwind. Adapter dùng `@theme inline` cho giá trị tham chiếu biến khác, giữ màu ngữ nghĩa được đọc tại phần tử sử dụng. Không đưa `@theme` vào selector Light/Dark hoặc media query; thay đổi theme nằm ở custom properties thông thường. Xem [Theme variables](https://tailwindcss.com/docs/theme).

## 2. Tích hợp UI-01

Các import trong `web/src/style.css`:

```css
@layer theme, base, components, utilities;
@import "tailwindcss";
@import "../../docs/ui-ux/tokens/tokens.css" layer(theme);
@import "../../docs/ui-ux/tokens/tailwind-v4.css";
```

Các import đứng trước rule thông thường. Tailwind resolve từ package `web`. Adapter chứa mapping và variant. Layer `theme` giữ thứ tự cascade của custom properties.

Ví dụ class tĩnh sau minh họa cách tiêu thụ; chưa là component có đủ hành vi/accessibility:

```tsx
<button type="button" className="bg-fmcg-surface-card text-fmcg-text-primary border border-fmcg-border-control min-h-(--fmcg-density-compact-control-min) px-fmcg-12 font-fmcg-body text-fmcg-body rounded-fmcg-small focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-fmcg-focus-ring">
  Xem nguồn
</button>
```

Component dùng màu theo vai trò. UI-01 nhúng hai static WOFF2 weight 400/700 từ `web/src/assets/fonts/` vào CSS ngoài cùng origin qua `?inline`, dùng normal width, `font-display: swap` và fallback Segoe UI/system-ui/sans-serif. [Font provenance và license](../../web/public/fonts/README.md).

## 3. Mapping, đơn vị và breakpoint

| Nhóm | Hợp đồng adapter |
|---|---|
| Màu | `--color-fmcg-*` tham chiếu `--fmcg-color-*` của 29 vai trò ngữ nghĩa; class giữ nguyên khi đổi theme |
| Font | `--font-fmcg-body` ánh xạ font family; `--text-fmcg-body/table/...` đi cùng biến `--line-height` của từng cỡ |
| Spacing/radius | Namespace `fmcg-*` giữ scale đề xuất; không reset toàn bộ theme mặc định của Tailwind |
| Density/layout | Control/wrapper dùng `min-h-(--fmcg-density-compact-control-min)`; native `tr` dùng `h-(--fmcg-density-compact-row-min)` như mức sàn của table layout, theo [recipe](components.md#compact-table-sizing); không khóa chiều cao block/grid hoặc cắt nội dung |
| Responsive | `fmcg-medium:` = 48rem, `fmcg-large:` = 64rem, `fmcg-wide:` = 80rem; là viewport variant, không phải container variant |

Kích thước token hiện giữ CSS px đã duyệt. Kiểm lại typography, density, spacing và utility khi thay root hoặc đổi đơn vị trong increment sau.

Spacing compact dùng namespace đúng: `px-fmcg-8` là 8px theo token, không phải `px-8` của scale mặc định Tailwind. Recipe bảng dùng `py-fmcg-0`, `border-separate border-spacing-0` và không thêm chiều cao tổng cho table; chỉ compile được `h-*`/`min-h-*` chưa chứng minh row, editor hoặc focus render đúng.

Breakpoint là literal lúc build. `48rem/64rem/80rem` tương ứng `768/1024/1280` CSS px khi initial font của browser là 16px. Các media query phụ thuộc initial font của browser.

Giữ cùng đơn vị rem với breakpoint mặc định của Tailwind để thứ tự variant ổn định. Không suy viewport variant thành container query và không lấy kích thước PNG làm breakpoint. Nguồn: [Responsive design](https://tailwindcss.com/docs/responsive-design), [CSS media query units](https://www.w3.org/TR/mediaqueries-4/#units), [giới hạn var()](https://www.w3.org/TR/css-variables-1/#using-variables).

## 4. Theme áp dụng ở root

`tokens.css` mặc định Light tại `:root`, với `color-scheme: light`. `:root[data-theme="dark"]` đổi đủ 29 màu ngữ nghĩa và đặt `color-scheme: dark`. Không có media query tự đổi palette. Không đảo màu bằng filter và không suy Dark từ màu primitive Light.

Hợp đồng runtime đã được Owner chấp thuận cho UI-01 dùng đúng một theme toàn tài liệu tại `<html data-theme="light">` hoặc `<html data-theme="dark">`. Không gắn theme ở wrapper React, component hoặc subtree. Portal của dialog/popover đặt dưới `document.body` sẽ kế thừa cùng root; portal vào iframe/document khác cần hợp đồng riêng.

Adapter khai báo variant `dark` theo root đã resolve. `dark:` chỉ dùng cho khác biệt trình bày cần riêng; semantic color thông thường không cần nhân đôi `bg-... dark:bg-...`. Không trộn selector root với variant mặc định theo OS. Nguồn: [Dark mode](https://tailwindcss.com/docs/dark-mode).

Nested theme không nằm trong reference này: selector ancestor Dark có thể vẫn match bên trong vùng Light, và alias custom properties được tính ở nơi khai báo. Chỉ đổi primitive ở một subtree không bảo đảm các semantic alias đã kế thừa đổi theo.

`color-scheme` phải khớp theme đã resolve để native input, scrollbar và nền browser phù hợp. Không đặt `light dark` vô điều kiện khi người dùng đã chọn Light hoặc Dark rõ ràng. Thuộc tính này không tự tạo palette cho component. Nguồn: [Color scheme](https://tailwindcss.com/docs/color-scheme).

## 5. Light/Dark/System là preference, không phải ba palette

| Preference hợp lệ | Giá trị trên `html[data-theme]` |
|---|---|
| `light` | `light`, không phụ thuộc OS |
| `dark` | `dark`, không phụ thuộc OS |
| `system` | `dark` nếu `matchMedia('(prefers-color-scheme: dark)')` match; còn lại `light` |
| Chưa có/không hợp lệ/không đọc được preference | Fallback `light` |

Không ghi `data-theme="system"`. Nếu API kiểm OS không khả dụng, System resolve về Light. Đọc/ghi storage phải chịu được exception; lỗi lưu preference không làm crash shell hoặc đổi lựa chọn vừa áp dụng trong phiên. Theme preference là tùy chọn trình bày, không chứa dữ liệu nghiệp vụ hay làm thay đổi quyền.

Chỉ phản hồi sự kiện đổi OS khi preference hiện tại là System; Light/Dark tường minh giữ nguyên. Khi đổi lựa chọn, cập nhật root và trạng thái control nhất quán, rồi lưu theo cơ chế được spec chọn. Listener cần cleanup; đồng bộ nhiều tab nếu được hỗ trợ phải dùng cùng quy tắc validate/resolve.

Script ngoài cùng origin `web/public/theme-runtime.js` đọc `fmcg.ui.theme.v1` trước React, đặt root và meta. `web/src/ui/theme.ts` kết nối `useSyncExternalStore` vào cùng controller. Ghi storage lỗi giữ lựa chọn trong document đang mở. CI kiểm first paint trên build với CSP enforcing.

Manifest dùng màu Light tĩnh trong khi trang đang mở đồng bộ `theme-color` với root. Kiểm cached shell, consent V1→V2 và cửa sổ PWA standalone theo [ma trận UI-01](ui-01-verification.md).

## 6. State, focus và các chế độ khác

Các cặp foreground/background, hover/pressed, selected, disabled, border control, focus và link phải kiểm riêng cho từng palette. Dark không chỉ đổi surface/text. Không dùng `text.inverse` thay action foreground khi hai vai trò cần màu khác nhau; không suy contrast của opacity/overlay từ cặp màu đặc đã kiểm.

Tailwind v4 mặc định `ring` rộng 1px và dùng `currentColor`; border cũng mặc định `currentColor`. Nêu rõ màu, width và offset cần dùng để giữ focus đề xuất 2px/offset 2px và nhận biết control. Không tắt outline khi chưa có focus thay thế. Xem [Upgrade guide](https://tailwindcss.com/docs/upgrade-guide).

Forced colors/high contrast của hệ điều hành là phạm vi kiểm riêng, không là tên khác của Dark. Giữ semantics và focus; không dùng `forced-color-adjust: none` hàng loạt để giữ palette. Reduced motion cũng độc lập với theme; token `motion.reduced` chưa tự làm animation tuân thủ preference.

Khi luồng in được triển khai, mặc định đề xuất là output Light trên nền trắng, giữ số liệu, nhãn trạng thái và khả năng phân biệt khi in đơn sắc. Không buộc người dùng đổi theme màn hình để in. Print stylesheet và evidence thuộc increment tương ứng; chưa có print rule trong reference hiện tại.

Thiết bị Field cần đối chiếu browser thật với baseline Tailwind v4: Chrome 111+, Safari 16.4+, Firefox 128+ cho phần nền tảng; utility dùng tính năng mới hơn cần kiểm riêng. Không suy support từ nhãn Android/iOS hay kích thước màn. Nguồn: [Compatibility](https://tailwindcss.com/docs/compatibility).

## 7. Class detection và kiểm chứng

Tailwind cần thấy tên class hoàn chỉnh. Dùng object map chứa literal cho các tone/state; không dựng class như `` `bg-fmcg-status-${tone}-background` ``. Không đưa class do API trả về vào UI để quyết định nghĩa/màu trạng thái. Nguồn: [Detecting classes](https://tailwindcss.com/docs/detecting-classes-in-source-files).

Chạy `node docs/ui-ux/tools/sync-tokens.mjs --check` cho parity/contrast và `node docs/ui-ux/tools/check-tailwind.mjs` sau khi cài lockfile `web` cho compiler contract. Kiểm reference ở [verification.md](verification.md) phải tách khỏi kiểm runtime. Các ca cần có khi tích hợp:

- JSON/CSS parity, alias resolve và đủ 29 override Dark; contrast theo từng theme và từng cặp được khai báo.
- Compile các class màu/font/spacing/focus và responsive mẫu bằng Tailwind đang pin; media query có literal, utility đọc đúng semantic variable.
- Light trên OS Dark, Dark trên OS Light, System khi OS đổi, thiếu/invalid/storage bị chặn; root, palette và native controls cùng kết quả.
- First paint/reload/PWA cache, dialog portal, keyboard/focus, nội dung dài và viewport thuộc phạm vi nghiệm thu; kiểm riêng forced colors, reduced motion và in khi được triển khai.

Kết quả chưa chạy ghi `NOT_RUN`. Palette/compile PASS không chứng minh component, theme controller, first paint, accessibility hoặc PWA đã PASS. Giữ [ngoại lệ 125% hiện hành](accessibility-and-responsive.md#ux-a01---phạm-vi-và-ngoại-lệ-đang-có) đúng phạm vi; không dùng hướng dẫn này mở lại gate đã được loại.
