import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const salesApi = '/api/synthetic/app02c2'
const deliveryApi = '/api/synthetic/app02c4'
const returnApi = '/api/synthetic/dev-f05'
const headers = (token: string) => ({ Authorization: 'Bearer ' + token })
const open = (page: Page, name: string) => page.getByRole('navigation', { name: 'Sales A' })
  .getByRole('button', { name, exact: true }).click()

const credentials = {
  source: process.env.APP02C1_BROWSER_TOKEN_A,
  peer: process.env.APP02C1_BROWSER_TOKEN_B,
  sales: process.env.DEV_F05_SALES,
  receiver: process.env.DEV_F05_RECEIVER,
  finance: process.env.DEV_F05_FINANCE,
  qa: process.env.DEV_F05_QA,
}

type Delivery = { deliveryId: string, lotId: string, lotCode: string, sku: string, available: number }
type Case = { id: string, deliveryId: string, lotId: string, state: string, releasedCost: number | null }
type Catalog = { customers: { id: string, label: string }[], skus: { id: string, label: string }[],
  trips: { id: string }[], warehouses: { id: string }[] }

async function login(page: Page, path: string, token: string) {
  await page.goto(path)
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

async function shipTo(request: APIRequestContext, token: string, customerId: string, line: string) {
  const response = await request.post('/api/synthetic/app02c1/ship-to-selections', { headers: headers(token),
    data: { commandId: crypto.randomUUID(), customerId,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line, recipient: 'Nhận DEV' },
    } })
  expect(response.status()).toBe(200)
  return (await response.json()).id as string
}

async function get<T>(request: APIRequestContext, url: string, token: string): Promise<T> {
  const response = await request.get(url, { headers: headers(token) })
  expect(response.status()).toBe(200)
  return await response.json() as T
}

function command(request: APIRequestContext, token: string, data: object) {
  return request.post(returnApi + '/commands', { headers: headers(token),
    data: { commandId: crypto.randomUUID(), ...data } })
}

async function checkpoint(phase: string) {
  const dir = process.env.DEV_F05_CHECKPOINTS
  const run = process.env.APP02C1_RUN_ID
  if (!dir || !run) throw new Error('Missing DEV-F05 checkpoint fixture')
  const staging = join(dir, phase + '.partial')
  await writeFile(staging, run)
  await rename(staging, join(dir, phase + '.request'))
  await expect.poll(async () => {
    try { return await readFile(join(dir, phase + '.ready'), 'utf8') } catch { return '' }
  }, { timeout: 15_000 }).toBe(run)
}

function caseRow(page: Page, caseId: string) {
  return page.getByRole('region', { name: 'Danh sách hồ sơ trả hàng' }).getByRole('listitem')
    .filter({ hasText: 'Mã hồ sơ ' + caseId })
}

async function stage(page: Page, caseId: string, token: string, action: string, evidence: string, state: string) {
  await login(page, '/sales/returns', token)
  await page.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true }).selectOption(caseId)
  await page.getByLabel('Lý do', { exact: true }).fill(evidence)
  await page.getByLabel('Chứng từ DEV').fill(evidence)
  const [post] = await Promise.all([
    page.waitForRequest(req => req.method() === 'POST' && req.url().endsWith(returnApi + '/commands')),
    page.getByRole('button', { name: action, exact: true }).click(),
  ])
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  await expect(caseRow(page, caseId)).toContainText(state)
  const result = await post.response()
  expect(result?.status()).toBe(200)
  return { body: post.postData()!, sourceId: (await result!.json()).sourceId as string }
}

test('F05-01–04: sourced returns stay held until QA, prove residual and reject overlap', async ({ page, request }, info) => {
  const { source, peer, sales, receiver, finance, qa } = credentials
  const quarantine = process.env.DEV_F05_QA_LOCATION
  if (!source || !peer || !sales || !receiver || !finance || !qa || !quarantine)
    throw new Error('Missing protected return fixture')
  const catalog = await get<Catalog>(request, salesApi + '/catalog', source)
  const customer = catalog.customers.find(option => option.label.startsWith('C2-FIXTURE'))
  const sku = catalog.skus.find(option => option.label.startsWith('R-05 ·'))
  if (!customer || !sku) throw new Error('Missing R-05 opening')
  const tripId = catalog.trips[0].id
  const address = await shipTo(request, source, customer.id, 'Return source')
  await login(page, '/sales', source)
  await open(page, 'Đơn A và giữ hàng')
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer.id)
  await page.getByRole('combobox', { name: 'ShipTo đã chọn', exact: true }).selectOption(address)
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(tripId)
  await page.getByRole('combobox', { name: 'SKU dòng 1', exact: true }).selectOption(sku.id)
  await page.getByLabel('Số lượng dòng 1').fill('2')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(catalog.warehouses[0].id)
  await page.getByLabel('Số lượng giữ').fill('2')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ')).toBeVisible()

  const guardAddress = await shipTo(request, source, customer.id, 'Quarantine reservation guard')
  const guardDraft = await request.post(salesApi + '/drafts', { headers: headers(source), data: {
    commandId: crypto.randomUUID(), customerId: customer.id, tripId, selectionId: guardAddress,
    lines: [{ skuId: sku.id, quantity: 1 }],
  } })
  expect(guardDraft.status()).toBe(200)
  const guardId = (await guardDraft.json()).sourceId as string
  const guardConfirm = await request.post(salesApi + '/confirm', { headers: headers(source), data: {
    commandId: crypto.randomUUID(), orderId: guardId,
  } })
  expect(guardConfirm.status()).toBe(200)
  const guardOrder = (await get<{ id: string, confirmSourceId: string, lines: { lineId: string }[] }[]>(
    request, salesApi + '/orders', source)).find(row => row.id === guardId)
  if (!guardOrder) throw new Error('Missing confirmed quarantine guard order')

  await open(page, 'Nạp hàng')
  await page.getByLabel('Số lượng nạp dòng 1').fill('2')
  await page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText(/Đã nạp 2 EA/)).toBeVisible()
  await open(page, 'Giao hàng')
  await page.getByLabel('Số lượng đã giao dòng 1').fill('2')
  await page.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  const ar = await get<{ amount: string, outstanding: string, cost: string }[]>(
    request, deliveryApi + '/trips/' + tripId + '/ar', sales)
  expect(ar).toHaveLength(1)
  expect([ar[0].amount, ar[0].outstanding, ar[0].cost].map(Number)).toEqual([20000, 20000, 6667])
  await checkpoint('delivered')

  const deliveries = await get<Delivery[]>(request, returnApi + '/deliveries', sales)
  expect(deliveries).toHaveLength(1)
  const delivery = deliveries[0]
  expect(delivery).toMatchObject({ sku: 'R-05', lotCode: 'RET-ORIGIN', available: 2 })
  const originalRequest = { action: 'REQUEST', deliveryId: delivery.deliveryId, lotId: delivery.lotId, quantity: 1,
    reason: 'Return fixture one unit' }
  const oversupply = await command(request, sales, { ...originalRequest, quantity: 3 })
  expect(oversupply.status()).toBe(422)
  expect((await oversupply.json()).reasonCode).toBe('F05_RETURN_QUANTITY_EXCEEDED')
  const foreign = await command(request, peer, originalRequest)
  expect(foreign.status()).toBe(422)
  expect((await foreign.json()).reasonCode).toBe('F05_SOURCE_NOT_FOUND')
  expect(await get<Delivery[]>(request, returnApi + '/deliveries', peer)).toEqual([])

  await login(page, '/sales/returns', sales)
  await page.getByLabel('Nguồn giao và lô').selectOption(delivery.deliveryId + ':' + delivery.lotId)
  await page.getByLabel('Số lượng trả').fill('1')
  await page.getByLabel('Lý do', { exact: true }).fill('Return fixture one unit')
  await page.getByRole('button', { name: 'Đề nghị trả hàng', exact: true }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const cases = await get<Case[]>(request, returnApi + '/cases', sales)
  expect(cases).toHaveLength(1)
  const first = cases[0].id
  await expect(caseRow(page, first)).toContainText('Đã đề nghị')
  await checkpoint('requested')

  await stage(page, first, receiver, 'Nhận vào cách ly', 'F05-receive-first', 'Đang cách ly')
  await checkpoint('received')
  await page.screenshot({ path: info.outputPath('quarantine-held.png') })
  const unapproved = await command(request, qa, { action: 'RELEASE', caseId: first,
    reason: 'QA before Finance guard', evidence: 'F05-qa-before-finance' })
  expect(unapproved.status()).toBe(422)
  expect((await unapproved.json()).reasonCode).toBe('F05_APPROVAL_REQUIRED')
  const unauthorized = await command(request, sales, { action: 'APPROVE', caseId: first,
    reason: 'Sales cannot approve', evidence: 'F05-sales-denied' })
  expect(unauthorized.status()).toBe(403)
  const heldReserve = await request.post(salesApi + '/reserve', { headers: headers(source), data: {
    commandId: crypto.randomUUID(), orderId: guardId, confirmSourceId: guardOrder.confirmSourceId,
    lineId: guardOrder.lines[0].lineId, locationId: quarantine, quantity: 1,
  } })
  expect(heldReserve.status()).toBe(422)
  expect((await heldReserve.json()).reasonCode).toBe('F03_LOT_STOCK_INSUFFICIENT')

  await stage(page, first, finance, 'Duyệt trả hàng', 'F05-approve-first', 'Đã duyệt')
  await checkpoint('approved')
  const firstRelease = await stage(page, first, qa, 'Giải phóng hàng', 'F05-release-first', 'Đã giải phóng')
  await expect(caseRow(page, first)).toContainText('3.334 VND')
  await checkpoint('released-first')
  const repeated = await request.post(returnApi + '/commands', {
    headers: { ...headers(qa), 'content-type': 'application/json' }, data: firstRelease.body,
  })
  expect(repeated.status()).toBe(200)
  expect(await repeated.json()).toMatchObject({ replayed: true, sourceId: firstRelease.sourceId })

  const challenged = await Promise.all([
    command(request, sales, { ...originalRequest, reason: 'Second unit concurrent request A' }),
    command(request, sales, { ...originalRequest, reason: 'Second unit concurrent request B' }),
  ])
  const applied = challenged.filter(connection => connection.status() === 200)
  const rejected = challenged.filter(connection => connection.status() === 422)
  expect(applied).toHaveLength(1)
  expect(rejected).toHaveLength(1)
  expect((await rejected[0].json()).reasonCode).toBe('F05_RETURN_QUANTITY_EXCEEDED')
  const second = (await applied[0].json()).sourceId as string
  expect(second).not.toBe(first)
  await stage(page, second, receiver, 'Nhận vào cách ly', 'F05-receive-second', 'Đang cách ly')
  await stage(page, second, finance, 'Duyệt trả hàng', 'F05-approve-second', 'Đã duyệt')
  await stage(page, second, qa, 'Giải phóng hàng', 'F05-release-second', 'Đã giải phóng')
  await expect(caseRow(page, second)).toContainText('3.333 VND')
  await page.screenshot({ path: info.outputPath('released-returns.png') })

  const finalCases = await get<Case[]>(request, returnApi + '/cases', sales)
  expect(finalCases).toHaveLength(2)
  expect(finalCases).toEqual(expect.arrayContaining([
    expect.objectContaining({ id: first, deliveryId: delivery.deliveryId, lotId: delivery.lotId,
      state: 'RELEASED', releasedCost: 3334 }),
    expect.objectContaining({ id: second, deliveryId: delivery.deliveryId, lotId: delivery.lotId,
      state: 'RELEASED', releasedCost: 3333 }),
  ]))
  expect(await get<Case[]>(request, returnApi + '/cases', peer)).toEqual([])
})
