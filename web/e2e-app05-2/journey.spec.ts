import { access, readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { auth, open } from '../e2e-app03-7/return-refund'
import { accounting, app, c2, c4, d3, get, post, setup, type Closure, type ClosureHistory,
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
  await fixture.reserve(held, 5)
  const unreserved = await fixture.order(zero, 5)
  const before = await read(request, zero)
  expect(before).toMatchObject({ state: 'BLOCKED', canClose: false })
  expect(before.aLines).toEqual(expect.arrayContaining([
    expect.objectContaining({ orderId: held.orderId, held: 5, remaining: 5, loaded: 0 }),
    expect.objectContaining({ orderId: unreserved.orderId, held: 0, remaining: 5, loaded: 0 }),
  ]))
  await checkpoint('shared-pool-fault', { trip: zero, original: 20, fault: 5 })
  const fixtureCheck = await request.post(d3 + '/unload', {
    headers: auth('RECEIVER'),
    data: { producerId: 'f05-receiver', commandId: crypto.randomUUID(),
      sourceNumber: 'APP05-2-POOL-FAULT', tripId: zero },
  })
  expect(fixtureCheck.status(), await fixtureCheck.text()).toBe(200)
  expect(await fixtureCheck.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'LOADED_MANIFEST_NOT_ELIGIBLE' })
  expect(await history(request, zero)).toMatchObject({ closure: null })
  await checkpoint('shared-pool-rejected')
  await checkpoint('before-zero-handoff', { zero, held, unreserved })

  const warehouse = await open(context, 'RECEIVER', 'settlement?trip=' + zero)
  const response = warehouse.waitForResponse(result => result.url().endsWith(d3 + '/unload') && result.request().method() === 'POST')
  await warehouse.getByRole('button', { name: 'Bàn giao để đối soát', exact: true }).click()
  const handoff = await response
  expect(handoff.ok(), await handoff.text()).toBe(true)
  const zeroHandoff = (await handoff.json()).sourceId as string
  expect(zeroHandoff).toBeTruthy()
  const zeroPreview = await read(request, zero)
  expect(zeroPreview).toMatchObject({ state: 'NEEDS_ASSIGNMENT', status: 'RECONCILING', canClose: false })
  expect(zeroPreview.work).toHaveLength(2)
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
  expect(zeroReceipt).toMatchObject({ status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: 2, handoffId: zeroHandoff })
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
  const order = await fixture.order(trip, 3)
  await fixture.reserve(order, 3)
  await fixture.load(trip, 3)
  await fixture.deliver(trip, 2)
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
