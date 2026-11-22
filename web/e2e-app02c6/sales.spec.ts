import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { expect, test, type APIResponse, type Page, type Response } from '@playwright/test'

type Slot = 'A' | 'B'
type Customer = { customerId: string; selectionId: string }
type Option = { id: string; label: string }
type Catalog = { customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }
type Order = { id: string; customer: string; confirmSourceId: string; shipToSnapshotId: string
  lines: { lineId: string; reserved: number; heldOnTrip: number; policySourceHash: string; policyVersion: number; termsDays: number }[] }
type Right = { id: string; orderId: string; warehouseHeld: string }
type DeliveryLine = { id: string; orderId: string; loaded: string; available: string; shipToVersion: number }
type Command = { commandId: string; [key: string]: unknown }
type Checkpoint = { slot: Slot; caseId: string; phase: string; orderId?: string; customerId?: string }
type Snapshot = {
  main: number; truck: number; mainValue: number; truckValue: number; allocated: number
  deliveries: number; ar: number; cogs: number; manifests: number; held: number; orders: number
  fingerprint: string
  orderTrace?: {
    confirmation: { OrderId: string; CustomerId: string; SelectionId: string; ConfirmSourceId: string
      ShipToSnapshotId: string; ShipToHash: string; OutletCount: number } | null
    policy: { Version: number; TermsDays: number; PolicyHash: string; Reserved: number } | null
    deliveries: { Id: string; BusinessSourceId: string; BaseQuantity: number; CostAmount: number; PayableAmount: number
      ArId: string; DebtorId: string; OriginalAmount: number; BusinessDate: string; DueBusinessDate: string }[]
  }
}

const synthetic = '/api/synthetic/app02c'
const address = { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: '01 Duong DEV', recipient: 'Nguoi Nhan' }
const checkpoints = process.env.APP02C6_CHECKPOINTS!
const checkpointBySnapshot = new WeakMap<Snapshot, Checkpoint>()
const assertionsByTest = new Map<string, object[]>()
const httpByTest = new Map<string, object[]>()

test.use({ timezoneId: 'America/Los_Angeles' })
test.afterEach(async ({}, info) => {
  const assertions = assertionsByTest.get(info.testId) ?? []
  await writeFile(info.outputPath('sql-assertions.json'), JSON.stringify(assertions, null, 2))
  assertionsByTest.delete(info.testId)
  await writeFile(info.outputPath('http-observations.json'), JSON.stringify(httpByTest.get(info.testId) ?? [], null, 2))
  httpByTest.delete(info.testId)
})

const nav = (page: Page, name: string) =>
  page.getByRole('navigation', { name: 'Sales A' }).getByRole('button', { name, exact: true })

function auth(slot: Slot) {
  const token = process.env['APP02C1_BROWSER_TOKEN_' + slot]
  if (!token) throw new Error('Missing synthetic browser credential')
  return { Authorization: 'Bearer ' + token }
}

async function sql(slot: Slot, caseId: string, phase: string, ids: { orderId?: string; customerId?: string } = {}) {
  const base = join(checkpoints, `${caseId}-${slot}-${phase}`)
  const temp = base + '.request.tmp'
  const checkpoint = { slot, caseId, phase, ...ids }
  await writeFile(temp, JSON.stringify(checkpoint), { flag: 'wx' })
  await rename(temp, base + '.request.json')
  let answer: string | undefined
  await expect.poll(async () => {
    try { answer = await readFile(base + '.response.json', 'utf8') }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error }
    return answer !== undefined
  }, { timeout: 15_000, message: `Wait for SQL readback ${caseId}/${slot}/${phase}` }).toBe(true)
  const snapshot = JSON.parse(answer!) as Snapshot
  checkpointBySnapshot.set(snapshot, checkpoint)
  return snapshot
}

function match(snapshot: Snapshot, values: Partial<Snapshot>) {
  const checkpoint = checkpointBySnapshot.get(snapshot)
  const actual = Object.fromEntries(Object.keys(values).map(name => [name, snapshot[name as keyof Snapshot]]))
  const diff = Object.entries(values).filter(([name, expected]) => actual[name] !== expected)
    .map(([name, expected]) => ({ field: name, expected, actual: actual[name] }))
  const { testId } = test.info()
  const assertions = assertionsByTest.get(testId) ?? []
  assertions.push({ ...checkpoint, expected: values, actual, diff })
  assertionsByTest.set(testId, assertions)
  expect(snapshot, JSON.stringify(checkpoint)).toMatchObject(values)
}

async function withoutCredentials<T>(action: () => Promise<T>, operation: string): Promise<T> {
  try { return await action() }
  catch { throw new Error(`${operation}: local transport failed`) }
}

async function get<T>(page: Page, slot: Slot, path: string) {
  const response = await withoutCredentials(() => page.request.get(synthetic + path, { headers: auth(slot) }), `GET ${path}`)
  expect(response.status(), path).toBe(200)
  return await response.json() as T
}

function recordHttp(observation: object) {
  const { testId } = test.info()
  const results = httpByTest.get(testId) ?? []
  results.push(observation)
  httpByTest.set(testId, results)
}

async function post(page: Page, slot: Slot, path: string, fields: Record<string, unknown>,
    status = 200, reason?: string): Promise<APIResponse> {
  const data = { commandId: randomUUID(), ...fields }
  const response = await withoutCredentials(() => page.request.post(synthetic + path, { headers: auth(slot), data }), `POST ${path}`)
  let actualReason: string | undefined
  if (reason && response.headers()['content-type']?.includes('json')) {
    const body = await response.json()
    actualReason = body.reasonCode ?? body.code
  }
  recordHttp({ slot, path, commandId: data.commandId, expectedStatus: status, status: response.status(),
    expectedReason: reason, reason: actualReason })
  expect(response.status(), path).toBe(status)
  if (reason) expect(actualReason, path).toBe(reason)
  return response
}

async function action(page: Page, path: string, doAction: () => Promise<void>, status = 200): Promise<Response> {
  const pending = page.waitForResponse(response =>
    response.url().endsWith(synthetic + path) && response.request().method() === 'POST')
  await doAction()
  const response = await pending
  recordHttp({ path, commandId: response.request().postDataJSON().commandId, expectedStatus: status, status: response.status() })
  expect(response.status(), path).toBe(status)
  return response
}

async function signIn(page: Page, slot: Slot) {
  await page.goto('/sales')
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value },
    auth(slot).Authorization.slice(7))
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await expect(page.getByText('DEV tổng hợp · tenant-' + slot.toLowerCase())).toBeVisible()
}

async function createDebtor(page: Page, slot: Slot): Promise<Customer> {
  await page.getByRole('button', { name: 'Thêm khách hàng', exact: true }).click()
  await page.getByLabel('Mã khách hàng').fill('C6-' + slot)
  await page.getByLabel('Tên khách hàng').fill('Khách giả lập ' + slot)
  const saved = await action(page, '1/customers/save', () => page.getByRole('button', { name: 'Lưu', exact: true }).click())
  const customerId = (await saved.json()).id as string
  await expect(page.getByText('Khách hàng chưa có điểm bán.')).toBeVisible()
  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await page.getByLabel('Quốc gia').selectOption(address.country)
  await page.getByLabel('Vùng DEV').selectOption(address.region)
  await page.getByLabel('Khu vực').fill(address.locality)
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill(address.line)
  await page.getByLabel('Người nhận').fill(address.recipient)
  const selected = await action(page, '1/ship-to-selections', () => page.getByRole('button', { name: 'Lưu lựa chọn' }).click())
  await expect(page.getByText('01 Duong DEV, Khu DEV, HCM, VN')).toBeVisible()
  return { customerId, selectionId: (await selected.json()).id as string }
}

async function apiDebtor(page: Page, slot: Slot, code: string): Promise<Customer> {
  const created = await post(page, slot, '1/customers/save', { code, name: code })
  const { id: customerId } = await created.json()
  const chosen = await post(page, slot, '1/ship-to-selections', { customerId, address })
  return { customerId, selectionId: (await chosen.json()).id }
}

async function freshSelection(page: Page, slot: Slot, customerId: string): Promise<Customer> {
  const selected = await post(page, slot, '1/ship-to-selections', { customerId, address })
  return { customerId, selectionId: (await selected.json()).id }
}

function draftFields(customer: Customer, catalog: Catalog, quantity: number) {
  return { ...customer, tripId: catalog.trips[0].id, lines: [{ skuId: catalog.skus[0].id, quantity }] }
}

async function draft(page: Page, slot: Slot, fields: ReturnType<typeof draftFields>) {
  const response = await post(page, slot, '2/drafts', fields)
  const { sourceId } = await response.json()
  expect(sourceId).toBeTruthy()
  return sourceId as string
}

async function order(page: Page, slot: Slot, id: string) {
  const found = (await get<Order[]>(page, slot, '2/orders')).find(row => row.id === id)
  expect(found, 'expected persisted order').toBeDefined()
  return found!
}

function reserveFields(row: Order, catalog: Catalog, quantity: number) {
  return { orderId: row.id, lineId: row.lines[0].lineId, confirmSourceId: row.confirmSourceId,
    locationId: catalog.warehouses[0].id, quantity }
}

async function loadRights(page: Page, slot: Slot, catalog: Catalog, id: string) {
  return (await get<Right[]>(page, slot, `3/trips/${catalog.trips[0].id}/rights`)).filter(row => row.orderId === id)
}

async function lineWithQuantity(page: Page, slot: Slot, catalog: Catalog, orderId: string, loaded: number) {
  const tripId = catalog.trips[0].id
  const lines = await get<DeliveryLine[]>(page, slot, `4/trips/${tripId}/lines`)
  const index = lines.findIndex(row => row.orderId === orderId && Number(row.loaded) === loaded)
  expect(index, 'expected persisted manifest line').toBeGreaterThanOrEqual(0)
  return { tripId, line: lines[index], index }
}

function deliveryFields(tripId: string, line: DeliveryLine, quantity: number) {
  return { tripId, manifestLineId: line.id, shipToVersion: line.shipToVersion, quantity }
}

for (const slot of ['A', 'B'] as const) {
  test(`${slot} DEV-01 DEV-03 DEV-05 DEV-06${slot === 'A' ? ' DEV-11' : ''} workspace and SQL checkpoints`, async ({ page, context }, info) => {
    const errors: string[] = []
    const evidence: Record<string, unknown> = { slot }
    page.on('pageerror', error => { errors.push(error.message) })
    await signIn(page, slot)
    const customer = await createDebtor(page, slot)
    const catalog = await get<Catalog>(page, slot, '2/catalog')
    const ids = { customerId: customer.customerId }
    await sql(slot, 'DEV-01', 'register-policy', ids)

    await test.step('DEV-01 rejects invalid address and mismatched selection', async () => {
      const before = await sql(slot, 'DEV-01', 'before-negative', ids)
      await post(page, slot, '1/ship-to-selections', { customerId: customer.customerId, address: { ...address, line: '' } }, 400)
      const other = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE'))!
      expect(other).toBeDefined()
      await post(page, slot, '2/drafts', draftFields({ ...customer, customerId: other.id }, catalog, 40),
        422, 'C2_SHIP_TO_INVALID')
      match(await sql(slot, 'DEV-01', 'after-negative', ids), { fingerprint: before.fingerprint })
    })

    await nav(page, 'Đơn A và giữ hàng').click()
    const draftForm = page.getByRole('form', { name: 'Tạo đơn A nháp' })
    await draftForm.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer.customerId)
    await draftForm.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption(customer.selectionId)
    await draftForm.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption({ label: 'C2-TRIP' })
    await draftForm.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption(catalog.skus[0].id)
    await draftForm.getByLabel('Số lượng dòng 1').fill('40')
    const draftReply = await action(page, '2/drafts', () => draftForm.getByRole('button', { name: 'Lưu đơn nháp' }).click())
    const orderId = (await draftReply.json()).sourceId as string
    const scope = { ...ids, orderId }
    const card = page.getByRole('article').filter({ hasText: 'Khách giả lập ' + slot })
    await action(page, '2/confirm', () => card.getByRole('button', { name: 'Confirm đơn' }).click())
    await expect(card.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
    await expect(card.getByText('Chính sách v1 · Giá 15 · Hạn 7 ngày')).toBeVisible()
    const confirmed = await order(page, slot, orderId)
    evidence.orderId = orderId
    evidence.customerId = customer.customerId
    evidence.selectionId = customer.selectionId
    evidence.confirmSourceId = confirmed.confirmSourceId
    evidence.policyHash = confirmed.lines[0].policySourceHash
    expect(confirmed.lines[0]).toMatchObject({ policyVersion: 1, termsDays: 7 })
    expect(confirmed.lines[0].policySourceHash).toMatch(/^[0-9A-F]{64}$/i)

    await test.step('DEV-03 confirms before Reserve and denies policies outside the fixture', async () => {
      const zero = { main: 100, truck: 0, allocated: 0, manifests: 0, deliveries: 0, ar: 0, cogs: 0 }
      const snapshot = await sql(slot, 'DEV-03', 'confirmed', scope)
      match(snapshot, zero)
      expect(snapshot.orderTrace?.confirmation).toMatchObject({
        OrderId: orderId, CustomerId: customer.customerId, SelectionId: customer.selectionId,
        ConfirmSourceId: confirmed.confirmSourceId, ShipToSnapshotId: confirmed.shipToSnapshotId,
        OutletCount: 0, ShipToHash: expect.stringMatching(/^[0-9A-F]{64}$/i),
      })
      evidence.shipToHash = snapshot.orderTrace!.confirmation!.ShipToHash
      const frozen = { Version: 1, TermsDays: 7, PolicyHash: confirmed.lines[0].policySourceHash, Reserved: 0 }
      expect(snapshot.orderTrace?.policy).toMatchObject(frozen)
      expect(confirmed.lines[0].reserved).toBe(0)
      expect(await loadRights(page, slot, catalog, orderId)).toEqual([])
      await post(page, slot, '3/load', { tripId: catalog.trips[0].id, lines: [
        { rightId: randomUUID(), expectedWarehouseHeld: 15, quantity: 15 },
      ] }, 422, 'C3_RESERVATION_NOT_ELIGIBLE')
      match(await sql(slot, 'DEV-03', 'blocked-load', scope), zero)

      for (const policy of ['missing', 'expired', 'wrong-scope'] as const) {
        const offScope = await apiDebtor(page, slot, `C6-${slot}-${policy.toUpperCase()}`)
        if (policy === 'expired') {
          await sql(slot, 'DEV-03', 'register-expired-policy', { customerId: offScope.customerId })
        }
        const rejectedDraft = await draft(page, slot, draftFields(offScope, catalog, 1))
        await post(page, slot, '2/confirm', { orderId: rejectedDraft }, 422, 'C2_POLICY_REQUIRED')
        expect((await order(page, slot, rejectedDraft)).confirmSourceId).toBeNull()
        match(await sql(slot, 'DEV-03', 'policy-' + policy, { orderId: rejectedDraft, customerId: offScope.customerId }), zero)
      }

      const reserve = card.getByRole('button', { name: 'Reserve hàng' })
      await card.getByRole('combobox', { name: 'Kho', exact: true }).selectOption({ label: 'C2-MAIN' })
      await card.getByLabel('Số lượng giữ').fill('15')
      await action(page, '2/reserve', () => reserve.click())
      await expect(card.getByText('Đã giữ một phần')).toBeVisible()
      const partial = await sql(slot, 'DEV-03', 'reserved-15', scope)
      match(partial, { ...zero, allocated: 15 })
      expect(partial.orderTrace?.policy).toMatchObject({ ...frozen, Reserved: 15 })
      await card.getByLabel('Số lượng giữ').fill('25')
      await action(page, '2/reserve', () => reserve.click())
      await expect(card.getByText('Đã giữ đủ', { exact: true })).toBeVisible()
      const full = await sql(slot, 'DEV-03', 'reserved-40', scope)
      match(full, { ...zero, allocated: 40 })
      expect(full.orderTrace?.policy).toMatchObject({ ...frozen, Reserved: 40 })
    })

    await test.step('DEV-05 loads 15 and 25 and replays without another manifest', async () => {
      await nav(page, 'Nạp hàng').click()
      const load = page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true })
      const first = page.locator('fieldset').filter({ hasText: /Đã giữ 15 EA · Đã nạp 0 EA/ })
      await first.getByRole('spinbutton').fill('15')
      const loaded = await action(page, '3/load', () => load.click())
      await expect(page.getByText(/Đã nạp 15 EA · Còn giữ tại kho 0 EA/)).toBeVisible()
      const partial = await sql(slot, 'DEV-05', 'loaded-15', scope)
      match(partial, { main: 85, mainValue: 850, truck: 15, truckValue: 150, allocated: 25,
        manifests: 1, deliveries: 0, ar: 0, cogs: 0 })
      const original = loaded.request().postDataJSON() as Command
      const replay = await post(page, slot, '3/load', original)
      expect(await replay.json()).toMatchObject({ replayed: true, sourceId: (await loaded.json()).sourceId })
      match(await sql(slot, 'DEV-05', 'replay', scope), { fingerprint: partial.fingerprint })
      await post(page, slot, '3/load', { ...original, commandId: randomUUID() }, 422)
      match(await sql(slot, 'DEV-05', 'stale-load', scope), { fingerprint: partial.fingerprint })

      const second = page.locator('fieldset').filter({ hasText: /Đã giữ 25 EA · Đã nạp 0 EA/ })
      await second.getByRole('spinbutton').fill('25')
      await action(page, '3/load', () => load.click())
      await expect(page.getByText(/Đã nạp 25 EA · Còn giữ tại kho 0 EA/)).toBeVisible()
      match(await sql(slot, 'DEV-05', 'loaded-40', scope), { main: 60, mainValue: 600, truck: 40, truckValue: 400,
        allocated: 0, manifests: 2, deliveries: 0, ar: 0, cogs: 0 })
    })

    const full = await lineWithQuantity(page, slot, catalog, orderId, 25)
    const remainder = await lineWithQuantity(page, slot, catalog, orderId, 15)
    let deliveryPosts = 0
    page.on('request', request => {
      if (request.method() === 'POST' && request.url().endsWith(synthetic + '4/deliver')) deliveryPosts++
    })
    await nav(page, 'Giao hàng').click()
    const form = page.getByRole('region', { name: 'Dòng đã nạp' }).locator('form').nth(full.index)
    const confirmDelivery = form.getByRole('button', { name: 'Xác nhận đã giao' })
    await form.getByRole('spinbutton').fill('25')

    let command: Command | undefined
    if (slot === 'A') {
      await test.step('DEV-11 offline submit, lost response and pane recovery', async () => {
        const before = await sql(slot, 'DEV-11', 'before-offline', scope)
        await context.setOffline(true)
        await expect(confirmDelivery).toBeDisabled()
        await form.evaluate((element: HTMLFormElement) => element.requestSubmit())
        expect(deliveryPosts).toBe(0)
        await context.setOffline(false)
        match(await sql(slot, 'DEV-11', 'after-offline', scope), { fingerprint: before.fingerprint })
        await page.evaluate(() => {
          const setItem = Storage.prototype.setItem
          Storage.prototype.setItem = function (key, value) {
            if (key.startsWith('app02c4.delivery.')) throw new DOMException('Storage unavailable', 'SecurityError')
            return setItem.call(this, key, value)
          }
        })
        let lostCommand: Command | undefined
        await page.route('**/app02c4/deliver', async route => {
          lostCommand = route.request().postDataJSON() as Command
          const applied = await withoutCredentials(() => route.fetch(), "POST delivery ACK probe")
          expect(applied.status()).toBe(200)
          await route.abort('failed')
        }, { times: 1 })
        await confirmDelivery.click()
        await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
        await nav(page, 'Báo cáo AR').click()
        await expect(page.getByText(/Nguồn đã đối soát 1\/1/)).toBeVisible()
        await nav(page, 'Giao hàng').click()
        const outcomeLookup = page.waitForResponse(response =>
          response.request().method() === 'GET' && response.url().endsWith('/commands/' + lostCommand!.commandId))
        await page.getByRole('button', { name: 'Tra cứu kết quả giao' }).click()
        expect((await outcomeLookup).status()).toBe(200)
        await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
        expect(deliveryPosts).toBe(1)
        command = lostCommand!
      })
    } else {
      const delivery = await action(page, '4/deliver', () => confirmDelivery.click())
      command = delivery.request().postDataJSON() as Command
    }
    evidence.deliveryCommandId = command!.commandId
    const ar = page.getByRole('region', { name: 'Công nợ theo lần giao' })
    await expect(ar.getByText('Còn nợ 375 · Giá vốn 250')).toBeVisible()
    await expect(ar.getByRole('heading', { name: 'Khách giả lập ' + slot + ' · 375' })).toBeVisible()
    const fixtureUtc = process.env.APP02C6_CANONICAL_UTC
    if (!fixtureUtc) throw new Error('Missing canonical DEV fixture clock')
    const browserZone = await page.evaluate(() => Intl.DateTimeFormat().resolvedOptions().timeZone)
    expect(browserZone).toBe('America/Los_Angeles')
    const sellerDate = new Date(Date.parse(fixtureUtc) + 7 * 3_600_000)
    const dueDate = new Date(sellerDate.getTime() + 7 * 86_400_000)
    const formatDate = (date: Date) => date.toISOString().slice(0, 10)
    await expect(ar.getByText(`Ngày giao ${formatDate(sellerDate)} · Hạn AR ${formatDate(dueDate)}`)).toBeVisible()
    evidence.browserZone = browserZone
    evidence.sellerDate = formatDate(sellerDate)

    await test.step('DEV-06 standard branded custody and delivery rejects invalid attempts', async () => {
      const posted = { main: 60, mainValue: 600, truck: 15, truckValue: 150, deliveries: 1, ar: 375, cogs: 250, held: 15 }
      const debtor = await sql(slot, 'DEV-01', 'delivered', scope)
      match(debtor, posted)
      expect(debtor.orderTrace?.confirmation).toMatchObject({
        CustomerId: customer.customerId, SelectionId: customer.selectionId,
        ShipToSnapshotId: confirmed.shipToSnapshotId, ShipToHash: evidence.shipToHash, OutletCount: 0,
      })
      expect(debtor.orderTrace?.deliveries).toHaveLength(1)
      const delivery = debtor.orderTrace!.deliveries[0]
      expect(delivery).toMatchObject({ DebtorId: customer.customerId, BaseQuantity: 25, CostAmount: 250,
        PayableAmount: 375, OriginalAmount: 375, ArId: expect.any(String), BusinessSourceId: expect.any(String) })
      expect(delivery.BusinessDate.slice(0, 10)).toBe(formatDate(sellerDate))
      expect(delivery.DueBusinessDate.slice(0, 10)).toBe(formatDate(dueDate))
      evidence.deliverySourceId = delivery.BusinessSourceId
      evidence.deliveryId = delivery.Id
      match(await sql(slot, 'DEV-06', 'delivered', scope), posted)
      const before = await sql(slot, 'DEV-06', 'before-negative', scope)
      await post(page, slot, '4/deliver', deliveryFields(remainder.tripId, remainder.line, 16), 422, 'C4_QUANTITY_NOT_AVAILABLE')
      await post(page, slot, '4/deliver', { ...deliveryFields(remainder.tripId, remainder.line, 1), tripId: randomUUID() }, 403)
      await post(page, slot, '4/deliver', { ...command!, quantity: 24 }, 409)
      match(await sql(slot, 'DEV-06', 'after-negative', scope), { fingerprint: before.fingerprint })
    })

    if (slot === 'A') {
      await test.step('DEV-11 posting fault remains pending with no extra AR or request', async () => {
        const pending = page.getByRole('region', { name: 'Dòng đã nạp' }).locator('form').nth(remainder.index)
        await pending.getByRole('spinbutton').fill('13')
        await action(page, '4/deliver', () => pending.getByRole('button', { name: 'Xác nhận đã giao' }).click(), 500)
        await expect(page.getByText(/Chưa rõ kết quả ghi sổ/)).toBeVisible()
        await page.getByRole('button', { name: 'Tra cứu kết quả giao' }).click()
        await expect(page.getByText(/Chưa có kết quả ghi sổ/)).toBeVisible()
        await expect(page.getByRole('region', { name: 'Giao hàng cần đối soát' }).getByText(/13 · Chờ ghi sổ/)).toBeVisible()
        match(await sql(slot, 'DEV-11', 'pending', scope), { deliveries: 1, ar: 375, cogs: 250, truck: 15 })
      })
    }

    await test.step('DEV-03 refuses insufficient physical stock', async () => {
      const excessive = await draft(page, slot, draftFields(await freshSelection(page, slot, customer.customerId), catalog, 61))
      await post(page, slot, '2/confirm', { orderId: excessive })
      const before = await sql(slot, 'DEV-03', 'before-insufficient', { orderId: excessive })
      await post(page, slot, '2/reserve', reserveFields(await order(page, slot, excessive), catalog, 61), 422, 'STOCK_INSUFFICIENT')
      match(await sql(slot, 'DEV-03', 'stock-insufficient', { orderId: excessive }), { fingerprint: before.fingerprint })
    })

    await test.step('DEV-05 multi-line fault rolls back and DEV-06 precision reconciles', async () => {
      const precision = await draft(page, slot, draftFields(await freshSelection(page, slot, customer.customerId), catalog, 40.001))
      const precisionScope = { ...ids, orderId: precision }
      await post(page, slot, '2/confirm', { orderId: precision })
      const row = await order(page, slot, precision)
      await post(page, slot, '2/reserve', reserveFields(row, catalog, 15))
      await post(page, slot, '2/reserve', reserveFields(row, catalog, 25.001))
      const rights = await loadRights(page, slot, catalog, precision)
      expect(rights).toHaveLength(2)
      const lines = rights.map(item => ({ rightId: item.id, expectedWarehouseHeld: item.warehouseHeld,
        quantity: Number(item.warehouseHeld) === 15 ? 2 : 13 }))
      const before = await sql(slot, 'DEV-05', 'before-fault', precisionScope)
      await post(page, slot, '3/load', { tripId: full.tripId, lines }, 500)
      match(await sql(slot, 'DEV-05', 'after-fault', precisionScope), { fingerprint: before.fingerprint })
      await post(page, slot, '3/load', { tripId: full.tripId,
        lines: rights.map(item => ({ rightId: item.id, expectedWarehouseHeld: item.warehouseHeld, quantity: item.warehouseHeld })) })
      const preciseLine = await lineWithQuantity(page, slot, catalog, precision, 25.001)
      await post(page, slot, '4/deliver', deliveryFields(full.tripId, preciseLine.line, 25))
      expect((await order(page, slot, precision)).lines[0].heldOnTrip).toBe(15.001)
      const precise = await sql(slot, 'DEV-06', 'precision', precisionScope)
      match(precise, { main: 19.999, mainValue: 199.99, truck: 30.001, truckValue: 300.01,
        deliveries: 2, ar: 750, cogs: 500 })
      expect(precise.orderTrace?.policy?.Reserved).toBe(15.001)
      expect(Math.round((precise.truckValue - 150) * 10000)).toBe(1500100)
      evidence.precisionOrderId = precision
    })
    evidence.tripId = full.tripId
    evidence.manifestLineId = full.line.id
    await writeFile(info.outputPath('browser-evidence.json'), JSON.stringify(evidence, null, 2))
    expect(errors).toEqual([])
  })
}

test('DEV-01 DEV-06 deny actual peer selection and delivery in both directions', async ({ page }) => {
  const scopes = new Map<Slot, { customer: Customer; catalog: Catalog; line: DeliveryLine }>()
  for (const slot of ['A', 'B'] as const) {
    const catalog = await get<Catalog>(page, slot, '2/catalog')
    const customer = catalog.customers.find(row => row.label.startsWith(`C6-${slot} ·`))!
    expect(customer).toBeDefined()
    const details = await get<{ selections: { id: string }[] }>(page, slot, '1/customers/' + customer.id)
    const lines = await get<DeliveryLine[]>(page, slot, `4/trips/${catalog.trips[0].id}/lines`)
    scopes.set(slot, { customer: { customerId: customer.id, selectionId: details.selections[0].id }, catalog,
      line: lines.find(row => Number(row.available) > 0)! })
  }
  for (const slot of ['A', 'B'] as const) {
    const local = scopes.get(slot)!
    const peer = scopes.get(slot === 'A' ? 'B' : 'A')!
    const before = await sql(slot, 'DEV-01', 'before-peer')
    await post(page, slot, '2/drafts', draftFields({ ...local.customer, selectionId: peer.customer.selectionId },
      local.catalog, 1), 422, 'C2_SHIP_TO_INVALID')
    match(await sql(slot, 'DEV-01', 'after-peer'), { fingerprint: before.fingerprint })
    const beforeDelivery = await sql(slot, 'DEV-06', 'before-peer')
    await post(page, slot, '4/deliver', deliveryFields(local.catalog.trips[0].id, peer.line, 1), 422, 'C4_LOADED_LINE_NOT_FOUND')
    match(await sql(slot, 'DEV-06', 'after-peer'), { fingerprint: beforeDelivery.fingerprint })
  }
})
