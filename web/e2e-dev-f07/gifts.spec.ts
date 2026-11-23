import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const customerApi = '/api/synthetic/app02c1'
const orderApi = '/api/synthetic/app02c2'
const loadApi = '/api/synthetic/app02c3'
const deliveryApi = '/api/synthetic/app02c4'
const returnApi = '/api/synthetic/dev-f05'
const giftApi = '/api/synthetic/dev-f07'
const headers = (token: string) => ({ Authorization: 'Bearer ' + token })
type Catalog = {
  customers: { id: string, label: string }[]
  skus: { id: string, label: string }[]
  trips: { id: string }[]
  warehouses: { id: string, label: string }[]
}
type ReturnSource = { deliveryId: string, lotId: string, lotCode: string, sku: string, available: number }
type CommandOutcome = { status: string, sourceId: string, reasonCode?: string, replayed?: boolean }

async function login(page: Page, path: string, token: string) {
  await page.goto(path)
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

async function get<T>(request: APIRequestContext, path: string, token: string): Promise<T> {
  const response = await request.get(path, { headers: headers(token) })
  expect(response.status(), path).toBe(200)
  return await response.json() as T
}

async function post(request: APIRequestContext, path: string, token: string, data: object,
  status = 200, rejection?: string): Promise<CommandOutcome> {
  const response = await request.post(path, { headers: headers(token),
    data: { commandId: crypto.randomUUID(), ...data } })
  expect(response.status(), path + (rejection ? ' ' + rejection : '')).toBe(status)
  if (status === 403) return { status: 'FORBIDDEN', sourceId: '' }
  const body = await response.json() as CommandOutcome
  if (rejection) expect(body.reasonCode).toBe(rejection)
  return body
}

async function checkpoint(phase: string) {
  const dir = process.env.DEV_F07_CHECKPOINTS
  const run = process.env.APP02C1_RUN_ID
  if (!dir || !run) throw new Error('Missing DEV-F07 checkpoint fixture')
  const staging = join(dir, phase + '.partial')
  await writeFile(staging, run)
  await rename(staging, join(dir, phase + '.request'))
  await expect.poll(async () => {
    try { return await readFile(join(dir, phase + '.ready'), 'utf8') } catch { return '' }
  }, { timeout: 15_000 }).toBe(run)
}

async function loadSource(request: APIRequestContext, source: string, sales: string, customerCode: string, quantity: number) {
  const catalog = await get<Catalog>(request, orderApi + '/catalog', source)
  const customer = catalog.customers.find(item => item.label.startsWith(customerCode + ' ·'))
  const sku = catalog.skus.find(item => item.label.startsWith('G-BUY ·'))
  const warehouse = catalog.warehouses.find(item => item.label.startsWith('C2-MAIN'))
  if (!customer || !sku || !warehouse) throw new Error('Missing protected G-BUY source fixtures')
  const tripId = catalog.trips[0].id
  const selection = await request.post(customerApi + '/ship-to-selections', {
    headers: headers(source), data: { commandId: crypto.randomUUID(), customerId: customer.id,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'Gift qualification DEV', recipient: 'Nhận DEV' },
    },
  })
  expect(selection.status()).toBe(200)
  const draft = await post(request, orderApi + '/drafts', source, {
    customerId: customer.id, tripId, selectionId: (await selection.json()).id,
    lines: [{ skuId: sku.id, quantity, uomCode: 'EA' }],
  })
  const orderId = draft.sourceId
  const confirmation = await post(request, orderApi + '/confirm', source, { orderId })
  const orders = await get<{ id: string, lines: { lineId: string }[] }[]>(request, orderApi + '/orders', source)
  const order = orders.find(item => item.id === orderId)
  if (!order) throw new Error('Missing protected qualifying purchase order')
  await post(request, orderApi + '/reserve', source, { orderId, lineId: order.lines[0].lineId,
    locationId: warehouse.id, quantity, confirmSourceId: confirmation.sourceId })
  const rights = await get<{ id: string, orderId: string, warehouseHeld: number | string }[]>(
    request, loadApi + '/trips/' + tripId + '/rights', source)
  const right = rights.find(item => item.orderId === orderId)
  if (!right) throw new Error('Missing warehouse right for ' + customerCode)
  await post(request, loadApi + '/load', source, { tripId,
    lines: [{ rightId: right.id, expectedWarehouseHeld: Number(right.warehouseHeld), quantity }] })
  const loaded = await get<{ id: string, orderId: string, shipToVersion: number, available: number | string }[]>(
    request, deliveryApi + '/trips/' + tripId + '/lines', sales)
  const line = loaded.find(item => item.orderId === orderId)
  if (!line) throw new Error('Missing loaded line for ' + customerCode)
  expect(Number(line.available)).toBe(quantity)
  return { tripId, manifestLineId: line.id, shipToVersion: line.shipToVersion, quantity }
}

type Position = { customerId: string, customer: string, netPurchases: number, entitlement: number,
  giftHeld: number, waivedHeld: number, toDeliver: number }
type Issue = { id: string, customerId: string, quantity: number }
type Waiver = { id: string, issueId: string, quantity: number, status: string }

async function deliverPurchase(request: APIRequestContext, source: string, sales: string, customer: string, quantity: number) {
  const existing = await get<ReturnSource[]>(request, returnApi + '/deliveries', sales)
  const shipment = await loadSource(request, source, sales, customer, quantity)
  await post(request, deliveryApi + '/deliver', sales, shipment)
  const available = await get<ReturnSource[]>(request, returnApi + '/deliveries', sales)
  const delivery = available.find(row => !existing.some(old => old.deliveryId === row.deliveryId))
  expect(delivery).toMatchObject({ sku: 'G-BUY', lotCode: 'BUY-ORIGIN', available: quantity })
  if (!delivery) throw new Error('Missing new qualifying purchase delivery')
  return delivery
}

async function acceptPurchaseReturn(page: Page, request: APIRequestContext,
  roles: { sales: string, receiver: string, finance: string, qa: string }, delivery: ReturnSource, branch: string) {
  const created = await post(request, returnApi + '/commands', roles.sales, { action: 'REQUEST',
    deliveryId: delivery.deliveryId, lotId: delivery.lotId, quantity: 11, reason: 'DEV-F07 qualifying purchase return' })
  const caseId = created.sourceId
  await post(request, returnApi + '/commands', roles.receiver, { action: 'RECEIVE', caseId, quantity: 11,
    reason: 'Count and hold eleven EA', evidence: 'F07-purchase-receive-' + branch })
  await post(request, returnApi + '/commands', roles.finance, { action: 'APPROVE', caseId,
    reason: 'Approved qualifying purchase return', evidence: 'F07-purchase-approve-' + branch })
  await login(page, '/sales/returns', roles.qa)
  await page.getByLabel('Hồ sơ trả hàng', { exact: true }).selectOption(caseId)
  await page.getByLabel('Lý do', { exact: true }).fill('Release eleven eligible EA')
  await page.getByLabel('Chứng từ DEV').fill('F07-purchase-release-' + branch)
  await page.getByRole('button', { name: 'Giải phóng hàng', exact: true }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
}

async function giftDetails(page: Page, evidence: string, reason: string) {
  await page.getByLabel('Lý do', { exact: true }).fill(reason)
  await page.getByLabel('Chứng từ DEV').fill(evidence)
}

const outcome = 'Đã ghi nghiệp vụ quà.'
const giftWorkspace = '/sales/gifts'
const wallet = (page: Page, customer: string) => page.getByRole('region', { name: 'Quyền quà ' + customer, exact: true })

async function checkPositions(request: APIRequestContext, token: string, customers: readonly string[],
  expected: readonly (readonly number[])[]) {
  const records = await get<Position[]>(request, giftApi + '/positions', token)
  expect(records).toHaveLength(customers.length)
  for (const [index, id] of customers.entries()) {
    const row = records.find(position => position.customerId === id)
    expect(row, 'gift position for ' + id).toBeDefined()
    if (!row) throw new Error('Missing customer position')
    expect([row.netPurchases, row.entitlement, row.giftHeld, row.waivedHeld, row.toDeliver].map(Number))
      .toEqual(expected[index])
  }
}

test('F07-01–05: recover issuance, distinguish actual returns from waivers, and reconcile gift rights', async ({ page, request }, info) => {
  const source = process.env.APP02C1_BROWSER_TOKEN_A
  const peer = process.env.APP02C1_BROWSER_TOKEN_B
  const sales = process.env.DEV_F05_SALES
  const receiver = process.env.DEV_F05_RECEIVER
  const finance = process.env.DEV_F05_FINANCE
  const qa = process.env.DEV_F05_QA
  const selfCheck = process.env.DEV_F07_SELF_CHECK
  const peerFinance = process.env.DEV_F07_PEER_FINANCE
  if (!source || !peer || !sales || !receiver || !finance || !qa || !selfCheck || !peerFinance)
    throw new Error('Missing protected DEV-F07 browser role fixtures')
  const catalog = await get<Catalog>(request, orderApi + '/catalog', source)
  const returnedCustomer = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE ·'))?.id
  const waivedCustomer = catalog.customers.find(row => row.label.startsWith('F07-WAIVER ·'))?.id
  if (!returnedCustomer || !waivedCustomer) throw new Error('Missing AC-07 customer fixtures')
  const returnedPurchase = await deliverPurchase(request, source, sales, 'C2-FIXTURE', 20)
  const waivedPurchase = await deliverPurchase(request, source, sales, 'F07-WAIVER', 20)
  const customers = [returnedCustomer, waivedCustomer] as const
  await checkPositions(request, sales, customers, [[20, 2, 0, 0, 2], [20, 2, 0, 0, 2]])

  await login(page, giftWorkspace, sales)
  await expect(page.getByRole('heading', { name: 'Quà khuyến mại' })).toBeVisible()
  await page.getByLabel('Thao tác quà').selectOption('ISSUE')
  await page.getByLabel('Khách hàng', { exact: true }).selectOption(returnedCustomer)
  await page.getByLabel('Số lượng quà', { exact: true }).fill('2')
  await giftDetails(page, 'F07-return-branch-original-issue', 'Issue two gifts earned by delivered purchase')
  const interrupted: { body?: object, status?: number, outcome?: CommandOutcome } = {}
  await page.route('**' + giftApi + '/commands', async route => {
    interrupted.body = route.request().postDataJSON() as object
    const applied = await route.fetch()
    interrupted.status = applied.status()
    interrupted.outcome = await applied.json()
    await route.abort('connectionreset')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Giao quà', exact: true }).click()
  await expect(page.getByText('Chưa rõ kết quả ghi nghiệp vụ quà.')).toBeVisible()
  expect(interrupted.status, JSON.stringify({ body: interrupted.body, outcome: interrupted.outcome })).toBe(200)
  expect(interrupted.outcome?.sourceId).toBeTruthy()
  await page.getByRole('button', { name: 'Tra cứu kết quả', exact: true }).click()
  await expect(page.getByText(outcome)).toBeVisible()
  const replay = await post(request, giftApi + '/commands', sales, interrupted.body ?? {})
  expect(replay).toMatchObject({ sourceId: interrupted.outcome?.sourceId, replayed: true })

  await page.getByLabel('Khách hàng', { exact: true }).selectOption(waivedCustomer)
  await page.getByLabel('Số lượng quà', { exact: true }).fill('2')
  await giftDetails(page, 'F07-waiver-branch-original-issue', 'Issue two gifts earned by delivered purchase')
  const issued = page.waitForResponse(response => response.url().endsWith(giftApi + '/commands') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Giao quà', exact: true }).click()
  expect((await issued).status()).toBe(200)
  await expect(page.getByText(outcome)).toBeVisible()
  const issues = await get<Issue[]>(request, giftApi + '/issues', sales)
  expect(issues).toHaveLength(2)
  const returnedIssue = issues.find(issue => issue.customerId === returnedCustomer)
  const waivedIssue = issues.find(issue => issue.customerId === waivedCustomer)
  if (!returnedIssue || !waivedIssue) throw new Error('Missing distinct starting gift sources')
  expect(returnedIssue.id).toBe(interrupted.outcome?.sourceId)
  await checkPositions(request, sales, customers, [[20, 2, 2, 0, 0], [20, 2, 2, 0, 0]])
  await checkpoint('issued')

  await acceptPurchaseReturn(page, request, { sales, receiver, finance, qa }, returnedPurchase, 'returned')
  await acceptPurchaseReturn(page, request, { sales, receiver, finance, qa }, waivedPurchase, 'waived')
  await checkPositions(request, sales, customers, [[9, 0, 2, 0, 0], [9, 0, 2, 0, 0]])
  const returnPayload = { action: 'RETURN', issueId: returnedIssue.id, quantity: 2,
    reason: 'Count two actual gifts received', evidence: 'F07-unassigned-gift-receipt' }
  await post(request, giftApi + '/commands', source, returnPayload, 403)
  await post(request, giftApi + '/commands', receiver, { ...returnPayload, quantity: 3 },
    422, 'F07_GIFT_QUANTITY_EXCEEDED')
  await login(page, giftWorkspace, receiver)
  await page.getByLabel('Lần giao quà', { exact: true }).selectOption(returnedIssue.id)
  await page.getByLabel('Số lượng quà thực nhận').fill('2')
  await giftDetails(page, 'F07-actual-gift-receipt', 'Count and quarantine two returned gifts')
  await page.getByRole('button', { name: 'Nhận quà trả', exact: true }).click()
  await expect(page.getByText(outcome)).toBeVisible()

  await login(page, giftWorkspace, sales)
  await page.getByLabel('Thao tác quà').selectOption('PROPOSE')
  await page.getByLabel('Lần giao quà', { exact: true }).selectOption(waivedIssue.id)
  await page.getByLabel('Số lượng quà', { exact: true }).fill('2')
  await giftDetails(page, 'F07-waiver-maker-evidence', 'Request waiver for two gifts still with the customer')
  await page.getByRole('button', { name: 'Đề nghị miễn thu hồi quà', exact: true }).click()
  await expect(page.getByText(outcome)).toBeVisible()
  const waivers = await get<Waiver[]>(request, giftApi + '/waivers', finance)
  expect(waivers).toHaveLength(1)
  expect(waivers[0]).toMatchObject({ issueId: waivedIssue.id, quantity: 2, status: 'PROPOSED' })
  const approval = { action: 'APPROVE', waiverId: waivers[0].id,
    reason: 'Authority boundary guard', evidence: 'F07-waiver-approval-guard' }
  await post(request, giftApi + '/commands', sales, approval, 403)
  await post(request, giftApi + '/commands', selfCheck, approval, 422, 'F07_SEPARATION_REQUIRED')
  await post(request, giftApi + '/commands', peerFinance, approval, 422, 'F07_SOURCE_NOT_FOUND')
  await login(page, giftWorkspace, finance)
  await page.getByLabel('Đề nghị miễn thu hồi', { exact: true }).selectOption(waivers[0].id)
  await giftDetails(page, 'F07-waiver-checker-evidence', 'Independent finance verifies supported waiver quantity')
  await page.getByRole('button', { name: 'Duyệt miễn thu hồi quà', exact: true }).click()
  await expect(page.getByText(outcome)).toBeVisible()
  await checkPositions(request, sales, customers, [[9, 0, 0, 0, 0], [9, 0, 2, 2, 0]])
  await checkpoint('returned-waived')

  await deliverPurchase(request, source, sales, 'C2-FIXTURE', 11)
  await deliverPurchase(request, source, sales, 'F07-WAIVER', 11)
  await checkPositions(request, sales, customers, [[20, 2, 0, 0, 2], [20, 2, 2, 2, 0]])
  await login(page, giftWorkspace, sales)
  const positions = await get<Position[]>(request, giftApi + '/positions', sales)
  const returnedName = positions.find(row => row.customerId === returnedCustomer)?.customer ?? ''
  const waivedName = positions.find(row => row.customerId === waivedCustomer)?.customer ?? ''
  await expect(wallet(page, returnedName)).toContainText('Còn có thể giao')
  await expect(wallet(page, waivedName)).toContainText('Miễn thu hồi đang giữ')
  await info.attach('repurchase-entitlement', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
  await checkpoint('repurchased')

  const issue = { action: 'ISSUE', quantity: 2, reason: 'Two earned gifts at the AC-07 follow-up' }
  await post(request, giftApi + '/commands', sales, { ...issue, customerId: waivedCustomer, evidence: 'F07-waived-block' },
    422, 'F07_ENTITLEMENT_EXCEEDED')
  await post(request, giftApi + '/commands', sales, { ...issue, quantity: 3, customerId: returnedCustomer,
    evidence: 'F07-returned-over-entitlement' }, 422, 'F07_ENTITLEMENT_EXCEEDED')
  await post(request, giftApi + '/commands', peer, { ...issue, customerId: returnedCustomer, evidence: 'F07-peer-guard' },
    422, 'F07_SOURCE_NOT_FOUND')
  const competing = await Promise.all(['first', 'second'].map(evidence => request.post(giftApi + '/commands', {
    headers: headers(sales),
    data: { ...issue, commandId: crypto.randomUUID(), customerId: returnedCustomer, evidence: 'F07-race-' + evidence },
  })))
  expect(competing.map(response => response.status()).sort()).toEqual([200, 422])
  const applied = await competing.find(response => response.status() === 200)?.json() as CommandOutcome
  const rejected = await competing.find(response => response.status() === 422)?.json() as CommandOutcome
  expect(rejected.reasonCode).toBe('F07_ENTITLEMENT_EXCEEDED')
  expect(applied.sourceId).toBeTruthy()
  expect(issues.some(row => row.id === applied.sourceId)).toBe(false)
  await checkPositions(request, sales, customers, [[20, 2, 2, 0, 0], [20, 2, 2, 2, 0]])
  await checkpoint('reissued')

  const approvedBeforeReturn = await get<Waiver[]>(request, giftApi + '/waivers', finance)
  expect(approvedBeforeReturn).toHaveLength(1)
  expect(approvedBeforeReturn[0]).toMatchObject({ issueId: waivedIssue.id, quantity: 2, status: 'APPROVED' })
  await login(page, giftWorkspace, receiver)
  await page.getByLabel('Lần giao quà', { exact: true }).selectOption(waivedIssue.id)
  await page.getByLabel('Số lượng quà thực nhận').fill('1')
  await giftDetails(page, 'F07-after-waiver-actual-receipt', 'Count and quarantine one gift from the approved waiver')
  const lateReceipt = page.waitForResponse(response => response.url().endsWith(giftApi + '/commands')
    && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Nhận quà trả', exact: true }).click()
  const committed = await lateReceipt
  expect(committed.status()).toBe(200)
  const committedReceipt = await committed.json() as CommandOutcome
  expect(committedReceipt.sourceId).toBeTruthy()
  await expect(page.getByText(outcome)).toBeVisible()
  const lateReplay = await post(request, giftApi + '/commands', receiver, committed.request().postDataJSON() as object)
  expect(lateReplay).toMatchObject({ sourceId: committedReceipt.sourceId, replayed: true })

  await checkPositions(request, sales, customers, [[20, 2, 2, 0, 0], [20, 2, 1, 1, 1]])
  const issueAfterReturn = (await get<Issue[]>(request, giftApi + '/issues', sales)).find(row => row.id === waivedIssue.id)
  expect(issueAfterReturn).toMatchObject({ quantity: 2, returned: 1, waivedHeld: 1 })
  expect(await get<Waiver[]>(request, giftApi + '/waivers', finance)).toEqual(approvedBeforeReturn)
  const waivedWallet = wallet(page, waivedName)
  const expectedAfterReturn = [
    ['Mua ròng', '20 EA'], ['Quyền nhận', '2 EA'], ['Khách còn giữ', '1 EA'],
    ['Miễn thu hồi đang giữ', '1 EA'], ['Còn có thể giao', '1 EA'],
  ]
  for (const [label, expected] of expectedAfterReturn) {
    await expect(waivedWallet.getByText(label, { exact: true }).locator('xpath=following-sibling::dd[1]')).toHaveText(expected)
  }
  await info.attach('actual-return-after-approved-waiver', {
    body: await page.screenshot({ fullPage: true }), contentType: 'image/png',
  })
  expect(await get<Position[]>(request, giftApi + '/positions', peerFinance)).toEqual([])
})
