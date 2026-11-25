import { access, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { auth, open, signIn } from '../e2e-app03-7/return-refund'

const app = '/api/synthetic/app05/trip-close/trips'
const c2 = '/api/synthetic/app02c2'
const c4 = '/api/synthetic/app02c4'
const unload = '/api/synthetic/app02d3/unload'
const evidence = process.env.APP05_EVIDENCE_DIR!
const assignee = 'app05-assignee'
const dueAt = '2026-11-24T10:00:00+07:00'

type Option = { id: string; label: string }
type Catalog = { customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }
type Line = { orderId: string; orderLineId: string; confirmSourceId: string; confirmed: number; remaining: number; held: number }
type Work = { sourceKind: string; sourceId: string; relatedId?: string; label: string; followUp: { followUpId: string; assignee: string } | null }
type Readiness = { state: string; revision: string; aLines: Line[]; work: Work[]; blockers: { code: string }[] }
type Assignment = { commandId: string; sourceKind: string; sourceId: string; expectedRevision: string;
  assignee: string; dueAt: string; nextAction: string; reason: string }

async function get<T>(request: APIRequestContext, path: string, role: string): Promise<T> {
  const reply = await request.get(path, { headers: auth(role) })
  expect(reply.status(), path + ' ' + await reply.text()).toBe(200)
  return reply.json()
}

async function clickCommand<T>(page: Page, path: string, button: string): Promise<T> {
  const pending = page.waitForResponse(reply => reply.url().endsWith(path) && reply.request().method() === 'POST')
  await page.getByRole('button', { name: button, exact: true }).click()
  const reply = await pending
  expect(reply.ok(), path + ' ' + await reply.text()).toBe(true)
  return reply.json()
}

async function sqlCheckpoint(name: string, payload: unknown) {
  await writeFile(join(evidence, name + '.json'), JSON.stringify(payload, null, 2))
  await writeFile(join(evidence, name + '.ready'), '')
  await expect.poll(() => access(join(evidence, name + '.checked')).then(() => true, () => false), {
    timeout: 20_000, message: 'Wait for independent SQL check ' + name,
  }).toBe(true)
}

async function draft(sales: Page, catalog: Catalog, trip: string, quantity: number) {
  const customer = catalog.customers.find(row => row.label.startsWith('DAY-A-DEV'))!.id
  const sku = catalog.skus.find(row => row.label.startsWith('DAY-A-EA'))!.id
  await sales.goto('/work/customers?customer=' + customer)
  const selection = await clickCommand<{ id: string }>(sales, '/api/synthetic/app02c1/ship-to-selections', 'Chọn địa chỉ')
  await expect(sales.getByText('Đã ghi vào cơ sở dữ liệu.')).toBeVisible()
  await sales.goto('/work/orders')
  const form = sales.getByRole('form', { name: 'Tạo đơn A nháp' })
  await form.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer)
  await form.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption(selection.id)
  await form.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(trip)
  await form.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption(sku)
  await form.getByLabel('Số lượng dòng 1').fill(String(quantity))
  const order = await clickCommand<{ sourceId: string }>(sales, c2 + '/drafts', 'Lưu đơn nháp')
  await sales.goto(`/work/orders?trip=${trip}&order=${order.sourceId}`)
  await clickCommand(sales, c2 + '/confirm', 'Confirm đơn')
  return order.sourceId
}

async function runA(sales: Page, warehouse: Page, warehouseId: string, trip: string, order: string,
  held: number, loaded: number, delivered: number, beforeReturn?: () => Promise<void>) {
  await warehouse.goto(`/work/orders?trip=${trip}&order=${order}`)
  await warehouse.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(warehouseId)
  await warehouse.getByLabel('Số lượng giữ').fill(String(held))
  await clickCommand(warehouse, c2 + '/reserve', 'Reserve hàng')
  await warehouse.goto(`/work/load?trip=${trip}&order=${order}`)
  await warehouse.getByLabel('Số lượng nạp dòng 1').fill(String(loaded))
  await clickCommand(warehouse, '/api/synthetic/app02c3/load', 'Nạp hàng')
  await expect(warehouse.getByText('Đã nạp hàng lên xe.')).toBeVisible()
  await sales.goto('/work/delivery?trip=' + trip)
  await sales.getByLabel(/Số lượng đã giao dòng/).fill(String(delivered))
  const delivery = await clickCommand<{ sourceId: string }>(sales, c4 + '/deliver', 'Xác nhận đã giao')
  if (beforeReturn) await beforeReturn()
  await warehouse.goto('/work/settlement?trip=' + trip)
  const receipt = await clickCommand<{ sourceId: string }>(warehouse, unload, 'Trả hàng tồn xe')
  return { deliverySource: delivery.sourceId, unloadSource: receipt.sourceId }
}

test('readiness projects exact A and D.4 sources and persists an audited assignment through the workspace', async ({ context, request }, info) => {
  const catalog = await get<Catalog>(request, c2 + '/catalog', 'SOURCE')
  const trip = catalog.trips.find(row => row.label === 'DEV-20261123-A')!.id
  const alternate = catalog.trips.find(row => row.label === 'DEV-20261123-B')!.id
  const warehouseId = catalog.warehouses.find(row => row.label === 'C2-MAIN')!.id
  const read = (id: string) => get<Readiness>(request, app + '/' + id, 'OPS')
  expect(await read(alternate)).toMatchObject({ state: 'UNUSED', aLines: [], work: [] })

  const sales = await open(context, 'SOURCE', 'orders')
  const warehouse = await open(context, 'RECEIVER', 'orders')
  const order = await draft(sales, catalog, trip, 40)
  const mainSources = await runA(sales, warehouse, warehouseId, trip, order, 40, 40, 25, async () => {
    const loaded = await read(trip)
    expect(loaded.state).toBe('BLOCKED')
    expect(loaded.blockers.length).toBeGreaterThan(0)
  })

  await sqlCheckpoint('main-unload', mainSources)
  const otherOrder = await draft(sales, catalog, alternate, 5)
  expect(await read(alternate)).toMatchObject({
    aLines: [expect.objectContaining({ orderId: otherOrder, confirmed: 5, remaining: 5, held: 0 })],
  })
  const otherSources = await runA(sales, warehouse, warehouseId, alternate, otherOrder, 4, 3, 1)
  const comparison = await read(alternate)
  expect(comparison.aLines).toEqual([expect.objectContaining({ orderId: otherOrder, confirmed: 5, remaining: 4, held: 3 })])

  const initial = await read(trip)
  const d4 = initial.work.find(item => item.sourceKind === 'D4_RECONCILIATION')
  const accounting = '/api/synthetic/app02d4/accounting'
  async function transfer(operation: 'receive' | 'post') {
    const response = await request.post(`${accounting}/${d4!.relatedId}/${operation}`, {
      headers: auth('ACCOUNTANT'), data: { transportId: crypto.randomUUID() },
    })
    expect(response.ok(), operation + ' ' + await response.text()).toBe(true)
    const result = await response.json() as { id: string; sourceLineId: string }
    expect(result.sourceLineId).toBe(d4!.sourceId)
    return result.id
  }
  const accountingReceipt = await transfer('receive')
  const accountingPosting = await transfer('post')
  const posted = await read(trip)
  expect(posted.work.find(row => row.sourceId === d4!.sourceId)?.label).toContain('Chờ cutoff')

  // A return case belongs only to the delivery from which it was opened.
  const otherDelivery = comparison.work.find(item => item.sourceKind === 'D4_RECONCILIATION')!.relatedId
  await sales.goto('/work/returns?delivery=' + otherDelivery)
  await expect(sales.getByLabel('Nguồn giao và lô')).toHaveValue(new RegExp('^' + otherDelivery + ':'))
  await sales.getByLabel('Số lượng trả', { exact: true }).fill('1')
  await sales.getByLabel('Lý do', { exact: true }).fill('APP-05.1 · Hồ sơ trả từ chuyến B')
  await sales.getByRole('button', { name: 'Đề nghị trả hàng', exact: true }).click()
  await expect(sales.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const withReturn = await read(alternate)
  const returnWork = withReturn.work.find(item => item.sourceKind === 'F05_RETURN')
  expect(returnWork).toMatchObject({ relatedId: otherDelivery, followUp: null })
  expect(withReturn.work).toHaveLength(3)
  expect(new Set(withReturn.work.map(row => row.sourceKind + ':' + row.sourceId)).size).toBe(3)

  const before = await read(trip)

  expect(before).toMatchObject({
    state: 'NEEDS_ASSIGNMENT',
    aLines: [expect.objectContaining({ orderId: order, confirmed: 40, remaining: 15, held: 15 })],
  })
  const line = before.aLines[0]
  expect(before.work.find(item => item.sourceKind === 'D4_RECONCILIATION')).toMatchObject({ sourceId: d4!.sourceId })
  expect(d4?.relatedId).toBeTruthy()
  expect(before.work).toHaveLength(2)
  expect(before.work).toContainEqual(expect.objectContaining({ sourceKind: 'A_REMAINDER', sourceId: line.orderLineId, followUp: null }))
  const checkpoint = { trip, alternate, line, otherLine: comparison.aLines[0], d4, mainSources, otherSources, accountingReceipt, accountingPosting, returnWork }
  await sqlCheckpoint('before-assignment', checkpoint)

  const path = app + '/' + trip + '/follow-ups'
  const candidate: Assignment = {
    commandId: crypto.randomUUID(), sourceKind: 'A_REMAINDER', sourceId: line.orderLineId,
    assignee, dueAt, nextAction: 'Xếp chuyến giao tiếp ngày sau', reason: 'Bàn giao phần chưa giao APP-05.1',
    expectedRevision: before.revision,
  }
  async function post(input: Assignment, role = 'OPS') {
    return request.post(path, { headers: auth(role), data: input })
  }
  for (const invalid of [{ assignee: '' }, { dueAt: '' }, { dueAt: '2026-11-24T10:00:00' }]) {
    const rejected = await post({ ...candidate, ...invalid })
    expect([400, 422], await rejected.text()).toContain(rejected.status())
  }
  expect((await read(trip)).revision).toBe(before.revision)

  const ops = await open(context, 'OPS', 'trip-close?trip=' + trip)
  await expect(ops.getByRole('heading', { name: 'Chuẩn bị đóng chuyến' })).toBeVisible()
  const quantity = ops.locator(`[data-order-line-id="${line.orderLineId}"]`)
  await expect(quantity.locator('[data-quantity=remaining]')).toHaveText('15 EA')
  await expect(quantity.locator('[data-quantity=held]')).toHaveText('15 EA')
  const item = ops.locator(`[data-source-kind="A_REMAINDER"][data-source-id="${line.orderLineId}"]`)
  await expect(item).toContainText('Chưa giao người phụ trách và hạn xử lý.')
  await ops.screenshot({ path: info.outputPath('assignment-missing.png') })

  async function openSource(link: ReturnType<Page['getByRole']>, role: string) {
    await expect(link).toHaveAttribute('target', '_blank')
    const popup = context.waitForEvent('page')
    await link.click()
    const destination = await popup
    await signIn(destination, role)
    return destination
  }
  const accountingItem = ops.locator(`[data-source-kind="D4_RECONCILIATION"][data-source-id="${d4!.sourceId}"]`)
  const postingPage = await openSource(accountingItem.getByRole('link', { name: 'Mở dòng nguồn kế toán', exact: true }), 'ACCOUNTANT')
  await expect(postingPage).toHaveURL(new RegExp('/work/finance\\?delivery=' + d4!.relatedId + '&source=' + d4!.sourceId + '$'))
  await expect(postingPage.locator(`li[data-delivery="${d4!.relatedId}"]`).getByRole('button')).toHaveAttribute('aria-pressed', 'true')
  await expect(postingPage.locator('dl.sales-facts')).toContainText(d4!.sourceId)
  await postingPage.close()

  await ops.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(alternate)
  const returnItem = ops.locator(`[data-source-kind="F05_RETURN"][data-source-id="${returnWork!.sourceId}"]`)
  const returnsPage = await openSource(returnItem.getByRole('link', { name: 'Mở hồ sơ trả hàng', exact: true }), 'RECEIVER')
  await expect(returnsPage).toHaveURL(new RegExp('/work/returns\\?source=' + returnWork!.sourceId + '#return-' + returnWork!.sourceId + '$'))
  await expect(returnsPage.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true })).toHaveValue(returnWork!.sourceId)
  await expect(returnsPage.getByRole('button', { name: 'Nhận vào cách ly', exact: true })).toBeVisible()
  await returnsPage.close()
  await ops.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(trip)
  await item.getByRole('button', { name: 'Giao việc', exact: true }).click()
  await ops.getByLabel('Người phụ trách').selectOption(assignee)
  await ops.getByLabel('Hạn xử lý (ICT)').fill('2026-11-24T10:00')
  await ops.getByLabel('Hành động tiếp theo').fill(candidate.nextAction)
  await ops.getByLabel('Lý do').fill(candidate.reason)
  const submitted = ops.waitForRequest(req => req.url().endsWith(path) && req.method() === 'POST')
  const assigned = await clickCommand<{ followUpId: string }>(ops, path, 'Lưu giao việc')
  const command = (await submitted).postDataJSON() as Assignment
  expect(command).toMatchObject({ ...candidate, commandId: expect.any(String), dueAt: expect.any(String) })
  expect(Date.parse(command.dueAt)).toBe(Date.parse(dueAt))
  await expect(item).toContainText(assignee)
  await ops.screenshot({ path: info.outputPath('assignment-saved.png') })
  const replay = await post(command)
  expect(replay.ok(), await replay.text()).toBe(true)
  expect(await replay.json()).toMatchObject({ followUpId: assigned.followUpId })
  expect((await post({ ...command, reason: 'Changed payload' })).status()).toBe(409)
  expect((await post(candidate)).status()).toBe(409)
  expect([403, 404]).toContain((await post(command, 'PEER_OPS')).status())
  expect([403, 404]).toContain((await post(command, 'ASSIGNEE')).status())
  const peerTrips = await get<{ tripId: string }[]>(request, app, 'PEER_OPS')
  expect(peerTrips.length).toBeGreaterThan(0)
  expect(peerTrips.map(row => row.tripId)).not.toContain(trip)
  expect([403, 404]).toContain((await request.get(app + '/' + trip, { headers: auth('PEER_OPS') })).status())

  const updated = await read(trip)
  expect(updated.work.find(item => item.sourceId === line.orderLineId)?.followUp)
    .toMatchObject({ followUpId: assigned.followUpId, assignee })
  // D.4 remains open with no covering cutoff even though a regular AR item already exists.
  expect(updated.work.find(item => item.sourceId === d4!.sourceId)?.followUp).toBeNull()

  async function assignWork(id: string, work: Work, expectedRevision: string, nextAction: string) {
    const input = { ...candidate, commandId: crypto.randomUUID(), sourceKind: work.sourceKind,
      sourceId: work.sourceId, expectedRevision, nextAction }
    const reply = await request.post(app + '/' + id + '/follow-ups', { headers: auth('OPS'), data: input })
    expect(reply.ok(), await reply.text()).toBe(true)
    const result = await reply.json() as { followUpId: string }
    return { tripId: id, sourceId: work.sourceId, sourceKind: work.sourceKind, ...result }
  }
  const accountingAssignment = await assignWork(trip, d4!, updated.revision, 'Đối soát nguồn ngày 23/11 sau cutoff')
  expect((await read(trip)).state).toBe('READY_WITH_WORK')
  const returnAssignment = await assignWork(alternate, returnWork!, withReturn.revision, 'Tiếp nhận hồ sơ trả giao hàng')
  await sqlCheckpoint('after-assignment', { command, assigned, accountingAssignment, returnAssignment })

  expect([403, 404]).toContain((await request.get(app + '/' + trip, { headers: auth('OPS') })).status())
  expect([403, 404]).toContain((await post(command)).status())
  await sqlCheckpoint('after-revocation', { trip })
})
