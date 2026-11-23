import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const customerApi = '/api/synthetic/app02c1'
const orderApi = '/api/synthetic/app02c2'
const loadApi = '/api/synthetic/app02c3'
const deliveryApi = '/api/synthetic/app02c4'
const returnApi = '/api/synthetic/dev-f05'
const costApi = '/api/synthetic/dev-f06'
const headers = (token: string) => ({ Authorization: 'Bearer ' + token })
type Catalog = {
  customers: { id: string, label: string }[]
  skus: { id: string, label: string }[]
  trips: { id: string }[]
  warehouses: { id: string }[]
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
  const dir = process.env.DEV_F06_CHECKPOINTS
  const run = process.env.APP02C1_RUN_ID
  if (!dir || !run) throw new Error('Missing DEV-F06 checkpoint fixture')
  const staging = join(dir, phase + '.partial')
  await writeFile(staging, run)
  await rename(staging, join(dir, phase + '.request'))
  await expect.poll(async () => {
    try { return await readFile(join(dir, phase + '.ready'), 'utf8') } catch { return '' }
  }, { timeout: 15_000 }).toBe(run)
}

async function loadSource(request: APIRequestContext, source: string, sales: string, code: string, quantity: number) {
  const catalog = await get<Catalog>(request, orderApi + '/catalog', source)
  const customer = catalog.customers.find(item => item.label.startsWith('C2-FIXTURE'))
  const sku = catalog.skus.find(item => item.label.startsWith(code + ' ·'))
  if (!customer || !sku) throw new Error('Missing protected ' + code + ' source')
  const tripId = catalog.trips[0].id
  const selection = await request.post(customerApi + '/ship-to-selections', {
    headers: headers(source), data: { commandId: crypto.randomUUID(), customerId: customer.id,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'Period return DEV', recipient: 'Nhận DEV' },
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
  if (!order) throw new Error('Missing protected November shipment order')
  await post(request, orderApi + '/reserve', source, { orderId, lineId: order.lines[0].lineId,
    locationId: catalog.warehouses[0].id, quantity, confirmSourceId: confirmation.sourceId })
  const rights = await get<{ id: string, orderId: string, warehouseHeld: number | string }[]>(
    request, loadApi + '/trips/' + tripId + '/rights', source)
  const right = rights.find(item => item.orderId === orderId)
  if (!right) throw new Error('Missing warehouse right for ' + code)
  await post(request, loadApi + '/load', source, { tripId,
    lines: [{ rightId: right.id, expectedWarehouseHeld: Number(right.warehouseHeld), quantity }] })
  const loaded = await get<{ id: string, orderId: string, shipToVersion: number, available: number | string }[]>(
    request, deliveryApi + '/trips/' + tripId + '/lines', sales)
  const line = loaded.find(item => item.orderId === orderId)
  if (!line) throw new Error('Missing loaded line for ' + code)
  expect(Number(line.available)).toBe(quantity)
  return { tripId, manifestLineId: line.id, shipToVersion: line.shipToVersion, quantity }
}

async function prepareShipment(request: APIRequestContext, source: string, sales: string) {
  const shipment = await loadSource(request, source, sales, 'R-05', 2)
  await post(request, deliveryApi + '/deliver', sales, shipment)
  const returnable = await get<ReturnSource[]>(request, returnApi + '/deliveries', sales)
  expect(returnable).toHaveLength(1)
  expect(returnable[0]).toMatchObject({ sku: 'R-05', lotCode: 'RET-ORIGIN', available: 2 })
  return { tripId: shipment.tripId, delivery: returnable[0] }
}

async function prepareReturn(request: APIRequestContext, token: { sales: string, receiver: string, finance: string },
  source: ReturnSource, suffix: string) {
  const created = await post(request, returnApi + '/commands', token.sales, { action: 'REQUEST',
    deliveryId: source.deliveryId, lotId: source.lotId, quantity: 1, reason: 'DEV-F06 return ' + suffix })
  const caseId = created.sourceId
  await post(request, returnApi + '/commands', token.receiver, { action: 'RECEIVE', caseId, quantity: 1,
    reason: 'Count and hold one EA', evidence: 'F06-receive-' + suffix })
  await post(request, returnApi + '/commands', token.finance, { action: 'APPROVE', caseId,
    reason: 'Approved physical return paperwork', evidence: 'F06-approve-return-' + suffix })
  return caseId
}

test('F06-01–04: lock November, recover December adjustment and release updated residual', async ({ page, request }, info) => {
  const source = process.env.APP02C1_BROWSER_TOKEN_A
  const peer = process.env.APP02C1_BROWSER_TOKEN_B
  const sales = process.env.DEV_F05_SALES
  const receiver = process.env.DEV_F05_RECEIVER
  const finance = process.env.DEV_F05_FINANCE
  const qa = process.env.DEV_F05_QA
  const unassigned = process.env.DEV_F05_QA_UNASSIGNED
  const peerFinance = process.env.DEV_F06_PEER_FINANCE
  const selfCheck = process.env.DEV_F06_SELF_CHECK
  if (!source || !peer || !sales || !receiver || !finance || !qa || !unassigned || !peerFinance || !selfCheck)
    throw new Error('Missing protected calendar and cost roles')
  const { tripId, delivery } = await prepareShipment(request, source, sales)
  const lockGuard = await loadSource(request, source, sales, 'R-LOCK', 1)
  const roleTokens = { sales, receiver, finance }
  const first = await prepareReturn(request, roleTokens, delivery, 'november')
  await post(request, returnApi + '/commands', qa, { action: 'RELEASE', caseId: first,
    reason: 'Original cost released while November is open', evidence: 'F06-release-november' })
  const originalReturn = await get<{ releasedCost: number, state: string }[]>(request, returnApi + '/cases', sales)
  expect(originalReturn).toHaveLength(1)
  expect(originalReturn[0]).toMatchObject({ releasedCost: 3334, state: 'RELEASED' })
  await checkpoint('lock-november')
  await post(request, deliveryApi + '/deliver', sales, lockGuard, 422, 'F06_OPEN_PERIOD_REQUIRED')

  const proposal = { action: 'PROPOSE', deliveryId: delivery.deliveryId, adjustedCost: 6768,
    reason: 'Late validated source cost', evidence: 'F06-proposal-closed-month' }
  await post(request, costApi + '/commands', finance, proposal, 422, 'F06_OPEN_PERIOD_REQUIRED')
  await checkpoint('advance-december')
  await post(request, costApi + '/commands', peerFinance, { ...proposal, evidence: 'F06-cross-tenant' },
    422, 'F06_SOURCE_NOT_FOUND')
  expect(await get<unknown[]>(request, returnApi + '/cases', peer)).toEqual([])

  const second = await prepareReturn(request, roleTokens, delivery, 'december')
  await login(page, '/sales/cost-adjustments', finance)
  await page.getByRole('combobox', { name: 'Lần giao nguồn', exact: true }).selectOption(delivery.deliveryId)
  await page.getByLabel('Giá vốn sau điều chỉnh (VND)').fill('6768')
  await page.getByLabel('Lý do', { exact: true }).fill('Late validated source cost')
  await page.getByLabel('Chứng từ DEV').fill('F06-proposal-ui')
  await page.getByRole('button', { name: 'Đề nghị điều chỉnh', exact: true }).click()
  await expect(page.getByText('Đã ghi đề nghị điều chỉnh.')).toBeVisible()
  const proposals = await get<{ id: string, deliveryId: string, status: string, adjustedCost: number }[]>(
    request, costApi + '/proposals', finance)
  expect(proposals).toHaveLength(1)
  expect(proposals[0]).toMatchObject({ deliveryId: delivery.deliveryId, status: 'PROPOSED', adjustedCost: 6768 })
  const proposalId = proposals[0].id
  await post(request, returnApi + '/commands', qa, { action: 'RELEASE', caseId: second,
    reason: 'Next-period release must wait for the submitted adjustment', evidence: 'F06-release-too-early' },
    422, 'F06_ADJUSTMENT_REQUIRED')
  const approve = { action: 'APPROVE', proposalId, reason: 'Check authorization', evidence: 'F06-check-guard' }
  await post(request, costApi + '/commands', sales, approve, 403)
  await post(request, costApi + '/commands', unassigned, approve, 403)
  await post(request, costApi + '/commands', selfCheck, approve, 422, 'F06_SEPARATION_REQUIRED')

  await login(page, '/sales/cost-adjustments', qa)
  await page.getByRole('combobox', { name: 'Đề nghị chờ duyệt', exact: true }).selectOption(proposalId)
  await page.getByLabel('Lý do', { exact: true }).fill('Independent warehouse cost review')
  await page.getByLabel('Chứng từ DEV').fill('F06-adjustment-ui')
  const interrupted: { body?: object, status?: number, outcome?: CommandOutcome } = {}
  await page.route('**' + costApi + '/commands', async route => {
    interrupted.body = route.request().postDataJSON() as object
    const applied = await route.fetch()
    interrupted.status = applied.status()
    interrupted.outcome = await applied.json()
    await route.abort('connectionreset')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Duyệt điều chỉnh', exact: true }).click()
  await expect(page.getByText('Chưa rõ kết quả ghi điều chỉnh giá vốn.')).toBeVisible()
  expect(interrupted.status).toBe(200)
  await page.getByRole('button', { name: 'Tra cứu kết quả', exact: true }).click()
  await expect(page.getByText('Đã ghi điều chỉnh giá vốn.')).toBeVisible()
  const board = page.getByRole('region', { name: 'Danh sách điều chỉnh' })
  await expect(board).toContainText('Đã duyệt')
  await expect(board).toContainText('+50 VND')
  await expect(board).toContainText('6.768 VND')
  await page.screenshot({ path: info.outputPath('adjusted-cost-recovered.png') })
  expect(interrupted.body).toBeDefined()
  expect(interrupted.outcome?.sourceId).toBeTruthy()

  const replay = await post(request, costApi + '/commands', qa, interrupted.body!)
  expect(replay).toMatchObject({ replayed: true, sourceId: interrupted.outcome?.sourceId })
  await post(request, costApi + '/commands', qa, { ...interrupted.body!, reason: 'Payload changed on same command' },
    409, 'IDEMPOTENCY_PAYLOAD_MISMATCH')
  await post(request, costApi + '/commands', finance, { ...proposal, evidence: 'F06-duplicate-revision' },
    422, 'F06_ADJUSTMENT_EXISTS')
  await checkpoint('approved-adjustment')

  await login(page, '/sales/returns', qa)
  await page.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true }).selectOption(second)
  await page.getByLabel('Lý do', { exact: true }).fill('Release against approved December cost')
  await page.getByLabel('Chứng từ DEV').fill('F06-release-december')
  await page.getByRole('button', { name: 'Giải phóng hàng', exact: true }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const secondRow = page.getByRole('region', { name: 'Danh sách hồ sơ trả hàng' }).getByRole('listitem')
    .filter({ hasText: 'Mã hồ sơ ' + second })
  await expect(secondRow).toContainText('3.384 VND')
  const ar = await get<{ amount: string, outstanding: string, cost: string }[]>(
    request, deliveryApi + '/trips/' + tripId + '/ar', sales)
  expect(ar).toHaveLength(1)
  expect([ar[0].amount, ar[0].outstanding, ar[0].cost].map(Number)).toEqual([20000, 20000, 6667])
  await page.screenshot({ path: info.outputPath('adjusted-second-return.png') })
})
