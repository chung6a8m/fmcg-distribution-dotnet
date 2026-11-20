// Deterministic reference data. The only interaction under test is keyboard focus.
const titles = ['Mã hàng', 'Tên hàng', 'Trạng thái', 'Số lượng (thùng)', 'Đơn giá (₫)', 'Thành tiền (₫)', 'Thao tác']
const names = ['Nước cam ép nguyên chất', 'Trà xanh hương lài', 'Sữa tươi ít đường', 'Bánh quy bơ giòn', 'Nước khoáng thiên nhiên']
const longName = 'Nước giải khát hương chanh dây và mật ong đóng chai thủy tinh dành cho quầy trưng bày tại cửa hàng, phiên bản giới hạn cho chương trình giới thiệu sản phẩm mùa hè'
const error = 'Số lượng vượt mức minh họa. Hãy nhập tối đa 24 thùng và đối chiếu lại số lượng trên phiếu trước khi tiếp tục.'
const number = new Intl.NumberFormat('vi-VN')
const focus = 'focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-fmcg-focus-ring'
const button = (id = '') => `<button type="button" ${id ? `id="${id}"` : ''} class="target ${focus} focus-visible:-outline-offset-4">Xem dòng</button>`
const input = (invalid) => `<input id="${invalid ? 'invalid' : 'editor'}" class="target cell-editor ${focus} focus-visible:outline-offset-2"
  type="text" inputmode="numeric" aria-label="Số lượng hàng HH-${invalid ? '005' : '003'}"
  ${invalid ? 'value="36" aria-invalid="true" aria-describedby="quantity-error"' : 'value="12"'}>`

function row(index, density) {
  const kind = ['read', 'before', 'edit', 'long', 'validation'][index] ?? 'read'
  const invalid = kind === 'validation'
  const editing = kind === 'edit'
  const quantity = invalid ? 36 : editing ? 12 : 5 + index
  const price = 118000 + index * 3500
  const status = invalid ? 'danger' : editing ? 'info' : 'success'
  const statusLabel = invalid ? 'Cần kiểm tra' : editing ? 'Đang sửa' : 'Đủ thông tin'
  const code = `HH-${String(index + 1).padStart(3, '0')}`
  return `<tr data-kind="${kind}" class="h-(--fmcg-density-${density}-row-min)">
    <th scope="row">${code}</th>
    <td class="product"><span>${kind === 'long' ? longName : names[index % names.length]}</span></td>
    <td><span class="status bg-fmcg-status-${status}-background text-fmcg-status-${status}-foreground">${statusLabel}</span></td>
    <td class="numeric${invalid || editing ? ' editor-cell' : ''}">
      ${invalid || editing ? input(invalid) : number.format(quantity)}
      ${invalid ? `<p id="quantity-error" class="text-fmcg-status-danger-foreground">${error}</p>` : ''}
    </td>
    <td class="numeric">${number.format(price)}</td>
    <td class="numeric">${number.format(quantity * price)}</td>
    <td>${button(editing ? 'after-editor' : kind === 'before' ? 'before-editor' : '')}</td>
  </tr>`
}

export function fixture(theme, density, clearance = true) {
  return `<!doctype html>
<html lang="vi" data-theme="${theme}">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bảng hàng hóa tham chiếu</title><link rel="icon" href="/favicon.ico"><link rel="stylesheet" href="/reference.css"></head>
<body class="bg-fmcg-surface-page text-fmcg-text-primary font-fmcg-body text-fmcg-body">
<main>
  <h1 class="text-fmcg-page-title font-fmcg-bold">Bảng hàng hóa tham chiếu</h1>
  <p class="intro text-fmcg-text-secondary">Dữ liệu minh họa · ${theme === 'dark' ? 'Tối' : 'Sáng'} · ${density === 'compact' ? 'Gọn' : 'Thoáng'}</p>
  <table aria-label="Danh sách hàng hóa minh họa" data-density="${density}" data-clearance="${clearance}"
    class="reference-table bg-fmcg-surface-card border-separate border-spacing-0 text-fmcg-table">
    <thead class="bg-fmcg-surface-subtle"><tr class="h-(--fmcg-density-${density}-row-min)">
      ${titles.map((title, index) => `<th scope="col"${index >= 3 && index <= 5 ? ' class="numeric"' : ''}>${title}</th>`).join('')}
    </tr></thead>
    <tbody>${Array.from({length: 24}, (_, index) => row(index, density)).join('')}</tbody>
  </table>
</main>
</body></html>`
}
