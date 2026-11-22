import { expect, test, type Page } from '@playwright/test'

async function signIn(page: Page, route: 'customers' | 'orders' | 'load', slot: 'A' | 'B' = 'A') {
  const token = process.env[(route === 'load' ? 'APP02C3_WAREHOUSE_TOKEN_' : 'APP02C1_BROWSER_TOKEN_') + slot]
  if (!token) throw new Error('Missing synthetic browser credential')
  await page.goto('/sales/' + route)
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

test('C.2 reservation becomes two partial manifests and a failed attempt rolls back', async ({ page, browser, context }, info) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await signIn(page, 'customers')
  await page.getByRole('button', { name: /C2-FIXTURE/ }).click()
  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await page.getByLabel('Quốc gia').selectOption('VN')
  await page.getByLabel('Vùng DEV').selectOption('HCM')
  await page.getByLabel('Khu vực').fill('Khu DEV')
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill('03 Duong DEV')
  await page.getByLabel('Người nhận').fill('Nguoi Nhan')
  await page.getByRole('button', { name: 'Lưu lựa chọn' }).click()
  await expect(page.getByText('03 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()

  await signIn(page, 'orders')
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption({ label: 'C2-FIXTURE · Khách hàng C.2 DEV' })
  await page.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption({ label: '03 Duong DEV · Khu DEV · Một lần' })
  await page.getByRole('combobox', { name: 'Chuyến' }).selectOption({ label: 'C2-TRIP' })
  await page.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption({ label: 'C2-SKU · Hàng tổng hợp' })
  await page.getByLabel('Số lượng dòng 1').fill('40')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption({ label: 'C2-MAIN' })
  await page.getByLabel('Số lượng giữ').fill('40')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ', { exact: true })).toBeVisible()

  await signIn(page, 'load')
  const quantity = page.getByLabel('Số lượng nạp dòng 1')
  const load = page.getByRole('button', { name: 'Nạp hàng', exact: true })
  await expect(page.getByText(/Đã nạp 0 EA · Còn giữ tại kho 40 EA/)).toBeVisible()
  await quantity.fill('15')
  await context.setOffline(true)
  await expect(load).toBeDisabled()
  await context.setOffline(false)
  await page.route('**/app02c3/load', async route => {
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await load.click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await signIn(page, 'load')
  await page.getByRole('button', { name: 'Tra cứu kết quả nạp' }).click()
  await expect(page.getByText(/Đã nạp 15 EA · Còn giữ tại kho 25 EA/)).toBeVisible()

  await quantity.fill('10')
  await load.click()
  await expect(page.getByText(/Đã nạp 25 EA · Còn giữ tại kho 15 EA/)).toBeVisible()
  await page.setViewportSize({ width: 320, height: 800 })
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  await page.screenshot({ path: info.outputPath('load-narrow-dark.png'), fullPage: true })

  await quantity.fill('13')
  const failed = page.waitForResponse(response => response.url().endsWith('/app02c3/load') && response.status() === 500)
  await load.click()
  await failed
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await page.getByRole('button', { name: 'Tra cứu kết quả nạp' }).click()
  await expect(page.getByText(/Chưa tìm thấy kết quả/)).toBeVisible()
  await expect(page.getByText(/Đã nạp 25 EA · Còn giữ tại kho 15 EA/)).toBeVisible()
  await expect(load).toBeDisabled()

  const other = await browser.newContext()
  try {
    const pageB = await other.newPage()
    await signIn(pageB, 'load', 'B')
    await expect(pageB.getByText('Chưa có chuyến được phân công.')).toBeVisible()
  } finally { await other.close() }
  expect(errors).toEqual([])
})
