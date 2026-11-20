import { expect, test, type Locator } from '@playwright/test'

test.use({ serviceWorkers: 'block' })
const path = '/ui/data-entry'

test('LAB link, selected lookup identity, comma decimal, form validation and no API writes', async ({ page }) => {
  const apiRequests: string[] = []
  page.on('request', request => {
    if (['fetch', 'xhr'].includes(request.resourceType())) apiRequests.push(request.url())
  })
  await page.goto('/')
  await page.getByRole('link', { name: 'UI-02 · Form & Grid Lab' }).click()
  await expect(page).toHaveURL(new RegExp(path + '$'))
  await expect(page.getByRole('heading', { name: 'Biểu mẫu và bảng tương tác' })).toBeVisible()
  const submit = page.getByRole('button', { name: 'Kiểm tra nháp mẫu' })
  await submit.click()
  await expect(page.getByRole('alert')).toContainText('Nháp chưa hợp lệ')
  const lookup = page.getByRole('combobox', { name: 'Khách hàng' })
  await expect(lookup).toHaveAttribute('aria-invalid', 'true')
  const qty = page.getByRole('textbox', { name: 'Số lượng minh họa (thùng)' })
  await expect(qty).toHaveAttribute('aria-invalid', 'true')

  await lookup.fill('Minh Anh')
  const candidates = page.getByRole('listbox', { name: 'Kết quả Khách hàng' })
  await expect(candidates.getByRole('option')).toHaveCount(2)
  await lookup.press('ArrowDown')
  await expect(lookup).toHaveAttribute('aria-activedescendant', /customer-101/)
  await lookup.press('Enter')
  await expect(lookup).toHaveValue('KH-101 · Tạp hóa Minh Anh')
  await expect(qty).toBeFocused()
  await qty.fill('2,125')
  await submit.click()
  await expect(page.getByText('Dữ liệu mẫu hợp lệ')).toBeVisible()
  await expect(page.getByText(/KH-101 · Tạp hóa Minh Anh; số lượng 2,125/)).toBeVisible()
  expect(apiRequests).toEqual([])
})

test('lookup does not auto-select on Tab or composition; Escape restores committed identity', async ({ page }) => {
  await page.goto(path)
  const lookup = page.getByRole('combobox', { name: 'Khách hàng' })
  await lookup.fill('Minh Anh')
  await lookup.press('ArrowDown')
  // A composing Enter is not an option commit. It must not submit the form either.
  await lookup.evaluate(element => element.dispatchEvent(new KeyboardEvent('keydown', {
    key: 'Enter', bubbles: true, cancelable: true, isComposing: true
  })))
  await expect(lookup).toHaveValue('Minh Anh')
  await lookup.press('Tab')
  await expect(page.getByText(/Chưa chọn mã hợp lệ/)).toBeVisible()
  await page.getByRole('button', { name: 'Kiểm tra nháp mẫu' }).click()
  await expect(lookup).toHaveAttribute('aria-invalid', 'true')

  await lookup.focus()
  await lookup.press('ArrowDown')
  await lookup.press('ArrowDown')
  await lookup.press('Enter')
  await expect(lookup).toHaveValue('KH-102 · Tạp hóa Minh Anh')
  await lookup.fill('Đại lý')
  await expect(page.getByText(/Chưa chọn mã hợp lệ/)).toBeVisible()
  await lookup.press('Escape')
  await expect(lookup).toHaveValue('KH-102 · Tạp hóa Minh Anh')
  // Editing the input keeps the first character and native caret.
  await lookup.fill('Nguyễn Đình Chiểu')
  await lookup.evaluate((input: HTMLInputElement) => input.setSelectionRange(6, 9))
  expect(await lookup.evaluate((input: HTMLInputElement) => [input.selectionStart, input.selectionEnd])).toEqual([6, 9])
  await lookup.press('ArrowDown')
  await lookup.press('Enter')
  await expect(lookup).toHaveValue(/KH-103/)
})

test('lookup exposes loading, error, forbidden and empty states without identity guessing', async ({ page }) => {
  await page.goto(path)
  const input = page.getByRole('combobox', { name: 'Khách hàng' })
  const source = page.getByLabel('Nguồn danh mục (mô phỏng)')
  for (const [state, message] of [
    ['loading', 'Đang tải'], ['error', 'Không đọc'], ['forbidden', 'Không có quyền']
  ]) {
    await source.selectOption(state)
    await input.focus()
    await expect(page.getByRole('listbox')).toContainText(message)
  }
  await source.selectOption('ready')
  await input.fill('Không có tên này')
  await expect(page.getByRole('listbox')).toContainText('Không có kết quả')
  await expect(page.getByRole('listbox').getByRole('option')).toHaveCount(0)
  await input.fill('Minh Anh')
  await input.press('ArrowDown')
  await input.press('Enter')
  await expect(input).toHaveValue(/KH-101/)
  await source.selectOption('forbidden')
  await page.getByRole('button', { name: 'Kiểm tra nháp mẫu' }).click()
  await expect(input).toHaveAccessibleDescription(/Nguồn danh mục chưa sẵn sàng/)
})

test('decimal buffer validates on blur, keeps incomplete edits and accepts a point', async ({ page }) => {
  await page.goto(path)
  const qty = page.getByRole('textbox', { name: 'Số lượng minh họa (thùng)' })
  await qty.fill('12,')
  await qty.blur()
  await expect(qty).toHaveValue('12,')
  await expect(qty).toHaveAttribute('aria-invalid', 'true')
  await qty.fill('12.375')
  await expect(qty).toHaveValue('12.375')
  await expect(qty).not.toHaveAttribute('aria-invalid', 'true')
})

test('grid roving navigation, numeric caret, validation, Escape, Enter and Tab', async ({ page }) => {
  await page.goto(path)
  const grid = page.getByRole('grid', { name: 'Dòng hàng mẫu' })
  const first = grid.getByRole('gridcell', { name: /^Số lượng SP-001:/ })
  await first.focus()
  await first.press('Enter')
  const editor = grid.getByRole('textbox', { name: 'Số lượng SP-001' })
  await expect(editor).toBeFocused()
  await editor.fill('8,')
  await editor.press('Enter')
  await expect(editor).toHaveAttribute('aria-invalid', 'true')
  await expect(editor).toHaveAccessibleDescription(/Nhập số dương/)
  await editor.press('Escape')
  await expect(grid.getByRole('textbox', { name: 'Số lượng SP-001' })).toHaveCount(0)
  await expect(first).toContainText('12,5')
  await expect(first).toBeFocused()

  await first.press('F2')
  await editor.fill('16,25')
  await editor.evaluate((input: HTMLInputElement) => input.setSelectionRange(3, 3))
  await editor.press('ArrowLeft')
  await expect(editor).toBeFocused()
  expect(await editor.evaluate((input: HTMLInputElement) => input.selectionStart)).toBe(2)
  await editor.press('Tab')
  await expect(grid.getByRole('gridcell', { name: /^Số lượng SP-001: 16,25/ })).toBeVisible()
  const next = grid.getByRole('gridcell', { name: /^Số lượng SP-002:/ })
  await expect(next).toBeFocused()
  await next.press('ArrowLeft')
  await expect(grid.getByRole('gridcell', { name: /^Đơn vị SP-002:/ })).toBeFocused()

  const last = grid.getByRole('gridcell', { name: /^Số lượng SP-004:/ })
  await last.focus()
  await last.press('Enter')
  await grid.getByRole('textbox', { name: 'Số lượng SP-004' }).press('Tab')
  await expect(page.getByRole('button', { name: 'Đặt lại các dòng mẫu' })).toBeFocused()
})

test('selection uses stable row ID; filter and sort are staged while editing', async ({ page }) => {
  await page.goto(path)
  const grid = page.getByRole('grid', { name: 'Dòng hàng mẫu' })
  const code = grid.getByRole('gridcell', { name: /^Mã hàng SP-001:/ })
  await code.focus()
  await code.press('Enter')
  await expect(page.getByRole('status').filter({ hasText: 'line-001' })).toContainText('Đang chọn: SP-001')
  const first = grid.getByRole('gridcell', { name: /^Số lượng SP-001:/ })
  await first.focus()
  await first.press('Enter')
  const editor = grid.getByRole('textbox', { name: 'Số lượng SP-001' })
  await editor.fill('9,5')
  const filter = page.getByLabel('Lọc mặt hàng (mã hoặc tên)')
  await filter.fill('SP-003')
  await expect(page.getByText('Bộ lọc hoặc sắp xếp đang chờ.', { exact: false })).toBeVisible()
  await expect(editor).toHaveValue('9,5')
  await editor.press('Enter')
  await expect(grid.getByRole('gridcell', { name: /^Mã hàng SP-003:/ })).toBeVisible()
  await expect(grid.getByRole('gridcell', { name: /^Mã hàng SP-003:/ })).toHaveAttribute('tabindex', '0')
  await expect(grid.getByRole('gridcell', { name: /^Mã hàng SP-001:/ })).toHaveCount(0)
  await expect(page.getByRole('status').filter({ hasText: 'line-001' })).toContainText('Dòng đang ẩn bởi bộ lọc')
  await page.getByRole('button', { name: 'Xóa bộ lọc' }).click()
  await expect(grid.getByRole('gridcell', { name: /^Số lượng SP-001: 9,5/ })).toBeVisible()
  await page.getByLabel('Sắp xếp').selectOption('quantity')
  await expect(page.getByText('4 / 4 dòng trong bộ lọc')).toBeVisible()
  await page.getByLabel('Nguồn dòng (mô phỏng)').selectOption('error')
  await expect(page.getByText('Không đọc được các dòng mẫu.')).toBeVisible()
})

async function measure(locator: Locator) {
  return locator.evaluate(element => {
    const box = element.getBoundingClientRect()
    return { width: box.width, height: box.height }
  })
}

for (const width of [1440, 320]) {
  test('theme and density at ' + width + 'px, no page overflow or clipped editor', async ({ page }, info) => {
    await page.setViewportSize({ width, height: 900 })
    await page.goto(path)
    const select = page.getByLabel('Mật độ')
    await select.selectOption(width === 320 ? 'field' : 'compact')
    await page.getByLabel('Chế độ hiển thị').selectOption('dark')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
    const table = page.getByRole('grid', { name: 'Dòng hàng mẫu' })
    const first = table.getByRole('gridcell', { name: /^Số lượng SP-001:/ })
    await first.focus()
    await first.press('Enter')
    const editor = table.getByRole('textbox', { name: 'Số lượng SP-001' })
    await expect(editor).toBeFocused()
    const geometry = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      clientWidth: document.documentElement.clientWidth
    }))
    expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth)
    const row = await measure(table.getByRole('row').filter({ has: editor }))
    const cell = await measure(first)
    const input = await measure(editor)
    const min = width === 320 ? 48 : 32
    expect(input.height).toBeGreaterThanOrEqual(min)
    expect(row.height).toBeGreaterThanOrEqual(width === 320 ? 48 : 41)
    if (width === 320) {
      const targets = await page.locator('main button, main input, main select, main a').evaluateAll(elements =>
        elements.filter(e => e.getClientRects().length && !(e as HTMLButtonElement).disabled)
          .map(e => { const box = e.getBoundingClientRect(); return { width: box.width, height: box.height } }))
      for (const target of targets) expect(Math.min(target.width, target.height)).toBeGreaterThanOrEqual(48)
    }
    await info.attach('ui-02-layout-' + width, { body: JSON.stringify({ row, cell, input, geometry }),
      contentType: 'application/json' })
    await info.attach('ui-02-dark-' + width, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
  })
}
