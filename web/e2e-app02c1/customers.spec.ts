import { expect, test, type Page } from '@playwright/test'

async function signIn(page: Page, slot: 'A' | 'B') {
  const token = process.env[`APP02C1_BROWSER_TOKEN_${slot}`]
  if (!token) throw new Error('Missing synthetic browser credential')
  await page.goto('/sales/customers')
  await expect(page.getByRole('heading', { name: 'Khách hàng và địa chỉ giao' })).toBeVisible()
  // Never put the credential in Playwright's locator action logs.
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Khách hàng', exact: true })).toBeVisible()
}

async function enterAddress(page: Page, line: string) {
  await page.getByLabel('Quốc gia').selectOption('VN')
  await page.getByLabel('Vùng DEV').selectOption('HCM')
  await page.getByLabel('Khu vực').fill('  Khu   DEV ')
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill(line)
  await page.getByLabel('Người nhận').fill('  Nguoi Nhan  ')
}

test('Customer, optional Outlet, direct and registered ShipTo persist via the real DEV API', async ({ page, context, browser }, testInfo) => {
  const pageErrors: string[] = []
  page.on('pageerror', error => pageErrors.push(error.message))
  await signIn(page, 'A')
  await expect(page.getByText('Chưa có khách hàng.')).toBeVisible()
  await page.getByRole('button', { name: 'Thêm khách hàng', exact: true }).press('Enter')
  await expect(page.getByRole('heading', { name: 'Thêm khách hàng' })).toBeFocused()
  await page.getByLabel('Mã khách hàng').fill('C1-DEV')
  await page.getByLabel('Tên khách hàng').fill('Khách hàng DEV')
  await page.route('**/customers/*', route => {
    if (route.request().method() === 'GET') return route.abort('failed')
    return route.continue()
  })
  await page.getByRole('button', { name: 'Lưu', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Không tải được dữ liệu từ API.')
  await page.unroute('**/customers/*')
  await page.getByRole('button', { name: 'Thử tải lại' }).click()
  await expect(page.getByText('Khách hàng chưa có điểm bán.')).toBeVisible()

  const stale = await context.newPage()
  try {
    await signIn(stale, 'A')
    await stale.getByRole('button', { name: /C1-DEV/ }).click()
    await stale.getByRole('button', { name: 'Sửa khách hàng', exact: true }).click()
    await stale.getByLabel('Tên khách hàng').fill('Bản sửa cũ')
    await page.getByRole('button', { name: 'Sửa khách hàng', exact: true }).click()
    await page.getByLabel('Tên khách hàng').fill('Khách hàng DEV đã sửa')
    await page.getByRole('button', { name: 'Lưu', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Khách hàng DEV đã sửa' })).toBeVisible()
    await stale.getByRole('button', { name: 'Lưu', exact: true }).click()
    await expect(stale.getByText(/Dữ liệu đã thay đổi/)).toBeVisible()
    await stale.getByRole('button', { name: 'Tải lại dữ liệu' }).click()
    await expect(stale.getByRole('heading', { name: 'Khách hàng DEV đã sửa' })).toBeVisible()
  } finally { await stale.close() }

  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await enterAddress(page, ' 01  Duong DEV ')
  await context.setOffline(true)
  await expect(page.getByText('Đang ngoại tuyến')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Lưu lựa chọn' })).toBeDisabled()
  await context.setOffline(false)
  await page.getByRole('button', { name: 'Lưu lựa chọn' }).click()
  const selections = page.getByRole('region', { name: 'Lựa chọn địa chỉ giao' })
  await expect(selections.getByText('01 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()

  await page.route('**/outlets/save', async route => {
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Thêm điểm bán' }).click()
  await page.getByLabel('Tên điểm bán').fill('Điểm bán DEV')
  await page.getByRole('button', { name: 'Lưu', exact: true }).click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await page.reload()
  await signIn(page, 'A')
  await page.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(page.getByText('Điểm bán DEV', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Sửa điểm bán' }).click()
  await page.getByLabel('Tên điểm bán').fill('Điểm bán DEV đã sửa')
  await page.getByRole('button', { name: 'Lưu', exact: true }).click()
  await expect(page.getByText('Điểm bán DEV đã sửa', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Thêm địa chỉ đăng ký' }).click()
  await page.getByLabel('Điểm bán (tùy chọn)').selectOption({ label: 'Điểm bán DEV đã sửa' })
  await enterAddress(page, '02 Duong DEV')
  await page.getByRole('button', { name: 'Lưu', exact: true }).click()
  await page.getByRole('button', { name: 'Chọn địa chỉ' }).click()
  await expect(selections.getByText('02 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()

  await page.getByRole('button', { name: 'Sửa địa chỉ', exact: true }).click()
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill('03 Duong DEV')
  await page.getByRole('button', { name: 'Lưu', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Địa chỉ đã đăng ký' }).getByText(/Phiên bản 2/)).toBeVisible()
  await expect(selections.getByText('02 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()
  await expect(selections.getByText('03 Duong DEV, Khu DEV, HCM, VN')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('desktop-light.png'), fullPage: true })

  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390)
  await page.screenshot({ path: testInfo.outputPath('narrow-dark.png'), fullPage: true })
  await page.setViewportSize({ width: 320, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  expect(pageErrors).toEqual([])

  const other = await browser.newContext()
  try {
    const tenantB = await other.newPage()
    await signIn(tenantB, 'B')
    await expect(tenantB.getByText('Chưa có khách hàng.')).toBeVisible()
  } finally { await other.close() }
})
