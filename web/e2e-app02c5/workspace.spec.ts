import { expect, test, type Page } from '@playwright/test'
import { readFile } from 'node:fs/promises'

async function signIn(page: Page, slot = 'A') {
  const token = process.env['APP02C1_BROWSER_TOKEN_' + slot]
  if (!token) throw new Error('Missing synthetic browser credential')
  await page.goto('/sales')
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

function open(page: Page, name: string) {
  return page.getByRole('navigation', { name: 'Sales A' }).getByRole('button', { name, exact: true }).click()
}

test('Load 40.001, deliver 25, keep 15.001 held and reconcile the report', async ({ page, browser, context }, info) => {
  const errors: string[] = []
  let deliveryPosts = 0
  let customerListReads = 0
  let orderListReads = 0
  page.on('pageerror', error => errors.push(error.message))
  page.on('request', request => {
    if (request.method() === 'GET' && request.url().endsWith('/app02c1/customers')) customerListReads++
    if (request.method() === 'GET' && request.url().endsWith('/app02c2/orders')) orderListReads++
    if (request.method() === 'POST' && request.url().endsWith('/app02c4/deliver')) deliveryPosts++
  })
  await signIn(page)
  await expect(page.getByRole('button', { name: /C2-FIXTURE/ })).toBeEnabled()
  expect.soft(customerListReads, 'one initial Customer list read').toBe(1)
  await page.getByRole('button', { name: /C2-FIXTURE/ }).click()
  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await page.getByLabel('Quốc gia').selectOption('VN')
  await page.getByLabel('Vùng DEV').selectOption('HCM')
  await page.getByLabel('Khu vực').fill('Khu DEV')
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill('04 Duong DEV')
  await page.getByLabel('Người nhận').fill('Nguoi Nhan')
  await page.getByRole('button', { name: 'Lưu lựa chọn' }).click()
  await expect(page.getByText('04 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()

  await open(page, 'Đơn A và giữ hàng')
  await expect(page.getByRole('combobox', { name: 'Khách hàng', exact: true })).toBeEnabled()
  expect.soft(orderListReads, 'one initial Order list read').toBe(1)
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption({ label: 'C2-FIXTURE · Khách hàng C.2 DEV' })
  await page.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption({ label: '04 Duong DEV · Khu DEV · Một lần' })
  await page.getByRole('combobox', { name: 'Chuyến' }).selectOption({ label: 'C2-TRIP' })
  await page.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption({ label: 'C2-SKU · Hàng tổng hợp' })
  await page.getByLabel('Số lượng dòng 1').fill('40.001')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption({ label: 'C2-MAIN' })
  await page.getByLabel('Số lượng giữ').fill('40.001')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ')).toBeVisible()

  await open(page, 'Nạp hàng')
  await page.getByLabel('Số lượng nạp dòng 1').fill('40.001')
  await page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText(/Đã nạp 40\.001 EA · Còn giữ tại kho 0 EA/)).toBeVisible()

  await open(page, 'Giao hàng')
  await open(page, 'Báo cáo AR')
  await expect(page.getByText('Chưa có bằng chứng giao hàng trong phạm vi này.')).toBeVisible()
  await open(page, 'Giao hàng')
  const quantity = page.getByLabel('Số lượng đã giao dòng 1')
  const post = page.getByRole('button', { name: 'Xác nhận đã giao' })
  await expect(page.getByText('Chưa phát sinh công nợ.')).toBeVisible()
  await expect(page.getByText(/ShipTo v1 · 04 Duong DEV/)).toBeVisible()
  await quantity.fill('25')
  await context.setOffline(true)
  await expect(post).toBeDisabled()
  await context.setOffline(false)
  await page.evaluate(() => {
    const setItem = Storage.prototype.setItem
    Storage.prototype.setItem = function (key, value) {
      if (key.startsWith('app02c4.delivery.')) throw new DOMException('Storage unavailable', 'SecurityError')
      return setItem.call(this, key, value)
    }
  })
  await page.route('**/app02c4/deliver', async route => {
    const outcome = await route.fetch()
    expect(outcome.status()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await post.click()
  await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
  await open(page, 'Báo cáo AR')
  await expect(page.getByText(/Nguồn đã đối soát 1\/1/)).toBeVisible()
  await open(page, 'Giao hàng')
  await page.getByRole('button', { name: 'Tra cứu kết quả giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  expect(deliveryPosts).toBe(1)
  await expect(page.getByText('Đã nạp 40.001 EA · Đã giao 25 EA · Còn giữ 15.001 EA')).toBeVisible()
  await expect(quantity).toHaveAttribute('max', '15.001')
  const ar = page.getByRole('region', { name: 'Công nợ theo lần giao' })
  await expect(ar.getByText('Còn nợ 375 · Giá vốn 250')).toBeVisible()
  const dates = await ar.getByText(/Ngày giao \d{4}-\d{2}-\d{2} · Hạn AR/).innerText()
  const [businessDate, dueDate] = dates.match(/\d{4}-\d{2}-\d{2}/g)!
  const expectedDue = new Date(businessDate)
  expectedDue.setUTCDate(expectedDue.getUTCDate() + 7)
  expect(dueDate).toBe(expectedDue.toISOString().slice(0, 10))
  expect(businessDate).not.toBe('2000-01-01')
  const reportUrl = '/api/synthetic/app02c5/ar'
  const headers = (name: string) => ({ Authorization: 'Bearer ' + process.env[name] })
  const tenantA = headers('APP02C1_BROWSER_TOKEN_A')
  const postedResponse = await page.request.get(reportUrl, { headers: tenantA })
  expect(postedResponse.ok()).toBeTruthy()
  expect(postedResponse.headers()['cache-control']).toBe('no-store')
  const posted = await postedResponse.json()
  expect(posted).toMatchObject({ expected: 1, reconciled: 1, knownBalance: '375.0000', knownOverdue: '0' })
  const orders = await page.request.get('/api/synthetic/app02c2/orders', { headers: tenantA })
  expect(orders.ok()).toBeTruthy()
  const [order] = await orders.json() as { id: string; lines: { lineId: string }[] }[]
  const orderLineId = order.lines[0].lineId
  expect(orderLineId).toMatch(/^[0-9a-f-]{36}$/i)
  expect(orderLineId).not.toBe(order.id)
  expect(posted.sources[0]).toMatchObject({
    overdue: false, dueBusinessDate: dueDate + 'T00:00:00',
    orderId: order.id, orderLineId, originTrip: 'C2-TRIP', trip: 'C2-TRIP',
  })
  const peerLine = await page.request.get(reportUrl, {
    params: { orderLineId }, headers: headers('APP02C1_BROWSER_TOKEN_B'),
  })
  expect(peerLine.ok()).toBeTruthy()
  expect(await peerLine.json()).toMatchObject({ expected: null, sources: [] })
  const otherLine = await page.request.get(reportUrl, {
    params: { orderLineId: '00000000-0000-0000-0000-000000000001' }, headers: tenantA,
  })
  expect(otherLine.ok()).toBeTruthy()
  expect(await otherLine.json()).toMatchObject({ expected: null, sources: [] })

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
  await expect(page.getByText(/Đã giao 25 EA · Còn giữ 15\.001 EA/)).toBeVisible()
  await expect(ar.getByRole('article')).toHaveCount(1)

  const recovery = await browser.newContext()
  try {
    const freshPage = await recovery.newPage()
    await signIn(freshPage)
    await open(freshPage, 'Giao hàng')
    await freshPage.getByRole('button', { name: 'Tra cứu lần giao đã lưu' }).click()
    await expect(freshPage.getByText(/Chưa có kết quả ghi sổ/)).toBeVisible()
    await expect(freshPage.getByRole('button', { name: 'Ghi lại lần giao đã lưu' })).toBeEnabled()
    await expect(freshPage.getByRole('button', { name: 'Xác nhận đã giao' })).toHaveCount(0)
  } finally { await recovery.close() }

  const peer = await browser.newContext()
  try {
    const pageB = await peer.newPage()
    await signIn(pageB, 'B')
    await open(pageB, 'Giao hàng')
    await expect(pageB.getByText('Chưa có chuyến giao được phân công.')).toBeVisible()
  } finally { await peer.close() }
  await open(page, 'Báo cáo AR')
  await expect(page.getByText(/Nguồn đã đối soát 1\/2/)).toBeVisible()
  await page.getByLabel('Mã dòng đơn A').fill('sai-dinh-dang')
  await page.getByRole('button', { name: 'Tải báo cáo' }).click()
  await expect(page.getByRole('alert')).toHaveText('Nhập mã dòng đơn A theo định dạng UUID.')
  await page.getByLabel('Mã dòng đơn A').fill(orderLineId)
  const filteredReport = page.waitForResponse(response =>
    new URL(response.url()).searchParams.get('orderLineId') === orderLineId)
  await page.getByRole('button', { name: 'Tải báo cáo' }).click()
  expect((await filteredReport).ok()).toBeTruthy()
  await expect(page.getByText(/Nguồn đã đối soát 1\/2/)).toBeVisible()
  await expect(page.getByText(/Tổng còn nợ đã đối soát 375/)).toBeVisible()
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Xuất JSON' }).click()
  const exported = await download
  expect(exported.suggestedFilename()).toBe('sales-a-ar.json')
  expect(JSON.parse(await readFile(await exported.path(), 'utf8'))).toMatchObject({
    filter: { orderLineId },
    sources: [{ orderLineId }, { orderLineId }],
  })
  await page.locator('summary').filter({ hasText: 'Chờ ghi sổ' }).press('Enter')
  await expect(page.getByText('c1-dev-sales', { exact: true })).toBeVisible()
  await page.locator('summary').filter({ hasText: 'Đã đối soát' }).press('Enter')
  await expect(page.getByText('Đến hạn ' + dueDate)).toBeVisible()
  const deliveryDetail = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: 'Đã đối soát' }) })
  await expect(deliveryDetail.locator('dt', { hasText: 'Chuyến gốc' }).locator('+ dd')).toHaveText('C2-TRIP')
  await expect(deliveryDetail.locator('dt', { hasText: 'Chuyến giao' }).locator('+ dd')).toHaveText('C2-TRIP')
  await page.screenshot({ path: info.outputPath('ar-narrow-dark.png'), fullPage: true })

  const historical = await page.request.get(reportUrl, { headers: tenantA, params: { asOf: posted.asOf } })
  expect(await historical.json()).toMatchObject({ expected: 1, reconciled: 1 })
  const customerId: string = posted.sources[0].customerId
  const orderId: string = posted.sources[0].orderId
  const scoped = { customerId, orderId }
  const scopedReport = await page.request.get(reportUrl, { headers: tenantA, params: scoped })
  expect(await scopedReport.json()).toMatchObject({ expected: 2, reconciled: 1, filter: scoped })
  const exportResult = await page.request.get(reportUrl, { headers: tenantA, params: {
    ...scoped, deliveryId: posted.sources[0].deliveryId, due: dueDate, overdue: 'false', download: 'true',
  } })
  expect(exportResult.headers()['content-disposition']).toContain('sales-a-ar.json')
  expect(await exportResult.json()).toMatchObject({ expected: 1, reconciled: 1, knownBalance: '375.0000' })
  const empty = { expected: null, knownBalance: null, sources: [] }
  const wrongCustomer = await page.request.get(reportUrl, { headers: tenantA,
    params: { ...scoped, customerId: '00000000-0000-0000-0000-000000000001' } })
  expect(await wrongCustomer.json()).toMatchObject(empty)
  const peerReport = await page.request.get(reportUrl, { headers: headers('APP02C1_BROWSER_TOKEN_B'), params: scoped })
  expect(await peerReport.json()).toMatchObject(empty)
  expect((await page.request.get(reportUrl)).status()).toBe(401)
  expect((await page.request.get(reportUrl, { headers: headers('APP02C3_WAREHOUSE_TOKEN_A') })).status()).toBe(403)
  expect((await page.request.get(reportUrl, { headers: tenantA, params: { asOf: '2000-01-01T00:00:00' } })).status()).toBe(400)
  expect((await page.request.get(reportUrl, { headers: tenantA, params: { asOf: '2099-01-01T00:00:00Z' } })).status()).toBe(400)
  expect(deliveryPosts).toBe(2)
  await open(page, 'Đơn A và giữ hàng')
  await expect(page.getByText('Đã nạp 40.001 EA · Còn giữ trên xe 15.001 EA')).toBeVisible()
  await page.locator('summary').filter({ hasText: 'Nguồn và địa chỉ đã Confirm' }).press('Enter')
  await page.getByRole('button', { name: 'Công nợ theo đơn' }).click()
  await expect(page.getByLabel('Mã đơn A')).toHaveValue(orderId)
  await page.getByLabel('Mốc UTC (ISO 8601)').fill('2000-01-01T00:00:00Z')
  await expect(page.getByRole('button', { name: 'Tải báo cáo' })).toBeEnabled()
  await page.getByLabel('Mốc UTC (ISO 8601)').press('Enter')
  await expect(page.getByText('Chưa có bằng chứng giao hàng trong phạm vi này.')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  expect(errors).toEqual([])
})
