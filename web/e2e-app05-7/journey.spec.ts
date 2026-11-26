import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { auth, open } from '../e2e-app03-7/return-refund'
import { accounting, app, c2, get, type CloseInput, type Closure, type ClosureHistory, type Preview, type Work } from '../e2e-app05-2/fixture'
import { verifyFreshCommandRecovery } from '../e2e-app05-2/recovery'
import { assign, assignSuccessor, checkpoint, choice, closeTrip, closeWithLostAck, deliver, draft, evidence,
  handoff, load, readCheckpoint, receiveAndPost, reserve, reserveSuccessor, submit, transferHeld,
  type Catalog, type OrderSource } from './browser'

type PostingSource = { accountingSourceLineId: string; accountingDeliveryId: string }
type Delivery = OrderSource & PostingSource & {
  reserveSourceId: string; loadSourceId: string; deliverySourceId: string; handoffSourceId: string
}
type DayOne = {
  mainTripId: string; cleanTripId: string; zeroTripId: string; unusedTripId: string; blockedCommandId: string
  main: Delivery & { aFollowUpId: string; accountingFollowUpId: string; closeCommand: CloseInput; closure: Closure }
  clean: Delivery
  zero: OrderSource & { handoffSourceId: string; aFollowUpId: string; closure: Closure }
}
type Successors = { rights: { rightId: string; held: number; returned: number }[]
  bindings: { bindingId: string; destinationTripId: string; quantity: number; transferred: number; reserved: number }[] }
type FinanceReport = { report: { id: string; status: string }; lines: { sourceLineId: string; status: string }[] }

const direct = process.env.APP05_DAILY_DIRECT === '1'
const preview = (request: APIRequestContext, trip: string) => get<Preview>(request, app + '/' + trip)
const history = (request: APIRequestContext, trip: string) => get<ClosureHistory>(request, app + '/' + trip + '/closure')
const successors = (request: APIRequestContext, origin: string, order: OrderSource) =>
  get<Successors>(request, app + '/' + origin + '/a-remainders/' + order.orderLineId + '/successors')
const orders = (request: APIRequestContext) =>
  get<{ id: string; lines: { lineId: string }[] }[]>(request, c2 + '/orders', 'SOURCE')

async function accountingSource(request: APIRequestContext, trip: string): Promise<Work> {
  const rows = (await preview(request, trip)).work.filter(row => row.sourceKind === 'D4_RECONCILIATION')
  expect(rows).toHaveLength(1)
  expect(rows[0].relatedId).toBeTruthy()
  return rows[0]
}

function posting(source: Work): PostingSource {
  return { accountingSourceLineId: source.sourceId, accountingDeliveryId: source.relatedId! }
}

async function readyForDirect(name: string, tripId: string) {
  const path = '/work/trip-close?trip=' + tripId
  await writeFile(join(evidence, name + '.json'), JSON.stringify({ tripId, path }, null, 2))
  await writeFile(join(evidence, name + '.ready'), '')
}

async function directClose(request: APIRequestContext, page: Page, trip: string) {
  const { revision } = await preview(request, trip)
  await readyForDirect('direct-close', trip)
  await expect.poll(async () => (await history(request, trip)).closure?.status, {
    timeout: 180_000, message: 'Chờ thao tác đóng chuyến DEV từ trình duyệt trực tiếp',
  }).toBe('CLOSED_WITH_OPEN_CASES')
  const closure = (await history(request, trip)).closure!
  await page.reload()
  return { closure, closeCommand: { commandId: closure.commandId, expectedRevision: revision } }
}

async function directAssign(request: APIRequestContext, page: Page, origin: string, order: OrderSource, target: string) {
  await readyForDirect('direct-successor', origin)
  const binding = async () => (await successors(request, origin, order)).bindings.find(
    row => row.destinationTripId === target && row.quantity === 15,
  )
  await expect.poll(async () => (await binding())?.bindingId, {
    timeout: 180_000, message: 'Chờ thao tác xếp chuyến kế nhiệm DEV từ trình duyệt trực tiếp',
  }).toBeTruthy()
  await page.reload()
  return (await binding())!.bindingId
}

test('day-one closes an A40/25/15 shift, with a clean candidate, zero handoff, and unused trip', async ({ context, request }, info) => {
  test.skip(process.env.APP05_DAILY_STAGE !== 'day-one', 'First-day DEV clock required')
  if (direct) test.setTimeout(360_000)
  const catalog = await get<Catalog>(request, c2 + '/catalog', 'SOURCE')
  const trip = (code: string) => choice(catalog.trips, code, true)
  const mainTripId = trip('DEV-20261123-A')
  const cleanTripId = trip('DEV-20261123-B')
  const zeroTripId = trip('DEV-20261123-Z')
  const unusedTripId = trip('DEV-20261123-U')
  const warehouseId = choice(catalog.warehouses, 'C2-MAIN', true)
  const mainCustomer = choice(catalog.customers, 'DAY-A-DEV')
  const otherCustomer = choice(catalog.customers, 'C2-FIXTURE')

  const ops = await open(context, 'OPS', 'trip-close?trip=' + unusedTripId)
  await expect(ops.getByText('Chuyến chưa phát sinh nghiệp vụ', { exact: true })).toBeVisible()
  await expect(ops.getByRole('button', { name: 'Đóng chuyến', exact: true })).toHaveCount(0)
  expect((await history(request, unusedTripId)).closure).toBeNull()
  const sales = await open(context, 'SOURCE', 'orders')
  const kho = await open(context, 'RECEIVER', 'orders')
  const finance = await open(context, 'ACCOUNTANT', 'finance')

  const mainOrder = await draft(sales, mainTripId, mainCustomer, choice(catalog.skus, 'DAY-A-EA'), 40, () => orders(request))
  const reserveSourceId = await reserve(kho, mainTripId, mainOrder.orderId, warehouseId, 40)
  const loadSourceId = await load(kho, mainTripId, mainOrder.orderId, 40)
  const deliverySourceId = await deliver(sales, mainTripId, 25)
  await ops.goto('/work/trip-close?trip=' + mainTripId)
  await expect(ops.locator('[data-blocker-code="VEHICLE_STOCK_OUTSTANDING"]')).toBeVisible()
  await expect(ops.getByRole('button', { name: 'Đóng chuyến', exact: true })).toHaveCount(0)
  const blocked = await preview(request, mainTripId)
  expect(blocked).toMatchObject({ state: 'BLOCKED', canClose: false })
  const blockedCommandId = crypto.randomUUID()
  const rejected = await request.post(app + '/' + mainTripId + '/close', {
    headers: auth('OPS'), data: { commandId: blockedCommandId, expectedRevision: blocked.revision },
  })
  expect(rejected.status(), await rejected.text()).toBe(409)
  expect(await rejected.json()).toMatchObject({ code: 'TRIP_CLOSE_NOT_READY' })
  expect((await history(request, mainTripId)).closure).toBeNull()
  await ops.screenshot({ path: info.outputPath('stock-blocks-close.png') })

  const handoffSourceId = await handoff(kho, mainTripId, true)
  const d4 = await accountingSource(request, mainTripId)
  await receiveAndPost(finance, d4)
  const pending = await preview(request, mainTripId)
  expect(pending).toMatchObject({ state: 'NEEDS_ASSIGNMENT', canClose: false, aLines: [
    expect.objectContaining({ orderLineId: mainOrder.orderLineId, remaining: 15, held: 15, delivered: 25, onVehicle: 0 }),
  ] })
  expect(pending.work).toHaveLength(2)
  const remainder = pending.work.find(row => row.sourceKind === 'A_REMAINDER')!
  await ops.reload()
  const aFollowUpId = await assign(ops, mainTripId, remainder, 'Xếp chuyến giao tiếp 15 EA')
  const accountingFollowUpId = await assign(ops, mainTripId, d4, 'Chạy báo cáo bao phủ nguồn ngày đầu')
  const mainClose = direct ? await directClose(request, ops, mainTripId) : await closeWithLostAck(ops, mainTripId)
  expect(mainClose.closure).toMatchObject({ status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: 2, handoffId: handoffSourceId })
  await expect(ops.locator('[data-closure-id="' + mainClose.closure.closureId + '"]')).toBeVisible()
  await ops.screenshot({ path: info.outputPath('main-closed-day-one.png') })

  const cleanOrder = await draft(sales, cleanTripId, otherCustomer, choice(catalog.skus, 'Z-REFUND-EA'), 1, () => orders(request))
  const cleanReserve = await reserve(kho, cleanTripId, cleanOrder.orderId, warehouseId, 1)
  const cleanLoad = await load(kho, cleanTripId, cleanOrder.orderId, 1)
  const cleanDelivery = await deliver(sales, cleanTripId, 1)
  const cleanHandoff = await handoff(kho, cleanTripId, false)
  const cleanD4 = await accountingSource(request, cleanTripId)
  await receiveAndPost(finance, cleanD4)
  expect(await preview(request, cleanTripId)).toMatchObject({ state: 'NEEDS_ASSIGNMENT', canClose: false })
  expect((await history(request, cleanTripId)).closure).toBeNull()

  const zeroOrder = await draft(sales, zeroTripId, otherCustomer, choice(catalog.skus, 'Z-FIELD-EA'), 5, () => orders(request))
  expect(await preview(request, zeroTripId)).toMatchObject({ state: 'BLOCKED', canClose: false,
    blockers: expect.arrayContaining([expect.objectContaining({ code: 'ZERO_STOCK_HANDOFF_REQUIRED' })]),
    aLines: [expect.objectContaining({ remaining: 5, held: 0, loaded: 0 })] })
  const zeroHandoff = await handoff(kho, zeroTripId, false)
  const zeroPending = await preview(request, zeroTripId)
  expect(zeroPending.work).toHaveLength(1)
  await ops.goto('/work/trip-close?trip=' + zeroTripId)
  const zeroFollowUp = await assign(ops, zeroTripId, zeroPending.work[0], 'Chọn chuyến rồi giữ bổ sung 5 EA')
  const zeroClosure = await closeTrip(ops, zeroTripId)
  expect(zeroClosure).toMatchObject({ status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: 1, handoffId: zeroHandoff })
  await checkpoint<DayOne>('day-one', {
    mainTripId, cleanTripId, zeroTripId, unusedTripId, blockedCommandId,
    main: { ...mainOrder, reserveSourceId, loadSourceId, deliverySourceId, handoffSourceId, ...posting(d4),
      aFollowUpId, accountingFollowUpId, ...mainClose },
    clean: { ...cleanOrder, reserveSourceId: cleanReserve, loadSourceId: cleanLoad, deliverySourceId: cleanDelivery,
      handoffSourceId: cleanHandoff, ...posting(cleanD4) },
    zero: { ...zeroOrder, handoffSourceId: zeroHandoff, aFollowUpId: zeroFollowUp, closure: zeroClosure },
  })
})

test('day-two recovers the old command and fulfills the same orders before closing the separate clean trip', async ({ browser, context, request }, info) => {
  test.skip(process.env.APP05_DAILY_STAGE !== 'day-two', 'Second-day DEV clock required')
  if (direct) test.setTimeout(360_000)
  const day = await readCheckpoint<DayOne>('day-one')
  const catalog = await get<Catalog>(request, c2 + '/catalog', 'SOURCE')
  const successorTripId = choice(catalog.trips, 'DEV-20261124-A', true)
  const zeroSuccessorTripId = choice(catalog.trips, 'DEV-20261124-B', true)
  const warehouseId = choice(catalog.warehouses, 'C2-MAIN', true)
  if (!direct) await verifyFreshCommandRecovery(browser, request, day.mainTripId, day.main.closure, day.main.closeCommand)
  const ops = await open(context, 'OPS', 'app05-work')
  await expect(ops.getByTestId('app05-command-' + day.main.closeCommand.commandId)).toContainText(day.main.closure.closureId)
  await expect(ops.getByTestId('app05-work-' + day.main.aFollowUpId)).toContainText('Còn phải giao')
  await ops.getByTestId('app05-work-' + day.main.aFollowUpId).getByRole('link', { name: 'Mở chuyến gốc' }).click()
  await expect(ops).toHaveURL(new RegExp('trip=' + day.mainTripId + '.*source=' + day.main.orderLineId))
  expect((await history(request, day.mainTripId)).currentOpenWorkCount).toBe(2)
  const firstScope = await successors(request, day.mainTripId, day.main)
  expect(firstScope.rights).toEqual([expect.objectContaining({ returned: 15, held: 15 })])
  const bindingId = direct ? await directAssign(request, ops, day.mainTripId, day.main, successorTripId)
    : await assignSuccessor(ops, day.mainTripId, day.main.orderLineId, successorTripId, 15)
  const transfer = await transferHeld(ops, day.mainTripId, day.main.orderLineId, bindingId, firstScope.rights[0].rightId, 15)
  expect(transfer).toMatchObject({ returned: 15, quantity: 15 })
  const kho = await open(context, 'RECEIVER', 'load?trip=' + successorTripId)
  const sales = await open(context, 'SOURCE', 'delivery?trip=' + successorTripId)
  const loadSourceId = await load(kho, successorTripId, day.main.orderId, 15)
  const deliverySourceId = await deliver(sales, successorTripId, 15)
  const mainD4 = await accountingSource(request, successorTripId)
  const finance = await open(context, 'ACCOUNTANT', 'finance')
  await receiveAndPost(finance, mainD4)
  const arPath = '/api/synthetic/app02c5/ar'
  const ar = await get(request, arPath + '?orderLineId=' + day.main.orderLineId, 'SOURCE')
  const originalLine = { orderId: day.main.orderId, orderLineId: day.main.orderLineId,
    confirmSourceId: day.main.confirmSourceId, originTrip: 'DEV-20261123-A', status: 'RECONCILED' }
  expect(ar).toMatchObject({ expected: 2, reconciled: 2, knownBalance: '400000.0000' })
  expect(ar).toHaveProperty('sources', expect.arrayContaining([
    expect.objectContaining({ ...originalLine, trip: 'DEV-20261123-A', sourceId: day.main.deliverySourceId,
      loadSourceId: day.main.loadSourceId, quantity: '25.000', amount: '250000.0000', dueBusinessDate: '2026-11-30T00:00:00' }),
    expect.objectContaining({ ...originalLine, trip: 'DEV-20261124-A', sourceId: deliverySourceId,
      loadSourceId, quantity: '15.000', amount: '150000.0000', dueBusinessDate: '2026-12-01T00:00:00' }),
  ]))
  expect(ar).toHaveProperty('sources.length', 2)

  await sales.goto('/work/ar?order=' + day.main.orderId + '&trip=' + day.mainTripId)
  await sales.getByLabel('Mã dòng đơn A').fill(day.main.orderLineId)
  await sales.getByRole('button', { name: 'Tải báo cáo', exact: true }).click()
  const arResults = sales.getByRole('region', { name: 'Kết quả báo cáo AR' })
  await expect(arResults.getByText('Mã dòng đơn A đang áp dụng ' + day.main.orderLineId)).toBeVisible()
  await expect(arResults.getByText(/Nguồn đã đối soát 2\/2/)).toBeVisible()
  await sales.screenshot({ path: info.outputPath('same-line-ar-both-days.png') })
  expect(await history(request, day.mainTripId)).toEqual({ closure: day.main.closure, currentOpenWorkCount: 1 })

  await ops.goto('/work/trip-close?trip=' + day.zeroTripId)
  expect((await successors(request, day.zeroTripId, day.zero)).rights).toEqual([])
  const zeroBinding = await assignSuccessor(ops, day.zeroTripId, day.zero.orderLineId, zeroSuccessorTripId, 5)
  const zeroReserve = await reserveSuccessor(kho, day.zeroTripId, day.zero, zeroBinding, warehouseId, 5)
  const zeroLoad = await load(kho, zeroSuccessorTripId, day.zero.orderId, 5)
  const zeroDelivery = await deliver(sales, zeroSuccessorTripId, 5)
  const zeroD4 = await accountingSource(request, zeroSuccessorTripId)
  await receiveAndPost(finance, zeroD4)
  expect(await history(request, day.zeroTripId)).toEqual({ closure: day.zero.closure, currentOpenWorkCount: 0 })

  const reconciler = await open(context, 'RECONCILER', 'finance')
  await reconciler.getByLabel('Từ (gồm)').fill('2026-11-23T00:00:00+07:00')
  await reconciler.getByLabel('Cutoff (loại trừ)').fill('2026-11-24T00:00:00+07:00')
  await reconciler.getByLabel('Đối soát tính đến').fill('2026-11-24T09:00:00+07:00')
  const report = await submit<FinanceReport>(reconciler, accounting + '/reconcile', 'Chạy đối soát')
  expect(report.report.status).toBe('RECONCILED_OK')
  expect(report.lines).toHaveLength(2)
  expect(report.lines).toEqual(expect.arrayContaining([
    expect.objectContaining({ sourceLineId: day.main.accountingSourceLineId, status: 'RECONCILED_OK' }),
    expect.objectContaining({ sourceLineId: day.clean.accountingSourceLineId, status: 'RECONCILED_OK' }),
  ]))
  expect(report.lines.map(row => row.sourceLineId)).not.toContain(mainD4.sourceId)
  expect(report.lines.map(row => row.sourceLineId)).not.toContain(zeroD4.sourceId)
  await expect(reconciler.getByRole('region', { name: 'Kết quả đối soát' })).toContainText(report.report.id)
  await reconciler.screenshot({ path: info.outputPath('only-day-one-reconciled.png') })
  expect((await preview(request, successorTripId)).work).toContainEqual(expect.objectContaining({ sourceId: mainD4.sourceId }))
  expect((await preview(request, zeroSuccessorTripId)).work).toContainEqual(expect.objectContaining({ sourceId: zeroD4.sourceId }))

  await ops.goto('/work/trip-close?trip=' + day.cleanTripId)
  const cleanClosure = await closeTrip(ops, day.cleanTripId)
  expect(cleanClosure).toMatchObject({ status: 'CLOSED', openWorkCount: 0, handoffId: day.clean.handoffSourceId })
  expect(await history(request, day.mainTripId)).toEqual({ closure: day.main.closure, currentOpenWorkCount: 0 })
  expect((await history(request, day.unusedTripId)).closure).toBeNull()
  await ops.goto('/work/app05-work')
  const completedA = ops.getByTestId('app05-work-' + day.main.aFollowUpId)
  const completedD4 = ops.getByTestId('app05-work-' + day.main.accountingFollowUpId)
  await expect(completedA).toContainText('Đã hoàn tất')
  await expect(completedA.getByRole('link', { name: 'Chuyến kế nhiệm DEV-20261124-A' })).toBeVisible()
  await expect(completedD4).toContainText(report.report.id)
  await expect(completedD4).toContainText('Đã hoàn tất')
  await expect(ops.getByTestId('app05-work-' + day.zero.aFollowUpId)).toContainText('Đã hoàn tất')
  await ops.screenshot({ path: info.outputPath('source-work-completed.png') })

  await checkpoint('day-two', {
    successorTripId, zeroSuccessorTripId, reportId: report.report.id, cleanClosure,
    main: { bindingId, successorRightId: transfer.successorRightId, loadSourceId, deliverySourceId, ...posting(mainD4) },
    zero: { bindingId: zeroBinding, reserveSourceId: zeroReserve.sourceId, rightId: zeroReserve.rightId,
      loadSourceId: zeroLoad, deliverySourceId: zeroDelivery, ...posting(zeroD4) },
  })
})
