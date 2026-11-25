import { expect, type APIRequestContext } from '@playwright/test'
import { auth } from '../e2e-app03-7/return-refund'
import { accounting, app, c4, d3, get, post, type CloseInput, type Closure, type ClosureHistory, type Preview, type Work } from './fixture'

type Checkpoint = (name: string, payload?: unknown) => Promise<void>
type Outcome = { status: string; sourceId: string | null; reasonCode?: string }
type ClosePreview = Preview & { blockers: { code: string; sourceId?: string }[] }

const closePath = (trip: string) => app + '/' + trip + '/close'
const read = (request: APIRequestContext, trip: string) => get<ClosePreview>(request, app + '/' + trip)
const history = (request: APIRequestContext, trip: string) => get<ClosureHistory>(request, app + '/' + trip + '/closure')

async function rejectedClose(request: APIRequestContext, trip: string, input: CloseInput, code: string) {
  const reply = await request.post(closePath(trip), { headers: auth('OPS'), data: input })
  expect(reply.status(), await reply.text()).toBe(409)
  expect(await reply.json()).toMatchObject({ code })
  expect((await history(request, trip)).closure).toBeNull()
}

async function rejectedUnload(request: APIRequestContext, tripId: string, reasonCode: string) {
  const commandId = crypto.randomUUID()
  const outcome = await post<Outcome>(request, d3 + '/unload', {
    producerId: 'f05-receiver', commandId, sourceNumber: 'APP05-RF01-' + commandId, tripId,
  }, 'RECEIVER')
  expect(outcome).toMatchObject({ status: 'REJECTED', reasonCode })
}

/** Interrupt exact posting after real C.4 intake. The second probe simulates the reserved PENDING value on that same intake row. */
export async function resolvePendingDelivery(request: APIRequestContext, tripId: string, quantity: number, checkpoint: Checkpoint) {
  const lines = await get<{ id: string; shipToVersion: number }[]>(request, `${c4}/trips/${tripId}/lines`, 'SOURCE')
  expect(lines).toHaveLength(1)
  const payload = { commandId: crypto.randomUUID(), tripId, manifestLineId: lines[0].id, shipToVersion: lines[0].shipToVersion, quantity }
  await checkpoint('arm-delivery-post-fault', payload)
  const failed = await request.post(c4 + '/deliver', { headers: auth('SOURCE'), data: payload })
  expect(failed.status()).toBe(500)

  const received = await blocked()
  await checkpoint('received-delivery-blocked', { evidenceId: received })
  const pending = await blocked()
  expect(pending).toBe(received)
  await checkpoint('pending-delivery-blocked', { evidenceId: pending })

  const applied = await post<Outcome>(request, c4 + '/deliver', payload, 'SOURCE')
  expect(applied).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })
  const replay = await post<Outcome>(request, c4 + '/deliver', payload, 'SOURCE')
  expect(replay).toMatchObject({ status: 'APPLIED', sourceId: applied.sourceId, replayed: true })
  expect((await read(request, tripId)).blockers.map(item => item.code)).not.toContain('DELIVERY_OUTCOME_PENDING')
  await checkpoint('delivery-resolved', { evidenceId: received, sourceId: applied.sourceId })
  return { sourceId: applied.sourceId!, payload }

  async function blocked() {
    const preview = await read(request, tripId)
    expect(preview).toMatchObject({ state: 'BLOCKED', canClose: false })
    const matches = preview.blockers.filter(item => item.code === 'DELIVERY_OUTCOME_PENDING')
    expect(matches).toHaveLength(1)
    expect(matches[0].sourceId).toBeTruthy()
    await rejectedUnload(request, tripId, 'C4_DELIVERY_PENDING')
    await rejectedClose(request, tripId, { commandId: crypto.randomUUID(), expectedRevision: preview.revision }, 'TRIP_CLOSE_NOT_READY')
    return matches[0].sourceId!
  }
}

export async function verifyCustodyAndOffer(request: APIRequestContext, tripId: string, checkpoint: Checkpoint) {
  const b = '/api/synthetic/app02d2'
  const { lines, customers, verifiedCash } = await get<{
    lines: { tripManifestLineId: string; skuId: string }[];
    customers: { customerId: string; label: string; selections: { selectionId: string; shipToSnapshotId: string; outletId: string | null }[] }[];
    verifiedCash: { evidenceRef: string; amount: number }[];
  }>(request, b + '/trips/' + tripId, 'SOURCE')
  expect(lines).toHaveLength(1)
  const customer = customers.find(item => item.label.startsWith('C2-FIXTURE'))!
  const selection = customer.selections[0]
  const cash = verifiedCash.find(item => item.evidenceRef === 'APP03-CASH-DEV-20261125-A-1')!
  const saleCommand = crypto.randomUUID()
  const sale = await post<Outcome>(request, b + '/sales', {
    producerId: 'c1-dev-sales', commandId: saleCommand, sourceNumber: 'APP05-RF01-' + saleCommand,
    tripId, customerId: customer.customerId, selectionId: selection.selectionId, shipToSnapshotId: selection.shipToSnapshotId,
    outletId: selection.outletId, tripManifestLineId: lines[0].tripManifestLineId, skuId: lines[0].skuId,
    baseQuantity: 1, receivedCashAmount: cash.amount,
    verificationEvidenceRef: cash.evidenceRef,
  }, 'SOURCE')
  expect(sale).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })

  await checkpoint('before-custody-probes', { tripId, saleSource: sale.sourceId, amount: cash.amount })
  const custody = await read(request, tripId)
  expect(custody.blockers.map(item => item.code)).toContain('CUSTODY_OUTSTANDING')
  await rejectedUnload(request, tripId, 'CUSTODY_NOT_SETTLED')
  await rejectedClose(request, tripId, { commandId: crypto.randomUUID(), expectedRevision: custody.revision }, 'TRIP_CLOSE_NOT_READY')
  await checkpoint('custody-blocked')

  const offerCommand = crypto.randomUUID()
  const offer = await post<Outcome>(request, d3 + '/offer', {
    producerId: 'c1-dev-sales', commandId: offerCommand, sourceNumber: 'APP05-RF01-' + offerCommand,
    tripId, offeredAmount: cash.amount,
  }, 'SOURCE')
  expect(offer).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })
  await checkpoint('before-offer-probes', { offerId: offer.sourceId })
  const offered = await read(request, tripId)
  expect(offered.blockers.map(item => item.code)).toEqual(expect.arrayContaining(['CASH_OFFER_PENDING', 'CUSTODY_OUTSTANDING']))
  await rejectedUnload(request, tripId, 'CASH_HANDOVER_PENDING')
  await rejectedClose(request, tripId, { commandId: crypto.randomUUID(), expectedRevision: offered.revision }, 'TRIP_CLOSE_NOT_READY')
  await checkpoint('offer-blocked')

  const countCommand = crypto.randomUUID()
  const accepted = await post<Outcome>(request, d3 + '/accept', {
    producerId: 'app03-cashier', commandId: countCommand, sourceNumber: 'APP05-RF01-' + countCommand,
    tripId, offerId: offer.sourceId, countedAmount: cash.amount,
  }, 'CASHIER')
  expect(accepted).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })
  const settled = await read(request, tripId)
  expect(settled.blockers.map(item => item.code)).not.toContain('CASH_OFFER_PENDING')
  expect(settled.blockers.map(item => item.code)).not.toContain('CUSTODY_OUTSTANDING')
  return { acceptanceId: accepted.sourceId, offerId: offer.sourceId }
}

export async function runAccountingSourceRace(request: APIRequestContext, tripId: string, work: Work,
  before: Preview, checkpoint: Checkpoint) {
  const input = { commandId: crypto.randomUUID(), expectedRevision: before.revision }
  await checkpoint('arm-accounting-source-race', { tripId, input, work })
  const competingClose = request.post(closePath(tripId), { headers: auth('OPS'), data: input })
  await checkpoint('competing-close-waits')
  const posting = await post<{ id: string; sourceLineId: string }>(request, `${accounting}/${work.relatedId}/post`, {
    transportId: crypto.randomUUID(),
  }, 'ACCOUNTANT')
  expect(posting.sourceLineId).toBe(work.sourceId)
  await checkpoint('concurrent-accounting-posted', { postingId: posting.id })
  const refused = await competingClose
  expect(refused.status(), await refused.text()).toBe(409)
  expect(await refused.json()).toMatchObject({ code: 'READINESS_CHANGED' })
  expect((await history(request, tripId)).closure).toBeNull()
  const current = await read(request, tripId)
  expect(current.revision).not.toBe(before.revision)
  expect(current.canClose).toBe(true)
  expect(current.work.find(row => row.sourceId === work.sourceId)).toMatchObject({ label: expect.stringContaining('Chờ cutoff') })
  await checkpoint('accounting-source-close-refused')
  const receipt = await post<Closure>(request, closePath(tripId), { commandId: crypto.randomUUID(), expectedRevision: current.revision }, 'OPS')
  expect(receipt).toMatchObject({ status: 'CLOSED_WITH_OPEN_CASES', openWorkCount: 3 })
  return receipt
}
