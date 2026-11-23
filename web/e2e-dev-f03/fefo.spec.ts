import { expect, test, type Page } from '@playwright/test'

const route = '/api/synthetic/app02c2'
function open(page: Page, name: string) {
  return page.getByRole('navigation', { name: 'Sales A' }).getByRole('button', { name, exact: true }).click()
}

test('F03-01–04: convert boxes, reserve FEFO, load and issue from eligible lots', async ({ page, request }, info) => {
  const token = process.env.APP02C1_BROWSER_TOKEN_A
  const qa = process.env.DEV_F03_QA
  if (!token || !qa) throw new Error('Missing protected FEFO fixture')
  const headers = { Authorization: 'Bearer ' + token }
  const catalog = await (await request.get(route + '/catalog', { headers })).json()
  const customerId = catalog.customers[0].id
  const selection = await request.post('/api/synthetic/app02c1/ship-to-selections', { headers,
    data: { commandId: crypto.randomUUID(), customerId, address: {
      country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'FEFO DEV', recipient: 'Nhận DEV',
    } } })
  expect(selection.status()).toBe(200)
  await page.goto('/sales')
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await open(page, 'Đơn A và giữ hàng')
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customerId)
  await page.getByRole('combobox', { name: 'ShipTo đã chọn', exact: true }).selectOption((await selection.json()).id)
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(catalog.trips[0].id)
  await page.getByRole('combobox', { name: 'SKU dòng 1', exact: true }).selectOption(catalog.skus[0].id)
  await page.getByRole('combobox', { name: 'Đơn vị dòng 1', exact: true }).selectOption('BOX')
  await page.getByLabel('Số lượng dòng 1').fill('0.5')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await expect(page.getByText('Đơn vị hoặc bước số lượng không hợp lệ.')).toBeVisible()
  await page.getByLabel('Số lượng dòng 1').fill('2')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()

  const orders = await (await request.get(route + '/orders', { headers })).json()
  const order = orders[0]
  expect(order.lines[0]).toMatchObject({ ordered: 48, baseUom: 'EA' })
  const refused = await request.post(route + '/reserve', { headers, data: {
    commandId: crypto.randomUUID(), orderId: order.id, lineId: order.lines[0].lineId,
    locationId: qa, quantity: 1, confirmSourceId: order.confirmSourceId,
  } })
  expect(refused.status()).toBe(422)
  expect((await refused.json()).reasonCode).toBe('F03_LOT_STOCK_INSUFFICIENT')

  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(catalog.warehouses[0].id)
  await page.getByLabel('Số lượng giữ').fill('48')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ')).toBeVisible()
  await open(page, 'Nạp hàng')
  const warehouseLots = page.getByRole('list', { name: 'Lô giữ tại kho' }).getByRole('listitem')
  await expect(warehouseLots).toHaveCount(2)
  await expect(warehouseLots.nth(0)).toContainText('EARLY')
  await expect(warehouseLots.nth(1)).toContainText('LATE')
  const load = page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true })
  await page.getByLabel('Số lượng nạp dòng 1').fill('30')
  await load.click()
  await expect(page.getByText(/Đã nạp 30 EA/)).toBeVisible()
  await page.getByLabel('Số lượng nạp dòng 1').fill('18')
  await load.click()
  await expect(page.getByText(/Đã nạp 48 EA/)).toBeVisible()

  const deliveryApi = '/api/synthetic/app02c4'
  const tripId = catalog.trips[0].id
  const lines = await (await request.get(deliveryApi + '/trips/' + tripId + '/lines', { headers })).json()
  const late = lines.find((line: { lots: { code: string }[] }) => line.lots.some(lot => lot.code === 'LATE'))
  const bypass = await request.post(deliveryApi + '/deliver', { headers, data: {
    commandId: crypto.randomUUID(), tripId, manifestLineId: late.id, shipToVersion: late.shipToVersion, quantity: 1,
  } })
  expect(bypass.status()).toBe(422)
  expect((await bypass.json()).reasonCode).toBe('F03_FEFO_EARLIER_LOT')

  await open(page, 'Giao hàng')
  const vehicleLots = page.getByRole('list', { name: 'Lô khả dụng trên xe' }).getByRole('listitem')
  await expect(vehicleLots).toHaveCount(2)
  const delivery = page.getByRole('region', { name: 'Dòng đã nạp' })
  const earlyForm = delivery.locator('form').filter({ has: vehicleLots.filter({ hasText: 'EARLY' }) })
  const lateForm = delivery.locator('form').filter({ has: vehicleLots.filter({ hasText: 'LATE' }) })
  await earlyForm.getByRole('spinbutton').fill('30')
  await earlyForm.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(vehicleLots).toHaveCount(1)

  let original: string | null = null
  page.on('request', req => {
    if (req.method() === 'POST' && req.url().endsWith('/app02c4/deliver')) original = req.postData()
  })
  await lateForm.getByRole('spinbutton').fill('5')
  await lateForm.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  await expect(vehicleLots).toHaveCount(1)
  await expect(vehicleLots.first()).toContainText('LATE')
  await expect(vehicleLots.first()).toContainText('13 EA')
  expect(original).toBeTruthy()
  const retry = await request.post('/api/synthetic/app02c4/deliver', {
    headers: { ...headers, 'content-type': 'application/json' }, data: original!,
  })
  expect(retry.status()).toBe(200)
  await page.screenshot({ path: info.outputPath('fefo-delivery.png') })
})
