import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const customerApi = '/api/synthetic/app02c1'
const orderApi = '/api/synthetic/app02c2'
const loadApi = '/api/synthetic/app02c3'
const deliveryApi = '/api/synthetic/app02c4'
const returnApi = '/api/synthetic/dev-f05'
const refundApi = '/api/synthetic/dev-f08'
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
  expect(response.status(), path + ' ' + JSON.stringify(data) + ' ' + (await response.text())).toBe(status)
  if (status === 403) return { status: 'FORBIDDEN', sourceId: '' }
  const body = await response.json() as CommandOutcome
  if (rejection) expect(body.reasonCode).toBe(rejection)
  return body
}

async function checkpoint(phase: string) {
  const dir = process.env.DEV_F08_CHECKPOINTS
  const run = process.env.APP02C1_RUN_ID
  if (!dir || !run) throw new Error('Missing DEV-F08 checkpoint fixture')
  const staging = join(dir, phase + '.partial')
  await writeFile(staging, run)
  await rename(staging, join(dir, phase + '.request'))
  await expect.poll(async () => {
    try { return await readFile(join(dir, phase + '.ready'), 'utf8') } catch { return '' }
  }, { timeout: 15_000 }).toBe(run)
}

async function loadSource(request: APIRequestContext, source: string, sales: string) {
  const catalog = await get<Catalog>(request, orderApi + '/catalog', source)
  const customer = catalog.customers.find(item => item.label.startsWith('F08-SOURCE ·'))
  const product = catalog.skus.find(item => item.label.startsWith('P ·'))
  const secondProduct = catalog.skus.find(item => item.label.startsWith('Q ·'))
  const quantity = 3
  if (!customer || !product || !secondProduct) throw new Error('Missing protected F08 fixture')
  const tripId = catalog.trips[0].id
  const selection = await request.post(customerApi + '/ship-to-selections', {
    headers: headers(source), data: { commandId: crypto.randomUUID(), customerId: customer.id,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'Verified refund DEV', recipient: 'Nhận DEV' },
    },
  })
  expect(selection.status()).toBe(200)
  const draft = await post(request, orderApi + '/drafts', source, {
    customerId: customer.id, tripId, selectionId: (await selection.json()).id,
    lines: [{ skuId: product.id, quantity, uomCode: 'EA' }, { skuId: secondProduct.id, quantity: 1, uomCode: 'EA' }],
  })
  const orderId = draft.sourceId
  const confirmation = await post(request, orderApi + '/confirm', source, { orderId })
  const orders = await get<{ id: string, lines: { lineId: string }[] }[]>(request, orderApi + '/orders', source)
  const order = orders.find(item => item.id === orderId)
  if (!order) throw new Error('Missing protected refund source order')
  await post(request, orderApi + '/reserve', source, { orderId, lineId: order.lines[0].lineId,
    locationId: catalog.warehouses[0].id, quantity, confirmSourceId: confirmation.sourceId })
  const rights = await get<{ id: string, orderId: string, warehouseHeld: number | string }[]>(
    request, loadApi + '/trips/' + tripId + '/rights', source)
  const right = rights.find(item => item.orderId === orderId)
  if (!right) throw new Error('Missing warehouse right for ')
  await post(request, loadApi + '/load', source, { tripId,
    lines: [{ rightId: right.id, expectedWarehouseHeld: Number(right.warehouseHeld), quantity }] })
  const loaded = await get<{ id: string, orderId: string, shipToVersion: number, available: number | string }[]>(
    request, deliveryApi + '/trips/' + tripId + '/lines', sales)
  const line = loaded.find(item => item.orderId === orderId)
  if (!line) throw new Error('Missing loaded line for ')
  expect(Number(line.available)).toBe(quantity)
  return { tripId, manifestLineId: line.id, shipToVersion: line.shipToVersion, quantity }
}

type Credit = { id: string, caseId: string, net: number, tax: number, gross: number, discount: number, available: number }
type Refund = { id: string, creditId: string, amount: number, status: string }
const refundWorkspace = '/sales/refunds'

async function details(page: Page, evidence: string, reason: string, evidenceLabel = 'Chứng từ DEV') {
  await page.getByLabel('Lý do', { exact: true }).fill(reason)
  await page.getByLabel(evidenceLabel, { exact: true }).fill(evidence)
}

async function receiveReturn(request: APIRequestContext, sales: string, receiver: string, delivery: ReturnSource, index: number) {
  const { sourceId: caseId } = await post(request, returnApi + '/commands', sales, { action: 'REQUEST',
    deliveryId: delivery.deliveryId, lotId: delivery.lotId, quantity: 1, reason: 'One EA from the original F08 delivery' })
  await post(request, returnApi + '/commands', receiver, { action: 'RECEIVE', caseId, quantity: 1,
    reason: 'Counted one EA and held in quarantine', evidence: 'F08-return-count-' + index })
  return caseId
}

async function releaseReturn(page: Page, request: APIRequestContext, finance: string, qa: string, caseId: string, index: number) {
  await post(request, returnApi + '/commands', finance, { action: 'APPROVE', caseId,
    reason: 'Approve physical return from original lot', evidence: 'F08-return-approved-' + index })
  await login(page, '/sales/returns', qa)
  await page.getByLabel('Hồ sơ trả hàng', { exact: true }).selectOption(caseId)
  await details(page, 'F08-return-release-' + index, 'Release one EA from the original priced delivery')
  await page.getByRole('button', { name: 'Giải phóng hàng', exact: true }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
}

async function selectAction(page: Page, action: string) {
  await page.getByLabel('Thao tác hoàn tiền', { exact: true }).selectOption(action)
}

async function creditReturn(page: Page, request: APIRequestContext, maker: string, caseId: string, index: number) {
  await login(page, refundWorkspace, maker)
  await selectAction(page, 'CREDIT')
  await page.getByRole('combobox', { name: 'Hồ sơ trả đã giải phóng', exact: true }).selectOption(caseId)
  await details(page, 'F08-credit-' + index, 'Issue price-snapshot credit for accepted physical return')
  await page.getByRole('button', { name: 'Ghi credit', exact: true }).click()
  await expect(page.getByText('Đã ghi credit.')).toBeVisible()
  const credit = (await get<Credit[]>(request, refundApi + '/credits', maker)).find(row => row.caseId === caseId)
  if (!credit) throw new Error('Missing independently queryable credit for ' + caseId)
  return credit
}

test('F08-01–05: reconcile price residuals and guard independent refunds against treasury exhaustion', async ({ page, request }, info) => {
  const source = process.env.APP02C1_BROWSER_TOKEN_A
  const sales = process.env.DEV_F05_SALES
  const receiver = process.env.DEV_F05_RECEIVER
  const finance = process.env.DEV_F05_FINANCE
  const qa = process.env.DEV_F05_QA
  const maker = process.env.DEV_F08_MAKER
  const checker = process.env.DEV_F08_CHECKER
  const cashier = process.env.DEV_F08_CASHIER
  const witness = process.env.DEV_F08_WITNESS
  const peer = process.env.DEV_F08_PEER_FINANCE
  if (!source || !sales || !receiver || !finance || !qa || !maker || !checker || !cashier || !witness || !peer)
    throw new Error('Missing protected DEV-F08 browser role fixtures')
  const shipment = await loadSource(request, source, sales)
  await post(request, deliveryApi + '/deliver', sales, shipment)
  const returnable = await get<ReturnSource[]>(request, returnApi + '/deliveries', sales)
  expect(returnable).toHaveLength(1)
  const delivery = returnable[0]
  expect(delivery).toMatchObject({ sku: 'P', lotCode: 'F08-P-ORIGIN', available: 3 })
  const deliveryId = delivery.deliveryId
  await checkpoint('delivered')

  const quarantined = await receiveReturn(request, sales, receiver, delivery, 1)
  const creditGuard = { action: 'CREDIT', caseId: quarantined, reason: 'Quarantine cannot create credit', evidence: 'F08-credit-guard' }
  await post(request, refundApi + '/commands', maker, creditGuard, 422, 'F08_RETURN_NOT_RELEASED')
  await post(request, refundApi + '/commands', maker, { ...creditGuard, caseId: crypto.randomUUID() }, 422, 'F08_RETURN_NOT_RELEASED')
  const settlement = { action: 'SETTLE', deliveryId, amount: 208800, reason: 'Verified synthetic inbound receipt',
    evidence: 'F08-payment-verified' }
  await post(request, refundApi + '/commands', maker, settlement, 422, 'F08_WITNESS_REQUIRED')
  await post(request, refundApi + '/commands', peer, settlement, 422, 'F08_SOURCE_NOT_FOUND')
  await post(request, refundApi + '/commands', finance, settlement, 403)
  await login(page, refundWorkspace, witness)
  await selectAction(page, 'WITNESS_PAYMENT')
  await page.getByLabel('Lần giao nguồn', { exact: true }).selectOption(deliveryId)
  await page.getByLabel('Số tiền thực nhận (VND)', { exact: true }).fill('208800')
  await details(page, settlement.evidence, 'Independent observation of synthetic customer payment')
  await page.getByRole('button', { name: 'Xác minh tiền vào', exact: true }).click()
  await expect(page.getByText('Đã xác minh chứng từ tiền vào.')).toBeVisible()
  await checkpoint('quarantined')
  await releaseReturn(page, request, finance, qa, quarantined, 1)
  await post(request, refundApi + '/commands', maker, creditGuard, 422, 'F08_PAYMENT_REQUIRED')

  await login(page, refundWorkspace, maker)
  await expect(page.getByRole('heading', { name: 'Credit và hoàn tiền' })).toBeVisible()
  await selectAction(page, 'SETTLE')
  await page.getByLabel('Lần giao nguồn', { exact: true }).selectOption(deliveryId)
  await page.getByLabel('Số tiền đối soát (VND)', { exact: true }).fill('208800')
  await details(page, settlement.evidence, settlement.reason)
  await page.getByRole('button', { name: 'Đối soát tiền vào', exact: true }).click()
  await expect(page.getByText('Đã đối soát tiền vào.')).toBeVisible()
  await checkpoint('settled')
  await post(request, refundApi + '/commands', peer, creditGuard, 422, 'F08_RETURN_NOT_RELEASED')
  const first = await creditReturn(page, request, maker, quarantined, 1)
  expect(first).toMatchObject({ net: 64444, tax: 5156, gross: 69600, discount: 2222, available: 69600 })
  await checkpoint('credited')
  await post(request, refundApi + '/commands', maker, creditGuard, 422, 'F08_RETURN_ALREADY_CREDITED')
  const proposed = { action: 'PROPOSE', creditId: first.id, amount: 20000, reason: 'Partial customer payout',
    evidence: 'F08-refund-proposal' }
  await post(request, refundApi + '/commands', maker, { ...proposed, amount: 69601 }, 422, 'F08_CREDIT_EXCEEDED')
  await selectAction(page, 'PROPOSE')
  await page.getByLabel('Nguồn credit', { exact: true }).selectOption(first.id)
  await page.getByLabel('Số tiền đề nghị (VND)', { exact: true }).fill('20000')
  await details(page, proposed.evidence, proposed.reason)
  await page.getByRole('button', { name: 'Đề nghị chi hoàn tiền', exact: true }).click()
  await expect(page.getByText('Đã ghi đề nghị chi hoàn tiền.')).toBeVisible()
  const refunds = await get<Refund[]>(request, refundApi + '/refunds', maker)
  expect(refunds).toHaveLength(1)
  expect(refunds[0]).toMatchObject({ creditId: first.id, amount: 20000, status: 'PROPOSED' })
  const refundId = refunds[0].id
  await checkpoint('proposed')
  const approval = { action: 'APPROVE', refundId, reason: 'Independent review of source and amount', evidence: 'F08-refund-approved' }
  const payout = { action: 'DISBURSE', refundId, amount: 20000, reason: 'Confirmed actual synthetic payout',
    evidence: 'F08-cashier-proof' }
  await post(request, refundApi + '/commands', maker, approval, 422, 'F08_SEPARATION_REQUIRED')
  await post(request, refundApi + '/commands', cashier, approval, 403)
  await post(request, refundApi + '/commands', cashier, payout, 422, 'F08_APPROVED_REFUND_REQUIRED')
  await login(page, refundWorkspace, checker)
  await selectAction(page, 'APPROVE')
  await page.getByLabel('Đề nghị chi', { exact: true }).selectOption(refundId)
  await details(page, approval.evidence, approval.reason)
  await page.getByRole('button', { name: 'Duyệt chi hoàn tiền', exact: true }).click()
  await expect(page.getByText('Đã duyệt chi hoàn tiền.')).toBeVisible()
  await post(request, refundApi + '/commands', cashier, { ...payout, evidence: '' }, 422, 'F08_FIELDS_INVALID')
  await post(request, refundApi + '/commands', cashier, payout, 422, 'F08_WITNESS_REQUIRED')
  await post(request, refundApi + '/commands', checker, payout, 403)

  await login(page, refundWorkspace, witness)
  await selectAction(page, 'WITNESS_PAYOUT')
  await page.getByLabel('Đề nghị đã duyệt', { exact: true }).selectOption(refundId)
  await page.getByLabel('Số tiền thực chi (VND)', { exact: true }).fill('20000')
  await details(page, payout.evidence, 'Independently witnessed actual synthetic payout', 'Chứng từ thực chi DEV')
  await page.getByRole('button', { name: 'Xác minh thực chi', exact: true }).click()
  await expect(page.getByText('Đã xác minh chứng từ thực chi.')).toBeVisible()
  await checkpoint('approved')

  await login(page, refundWorkspace, cashier)
  await page.getByLabel('Đề nghị đã duyệt', { exact: true }).selectOption(refundId)
  await page.getByLabel('Số tiền thực chi (VND)', { exact: true }).fill('20000')
  await details(page, payout.evidence, payout.reason, 'Chứng từ thực chi DEV')
  const interrupted: { body?: object, original?: CommandOutcome, concurrentStatus?: number, concurrent?: CommandOutcome } = {}
  await page.route('**' + refundApi + '/commands', async route => {
    interrupted.body = route.request().postDataJSON() as object
    const [applied, competing] = await Promise.all([route.fetch(), request.post(refundApi + '/commands', {
      headers: headers(cashier), data: { ...interrupted.body, commandId: crypto.randomUUID() },
    })])
    interrupted.original = await applied.json()
    interrupted.concurrentStatus = competing.status()
    interrupted.concurrent = await competing.json()
    await route.abort('connectionreset')
  }, { times: 1 })
  await page.getByRole('button', { name: 'Ghi thực chi hoàn tiền', exact: true }).click()
  await expect(page.getByText('Chưa rõ kết quả ghi nghiệp vụ hoàn tiền.')).toBeVisible()
  const applied = [interrupted.original, interrupted.concurrent].filter(row => row?.status === 'APPLIED')
  const rejected = [interrupted.original, interrupted.concurrent].filter(row => row?.status === 'REJECTED')
  expect(applied).toHaveLength(1)
  expect(rejected).toHaveLength(1)
  expect(rejected[0]?.reasonCode).toMatch(/F08_EVIDENCE_USED|F08_APPROVED_REFUND_REQUIRED/)
  await page.getByRole('button', { name: 'Tra cứu kết quả', exact: true }).click()
  if (interrupted.original?.status === 'APPLIED') await expect(page.getByText('Đã ghi thực chi hoàn tiền.')).toBeVisible()
  else await expect(page.getByText(/F08_EVIDENCE_USED|F08_APPROVED_REFUND_REQUIRED/)).toBeVisible()
  const same = await post(request, refundApi + '/commands', cashier, interrupted.body ?? {},
    interrupted.original?.status === 'APPLIED' ? 200 : 422)
  expect(same).toMatchObject({ status: interrupted.original?.status, replayed: true, sourceId: interrupted.original?.sourceId })
  await checkpoint('disbursed')
  expect((await get<Credit[]>(request, refundApi + '/credits', maker))[0].available).toBe(49600)

  const additionalCredits: Credit[] = []
  for (const index of [2, 3]) {
    const caseId = await receiveReturn(request, sales, receiver, delivery, index)
    await releaseReturn(page, request, finance, qa, caseId, index)
    additionalCredits.push(await creditReturn(page, request, maker, caseId, index))
  }
  const credits = await get<Credit[]>(request, refundApi + '/credits', maker)
  expect(credits.map(row => [row.net, row.tax, row.gross, row.discount])).toEqual(expect.arrayContaining([
    [64444, 5156, 69600, 2222], [64445, 5155, 69600, 2223],
  ]))
  expect(credits).toHaveLength(3)
  expect(credits.reduce((total, row) => total + row.available, 0)).toBe(188800)
  expect(await get<Credit[]>(request, refundApi + '/credits', peer)).toEqual([])
  expect(await get<Refund[]>(request, refundApi + '/refunds', peer)).toEqual([])
  await info.attach('final-credit-reconciliation', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
  await checkpoint('golden-final')

  const amount = 60000
  const contenders = []
  for (const [index, credit] of additionalCredits.entries()) {
    const reference = 'F08-treasury-' + (index + 2)
    const proposal = await post(request, refundApi + '/commands', maker, { action: 'PROPOSE', creditId: credit.id, amount,
      reason: 'Independent refund against a separate source credit', evidence: reference + '-proposal' })
    await post(request, refundApi + '/commands', checker, { action: 'APPROVE', refundId: proposal.sourceId,
      reason: 'Independent finance approves separate refund', evidence: reference + '-approval' })
    contenders.push({ creditId: credit.id, refundId: proposal.sourceId, amount, evidence: reference + '-witness' })
  }
  expect(contenders[0].creditId).not.toBe(contenders[1].creditId)
  expect(contenders[0].refundId).not.toBe(contenders[1].refundId)
  const approvedRefunds = await get<Refund[]>(request, refundApi + '/refunds', checker)
  for (const contender of contenders)
    expect(approvedRefunds).toContainEqual(expect.objectContaining({ id: contender.refundId, amount, status: 'APPROVED' }))
  await checkpoint('treasury-approved')

  const attempts = await Promise.all(contenders.map(async contender => {
    const response = await request.post(refundApi + '/commands', { headers: headers(witness), data: {
      commandId: crypto.randomUUID(), action: 'WITNESS_PAYOUT', refundId: contender.refundId, amount,
      reason: 'Verify separate payout against the common treasury', evidence: contender.evidence,
    } })
    return { ...contender, status: response.status(), result: await response.json() as CommandOutcome }
  }))
  expect(attempts.map(attempt => attempt.status).sort()).toEqual([200, 422])
  const winner = attempts.find(attempt => attempt.status === 200)
  const loser = attempts.find(attempt => attempt.status === 422)
  if (!winner || !loser) throw new Error('Expected a single treasury reservation')
  expect(winner.result).toMatchObject({ status: 'APPLIED' })
  expect(winner.result.sourceId).toBeTruthy()
  expect(loser.result).toMatchObject({ status: 'REJECTED', reasonCode: 'F08_TREASURY_UNAVAILABLE', sourceId: null })
  await post(request, refundApi + '/commands', cashier, { action: 'DISBURSE', amount, refundId: loser.refundId,
    reason: 'Unwitnessed payout must remain unposted', evidence: loser.evidence }, 422, 'F08_WITNESS_REQUIRED')
  await checkpoint('treasury-reserved')

  await login(page, refundWorkspace, cashier)
  await page.getByRole('combobox', { name: 'Đề nghị đã duyệt', exact: true }).selectOption(winner.refundId)
  await page.getByLabel('Số tiền thực chi (VND)', { exact: true }).fill(String(amount))
  await details(page, winner.evidence, 'Record only the witnessed payout with reserved treasury', 'Chứng từ thực chi DEV')
  await page.getByRole('button', { name: 'Ghi thực chi hoàn tiền', exact: true }).click()
  await expect(page.getByText('Đã ghi thực chi hoàn tiền.')).toBeVisible()

  await post(request, refundApi + '/commands', witness, { action: 'WITNESS_PAYOUT', amount, refundId: loser.refundId,
    reason: 'Remaining treasury after the paid refund still cannot cover this payout', evidence: loser.evidence },
    422, 'F08_TREASURY_UNAVAILABLE')
  const paidRefunds = await get<Refund[]>(request, refundApi + '/refunds', cashier)
  expect(paidRefunds).toContainEqual(expect.objectContaining({ id: winner.refundId, status: 'DISBURSED' }))
  expect(paidRefunds).toContainEqual(expect.objectContaining({ id: loser.refundId, status: 'APPROVED' }))
  const remainingCredits = await get<Credit[]>(request, refundApi + '/credits', maker)
  expect(remainingCredits.find(credit => credit.id === winner.creditId)?.available).toBe(9600)
  expect(remainingCredits.find(credit => credit.id === loser.creditId)?.available).toBe(69600)
  expect(remainingCredits.reduce((sum, credit) => sum + credit.available, 0)).toBe(128800)
  await info.attach('treasury-contention', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
})
