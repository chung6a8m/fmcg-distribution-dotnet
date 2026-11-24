import { expect, test, type APIResponse, type Page } from '@playwright/test'

const c1 = '/api/synthetic/app02c1'
const c2 = '/api/synthetic/app02c2'
const c3 = '/api/synthetic/app02c3'
const c4 = '/api/synthetic/app02c4'
const d1 = '/api/synthetic/app02d1'
const d2 = '/api/synthetic/app02d2'
const auth = (token: string) => ({ Authorization: 'Bearer ' + token })
type Option = { id: string; label: string }
type Order = { id: string; confirmSourceId: string; lines: { lineId: string }[] }
type Right = { id: string; warehouseHeld: string }
type SaleCatalog = {
  customers: { selections: { selectionId: string; shipToSnapshotId: string; outletId: string | null }[] }[]
  lines: { tripManifestLineId: string; availableQuantity: number }[]
  verifiedCash: { amount: number; evidenceRef: string }[]
}

async function signIn(page: Page, token: string) {
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await expect(page.getByRole('navigation', { name: 'Công việc DEV' })).toBeVisible()
}

test('Warehouse hands Mixed and B-only trips to Field with source recovery', async ({ page, context, request }, info) => {
  const { APP03_SOURCE_TOKEN: sales, APP03_RECEIVER_TOKEN: warehouse, APP03_PEER_TOKEN: peer } = process.env
  if (!sales || !warehouse || !peer) throw new Error('Missing APP-03.5 credentials')
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))

  async function accepted(response: APIResponse) {
    const content = await response.text()
    expect(response.status(), response.url() + ' ' + content).toBe(200)
    const body = JSON.parse(content)
    expect(body.status).not.toBe('REJECTED')
    return body
  }
  async function get<T>(path: string, token = sales): Promise<T> {
    return accepted(await request.get(path, { headers: auth(token) }))
  }
  const post = (path: string, data: object, token = sales) => request.post(path, { data, headers: auth(token) })
  async function rejected(response: APIResponse, reasonCode: string, status = 422) {
    expect(response.status()).toBe(status)
    expect(await response.json()).toMatchObject({ status: 'REJECTED', reasonCode, sourceId: null })
  }

  const catalog = await get<{ customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }>(c2 + '/catalog')
  const customerId = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE'))!.id
  const [mixed, bOnly] = catalog.trips.map(row => row.id)
  const locationId = catalog.warehouses[0].id
  const skuId = catalog.skus.find(row => row.label.startsWith('Z-FIELD-EA'))!.id
  const lotSku = catalog.skus.find(row => row.label.startsWith('R-05'))!.id
  const selection = await accepted(await post(c1 + '/ship-to-selections', {
    commandId: crypto.randomUUID(), customerId,
    address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: '24 Đường DEV', recipient: 'Điểm nhận A' },
  }))
  const draft = await accepted(await post(c2 + '/drafts', {
    commandId: crypto.randomUUID(), customerId, tripId: mixed, selectionId: selection.id,
    lines: [{ skuId, quantity: 4 }],
  }))
  const orderId = draft.sourceId
  await accepted(await post(c2 + '/confirm', { commandId: crypto.randomUUID(), orderId }))
  const order = (await get<Order[]>('/api/synthetic/app03/handoff')).find(row => row.id === orderId)!
  await accepted(await post(c2 + '/reserve', {
    commandId: crypto.randomUUID(), orderId, lineId: order.lines[0].lineId,
    confirmSourceId: order.confirmSourceId, locationId, quantity: 4,
  }, warehouse))
  const [aRight] = await get<Right[]>(c3 + '/trips/' + mixed + '/rights', warehouse)
  await accepted(await post(c3 + '/load', {
    commandId: crypto.randomUUID(), tripId: mixed,
    lines: [{ rightId: aRight.id, expectedWarehouseHeld: aRight.warehouseHeld, quantity: '4' }],
  }, warehouse))

  function quota(tripId: string, id: string, quantity: number) {
    const commandId = crypto.randomUUID()
    return { producerId: 'warehouse', commandId, sourceNumber: 'B-' + commandId,
      tripId, stockLocationId: locationId, skuId: id, baseQuantity: quantity }
  }
  await rejected(await post(d1 + '/quota', quota(mixed, lotSku, 1), warehouse), 'APP03_B_SKU_OR_QUANTITY_UNSUPPORTED')
  await rejected(await post(d1 + '/quota', quota(mixed, skuId, 7), warehouse), 'STOCK_INSUFFICIENT')
  expect((await post(d1 + '/quota', quota(mixed, skuId, 1))).status()).toBe(403)

  await page.goto('/work/b-stock?trip=' + mixed)
  await signIn(page, warehouse)
  await expect(page.getByRole('link', { name: 'Bán và giao hàng B' })).toHaveCount(0)
  await expect(page.getByLabel('Chuyến', { exact: true })).toHaveValue(mixed)
  await expect(page.getByLabel('SKU', { exact: true }).locator('option')).toHaveText([/Z-FIELD-EA/])
  await page.getByLabel('Số lượng quota').fill('3')
  await page.getByRole('button', { name: 'Cấp quota B', exact: true }).click()
  await expect(page.getByText('Đã cấp quota B.')).toBeVisible()
  await page.getByLabel('Số lượng nạp B').fill('3')
  await page.getByRole('button', { name: 'Nạp hàng B', exact: true }).click()
  await expect(page.getByText('Đã nạp hàng B.')).toBeVisible()

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, sales)
  await page.setViewportSize({ width: 320, height: 850 })
  const missingTrip = crypto.randomUUID()
  await page.goto('/work/b-sale?trip=' + missingTrip)
  const tripSelect = page.getByLabel('Chuyến', { exact: true })
  await expect(tripSelect.locator('option', { hasText: 'DEV-20261123-A' })).toHaveCount(1)
  await expect(page.getByRole('alert')).toContainText('Hãy chọn chuyến khác')
  await expect(tripSelect).toHaveValue('')
  await expect(page).toHaveURL(new RegExp('trip=' + missingTrip))
  await expect(page.getByRole('button', { name: 'Xem trước giao dịch' })).toHaveCount(0)
  const bTripsUrl = '**' + d2 + '/trips'
  await page.route(bTripsUrl, async route => {
    const response = await route.fetch()
    const trips = await response.json() as { tripId: string }[]
    await route.fulfill({ response, json: trips.filter(trip => trip.tripId !== bOnly) })
  })
  await page.getByRole('link', { name: 'Chuyến và công việc', exact: true }).focus()
  await page.keyboard.press('Enter')
  const field = page.getByRole('region', { name: 'Chuyến và công việc' })
  await expect(field).toBeFocused()
  await expect(field.getByRole('heading', { name: 'DEV-20261123-A' })).toBeVisible()
  const bOnlyRow = field.getByRole('listitem').filter({ has: page.getByRole('heading', { name: 'DEV-20261123-B' }) })
  await expect(bOnlyRow.getByRole('link', { name: 'Giao A' })).toBeVisible()
  await expect(bOnlyRow.getByRole('link', { name: /Bán B|Lịch sử B/ })).toHaveCount(0)
  await page.unroute(bTripsUrl)
  await field.getByRole('link', { name: 'Giao A', exact: true }).first().click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(mixed)
  const aForm = page.locator('form').filter({ has: page.getByRole('heading', { name: /Z-FIELD-EA/ }) })
  const aInput = aForm.getByLabel(/Số lượng đã giao dòng/)
  await aInput.fill('1')
  let aCommand: object | undefined
  await page.route('**' + c4 + '/deliver', async route => {
    aCommand = route.request().postDataJSON()
    const response = await route.fetch()
    expect(response.ok()).toBeTruthy()
    await route.abort()
  }, { times: 1 })
  await page.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
  await page.reload()
  await page.getByRole('button', { name: 'Gửi lại cùng yêu cầu' }).click()
  await expect(aForm.getByText(/Đã giao 1 EA/)).toBeVisible()
  const aReplay = await accepted(await post(c4 + '/deliver', aCommand!))
  expect(aReplay.replayed).toBe(true)

  await page.getByRole('link', { name: 'Bán và giao hàng B', exact: true }).click()
  await expect(page.getByLabel('Chuyến', { exact: true })).toHaveValue(mixed)
  await expect(page.getByLabel('Dòng hàng B').locator('option').nth(1)).toContainText('Còn 3 EA')
  await expect(page.getByRole('region', { name: 'Bán và giao hàng B' })).toBeFocused()
  const bCatalog = await get<SaleCatalog>(d2 + '/trips/' + mixed)
  const bCustomer = bCatalog.customers[0]
  const bShipTo = bCustomer.selections[0]
  const bLine = bCatalog.lines[0]
  const witness = bCatalog.verifiedCash.find(item => item.amount === 20000)!
  let bCommand: Record<string, unknown> | undefined

  async function prepareSale(tripId: string, quantity: number, expectedCash: number) {
    const snapshot = await get<SaleCatalog>(d2 + '/trips/' + tripId)
    await page.getByLabel('Dòng hàng B').selectOption(snapshot.lines[0].tripManifestLineId)
    await page.getByLabel('Khách hàng', { exact: true }).selectOption(customerId)
    await page.getByLabel('ShipTo đã chốt').selectOption(snapshot.customers[0].selections[0].selectionId)
    await page.getByLabel('Số lượng bán').fill(String(quantity))
    await page.getByLabel('Tiền mặt đã xác minh').selectOption(snapshot.verifiedCash.find(
      item => item.amount === expectedCash)!.evidenceRef)
    await page.getByRole('button', { name: 'Xem trước giao dịch' }).click()
    await expect(page.getByText(new RegExp('Phải trả ' + expectedCash.toLocaleString('vi-VN') + ' VND'))).toBeVisible()
  }

  const invalid = {
    producerId: 'c1-dev-sales', tripId: mixed, customerId, selectionId: bShipTo.selectionId,
    shipToSnapshotId: bShipTo.shipToSnapshotId, outletId: bShipTo.outletId,
    tripManifestLineId: bLine.tripManifestLineId, skuId, baseQuantity: 2,
    receivedCashAmount: 20000, verificationEvidenceRef: witness.evidenceRef,
  }
  for (const code of ['NO-PLAN', 'EXPIRED-PLAN', 'SELF-CHECK']) {
    const id = crypto.randomUUID()
    await rejected(await post(d2 + '/sales', { ...invalid, commandId: id, sourceNumber: id,
      customerId: catalog.customers.find(item => item.label.startsWith(code))!.id }), 'APP03_B_PRICE_PLAN_REQUIRED', 200)
  }
  const wrongAmount = crypto.randomUUID()
  await rejected(await post(d2 + '/sales', { ...invalid, commandId: wrongAmount, sourceNumber: wrongAmount,
    receivedCashAmount: 10000 }), 'APP03_B_EXACT_CASH_REQUIRED', 200)
  expect((await request.get(d2 + '/trips/' + mixed, { headers: auth(peer) })).status()).toBe(403)
  expect((await post(d2 + '/sales', { ...invalid, commandId: crypto.randomUUID(), sourceNumber: 'PEER' }, peer)).status()).toBe(403)

  await prepareSale(mixed, 2, 20000)
  const commit = page.getByRole('button', { name: 'Ghi bán và giao' })
  const box = await commit.boundingBox()
  expect(box!.height).toBeGreaterThanOrEqual(48)
  expect(box!.width).toBeGreaterThanOrEqual(48)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  await context.setOffline(true)
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(commit).toBeDisabled()
  await expect(page.getByText('Phải trả 20.000 VND')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Chuyến và công việc' })).toBeVisible()
  await context.setOffline(false)
  await page.route('**' + d2 + '/sales', async route => {
    bCommand = route.request().postDataJSON()
    const response = await route.fetch()
    expect(await response.json()).toMatchObject({ status: 'APPLIED' })
    await route.abort()
  }, { times: 1 })
  await commit.click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await page.reload()
  await page.getByRole('button', { name: 'Tra cứu yêu cầu', exact: true }).click()
  await expect(page.getByText('Đã ghi bán và giao.', { exact: true })).toBeVisible()
  const bReplay = await accepted(await post(d2 + '/sales', bCommand!))
  expect(bReplay.replayed).toBe(true)
  expect((await post(d2 + '/sales', { ...bCommand, baseQuantity: 1 })).status()).toBe(409)
  const bHistory = page.getByRole('region', { name: 'Bán và giao hàng B' })
    .getByRole('heading', { name: 'Giao dịch B đã ghi trên chuyến' })
  await expect(bHistory).toBeVisible()
  await expect(page.getByText(bReplay.sourceId, { exact: false }).first()).toBeVisible()
  await page.screenshot({ path: info.outputPath('field-320.png'), fullPage: true })

  await page.getByRole('link', { name: 'Giao hàng A', exact: true }).click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(mixed)
  await aInput.fill('1')
  await page.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(aForm.getByText(/Đã giao 2 EA/)).toBeVisible()
  expect((await get<SaleCatalog>(d2 + '/trips/' + mixed)).lines[0].availableQuantity).toBe(1)

  await accepted(await post(d1 + '/quota', quota(bOnly, skuId, 2), warehouse))
  const [right] = await get<Right[]>(d1 + '/trips/' + bOnly + '/load-rights', warehouse)
  await accepted(await post(d1 + '/load', { commandId: crypto.randomUUID(), tripId: bOnly,
    lines: [{ rightId: right.id, expectedWarehouseHeld: right.warehouseHeld, quantity: '2' }] }, warehouse))
  await page.getByRole('link', { name: 'Chuyến và công việc', exact: true }).click()
  await field.getByRole('link', { name: 'Bán B', exact: true }).last().click()
  await expect(page.getByLabel('Chuyến', { exact: true })).toHaveValue(bOnly)
  const lastResult = page.getByRole('button', { name: 'Mở lại kết quả', exact: true })
  await expect(lastResult).toHaveCount(0)
  await page.reload()
  await expect(tripSelect).toHaveValue(bOnly)
  await expect(lastResult).toHaveCount(0)
  await expect(page.getByText(bReplay.sourceId, { exact: false })).toHaveCount(0)
  await tripSelect.selectOption(mixed)
  await expect(lastResult).toBeVisible()
  await lastResult.click()
  await expect(page.getByText('Đã ghi bán và giao.', { exact: true })).toBeVisible()
  await expect(tripSelect).toHaveValue(mixed)
  await tripSelect.selectOption(bOnly)
  await expect(lastResult).toHaveCount(0)
  await expect(page.getByText(bReplay.sourceId, { exact: false })).toHaveCount(0)
  await prepareSale(bOnly, 1, 10000)
  await commit.click()
  await expect(page.getByText('Đã ghi bán và giao.', { exact: true })).toBeVisible()

  const reopened = await context.newPage()
  await reopened.goto('/work/b-sale?trip=' + mixed)
  await signIn(reopened, sales)
  await expect(reopened.getByText(bReplay.sourceId, { exact: false })).toBeVisible()
  await reopened.close()

  await page.getByRole('link', { name: 'Viếng thăm', exact: true }).click()
  await page.getByLabel('Khách hàng', { exact: true }).selectOption(customerId)
  await expect(page.getByLabel('Kết quả viếng thăm')).toHaveValue('NO_ORDER')
  await page.getByLabel('Lý do').fill('Điểm bán chưa cần đặt thêm')
  await page.getByRole('button', { name: 'Hoàn tất viếng thăm' }).click()
  await expect(page.getByText(/Không có đơn.*Điểm bán chưa cần/)).toBeVisible()
  expect(errors).toEqual([])
})
