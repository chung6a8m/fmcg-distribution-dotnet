import { expect, type APIRequestContext } from '@playwright/test'
import { auth } from '../e2e-app03-7/return-refund'

export const c1 = '/api/synthetic/app02c1'
export const c2 = '/api/synthetic/app02c2'
export const c3 = '/api/synthetic/app02c3'
export const c4 = '/api/synthetic/app02c4'
export const d3 = '/api/synthetic/app02d3'
export const accounting = '/api/synthetic/app02d4/accounting'
export const app = '/api/synthetic/app05/trip-close/trips'
type Choice = { id: string; label: string }
type Catalog = { customers: Choice[]; trips: Choice[]; skus: Choice[]; warehouses: Choice[] }
export type ALine = { orderId: string; orderLineId: string; confirmSourceId: string; loaded: number; held: number; remaining: number }
export type Work = { sourceKind: string; sourceId: string; relatedId: string | null; followUp: { followUpId: string } | null }
export type Preview = { revision: string; state: string; canClose: boolean; status: string; aLines: ALine[]; work: Work[];
  blockers: { code: string }[] }
export type CloseInput = { commandId: string; expectedRevision: string }
export type Closure = { closureId: string; tripId: string; commandId: string; status: string; handoffId: string;
  openWorkCount: number; snapshot: unknown; closedAt: string; closedBy: string }
export type ClosureHistory = { closure: Closure | null; currentOpenWorkCount: number }
type Outcome = { status: string; sourceId: string; reasonCode?: string }

export async function get<T>(request: APIRequestContext, path: string, role = 'OPS'): Promise<T> {
  const result = await request.get(path, { headers: auth(role) })
  expect(result.ok(), path + ' ' + await result.text()).toBe(true)
  return result.json()
}

export async function post<T>(request: APIRequestContext, path: string, data: unknown, role: string): Promise<T> {
  const result = await request.post(path, { headers: auth(role), data })
  expect(result.ok(), path + ' ' + await result.text()).toBe(true)
  return result.json()
}

export async function applied(request: APIRequestContext, path: string, data: unknown, role: string) {
  const outcome = await post<Outcome>(request, path, data, role)
  expect(outcome, path).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })
  return outcome.sourceId
}

export async function setup(request: APIRequestContext) {
  const catalog = await get<Catalog>(request, c2 + '/catalog', 'SOURCE')
  const customerId = catalog.customers.find(choice => choice.label.startsWith('DAY-A-DEV'))!.id
  const skuId = catalog.skus.find(choice => choice.label.startsWith('DAY-A-EA'))!.id
  const locationId = catalog.warehouses.find(choice => choice.label === 'C2-MAIN')!.id
  const trip = (label: string) => catalog.trips.find(choice => choice.label === label)!.id

  async function order(tripId: string, quantity: number) {
    const detail = await get<{ registeredShipTos: { id: string; revision: number }[] }>(
      request, c1 + '/customers/' + customerId, 'SOURCE',
    )
    expect(detail.registeredShipTos).toHaveLength(1)
    const address = detail.registeredShipTos[0]
    const selection = await post<{ id: string }>(request, c1 + '/ship-to-selections', {
      commandId: crypto.randomUUID(), customerId, registeredId: address.id, revision: address.revision,
    }, 'SOURCE')
    const payload = { commandId: crypto.randomUUID(), customerId, tripId, selectionId: selection.id, lines: [{ skuId, quantity }] }
    const orderId = await applied(request, c2 + '/drafts', payload, 'SOURCE')
    const confirm = { commandId: crypto.randomUUID(), orderId }
    const confirmSourceId = await applied(request, c2 + '/confirm', confirm, 'SOURCE')
    const orders = await get<{ id: string; lines: { lineId: string }[] }[]>(request, c2 + '/orders', 'SOURCE')
    const orderLineId = orders.find(candidate => candidate.id === orderId)!.lines[0].lineId
    return { orderId, orderLineId, confirmSourceId, payload, confirm }
  }

  async function reserve(record: Awaited<ReturnType<typeof order>>, quantity: number) {
    const payload = { commandId: crypto.randomUUID(), orderId: record.orderId, lineId: record.orderLineId,
      locationId, quantity, confirmSourceId: record.confirmSourceId }
    const sourceId = await applied(request, c2 + '/reserve', payload, 'RECEIVER')
    return { sourceId, payload }
  }

  async function load(tripId: string, quantity: number) {
    const rights = await get<{ id: string }[]>(request, c3 + '/trips/' + tripId + '/rights', 'RECEIVER')
    expect(rights).toHaveLength(1)
    return applied(request, c3 + '/load', {
      commandId: crypto.randomUUID(), tripId, lines: [{ rightId: rights[0].id, quantity, expectedWarehouseHeld: quantity }],
    }, 'RECEIVER')
  }

  async function deliver(tripId: string, quantity: number) {
    const lines = await get<{ id: string; shipToVersion: number }[]>(request, c4 + '/trips/' + tripId + '/lines', 'SOURCE')
    expect(lines).toHaveLength(1)
    const payload = { commandId: crypto.randomUUID(), tripId, manifestLineId: lines[0].id, shipToVersion: lines[0].shipToVersion, quantity }
    const sourceId = await applied(request, c4 + '/deliver', payload, 'SOURCE')
    return { sourceId, payload }
  }

  async function unload(tripId: string) {
    const commandId = crypto.randomUUID()
    const payload = { producerId: 'f05-receiver', commandId, sourceNumber: 'APP05-2-UNLOAD-' + commandId, tripId }
    const sourceId = await applied(request, d3 + '/unload', payload, 'RECEIVER')
    return { sourceId, payload }
  }

  async function settleAccounting(tripId: string) {
    const preview = await get<Preview>(request, app + '/' + tripId)
    const work = preview.work.find(item => item.sourceKind === 'D4_RECONCILIATION')!
    for (const action of ['receive', 'post'])
      expect(await post<{ sourceLineId: string }>(request, accounting + '/' + work.relatedId + '/' + action, {
        transportId: crypto.randomUUID(),
      }, 'ACCOUNTANT')).toMatchObject({ sourceLineId: work.sourceId })
    return work
  }

  return { trip, order, reserve, load, deliver, unload, settleAccounting }
}
