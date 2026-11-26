import { access, readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { auth, open } from '../e2e-app03-7/return-refund'
import { accounting, app, c2, c3, c4, d3, get, post, setup, type Closure, type ClosureHistory,
  type CloseInput, type Preview, type Work } from './fixture'
import { resolvePendingDelivery, runAccountingSourceRace, verifyCustodyAndOffer } from './source-guards'

const evidence = process.env.APP05_EVIDENCE_DIR!
const assignee = 'app05-assignee'
const dueAt = '2026-11-25T10:00:00+07:00'
const history = (request: APIRequestContext, trip: string) =>
  get<ClosureHistory>(request, app + '/' + trip + '/closure')
const read = (request: APIRequestContext, trip: string) =>
  get<Preview>(request, app + '/' + trip)
const close = (request: APIRequestContext, trip: string, data: CloseInput, role = 'OPS') =>
  request.post(app + '/' + trip + '/close', { headers: auth(role), data })
const closeInput = (expectedRevision: string): CloseInput => ({ commandId: crypto.randomUUID(), expectedRevision })

async function rejectClose(request: APIRequestContext, trip: string, input: CloseInput, code: string) {
  const result = await close(request, trip, input)
  expect(result.status(), await result.text()).toBe(409)
  expect(await result.json()).toMatchObject({ code })
}

function assignmentInput(work: Work, expectedRevision: string, cue?: string, commandId = crypto.randomUUID()) {
  return { commandId, sourceKind: work.sourceKind, sourceId: work.sourceId, assignee, dueAt,
    nextAction: cue ?? 'Xử lý sau chốt synthetic DEV', reason: 'Bàn giao có nguồn APP-05.2', expectedRevision }
}

async function assign(request: APIRequestContext, trip: string, work: Work, cue?: string) {
  const current = await read(request, trip)
  const input = assignmentInput(work, current.revision, cue)
  return { ...await post<{ followUpId: string }>(request, app + '/' + trip + '/follow-ups', input, 'OPS'), commandId: input.commandId }
}

async function checkpoint(name: string, payload?: unknown) {
  if (payload !== undefined)
    await writeFile(join(evidence, name + '.json'), JSON.stringify(payload, null, 2))
  await writeFile(join(evidence, name + '.ready'), '')
  await expect.poll(() => access(join(evidence, name + '.checked')).then(() => true, () => false), {
    timeout: 20_000, message: 'Đối chiếu SQL độc lập ' + name,
  }).toBe(true)
}

async function submitClose(page: Page) {
  await page.getByRole('button', { name: 'Đóng chuyến', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Xác nhận đóng chuyến' })
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: 'Xác nhận đóng chuyến', exact: true }).click()
}

test('day-one closes A15 and accounting work atomically after rollback and a lost ACK', async ({ context, request }, info) => {
  const fixture = await setup(request)
  const main = fixture.trip('DEV-20261123-A')
  const clean = fixture.trip('DEV-20261123-B')
  expect(await history(request, main)).toMatchObject({ closure: null })
  const unused = await read(request, clean)
  expect(unused).toMatchObject({ state: 'UNUSED', canClose: false })
  await rejectClose(request, clean, closeInput(unused.revision), 'TRIP_CLOSE_NOT_READY')

  const order = await fixture.order(main, 40)
  await fixture.reserve(order, 40)
  await fixture.load(main, 40)
  const delivery = await resolvePendingDelivery(request, main, 25, checkpoint)
  const vehicle = await read(request, main)
  expect(vehicle).toMatchObject({ state: 'BLOCKED', canClose: false })
  await rejectClose(request, main, closeInput(vehicle.revision), 'TRIP_CLOSE_NOT_READY')
  const unload = await fixture.unload(main)
  const accountingWork = await fixture.settleAccounting(main)

  const completeOrder = await fixture.order(clean, 1)
  await fixture.reserve(completeOrder, 1)
  await fixture.load(clean, 1)
  const cleanDelivery = await fixture.deliver(clean, 1)
  const cleanUnload = await fixture.unload(clean)
  await fixture.settleAccounting(clean)

  const unassigned = await read(request, main)
  expect(unassigned).toMatchObject({
    state: 'NEEDS_ASSIGNMENT', canClose: false,
    aLines: [expect.objectContaining({ orderId: order.orderId, held: 15, remaining: 15 })],
  })
  expect(unassigned.work).toHaveLength(2)
  const blockedClose = closeInput(unassigned.revision)
  await rejectClose(request, main, blockedClose, 'TRIP_CLOSE_NOT_READY')
  for (const work of unassigned.work) await assign(request, main, work)
  const ready = await read(request, main)
  expect(ready).toMatchObject({ canClose: true, state: 'READY_WITH_WORK' })
  const remainder = ready.work.find(work => work.sourceKind === 'A_REMAINDER')!
  const revised = await assign(request, main, remainder, 'Điều phối 15 EA cho ngày sau')
  await rejectClose(request, main, closeInput(ready.revision), 'READINESS_CHANGED')
  await rejectClose(request, main, blockedClose, 'TRIP_CLOSE_NOT_READY')

  const mainBeforeFault = await read(request, main)
  await checkpoint('main-pool-fault', { trip: main, original: 15, fault: 0 })
  const poolDrift = await read(request, main)
  expect(poolDrift).toMatchObject({ state: 'BLOCKED', canClose: false, status: 'RECONCILING' })
  expect(poolDrift.blockers).toContainEqual(expect.objectContaining({ code: 'MAIN_HELD_SOURCE_DRIFT' }))
  await rejectClose(request, main, closeInput(mainBeforeFault.revision), 'READINESS_CHANGED')
  await rejectClose(request, main, closeInput(poolDrift.revision), 'TRIP_CLOSE_NOT_READY')
  expect(await history(request, main)).toMatchObject({ closure: null })
  await checkpoint('main-pool-rejected')

  const before = await read(request, main)
  expect(before.canClose).toBe(true)
  await rejectClose(request, main, { commandId: revised.commandId, expectedRevision: before.revision }, 'COMMAND_PAYLOAD_CONFLICT')
  const failure = closeInput(before.revision)
  const denied = await close(request, main, failure, 'PEER_OPS')
  expect(denied.status()).toBe(403)
  await checkpoint('arm-failure', { main, clean, failure, order, unload, completeOrder, cleanUnload, cleanDelivery, accountingWork })
  const interrupted = await close(request, main, failure)
  expect(interrupted.status(), await interrupted.text()).toBe(500)
  expect(await history(request, main)).toMatchObject({ closure: null })
  expect(await read(request, main)).toMatchObject({ canClose: true, revision: before.revision, status: 'RECONCILING' })
  await checkpoint('rolled-back')

  const ops = await open(context, 'OPS', 'trip-close?trip=' + main)
  const path = app + '/' + main + '/close'
  let closeCommand: CloseInput | undefined
  let receipt: Closure | undefined
  await ops.route('**' + path, async route => {
    closeCommand = route.request().postDataJSON() as CloseInput
    const result = await route.fetch({ maxRetries: 0 })
    expect(result.ok(), await result.text()).toBe(true)
    receipt = await result.json() as Closure
    await route.abort()
  }, { times: 1 })
  const lost = ops.waitForEvent('requestfailed', request => request.url().endsWith(path))
  await submitClose(ops)
  await lost
  expect(receipt).toMatchObject({ tripId: main, status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: 2, handoffId: unload.sourceId })
  expect(closeCommand?.expectedRevision).toBe(before.revision)
  expect(receipt?.commandId).toBe(closeCommand?.commandId)
  const stored = (await history(request, main)).closure
  expect(stored).toEqual(receipt)
  const replay = await close(request, main, closeCommand!)
  expect(replay.ok(), await replay.text()).toBe(true)
  expect(await replay.json()).toEqual(receipt)
  const otherKind = await request.post(app + '/' + main + '/follow-ups', {
    headers: auth('OPS'), data: assignmentInput(remainder, before.revision, undefined, closeCommand!.commandId),
  })
  expect(otherKind.status()).toBe(409)
  expect(await otherKind.json()).toMatchObject({ code: 'COMMAND_PAYLOAD_CONFLICT' })
  await rejectClose(request, main, { ...closeCommand!, expectedRevision: 'A'.repeat(64) }, 'COMMAND_PAYLOAD_CONFLICT')
  await rejectClose(request, main, closeInput((await read(request, main)).revision), 'TRIP_ALREADY_CLOSED')
  expect((await close(request, main, closeCommand!, 'PEER_OPS')).status()).toBe(403)
  expect((await request.get(app + '/' + main + '/closure', { headers: auth('PEER_OPS') })).status()).toBe(403)

  const late = await request.post(c2 + '/drafts', {
    headers: auth('SOURCE'), data: { ...order.payload, commandId: crypto.randomUUID() },
  })
  expect(await late.json()).toMatchObject({ status: 'REJECTED' })
  const lateDelivery = await request.post(c4 + '/deliver', {
    headers: auth('SOURCE'), data: { ...delivery.payload, commandId: crypto.randomUUID(), quantity: 1 },
  })
  expect(await lateDelivery.json()).toMatchObject({ status: 'REJECTED' })
  await ops.screenshot({ path: info.outputPath('close-response-dropped.png') })
  await ops.close()
  await checkpoint('after-open-close', { receipt, command: closeCommand, delivery })
})

test('day-two recovers the first receipt, closes a reconciled trip and commits a zero-stock handoff', async ({ context, request }, info) => {
  const dayOne = JSON.parse(await readFile(join(evidence, 'arm-failure.json'), 'utf8')) as {
    main: string; clean: string; cleanUnload: { sourceId: string }; accountingWork: Work
    completeOrder: { confirmSourceId: string }
  }
  const { receipt } = JSON.parse(await readFile(join(evidence, 'after-open-close.json'), 'utf8')) as { receipt: Closure }
  const ops = await open(context, 'OPS', 'trip-close?trip=' + dayOne.main)
  const record = ops.locator(`[data-closure-id="${receipt.closureId}"]`)
  await expect(record.getByRole('heading', { name: 'Biên bản đóng chuyến' })).toBeVisible()
  await expect(record.getByTestId('closure-open-at-close')).toHaveText('2')
  await expect(record.getByTestId('closure-open-current')).toHaveText('2')

  const reportId = crypto.randomUUID()
  await post(request, accounting + '/reconcile', {
    requestId: reportId, from: '2026-11-23T00:00:00+07:00', cutoff: '2026-11-24T00:00:00+07:00',
    asOf: '2026-11-24T09:00:00+07:00', unknownScope: false,
  }, 'RECONCILER')
  await ops.reload()
  await expect(record.getByTestId('closure-open-at-close')).toHaveText('2')
  await expect(record.getByTestId('closure-open-current')).toHaveText('1')
  await ops.screenshot({ path: info.outputPath('closure-recovered-next-day.png') })
  expect(await history(request, dayOne.main)).toEqual({ closure: receipt, currentOpenWorkCount: 1 })

  const clean = await read(request, dayOne.clean)
  expect(clean).toMatchObject({ canClose: true, state: 'READY', work: [] })
  await checkpoint('before-clean-close', { reportId })
  const contenders = await Promise.all([
    close(request, dayOne.clean, closeInput(clean.revision)),
    close(request, dayOne.clean, closeInput(clean.revision)),
  ])
  expect(contenders.map(result => result.status()).sort()).toEqual([200, 409])
  expect(await contenders.find(result => result.ok())!.json()).toMatchObject({
    status: 'CLOSED', openWorkCount: 0, handoffId: dayOne.cleanUnload.sourceId,
  })
  expect(await contenders.find(result => !result.ok())!.json()).toMatchObject({ code: 'TRIP_ALREADY_CLOSED' })
  const cleanReceipt = (await history(request, dayOne.clean)).closure!
  await ops.goto('/work/trip-close?trip=' + dayOne.clean)
  const cleanRecord = ops.locator(`[data-closure-id="${cleanReceipt.closureId}"]`)
  await cleanRecord.getByText('Ảnh chốt và nguồn', { exact: true }).click()
  await expect(cleanRecord.getByRole('table', { name: 'Số lượng dòng A lúc đóng' }))
    .toContainText(dayOne.completeOrder.confirmSourceId)
  await expect(cleanRecord.getByRole('heading', { name: 'Phải thu lúc đóng' })).toBeVisible()
  await checkpoint('after-clean-close', { cleanReceipt })

  const fixture = await setup(request)
  const zero = fixture.trip('DEV-20261124-A')
  const unused = await read(request, zero)
  expect(unused).toMatchObject({ state: 'UNUSED', canClose: false })
  const held = await fixture.order(zero, 5)
  const heldQuantity = process.env.APP05_SUCCESSOR_EVIDENCE ? 4 : 5
  await fixture.reserve(held, heldQuantity)
  const unreserved = await fixture.order(zero, 5)
  const shared = process.env.APP05_SUCCESSOR_EVIDENCE ? await fixture.order(zero, 2, true) : undefined
  const before = await read(request, zero)
  expect(before).toMatchObject({ state: 'BLOCKED', canClose: false })
  expect(before.aLines).toEqual(expect.arrayContaining([
    expect.objectContaining({ orderId: held.orderId, held: heldQuantity, remaining: 5, loaded: 0 }),
    expect.objectContaining({ orderId: unreserved.orderId, held: 0, remaining: 5, loaded: 0 }),
  ]))
  await checkpoint('shared-pool-fault', { trip: zero, original: 15 + heldQuantity, fault: 5 })
  const fixtureCheck = await request.post(d3 + '/unload', {
    headers: auth('RECEIVER'),
    data: { producerId: 'f05-receiver', commandId: crypto.randomUUID(),
      sourceNumber: 'APP05-2-POOL-FAULT', tripId: zero },
  })
  expect(fixtureCheck.status(), await fixtureCheck.text()).toBe(200)
  expect(await fixtureCheck.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'LOADED_MANIFEST_NOT_ELIGIBLE' })
  expect(await history(request, zero)).toMatchObject({ closure: null })
  await checkpoint('shared-pool-rejected')
  await checkpoint('before-zero-handoff', { zero, held, unreserved, heldQuantity, shared })

  const warehouse = await open(context, 'RECEIVER', 'settlement?trip=' + zero)
  const response = warehouse.waitForResponse(result => result.url().endsWith(d3 + '/unload') && result.request().method() === 'POST')
  await warehouse.getByRole('button', { name: 'Bàn giao để đối soát', exact: true }).click()
  const handoff = await response
  expect(handoff.ok(), await handoff.text()).toBe(true)
  const zeroHandoff = (await handoff.json()).sourceId as string
  expect(zeroHandoff).toBeTruthy()
  const zeroPreview = await read(request, zero)
  expect(zeroPreview).toMatchObject({ state: 'NEEDS_ASSIGNMENT', status: 'RECONCILING', canClose: false })
  expect(zeroPreview.work).toHaveLength(shared ? 3 : 2)
  for (const work of zeroPreview.work) await assign(request, zero, work)
  const beforeRace = await read(request, zero)
  const competingAssignment = request.post(app + '/' + zero + '/follow-ups', {
    headers: auth('OPS'), data: assignmentInput(beforeRace.work[0], beforeRace.revision, 'Cập nhật hành động APP-05.2'),
  })
  const [assignment, decision] = await Promise.all([competingAssignment, close(request, zero, closeInput(beforeRace.revision))])
  expect([200, 409]).toContain(decision.status())
  if (decision.status() === 409) {
    expect(assignment.status(), await assignment.text()).toBe(200)
    expect(await decision.json()).toMatchObject({ code: 'READINESS_CHANGED' })
    const refreshed = await read(request, zero)
    expect(refreshed.canClose).toBe(true)
    expect((await close(request, zero, closeInput(refreshed.revision))).status()).toBe(200)
  } else {
    expect(assignment.status(), await assignment.text()).toBe(409)
    expect(await assignment.json()).toMatchObject({ code: 'TRIP_ASSIGNMENT_NOT_OPEN' })
  }
  const zeroReceipt = (await history(request, zero)).closure!
  expect(zeroReceipt).toMatchObject({ status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: shared ? 3 : 2, handoffId: zeroHandoff })
  await checkpoint('after-zero-close', { zeroReceipt })
  await checkpoint('revoke-close-scope', { main: dayOne.main })
  expect((await close(request, dayOne.main, { commandId: receipt.commandId,
    expectedRevision: JSON.parse(await readFile(join(evidence, 'after-open-close.json'), 'utf8')).command.expectedRevision,
  })).status()).toBe(403)
  expect((await request.get(app + '/' + dayOne.main + '/closure', { headers: auth('OPS') })).status()).toBe(403)
  await checkpoint('after-revocation')
})

test('day-three blocks unsettled custody and a waiting Close rechecks a concurrent D.4 posting', async ({ context, request }, info) => {
  const fixture = await setup(request)
  const trip = fixture.trip('DEV-20261125-A')
  const successorEvidence = Boolean(process.env.APP05_SUCCESSOR_EVIDENCE)
  const order = await fixture.order(trip, successorEvidence ? 5 : 3)
  await fixture.reserve(order, successorEvidence ? 4 : 3)
  await fixture.load(trip, 3, successorEvidence ? 4 : 3)
  await fixture.deliver(trip, successorEvidence ? 1 : 2)
  if (successorEvidence) await writeFile(join(evidence, 'mixed-reserve-order.json'), JSON.stringify({ trip, order }))
  await verifyCustodyAndOffer(request, trip, checkpoint)
  await fixture.unload(trip)

  const initial = await read(request, trip)
  const finance = initial.work.filter(work => work.sourceKind === 'D4_RECONCILIATION')
  expect(finance).toHaveLength(2)
  expect(initial.work).toHaveLength(3)
  const raced = finance[0]
  for (const work of finance) {
    const path = `${accounting}/${work.relatedId}`
    await post(request, path + '/receive', { transportId: crypto.randomUUID() }, 'ACCOUNTANT')
    if (work !== raced) await post(request, path + '/post', { transportId: crypto.randomUUID() }, 'ACCOUNTANT')
  }
  for (const work of initial.work) await assign(request, trip, work)
  const preview = await read(request, trip)
  expect(preview).toMatchObject({ canClose: true, state: 'READY_WITH_WORK' })
  expect(preview.work.find(item => item.sourceId === raced.sourceId)).toMatchObject({ label: expect.stringContaining('Chờ hạch toán') })
  const receipt = await runAccountingSourceRace(request, trip, raced, preview, checkpoint)
  const ops = await open(context, 'OPS', 'trip-close?trip=' + trip)
  const record = ops.locator(`[data-closure-id="${receipt.closureId}"]`)
  await expect(record.getByTestId('closure-open-at-close')).toHaveText('3')
  await ops.screenshot({ path: info.outputPath('fresh-close-after-accounting-post.png') })
  await checkpoint('after-accounting-source-fresh-close', receipt)
})
type SuccessorRead = {
  followUpId: string; revision: string; remaining: number; unheld: number; unscheduled: number
  bindings: { bindingId: string; destinationTripId: string; quantity: number; transferred: number }[]
  rights: { rightId: string; held: number; neverLoaded: number; returned: number }[]
}
type SourceOrder = { orderLineId: string; confirmSourceId: string }

test('successor schedules frozen A lines and transfers backed HELD with rollback, replay and races', async ({ context, request }, info) => {
  const { main, order } = JSON.parse(await readFile(join(evidence, 'arm-failure.json'), 'utf8')) as { main: string; order: SourceOrder }
  const { zero, held, unreserved, shared } = JSON.parse(await readFile(join(evidence, 'before-zero-handoff.json'), 'utf8')) as {
    zero: string; held: SourceOrder; unreserved: SourceOrder; shared: SourceOrder
  }
  const fixture = await setup(request)
  const first = fixture.trip('DEV-20261126-A')
  const second = fixture.trip('DEV-20261126-B')
  const base = (trip: string, source: SourceOrder) => app + '/' + trip + '/a-remainders/' + source.orderLineId
  const readSuccessors = (trip: string, source: SourceOrder) => get<SuccessorRead>(request, base(trip, source) + '/successors')
  const body = (source: SourceOrder, destinationTripId: string, quantity: number) => ({
    commandId: crypto.randomUUID(), destinationTripId, confirmSourceId: source.confirmSourceId, quantity,
  })
  const send = async (path: string, data: ReturnType<typeof body> & {
    bindingId?: string; originalRightId?: string; followUpId?: string; expectedRevision?: string
  }, role = 'OPS') => {
    if (!data.expectedRevision) {
      const state = await get<SuccessorRead>(request, path.replace(/(assign-successor|transfer-held)$/, 'successors'))
      data = { ...data, followUpId: state.followUpId, expectedRevision: state.revision }
    }
    return request.post(path, { headers: auth(role), data })
  }
  const assignSuccessor = async (path: string, data: ReturnType<typeof body>) => {
    const response = await send(path, data)
    expect(response.status(), await response.text()).toBe(200)
    return await response.json() as { bindingId: string }
  }
  const assignPath = base(main, order) + '/assign-successor'
  const transferPath = base(main, order) + '/transfer-held'
  const heldReport = async () => {
    const report = await get<{ metrics: { metric: string; unit: string; value: string;
      sources: { relatedId: string; value: string }[] }[] }>(request, '/api/synthetic/app03/owner-overview', 'OWNER')
    return report.metrics.find(metric => metric.metric === 'a-hold' && metric.unit === 'EA')!
  }
  const originalReport = await heldReport()
  const totalFor = (report: Awaited<ReturnType<typeof heldReport>>, trip: string) =>
    report.sources.filter(row => row.relatedId === trip).reduce((sum, row) => sum + Number(row.value), 0)
  const prior = (await history(request, main)).closure
  const right = (await readSuccessors(main, order)).rights[0]
  expect(right).toMatchObject({ held: 15, returned: 15, neverLoaded: 0 })
  await checkpoint('successor-before', { main, zero, order, held, unreserved })

  expect((await send(assignPath, body(order, first, 1), 'PEER_OPS')).status()).toBe(403)
  expect((await send(assignPath, body(order, first, 1), 'ASSIGNEE')).status()).toBe(403)
  expect(await (await send(assignPath, body(held, first, 1))).json()).toMatchObject({ code: 'A_SUCCESSOR_SOURCE_INVALID' })
  expect(await (await send(assignPath, body(order, zero, 1))).json()).toMatchObject({ code: 'A_SUCCESSOR_TRIP_NOT_ELIGIBLE' })

  const page = await open(context, 'OPS', 'trip-close?trip=' + main)
  const source = page.locator('[data-source-id="' + order.orderLineId + '"]')
  const assignment = source.getByRole('form', { name: 'Xếp chuyến kế nhiệm' })
  await assignment.getByLabel('Chuyến đích').selectOption(first)
  await assignment.getByLabel('Lượng xếp chuyến (EA)').fill('0000000000000015')
  const assignmentUrl = '**' + assignPath
  const assignRequest = page.waitForRequest(req => req.url().endsWith(assignPath) && req.method() === 'POST')
  await page.route(assignmentUrl, async route => {
    const completed = await route.fetch()
    expect(completed.status()).toBe(200)
    await route.fulfill({ response: completed, json: {} })
  }, { times: 1 })
  await assignment.getByRole('button', { name: 'Xếp chuyến' }).click()
  const assignOriginal = (await assignRequest).postDataJSON()
  expect(assignOriginal.quantity).toBe('0000000000000015')
  await expect(source.getByText('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')).toBeVisible()
  await page.reload()
  const assignReplay = page.waitForResponse(res => res.url().endsWith(assignPath) && res.request().method() === 'POST')
  await source.getByRole('button', { name: 'Gửi lại yêu cầu kế nhiệm' }).click()
  const assigned = await assignReplay
  expect(assigned.status()).toBe(200)
  expect(assigned.request().postDataJSON()).toEqual(assignOriginal)
  await expect(source.getByText('Đã xếp chuyến kế nhiệm.')).toBeVisible()
  const assignedBindings = (await readSuccessors(main, order)).bindings
  expect(assignedBindings).toHaveLength(1)
  const [binding] = assignedBindings
  expect(binding).toMatchObject({ destinationTripId: first, quantity: 15, transferred: 0 })
  expect(await (await send(assignPath, body(order, second, 1))).json()).toMatchObject({ code: 'A_SUCCESSOR_QUANTITY_EXCEEDED' })
  const revision = await readSuccessors(main, order)
  const fault = { ...body(order, first, 5), bindingId: binding.bindingId, originalRightId: right.rightId,
    followUpId: revision.followUpId, expectedRevision: revision.revision }
  expect(await (await send(transferPath, { ...fault, commandId: crypto.randomUUID(), followUpId: crypto.randomUUID() })).json())
    .toMatchObject({ code: 'A_SUCCESSOR_SOURCE_INVALID' })
  expect(await (await send(transferPath, { ...fault, commandId: crypto.randomUUID(), originalRightId: crypto.randomUUID() })).json())
    .toMatchObject({ code: 'A_HELD_RIGHT_INVALID' })
  expect(await (await send(transferPath, { ...fault, commandId: crypto.randomUUID(), quantity: 16 })).json())
    .toMatchObject({ code: 'A_HELD_QUANTITY_EXCEEDED' })

  await checkpoint('successor-arm', { commandId: fault.commandId })
  expect((await send(transferPath, fault)).status()).toBe(500)
  expect((await readSuccessors(main, order)).bindings[0].transferred).toBe(0)
  await checkpoint('successor-rollback')
  const transferred = await (await send(transferPath, fault)).json()
  expect(transferred).toMatchObject({ quantity: 5, returned: 5, neverLoaded: 0 })
  expect(await (await send(transferPath, fault)).json()).toEqual(transferred)
  expect(await (await send(transferPath, { ...fault, quantity: 6 })).json()).toMatchObject({ code: 'COMMAND_PAYLOAD_CONFLICT' })

  await page.reload()
  const transferForm = source.getByRole('form', { name: 'Chuyển HELD' })
  await transferForm.getByLabel('Phần đã xếp').selectOption(binding.bindingId)
  await transferForm.getByLabel('Quyền giữ gốc').selectOption(right.rightId)
  await transferForm.getByLabel('Lượng chuyển HELD (EA)').fill('4')
  const writeUrl = '**' + transferPath
  const originalRequest = page.waitForRequest(req => req.url().endsWith(transferPath) && req.method() === 'POST')
  await page.route(writeUrl, async route => {
    const completed = await route.fetch()
    expect(completed.status(), await completed.text()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await transferForm.getByRole('button', { name: 'Chuyển HELD', exact: true }).click()
  const original = (await originalRequest).postDataJSON() as { commandId: string }
  await expect(source.getByText('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')).toBeVisible()
  await checkpoint('successor-revoke', { tripId: first, commandId: original.commandId })
  expect((await readSuccessors(main, order)).bindings).toEqual([{
    bindingId: null, destinationTripId: null, code: null, quantity: 15, transferred: 9, reserved: 0,
  }])

  const replayButton = source.getByRole('button', { name: 'Gửi lại yêu cầu kế nhiệm' })
  async function replay(status: number) {
    const response = page.waitForResponse(res => res.url().endsWith(transferPath) && res.request().method() === 'POST')
    await replayButton.click()
    const result = await response
    expect(result.status()).toBe(status)
    expect(result.request().postDataJSON()).toEqual(original)
  }

  await page.reload()
  await replay(403)
  await expect(replayButton).toBeEnabled()
  await page.reload()
  await expect(replayButton).toBeEnabled()
  await checkpoint('successor-restore')
  await page.route(writeUrl, async route => {
    const completed = await route.fetch()
    expect(completed.status()).toBe(200)
    await route.fulfill({ status: 409, json: { code: 'A_HELD_QUANTITY_EXCEEDED' } })
  }, { times: 1 })
  await replay(409)
  await expect(replayButton).toBeEnabled()
  await page.reload()
  for (const invalidAck of [
    () => ({}),
    (receipt: Record<string, unknown>) => ({ ...receipt, bindingId: crypto.randomUUID() }),
    (receipt: Record<string, unknown>) => ({
      ...receipt, request: { ...(receipt.request as object), commandId: crypto.randomUUID() },
    }),
    (receipt: Record<string, unknown>) => ({ ...receipt, returned: 3, returnedBase: '3.000' }),
  ]) {
    await page.route(writeUrl, async route => {
      const completed = await route.fetch()
      expect(completed.status()).toBe(200)
      await route.fulfill({ response: completed, json: invalidAck(await completed.json()) })
    }, { times: 1 })
    await replay(200)
    await expect(source.getByText('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')).toBeVisible()
    await page.reload()
  }
  await replay(200)
  await expect(source.getByText('Đã chuyển lượng HELD.')).toBeVisible()
  await expect(source.getByText(/Đã chuyển HELD 9 EA/)).toBeVisible()
  expect((await readSuccessors(main, order)).rights[0].held).toBe(6)
  const updatedReport = await heldReport()
  expect(updatedReport.value).toBe(originalReport.value)
  expect(totalFor(updatedReport, main)).toBe(totalFor(originalReport, main) - 9)
  expect(totalFor(updatedReport, first)).toBe(totalFor(originalReport, first) + 9)

  const unheldPath = base(zero, unreserved) + '/assign-successor'
  await assignSuccessor(unheldPath, body(unreserved, second, 5))
  expect(await readSuccessors(zero, unreserved)).toMatchObject({ remaining: 5, unheld: 5, unscheduled: 0, rights: [] })
  const heldPath = base(zero, held)
  const firstBinding = await assignSuccessor(heldPath + '/assign-successor', body(held, first, 3))
  const secondBinding = await assignSuccessor(heldPath + '/assign-successor', body(held, second, 2))
  const heldState = await readSuccessors(zero, held)
  const heldRight = heldState.rights[0]
  expect(heldRight).toMatchObject({ held: 4, returned: 0, neverLoaded: 4 })
  const intent = (target: string, bindingId: string, quantity: number) => ({
    ...body(held, target, quantity), bindingId, originalRightId: heldRight.rightId,
  })
  const candidates = [intent(first, firstBinding.bindingId, 3), intent(second, secondBinding.bindingId, 2)]
  const race = await Promise.all(candidates.map(candidate => send(heldPath + '/transfer-held', {
    ...candidate, followUpId: heldState.followUpId, expectedRevision: heldState.revision,
  })))
  expect(race.map(response => response.status()).sort()).toEqual([200, 409])
  expect(await race.find(response => !response.ok())!.json()).toMatchObject({ code: 'A_SUCCESSOR_REVISION_CHANGED' })
  const lost = candidates[race.findIndex(response => !response.ok())]
  expect(await (await send(heldPath + '/transfer-held', { ...lost, commandId: crypto.randomUUID() })).json())
    .toMatchObject({ code: 'A_HELD_QUANTITY_EXCEEDED' })
  const winner = await race.find(response => response.ok())!.json() as { quantity: number }
  expect(await readSuccessors(zero, held)).toMatchObject({ unheld: 1, rights: [{ held: 4 - winner.quantity }] })
  expect((await history(request, main)).closure).toEqual(prior)
  await checkpoint('successor-after', { winner: winner.quantity })
  await page.screenshot({ path: info.outputPath('successor-partial-transfer.png') })

  const { trip: mixedTrip, order: mixed } = JSON.parse(await readFile(join(evidence, 'mixed-reserve-order.json'), 'utf8')) as {
    trip: string; order: SourceOrder & { orderId: string }
  }
  const mixedPath = base(mixedTrip, mixed)
  const mixedState = await readSuccessors(mixedTrip, mixed)
  expect(mixedState).toMatchObject({ remaining: 4, unheld: 1, rights: [{ held: 3, neverLoaded: 1, returned: 2 }] })
  const mixedBinding = await assignSuccessor(mixedPath + '/assign-successor', body(mixed, first, 4))
  const transfer = { ...body(mixed, first, 4), bindingId: mixedBinding.bindingId, originalRightId: mixedState.rights[0].rightId }
  expect(await (await send(mixedPath + '/transfer-held', transfer)).json()).toMatchObject({ code: 'A_HELD_QUANTITY_EXCEEDED' })
  const mixedTransfer = await (await send(mixedPath + '/transfer-held', { ...transfer, commandId: crypto.randomUUID(), quantity: 3 })).json()
  expect(mixedTransfer).toMatchObject({ quantity: 3, neverLoaded: 1, returned: 2 })
  expect(await readSuccessors(mixedTrip, mixed)).toMatchObject({ unheld: 1 })

  const catalog = await get<{ warehouses: { id: string; label: string }[]; skus: { id: string; label: string }[] }>(request, c2 + '/catalog', 'RECEIVER')
  const warehouseId = catalog.warehouses.find(w => w.label === 'C2-MAIN')!.id
  const skuId = catalog.skus.find(sku => sku.label.startsWith('DAY-A-EA'))!.id
  const reserveOptions = (path: string) => get<{
    revision: string; followUpId: string; unheld: number; bindings: { bindingId: string; destinationTripId: string; unfilled: number }[]
  }>(request, path + '/reserve-options', 'RECEIVER')
  const makeReserve = async (path: string, original: SourceOrder, bindingId: string, destination: string, quantity: number) => {
    const options = await reserveOptions(path)
    return { ...body(original, destination, quantity), warehouseId, bindingId,
      expectedRevision: options.revision, followUpId: options.followUpId }
  }
  const reserve = (path: string, data: unknown, role = 'RECEIVER') =>
    request.post(path + '/reserve-unheld', { headers: auth(role), data })
  const oversized = await makeReserve(mixedPath, mixed, mixedBinding.bindingId, first, 2)
  expect(await (await reserve(mixedPath, oversized)).json()).toMatchObject({ code: 'A_UNHELD_QUANTITY_EXCEEDED' })
  expect((await reserve(mixedPath, oversized, 'OPS')).status()).toBe(403)
  await checkpoint('reserve-before', { warehouseId, skuId, secondTrip: second, mixedTrip, firstTrip: first, mixedLine: mixed.orderLineId, unreservedLine: unreserved.orderLineId, heldLine: held.orderLineId })

  const originOrderUrl = 'orders?order=' + mixed.orderId + '&trip=' + mixedTrip
  const outOfScopeWarehouse = await open(context, 'SOURCE', originOrderUrl)
  const outOfScopeOrder = outOfScopeWarehouse.locator('article[data-order-id="' + mixed.orderId + '"]')
  await expect(outOfScopeOrder).toBeVisible()
  await expect(outOfScopeOrder.getByRole('button', { name: 'Giữ hàng trên chuyến kế nhiệm' })).toHaveCount(0)
  await outOfScopeWarehouse.close()
  const reservePage = await open(context, 'RECEIVER', originOrderUrl)
  const orderCard = reservePage.locator('[data-order-id="' + mixed.orderId + '"]')
  await orderCard.getByRole('button', { name: 'Giữ hàng trên chuyến kế nhiệm' }).click()
  const reserveForm = orderCard.getByRole('form', { name: 'Giữ bổ sung' })
  await reserveForm.getByLabel('Chuyến kế nhiệm').selectOption(mixedBinding.bindingId)
  await reserveForm.getByLabel('Kho giữ').selectOption(warehouseId)
  await reserveForm.getByLabel('Lượng giữ bổ sung').fill('1')
  const reserveUrl = mixedPath + '/reserve-unheld'
  const optionUrl = mixedPath + '/reserve-options'
  let committed: unknown
  await reservePage.route('**' + reserveUrl, async route => {
    const response = await route.fetch()
    expect(response.status(), await response.text()).toBe(200)
    committed = await response.json()
    await route.abort('failed')
  }, { times: 1 })
  const reserveIntent = reservePage.waitForRequest(req => req.url().endsWith(reserveUrl) && req.method() === 'POST')
  await reserveForm.getByRole('button', { name: 'Giữ bổ sung' }).click()
  const originalReserve = (await reserveIntent).postDataJSON()
  await expect(orderCard.getByText('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')).toBeVisible()

  for (const role of ['SOURCE', 'PEER_WAREHOUSE']) {
    expect((await request.get(optionUrl, { headers: auth(role) })).status()).toBe(403)
    expect((await reserve(mixedPath, originalReserve, role)).status()).toBe(403)
  }
  await checkpoint('reserve-revoke-origin', { commandId: originalReserve.commandId })
  expect((await request.get(optionUrl, { headers: auth('RECEIVER') })).status()).toBe(403)
  expect((await reserve(mixedPath, { ...originalReserve, commandId: crypto.randomUUID() })).status()).toBe(403)
  const repeat = orderCard.getByRole('button', { name: 'Gửi lại yêu cầu giữ bổ sung' })
  async function replayReserve(status: number) {
    const response = reservePage.waitForResponse(res => res.url().endsWith(reserveUrl) && res.request().method() === 'POST')
    await repeat.click()
    const result = await response
    expect(result.status()).toBe(status)
    expect(result.request().postDataJSON()).toEqual(originalReserve)
    return result
  }
  await replayReserve(403)
  await expect(orderCard.getByText('Chưa đủ quyền đọc phần giữ của chuyến kế nhiệm.')).toBeVisible()
  await expect(orderCard.getByText('Không tải được chuyến kế nhiệm.')).toHaveCount(0)
  await reservePage.reload()
  await expect(orderCard.getByText('Yêu cầu giữ bổ sung đang chờ xác nhận. Cần phân công trên chuyến gốc để gửi lại.')).toBeVisible()
  await expect(orderCard.getByRole('button', { name: 'Giữ hàng trên chuyến kế nhiệm' })).toHaveCount(0)

  await checkpoint('reserve-revoke-destination')
  expect((await reserveOptions(mixedPath)).bindings).toEqual([])
  expect((await reserve(mixedPath, originalReserve)).status()).toBe(403)
  expect((await reserve(mixedPath, { ...originalReserve, commandId: crypto.randomUUID() })).status()).toBe(403)
  await checkpoint('reserve-restore')
  await reservePage.reload()
  expect(await (await replayReserve(200)).json()).toEqual(committed)
  const changed = await reserve(mixedPath, { ...originalReserve, quantity: 2 })
  expect(changed.status()).toBe(409)
  expect(await changed.json()).toMatchObject({ code: 'COMMAND_PAYLOAD_CONFLICT' })
  await expect(orderCard.getByText('Đã giữ bổ sung cho chuyến kế nhiệm.')).toBeVisible()
  expect(await readSuccessors(mixedTrip, mixed)).toMatchObject({ unheld: 0, bindings: [{ transferred: 3, reserved: 1 }] })

  const never = base(zero, unreserved)
  const initial = await reserveOptions(never)
  expect(initial).toMatchObject({ unheld: 5, bindings: [{ unfilled: 5 }] })
  const noRight = await makeReserve(never, unreserved, initial.bindings[0].bindingId, second, 5)
  await checkpoint('reserve-arm', { commandId: noRight.commandId })
  expect((await reserve(never, noRight)).status()).toBe(500)
  expect(await reserveOptions(never)).toMatchObject({ unheld: 5, bindings: [{ unfilled: 5, reserved: 0 }] })
  await checkpoint('reserve-rollback')
  const firstReserve = await reserve(never, noRight)
  expect(firstReserve.status()).toBe(200)
  const firstReceipt = await firstReserve.json()
  expect(await (await reserve(never, noRight)).json()).toEqual(firstReceipt)
  expect(await reserveOptions(never)).toMatchObject({ unheld: 0, bindings: [{ unfilled: 0, reserved: 5 }] })

  const heldUnfilled = (await reserveOptions(heldPath)).bindings.find(row => row.unfilled > 0)!
  const lotShortage = await makeReserve(heldPath, held, heldUnfilled.bindingId, heldUnfilled.destinationTripId, 1)
  await checkpoint('reserve-lot-before')
  const noEligibleLots = await reserve(heldPath, lotShortage)
  expect(noEligibleLots.status(), await noEligibleLots.text()).toBe(409)
  expect(await noEligibleLots.json()).toMatchObject({ code: 'F03_LOT_STOCK_INSUFFICIENT' })
  expect(await reserveOptions(heldPath)).toMatchObject({ unheld: 1 })
  await checkpoint('reserve-lot-shortage', { commandId: lotShortage.commandId })
  await checkpoint('reserve-cross-trip')

  const sharedPath = base(zero, shared)
  const sharedBinding = await assignSuccessor(sharedPath + '/assign-successor', body(shared, first, 2))
  const stockA = await makeReserve(sharedPath, shared, sharedBinding.bindingId, first, 1)
  const sharedSkuId = catalog.skus.find(sku => sku.label.startsWith('Z-FIELD-EA'))!.id
  const grantB = (baseQuantity: number, target = second, role = 'SOURCE') => {
    const commandId = crypto.randomUUID()
    return request.post('/api/synthetic/app02d1/quota', { headers: auth(role), data: {
      commandId, sourceNumber: 'APP05-4A-B-' + commandId, producerId: 'ignored',
      tripId: target, stockLocationId: warehouseId, skuId: sharedSkuId, baseQuantity,
    } })
  }
  const [tooMuchA, tooMuchB] = await Promise.all([
    reserve(sharedPath, { ...stockA, commandId: crypto.randomUUID(), quantity: 3 }),
    grantB(10, first, 'RECEIVER'),
  ])
  expect(tooMuchA.status()).toBe(409)
  expect(await tooMuchA.json()).toMatchObject({ code: 'A_UNHELD_QUANTITY_EXCEEDED' })
  expect(await tooMuchB.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'STOCK_INSUFFICIENT' })
  const [holdA, quotaB] = await Promise.all([reserve(sharedPath, stockA), grantB(9)])
  const bOutcome = await quotaB.json() as { status: string; reasonCode?: string }
  expect(Number(holdA.ok()) + Number(bOutcome.status === 'APPLIED')).toBe(1)
  if (holdA.ok()) {
    expect(bOutcome).toMatchObject({ status: 'REJECTED', reasonCode: 'STOCK_INSUFFICIENT' })
    expect(await (await grantB(8)).json()).toMatchObject({ status: 'APPLIED' })
  } else {
    expect(await holdA.json()).toMatchObject({ code: 'STOCK_INSUFFICIENT' })
  }
  const shortage = await makeReserve(sharedPath, shared, sharedBinding.bindingId, first, 1)
  const unavailable = await reserve(sharedPath, shortage)
  expect(unavailable.status()).toBe(409)
  expect(await unavailable.json()).toMatchObject({ code: 'STOCK_INSUFFICIENT' })
  expect(await reserveOptions(sharedPath)).toMatchObject({ unheld: holdA.ok() ? 1 : 2 })
  await checkpoint('reserve-after', { winnerA: holdA.ok(), sharedSkuId, sharedLine: shared.orderLineId })

  const mixedRight = (committed as { rightId: string }).rightId
  const rights = await get<{ id: string; orderId: string; warehouseHeld: string }[]>(
    request, c3 + '/trips/' + first + '/rights', 'RECEIVER')
  expect(rights).toContainEqual(expect.objectContaining({
    id: mixedRight, orderId: mixed.orderId, warehouseHeld: '1.000', originTrip: 'DEV-20261125-A',
  }))
  expect(rights).not.toContainEqual(expect.objectContaining({ id: firstReceipt.rightId }))
  const load = { commandId: crypto.randomUUID(), tripId: first,
    lines: [{ rightId: mixedRight, expectedWarehouseHeld: 1, quantity: 1 }] }
  expect((await request.post(c3 + '/load', { headers: auth('PEER_WAREHOUSE'), data: load })).status()).toBe(403)
  const wrongRight = await request.post(c3 + '/load', { headers: auth('RECEIVER'), data: {
    ...load, commandId: crypto.randomUUID(),
    lines: [{ rightId: firstReceipt.rightId, expectedWarehouseHeld: 5, quantity: 1 }],
  } })
  expect(await wrongRight.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'C3_RESERVATION_NOT_ELIGIBLE' })
  const loaded = await post<{ status: string; sourceId: string }>(request, c3 + '/load', load, 'RECEIVER')
  expect(loaded.status).toBe('APPLIED')
  const lines = await get<{ id: string; orderId: string; shipToVersion: number; available: string }[]>(request, c4 + '/trips/' + first + '/lines', 'SOURCE')
  const line = lines.find(row => row.orderId === mixed.orderId)!
  expect(line).toMatchObject({ available: '1.000', originTrip: 'DEV-20261125-A' })
  const deliveryInput = {
    commandId: crypto.randomUUID(), tripId: first, manifestLineId: line.id, shipToVersion: line.shipToVersion, quantity: 1,
  }
  const staleInput = { ...deliveryInput, commandId: crypto.randomUUID() }
  await checkpoint('successor-delivery-stale', { tripId: first, commandId: staleInput.commandId })
  expect(await get<{ id: string; available: string }[]>(request, c4 + '/trips/' + first + '/lines', 'SOURCE'))
    .toContainEqual(expect.objectContaining({ id: line.id, available: '0.000' }))
  const stale = await request.post(c4 + '/deliver', { headers: auth('SOURCE'), data: staleInput })
  expect(await stale.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'TRIP_NOT_ELIGIBLE' })
  await checkpoint('successor-delivery-current')
  expect((await request.post(c4 + '/deliver', { headers: auth('RECEIVER'), data: deliveryInput })).status()).toBe(403)
  const delivery = await post<{ status: string; sourceId: string }>(request, c4 + '/deliver', deliveryInput, 'SOURCE')
  expect(delivery.status).toBe('APPLIED')
  expect(await post(request, c4 + '/deliver', deliveryInput, 'SOURCE'))
    .toMatchObject({ status: 'APPLIED', sourceId: delivery.sourceId, replayed: true })
  const report = await get<{ sources: { sourceId: string }[] }>(request,
    '/api/synthetic/app02c5/ar?orderLineId=' + mixed.orderLineId, 'SOURCE')
  expect(report.sources).toContainEqual(expect.objectContaining({
    orderId: mixed.orderId, orderLineId: mixed.orderLineId, confirmSourceId: mixed.confirmSourceId,
    originTrip: 'DEV-20261125-A', trip: 'DEV-20261126-A', sourceId: delivery.sourceId, loadSourceId: loaded.sourceId,
    status: 'RECONCILED', quantity: '1.000',
  }))
  await checkpoint('successor-reserve-delivered', {
    orderId: mixed.orderId, orderLineId: mixed.orderLineId, confirmSourceId: mixed.confirmSourceId,
    originTripId: mixedTrip, destinationTripId: first, bindingId: mixedBinding.bindingId,
    rightId: mixedRight, manifestLineId: line.id, deliverySourceId: delivery.sourceId, loadSourceId: loaded.sourceId,
  })
  await checkpoint('successor-ar-destination-only')
  const scoped = await open(context, 'SOURCE', 'ar?order=' + mixed.orderId + '&trip=' + mixedTrip)
  await expect(scoped.getByLabel('Mã đơn A')).toHaveValue(mixed.orderId)
  await expect(scoped.getByRole('region', { name: 'Kết quả báo cáo AR' }).getByText(/Nguồn đã đối soát 1\/1/)).toBeVisible()
  expect(await get<{ sources: { sourceId: string }[] }>(request,
    '/api/synthetic/app02c5/ar?orderId=' + mixed.orderId, 'SOURCE')).toMatchObject({
    expected: 1, reconciled: 1, sources: [{ sourceId: delivery.sourceId }],
  })
  await scoped.close()
  await checkpoint('successor-ar-origins-restored')

  const transferredRights = await get<{ id: string; warehouseHeld: string; originTrip?: string }[]>(
    request, c3 + '/trips/' + first + '/rights', 'RECEIVER')
  expect(transferredRights).toContainEqual(expect.objectContaining({
    id: mixedTransfer.successorRightId, warehouseHeld: '3.000', originTrip: 'DEV-20261125-A',
  }))
  const heldLoad = await post<{ status: string; sourceId: string }>(request, c3 + '/load', {
    commandId: crypto.randomUUID(), tripId: first,
    lines: [{ rightId: mixedTransfer.successorRightId, expectedWarehouseHeld: 3, quantity: 3 }],
  }, 'RECEIVER')
  expect(heldLoad.status).toBe('APPLIED')
  const heldLines = await get<{ id: string; orderId: string; shipToVersion: number; available: string }[]>(
    request, c4 + '/trips/' + first + '/lines', 'SOURCE')
  const heldLine = heldLines.find(row => row.orderId === mixed.orderId && row.available === '3.000')!
  const heldDelivery = await post<{ status: string; sourceId: string }>(request, c4 + '/deliver', {
    commandId: crypto.randomUUID(), tripId: first, manifestLineId: heldLine.id, shipToVersion: heldLine.shipToVersion, quantity: 3,
  }, 'SOURCE')
  expect(heldDelivery.status).toBe('APPLIED')
  expect(await get<{ sources: { sourceId: string }[] }>(request,
    '/api/synthetic/app02c5/ar?orderLineId=' + mixed.orderLineId, 'SOURCE')).toMatchObject({
    expected: 3, reconciled: 3, sources: expect.arrayContaining([expect.objectContaining({
      sourceId: heldDelivery.sourceId, originTrip: 'DEV-20261125-A', trip: 'DEV-20261126-A', quantity: '3.000',
    })]),
  })
  expect((await read(request, mixedTrip)).work).not.toContainEqual(expect.objectContaining({
    sourceKind: 'A_REMAINDER', sourceId: mixed.orderLineId,
  }))

  const mainRemainder = await readSuccessors(main, order)
  const finalTransfer = await (await send(transferPath, {
    ...body(order, first, 6), bindingId: binding.bindingId, originalRightId: right.rightId,
    followUpId: mainRemainder.followUpId, expectedRevision: mainRemainder.revision,
  })).json()
  expect(finalTransfer).toMatchObject({ quantity: 6, returned: 6, neverLoaded: 0 })
  const recoveredFour = await (await request.post(transferPath, { headers: auth('OPS'), data: original })).json()
  expect(recoveredFour).toMatchObject({ quantity: 4, returned: 4 })
  const mainHeld = [transferred, recoveredFour, finalTransfer] as { successorRightId: string; quantity: number }[]
  expect((await post<{ status: string }>(request, c3 + '/load', {
    commandId: crypto.randomUUID(), tripId: first,
    lines: mainHeld.map(row => ({ rightId: row.successorRightId, expectedWarehouseHeld: row.quantity, quantity: row.quantity })),
  }, 'RECEIVER')).status).toBe('APPLIED')
  const mainLines = (await get<{ id: string; originTrip?: string; shipToVersion: number; available: string }[]>(
    request, c4 + '/trips/' + first + '/lines', 'SOURCE')).filter(row => row.originTrip === 'DEV-20261123-A')
  expect(mainLines).toHaveLength(3)
  const mainDeliveries = []
  for (const [index, row] of mainLines.entries()) {
    const attempt = () => request.post(c4 + '/deliver', { headers: auth('SOURCE'), data: {
      commandId: crypto.randomUUID(), tripId: first, manifestLineId: row.id, shipToVersion: row.shipToVersion, quantity: Number(row.available),
    } })
    const results = await Promise.all(Array.from({ length: index === 0 ? 2 : 1 },
      async () => await (await attempt()).json() as { status: string; sourceId: string; reasonCode?: string }))
    const applied = results.filter(result => result.status === 'APPLIED')
    expect(applied).toHaveLength(1)
    if (index === 0) expect(['C4_EXISTING_DELIVERY_PENDING', 'C4_QUANTITY_NOT_AVAILABLE',
      'C4_LOADED_LINE_NOT_DELIVERABLE']).toContain(results.find(result => result.status === 'REJECTED')?.reasonCode)
    mainDeliveries.push(applied[0].sourceId)
  }
  const originHistory = await history(request, main)
  expect(originHistory.closure).toEqual(prior)
  expect((await read(request, main)).work).not.toContainEqual(expect.objectContaining({
    sourceKind: 'A_REMAINDER', sourceId: order.orderLineId,
  }))
  expect(await get<{ sources: { sourceId: string }[] }>(request,
    '/api/synthetic/app02c5/ar?orderLineId=' + order.orderLineId, 'SOURCE')).toMatchObject({
    reconciled: 4, sources: expect.arrayContaining(mainDeliveries.map(sourceId =>
      expect.objectContaining({ sourceId, originTrip: 'DEV-20261123-A', trip: 'DEV-20261126-A' }))),
  })
  await checkpoint('successor-held-delivered', {
    mainLineId: order.orderLineId, mixedLineId: mixed.orderLineId, successorTripId: first,
    mainBindingId: binding.bindingId, mixedBindingId: mixedBinding.bindingId, mixedRightId: mixedTransfer.successorRightId,
  })
})
