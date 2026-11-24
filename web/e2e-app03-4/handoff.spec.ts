import { expect, test, type APIResponse, type Page } from '@playwright/test'

const handoff = '/api/synthetic/app03/handoff'
const c1 = '/api/synthetic/app02c1'
const c2 = '/api/synthetic/app02c2'
const c3 = '/api/synthetic/app02c3'
const c4 = '/api/synthetic/app02c4'
const auth = (token: string) => ({ Authorization: 'Bearer ' + token })

type Order = {
  id: string; tripId: string; confirmSourceId: string
  lines: { lineId: string; ordered: number; pricing: { gross: number; version: number } }[]
}
type Option = { id: string; label: string }
type Right = { id: string; orderId: string; warehouseHeld: string }
type DeliveryLine = { id: string; shipToVersion: number; available: string }
type Load = { commandId: string; tripId: string; lines: { rightId: string; expectedWarehouseHeld: string; quantity: string }[] }

async function signIn(page: Page, token: string) {
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

test('Sales hands two orders to Warehouse on the same constrained lot', async ({ page, request }, info) => {
  const { APP03_SOURCE_TOKEN: sales, APP03_RECEIVER_TOKEN: warehouse, APP03_PEER_TOKEN: peer } = process.env
  if (!sales || !warehouse || !peer) throw new Error('Missing APP-03.4 credentials')
  const pageErrors: string[] = []
  page.on('pageerror', error => pageErrors.push(error.message))
  async function get<T>(path: string, token = sales): Promise<T> {
    const response = await request.get(path, { headers: auth(token) })
    expect(response.status(), path).toBe(200)
    return response.json()
  }
  const post = (path: string, data: object, token = sales) => request.post(path, { headers: auth(token), data })
  async function accepted(response: APIResponse) {
    expect(response.status()).toBe(200)
    return response.json()
  }
  async function rejected(response: APIResponse, reasonCode: string) {
    expect(response.status()).toBe(422)
    expect(await response.json()).toMatchObject({ status: 'REJECTED', reasonCode, sourceId: null })
  }

  expect(await get(handoff, warehouse)).toEqual([])
  const catalog = await get<{ customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }>(c2 + '/catalog')
  const customer = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE'))!
  const [tripA, tripB] = catalog.trips.map(row => row.id)
  const skuId = catalog.skus[0].id
  const locationId = catalog.warehouses[0].id
  async function selectAddress(customerId: string) {
    return (await accepted(await post(c1 + '/ship-to-selections', {
      commandId: crypto.randomUUID(), customerId,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: '24 Đường DEV', recipient: 'Điểm nhận DEV' },
    }))).id as string
  }
  async function draft(customerId: string, tripId: string) {
    const existing = new Set((await get<Order[]>(handoff)).map(row => row.id))
    const selectionId = await selectAddress(customerId)
    await accepted(await post(c2 + '/drafts', { commandId: crypto.randomUUID(), customerId, tripId, selectionId,
      lines: [{ skuId, uomCode: 'PACK', quantity: 2 }],
    }))
    return (await get<Order[]>(handoff)).find(order => order.tripId === tripId && !existing.has(order.id))!.id
  }

  const orderA = await draft(customer.id, tripA)
  expect(await get(handoff, warehouse)).toEqual([])
  expect((await post(c2 + '/confirm', { commandId: crypto.randomUUID(), orderId: orderA }, warehouse)).status()).toBe(403)
  const orderB = await draft(customer.id, tripB)
  await page.goto('/work/orders')
  await signIn(page, sales)
  await expect(page).toHaveTitle('FMCG DMS — APP-03 DEV')
  await page.getByLabel('Bước xử lý').selectOption('confirm')
  await page.getByRole('link', { name: /DEV-20261123-A/ }).focus()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(orderA))
  await expect(page.getByRole('region', { name: 'Đơn A và giữ hàng' })).toBeFocused()
  let droppedConfirm: string | null = null
  await page.route('**' + c2 + '/confirm', async route => {
    droppedConfirm = route.request().postData()
    await route.abort()
  }, { times: 1 })
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await page.goto(`/work/orders?trip=${tripB}&order=${orderB}`)
  await expect(page.getByRole('link', { name: 'Mở đơn của lệnh đang chờ' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gửi lại yêu cầu' })).toHaveCount(0)
  expect((await get<Order[]>(handoff)).filter(order => order.confirmSourceId)).toEqual([])
  await page.getByRole('link', { name: 'Mở đơn của lệnh đang chờ' }).click()
  await expect(page).toHaveURL(new RegExp(orderA))
  await page.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(page.getByText(/Chưa tìm thấy kết quả/)).toBeVisible()
  const replayedConfirm = page.waitForRequest(req => req.url().endsWith(c2 + '/confirm') && req.method() === 'POST')
  await page.getByRole('button', { name: 'Gửi lại yêu cầu' }).click()
  expect((await replayedConfirm).postData()).toBe(droppedConfirm)
  await expect(page.getByText(/Đã Confirm · Chưa giữ hàng/)).toBeVisible()
  expect((await get<Order[]>(handoff)).filter(order => order.confirmSourceId).map(order => order.id)).toEqual([orderA])
  await accepted(await post(c2 + '/confirm', { commandId: crypto.randomUUID(), orderId: orderB }))
  const orders = await get<Order[]>(handoff)
  const beforePricing = orders.map(o => o.lines[0].pricing)
  expect(beforePricing).toEqual(orders.map(() => expect.objectContaining({ gross: 40000, version: 1 })))

  for (const code of ['NO-PLAN', 'EXPIRED-PLAN', 'SELF-CHECK']) {
    const customerId = catalog.customers.find(row => row.label.startsWith(code))!.id
    const orderId = await draft(customerId, tripA)
    await rejected(await post(c2 + '/confirm', { commandId: crypto.randomUUID(), orderId }), 'F04_PRICE_PLAN_REQUIRED')
  }
  expect((await get<Order[]>(handoff)).filter(o => o.confirmSourceId).map(o => o.lines[0].pricing)).toEqual(beforePricing)
  expect(await get(handoff, peer)).toEqual([])

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, warehouse)
  await expect(page.getByLabel('Bước xử lý')).toHaveValue('all')
  await page.getByLabel('Bước xử lý').selectOption('reserve')
  await expect(page.getByRole('button', { name: 'Confirm đơn' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: /DEV-20261123-[AB]/ })).toHaveCount(2)
  await page.getByLabel('Lọc theo chuyến').selectOption(tripA)
  await expect(page.getByRole('link', { name: /DEV-20261123-B/ })).toHaveCount(0)
  await page.getByRole('link', { name: /DEV-20261123-A/ }).click()
  await expect(page.getByText(/2 PACK × 2 = 4 EA/)).toBeVisible()
  await page.goBack()
  await expect(page.getByLabel('Lọc theo chuyến')).toHaveValue(tripA)
  await expect(page.getByLabel('Bước xử lý')).toHaveValue('reserve')
  await page.goForward()
  await expect(page).toHaveURL(new RegExp(orderA))
  await page.getByRole('link', { name: 'Về hàng đợi', exact: true }).click()
  await expect(page.getByLabel('Lọc theo chuyến')).toHaveValue(tripA)
  await expect(page.getByLabel('Bước xử lý')).toHaveValue('reserve')
  await page.getByRole('link', { name: /DEV-20261123-A/ }).click()
  const reserve = (id: string, quantity: number) => {
    const order = orders.find(row => row.id === id)!
    return { commandId: crypto.randomUUID(), orderId: id, lineId: order.lines[0].lineId,
      confirmSourceId: order.confirmSourceId, locationId, quantity }
  }
  await rejected(await post(c2 + '/reserve', reserve(orderA, 1), peer), 'ORDER_NOT_FOUND')
  let results: [APIResponse, APIResponse] | undefined
  await page.route('**' + c2 + '/reserve', async route => {
    results = await Promise.all([route.fetch(), post(c2 + '/reserve', reserve(orderB, 4), warehouse)])
    await route.fulfill({ response: results[0] })
  }, { times: 1 })
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(locationId)
  await page.getByLabel('Số lượng giữ').fill('4')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect.poll(() => results?.map(response => response.status()).sort()).toEqual([200, 422])
  const loser = results![0].ok() ? orderB : orderA
  const winner = loser === orderA ? orderB : orderA
  const loserTrip = loser === orderA ? tripA : tripB
  const winnerTrip = loser === orderA ? tripB : tripA
  const winningSource = (await results!.find(response => response.ok())!.json()).sourceId as string
  await rejected(results!.find(response => !response.ok())!, 'F03_LOT_STOCK_INSUFFICIENT')
  await expect(page.getByRole('button', { name: 'Tải lại đơn' })).toBeEnabled()
  await page.goto(`/work/orders?trip=${loserTrip}&order=${loser}`)
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(locationId)
  await page.getByLabel('Số lượng giữ').fill('2')
  let heldSource = ''
  await page.route('**' + c2 + '/reserve', async route => {
    heldSource = (await accepted(await route.fetch())).sourceId
    await route.abort()
  }, { times: 1 })
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await page.goto(`/work/orders?trip=${winnerTrip}&order=${winner}`)
  await expect(page.getByRole('link', { name: 'Mở đơn của lệnh đang chờ' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gửi lại yêu cầu' })).toHaveCount(0)
  const reserveTrail = page.getByRole('table', { name: 'Lịch sử lô' })
  const early = reserveTrail.getByRole('row').filter({ hasText: 'RET-EARLY' })
  const origin = reserveTrail.getByRole('row').filter({ hasText: 'RET-ORIGIN' })
  await expect(early.getByRole('link', { name: winningSource })).toBeVisible()
  await origin.getByRole('link', { name: winningSource }).click()
  await expect(origin).toBeFocused()
  await expect(early).not.toBeFocused()
  await page.getByRole('link', { name: 'Mở đơn của lệnh đang chờ' }).click()
  const recovery = page.getByRole('region', { name: 'Khôi phục lệnh' })
  await expect(recovery).toContainText(loser)
  await expect(recovery).toContainText('2 EA')
  await recovery.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(recovery).toHaveCount(0)
  await expect(page.getByRole('link', { name: heldSource })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Tải lại đơn' })).toBeEnabled()
  await page.goto(`/work/orders?trip=${tripA}&order=${orderA}`)
  const [rightA] = await get<Right[]>(c3 + '/trips/' + tripA + '/rights', warehouse)
  const [rightB] = await get<Right[]>(c3 + '/trips/' + tripB + '/rights', warehouse)
  expect(rightA.orderId).toBe(orderA)
  expect(rightB.orderId).toBe(orderB)
  expect(Number(rightA.warehouseHeld) + Number(rightB.warehouseHeld)).toBe(6)
  expect((await request.get(c3 + '/trips/' + tripA + '/rights', { headers: auth(peer) })).status()).toBe(403)
  const load = (tripId: string, right: Right): Load => ({ commandId: crypto.randomUUID(), tripId,
    lines: [{ rightId: right.id, expectedWarehouseHeld: right.warehouseHeld, quantity: right.warehouseHeld }],
  })
  expect((await post(c3 + '/load', load(tripA, rightA), peer)).status()).toBe(403)
  expect((await post(c3 + '/load', load(tripA, rightA), sales)).status()).toBe(403)
  await page.getByRole('button', { name: 'Tải lại đơn' }).click()
  await page.getByRole('link', { name: 'Nạp đơn lên xe' }).click()
  await expect(page).toHaveURL(new RegExp('order=' + orderA))
  await expect(page.getByRole('combobox', { name: 'Chuyến', exact: true })).toHaveValue(tripA)
  await expect(page.getByText('RET-ORIGIN', { exact: true })).toBeVisible()
  await page.context().setOffline(true)
  await expect(page.getByRole('button', { name: 'Nạp hàng', exact: true })).toBeDisabled()
  await page.context().setOffline(false)
  let loadA: Load | undefined
  let loadBResult: APIResponse | undefined
  await page.route('**' + c3 + '/load', async route => {
    loadA = route.request().postDataJSON()
    const [response, other] = await Promise.all([route.fetch(), post(c3 + '/load', load(tripB, rightB), warehouse)])
    loadBResult = other
    await route.fulfill({ response })
  }, { times: 1 })
  await page.getByLabel('Số lượng nạp dòng 1').fill(String(Number(rightA.warehouseHeld)))
  await page.getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText('Đã nạp hàng lên xe.', { exact: true })).toBeVisible()
  await accepted(loadBResult!)
  const replay = await accepted(await post(c3 + '/load', loadA!, warehouse))
  expect(replay.replayed).toBe(true)
  await rejected(await post(c3 + '/load', load(tripA, rightA), warehouse), 'C3_STALE_WAREHOUSE_HELD')
  await page.getByRole('link', { name: 'Xem đơn và lịch sử lô' }).click()
  await expect(page).toHaveURL(new RegExp(`order=${orderA}&handoffSource=${replay.sourceId}`))
  if (winner === orderA)
    await expect(page.getByText(`Nguồn ${replay.sourceId} có 2 dòng lô.`, { exact: true })).toBeFocused()
  else
    await expect(page.getByRole('table', { name: 'Lịch sử lô' }).getByRole('row', {
      name: new RegExp(replay.sourceId),
    })).toBeFocused()
  await expect(page.getByText(new RegExp(`Còn ở kho 0 EA · Còn trên xe ${Number(rightA.warehouseHeld)} EA`))).toBeVisible()
  await page.screenshot({ path: info.outputPath('handoff-desktop.png'), fullPage: true })

  const deliveries = await Promise.all([tripA, tripB].map(async tripId => {
    const [line] = await get<DeliveryLine[]>(c4 + '/trips/' + tripId + '/lines')
    return { commandId: crypto.randomUUID(), tripId, manifestLineId: line.id,
      quantity: line.available, shipToVersion: line.shipToVersion }
  }))
  const issues = await Promise.all(deliveries.map(input => post(c4 + '/deliver', input)))
  for (const response of issues) await accepted(response)
  await page.reload()
  await expect(page.getByRole('table', { name: 'Lịch sử lô' }).getByRole('row', { name: /Đã giao.*RET-ORIGIN/ })).toBeVisible()
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.setViewportSize({ width: 320, height: 900 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  const lotTrail = page.getByRole('region', { name: 'Lô của R-05' })
  await expect(lotTrail.getByText('Mã nguồn', { exact: true }).last()).toBeVisible()
  await lotTrail.screenshot({ path: info.outputPath('handoff-320-dark.png') })
  expect(pageErrors).toEqual([])
  expect(await get(handoff, peer)).toEqual([])
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, peer)
  await expect(page.getByLabel('Lọc theo chuyến')).toHaveValue('')
  await expect(page.getByLabel('Bước xử lý')).toHaveValue('all')
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, warehouse)
  await expect(page.getByLabel('Lọc theo chuyến')).toHaveValue(tripA)
  await expect(page.getByLabel('Bước xử lý')).toHaveValue('reserve')
})
