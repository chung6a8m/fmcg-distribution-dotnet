import { expect, test, type Page } from '@playwright/test'

async function connect(page: Page, route: 'customers' | 'orders' | 'load' | 'delivery', slot = 'A') {
  const prefix = route === 'delivery' ? 'APP02C4_SALES_TOKEN_' : route === 'load' ? 'APP02C3_WAREHOUSE_TOKEN_' : 'APP02C1_BROWSER_TOKEN_'
  const token = process.env[prefix + slot]
  if (!token) throw new Error('Missing synthetic browser credential')
  await page.goto('/sales/' + route)
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

test('Load 40, deliver 25, keep 15 held and reconcile calendar due date', async ({ page, browser, context }, info) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await connect(page, 'customers')
  await page.getByRole('button', { name: /C2-FIXTURE/ }).click()
  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await page.getByLabel('Quốc gia').selectOption('VN')
  await page.getByLabel('Vùng DEV').selectOption('HCM')
  await page.getByLabel('Khu vực').fill('Khu DEV')
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill('04 Duong DEV')
  await page.getByLabel('Người nhận').fill('Nguoi Nhan')
  await page.getByRole('button', { name: 'Lưu lựa chọn' }).click()
  await expect(page.getByText('04 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()

  await connect(page, 'orders')
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption({ label: 'C2-FIXTURE · Khách hàng C.2 DEV' })
  await page.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption({ label: '04 Duong DEV · Khu DEV · Một lần' })
  await page.getByRole('combobox', { name: 'Chuyến' }).selectOption({ label: 'C2-TRIP' })
  await page.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption({ label: 'C2-SKU · Hàng tổng hợp' })
  await page.getByLabel('Số lượng dòng 1').fill('40')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption({ label: 'C2-MAIN' })
  await page.getByLabel('Số lượng giữ').fill('40')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ')).toBeVisible()

  await connect(page, 'load')
  await page.getByLabel('Số lượng nạp dòng 1').fill('40')
  await page.getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText(/Đã nạp 40 EA · Còn giữ tại kho 0 EA/)).toBeVisible()

  await connect(page, 'delivery')
  const quantity = page.getByLabel('Số lượng đã giao dòng 1')
  const post = page.getByRole('button', { name: 'Xác nhận đã giao' })
  await expect(page.getByText('Chưa phát sinh công nợ.')).toBeVisible()
  await expect(page.getByText(/ShipTo v1 · 04 Duong DEV/)).toBeVisible()
  await quantity.fill('25')
  await context.setOffline(true)
  await expect(post).toBeDisabled()
  await context.setOffline(false)
  await page.route('**/app02c4/deliver', async route => {
    const outcome = await route.fetch()
    expect(outcome.status()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await post.click()
  await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
  await connect(page, 'delivery')
  await page.getByRole('button', { name: 'Tra cứu kết quả giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  await expect(page.getByText(/Đã nạp 40 EA · Đã giao 25 EA · Còn giữ 15 EA/)).toBeVisible()
  const ar = page.getByRole('region', { name: 'Công nợ theo lần giao' })
  await expect(ar.getByText('Còn nợ 375 · Giá vốn 250')).toBeVisible()
  const dates = await ar.getByText(/Ngày giao \d{4}-\d{2}-\d{2} · Hạn AR/).innerText()
  const [businessDate, dueDate] = dates.match(/\d{4}-\d{2}-\d{2}/g)!
  const expectedDue = new Date(businessDate)
  expectedDue.setUTCDate(expectedDue.getUTCDate() + 7)
  expect(dueDate).toBe(expectedDue.toISOString().slice(0, 10))
  expect(businessDate).not.toBe('2000-01-01')

  await page.setViewportSize({ width: 320, height: 800 })
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  await page.screenshot({ path: info.outputPath('delivery-narrow-dark.png'), fullPage: true })

  await quantity.fill('13')
  const failed = page.waitForResponse(response => response.url().endsWith('/app02c4/deliver') && response.status() === 500)
  await post.click()
  await failed
  await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
  await page.getByRole('button', { name: 'Tra cứu kết quả giao' }).click()
  await expect(page.getByText(/Chưa có kết quả ghi sổ/)).toBeVisible()
  await expect(page.getByRole('region', { name: 'Giao hàng cần đối soát' }).getByText(/13 · Chờ ghi sổ/)).toBeVisible()
  await expect(page.getByText(/Đã giao 25 EA · Còn giữ 15 EA/)).toBeVisible()
  await expect(ar.getByRole('article')).toHaveCount(1)

  const recovery = await browser.newContext()
  try {
    const freshPage = await recovery.newPage()
    await connect(freshPage, 'delivery')
    await expect(freshPage.getByRole('button', { name: 'Ghi lại lần giao đã lưu' })).toBeEnabled()
    await expect(freshPage.getByRole('button', { name: 'Xác nhận đã giao' })).toHaveCount(0)
  } finally { await recovery.close() }

  const peer = await browser.newContext()
  try {
    const pageB = await peer.newPage()
    await connect(pageB, 'delivery', 'B')
    await expect(pageB.getByText('Chưa có chuyến giao được phân công.')).toBeVisible()
  } finally { await peer.close() }
  expect(errors).toEqual([])
})
