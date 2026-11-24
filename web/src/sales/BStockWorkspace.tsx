import { useEffect, useEffectEvent, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02d1'

type Option = { id: string; label: string }
type Trip = Option & { canLoad: boolean; canGrantCredit: boolean }
type ShipTo = Option & { customerId: string }
type Credit = {
  id: string; tripId: string; customerId: string; shipToSnapshotId: string
  approvalId: string; approvalVersion: string; granted: string; consumed: string
}
type Catalog = Identity & {
  warehouse: boolean; finance: boolean; trips: Trip[]; warehouses: Option[]; skus: Option[]
  customers: Option[]; shipTos: ShipTo[]; creditGrants: Credit[]
}
type Right = {
  id: string; warehouseId: string; warehouseCode: string; skuId: string; skuCode: string
  baseUom: string; granted: string; loaded: string; warehouseHeld: string
}
type Kind = 'quota' | 'credit' | 'load'
type Pending = { commandId: string; tripId: string; kind: Kind; body: string }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }
type Session = { token: string; catalog: Catalog; tripId: string; rights: Right[] }
const applied: Record<Kind, string> = { quota: 'Đã cấp quota B.', credit: 'Đã cấp hạn mức B.', load: 'Đã nạp hàng B.' }
const denied: Record<string, string> = {
  STOCK_INSUFFICIENT: 'Kho không đủ tồn khả dụng để cấp quota.',
  CREDIT_LIMIT_INSUFFICIENT: 'Hạn mức được duyệt còn lại không đủ.',
  CREDIT_APPROVAL_REQUIRED: 'Chưa có quyết định hạn mức đang có hiệu lực.',
  CREDIT_APPROVAL_AMBIGUOUS: 'Có nhiều quyết định hạn mức cùng hiệu lực.',
  C3_STALE_WAREHOUSE_HELD: 'Hàng giữ tại kho đã thay đổi. Hãy kiểm tra số liệu mới.',
  C3_WAREHOUSE_STOCK_INSUFFICIENT: 'Kho không đủ hàng giữ để nạp.',
}

function pendingKey(identity: Identity) {
  return 'app02d1.pending.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function restore(identity: Identity): Pending | null {
  try {
    const raw = pendingJson(pendingKey(identity))
    if (!raw) return null
    const command = JSON.parse(raw) as Pending
    return ['quota', 'credit', 'load'].includes(command.kind) &&
      /^[a-f0-9-]{32,36}$/i.test(command.commandId) && /^[a-f0-9-]{36}$/i.test(command.tripId) &&
      typeof command.body === 'string' && JSON.parse(command.body).commandId === command.commandId ? command : null
  } catch { return null }
}

async function read<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

async function open(token: string, catalog: Catalog, preferredTripId?: string): Promise<Session> {
  const trip = catalog.trips.find(candidate => candidate.id === preferredTripId) ?? catalog.trips[0]
  return { token, catalog, tripId: trip?.id ?? '', rights: trip?.canLoad
    ? await read<Right[]>(token, `/trips/${trip.id}/load-rights`) : [] }
}

function displayAmount(amount: string | number) {
  return String(amount).includes('.') ? String(amount).replace(/0+$/, '').replace(/\.$/, '') : String(amount)
}

function fieldValue(data: FormData, key: string) {
  return String(data.get(key) ?? '').trim()
}

function positive(value: string, scale: number) {
  return new RegExp(`^\\d{1,15}(\\.\\d{1,${scale}})?$`).test(value) && Number(value) > 0
}

function Choices({ name, label, items, value, onChange }: {
  name: string; label: string; items: Option[]; value?: string; onChange?: (value: string) => void
}) {
  const id = useId()
  return <div className="field">
    <label htmlFor={id}>{label}</label>
    <select id={id} name={name} required value={value} onChange={onChange ? event => onChange(event.currentTarget.value) : undefined}>
      {!items.length && <option value="">Không có dữ liệu</option>}
      {items.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}
    </select>
  </div>
}

export function BStockWorkspace({ workspaceToken, startTrip, onTripChange }: {
  workspaceToken?: string; startTrip?: string; onTripChange?: (id: string) => void
} = {}) {
  const [session, setSession] = useState<Session | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [offline, setOffline] = useState(!navigator.onLine)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [source, setSource] = useState('')
  const [customer, setCustomer] = useState('')
  const [formVersion, setFormVersion] = useState(0)
  const active = useRef(false)

  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function work(action: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await action() }
    catch (error) {
      setNotice(error instanceof HttpError && [401, 403].includes(error.status)
        ? 'Phiên DEV hoặc quyền hiện hành không hợp lệ.'
        : 'Không tải được dữ liệu D.1. Hãy thử tải lại.')
    } finally { active.current = false; setBusy(false) }
  }

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = fieldValue(new FormData(event.currentTarget), 'token')
    event.currentTarget.reset()
    connectToken(token)
  }

  function connectToken(token: string) {
    void work(async () => {
      const catalog = await read<Catalog>(token, '/catalog')
      const saved = restore(catalog)
      const selected = await open(token, catalog, saved?.tripId ?? startTrip)
      setSession(selected)
      if (selected.tripId) onTripChange?.(selected.tripId)
      setPending(saved)
      setCustomer(catalog.customers[0]?.id ?? '')
      setSource('')
    })
  }

  const openWorkspace = useEffectEvent(connectToken)
  useEffect(() => {
    if (workspaceToken) openWorkspace(workspaceToken)
  }, [workspaceToken])

  async function refresh(previous: Session, tripId = previous.tripId) {
    const catalog = await read<Catalog>(previous.token, '/catalog')
    const selected = await open(previous.token, catalog, tripId)
    setSession(selected)
    if (selected.tripId) onTripChange?.(selected.tripId)
    setCustomer(id => catalog.customers.some(item => item.id === id) ? id : catalog.customers[0]?.id ?? '')
    setFormVersion(version => version + 1)
  }

  async function resolve(command: Pending, lookup: boolean) {
    if (!session) return
    const current = session
    await work(async () => {
      if (!lookup) {
        rememberPending(pendingKey(current.catalog), command)
        setPending(command)
      }
      setSource('')
      let outcome: Outcome
      try {
        const group = command.kind === 'load' ? 'commands' : 'grants'
        const response = lookup
          ? await requestSalesApi(api, current.token, `/trips/${command.tripId}/${group}/${command.commandId}`)
          : await requestSalesApi(api, current.token, '/' + command.kind, command.body)
        if (lookup && response.status === 404) {
          setNotice('Chưa có kết quả. Có thể gửi lại cùng yêu cầu.')
          return
        }
        if (![200, 400, 403, 409, 422].includes(response.status)) throw new HttpError(response.status)
        outcome = await response.json() as Outcome
        if (outcome.status === 'CONFLICT') {
          setNotice('Mã yêu cầu đã dùng cho nội dung khác. Hãy tra cứu kết quả gốc.')
          return
        }
        if (!(outcome.status === 'APPLIED' && outcome.sourceId) && outcome.status !== 'REJECTED')
          throw new Error('Unknown command outcome')
      } catch {
        setNotice('Chưa rõ kết quả hoặc quyền đã thay đổi. Hãy tra cứu trước khi gửi yêu cầu mới.')
        return
      }
      setPending(null)
      forgetPending(pendingKey(current.catalog), command.commandId)
      if (outcome.sourceId) setSource(outcome.sourceId)
      const message = outcome.status === 'APPLIED' ? applied[command.kind]
        : denied[outcome.reasonCode ?? ''] ?? `Yêu cầu bị từ chối${outcome.reasonCode ? ' · ' + outcome.reasonCode : ''}.`
      try { await refresh(current) }
      catch { setNotice(message + ' Hãy tải lại số liệu.'); return }
      setNotice(message)
    })
  }

  function submit(event: FormEvent<HTMLFormElement>, kind: Kind) {
    event.preventDefault()
    if (!session || busy || pending || !navigator.onLine) return
    const { tripId, catalog, rights } = session
    const trip = catalog.trips.find(item => item.id === tripId)
    if (!trip || (kind === 'credit' ? !trip.canGrantCredit : !trip.canLoad)) return
    const data = new FormData(event.currentTarget)
    const amount = fieldValue(data, 'amount')
    if (!positive(amount, kind === 'credit' ? 4 : 3) || (workspaceToken && !Number.isInteger(Number(amount)))) {
      setNotice('Nhập số lượng hoặc số tiền dương với đúng đơn vị.')
      return
    }
    const id = crypto.randomUUID()
    const commandId = kind === 'load' ? id : id.replaceAll('-', '')
    const grant = { producerId: catalog.actorId, commandId, sourceNumber: 'D1-' + commandId, tripId }
    if (kind === 'quota') {
      const stockLocationId = fieldValue(data, 'warehouse')
      const skuId = fieldValue(data, 'sku')
      if (!stockLocationId || !skuId) return
      void resolve({ kind, commandId, tripId, body: JSON.stringify({ ...grant, stockLocationId, skuId, baseQuantity: amount }) }, false)
    } else if (kind === 'credit') {
      const shipToSnapshotId = fieldValue(data, 'shipTo')
      if (!customer || !shipToSnapshotId) return
      void resolve({ kind, commandId, tripId,
        body: JSON.stringify({ ...grant, customerId: customer, shipToSnapshotId, poolCode: 'B',
          currencyUnit: 'SYNTHETIC_UNITLESS', amount }) }, false)
    } else {
      const right = rights.find(item => item.id === fieldValue(data, 'right'))
      if (!right) return
      const lines = [{ rightId: right.id, expectedWarehouseHeld: right.warehouseHeld, quantity: amount }]
      void resolve({ kind, commandId, tripId, body: JSON.stringify({ commandId, tripId, lines }) }, false)
    }
  }

  const off = busy || offline
  const trip = session?.catalog.trips.find(item => item.id === session.tripId)
  const eligible = session?.rights.filter(right => Number(right.warehouseHeld) > 0) ?? []
  const Page = workspaceToken ? 'section' : 'main'
  return <Page className={workspaceToken ? 'sales-pane' : 'page c1-page sales-pane'}>
    {!workspaceToken ? <header className="page-heading c1-header">
      <div><h1>Quota, hạn mức và nạp hàng B</h1><p>DEV tổng hợp{session && ' · ' + session.catalog.tenantId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales">Workspace Sales A</a>
        {session && <Button disabled={busy} onClick={() => { setSession(null); setPending(null); setNotice(''); setSource('') }}>Đổi phiên DEV</Button>}</div>
    </header> : <h2>Quota và nạp hàng B</h2>}
    {!session ? workspaceToken ? <Button disabled={off} onClick={() => connectToken(workspaceToken)}>Tải dữ liệu kho B</Button>
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button variant="primary" type="submit" disabled={off}>Kết nối</Button>
    </form> : <>
      <section className="panel panel-content" aria-label="Chuyến và trạng thái">
        <fieldset className="sales-filter" disabled={off || !!pending}>
          <Choices name="trip" label="Chuyến" value={session.tripId} items={session.catalog.trips}
            onChange={id => { void work(() => refresh(session, id)) }} />
          <Button onClick={() => { void work(() => refresh(session)) }}>Tải lại dữ liệu</Button>
        </fieldset>
        {!trip && <p>Chưa có chuyến được phân công.</p>}
        {pending && <div className="button-row">
          <Button disabled={off} onClick={() => { void resolve(pending, true) }}>Tra cứu yêu cầu</Button>
          <Button disabled={off} onClick={() => { void resolve(pending, false) }}>Gửi lại cùng yêu cầu</Button>
        </div>}
      </section>
      {trip?.canLoad && <>
        <section className="panel panel-content" aria-label="Cấp quota B">
          <h2>Cấp quota B</h2>
          <form key={formVersion} onSubmit={event => submit(event, 'quota')}>
            <fieldset className="sales-filter" disabled={off || !!pending}>
              <Choices name="warehouse" label="Kho cấp quota" items={session.catalog.warehouses} />
              <Choices name="sku" label="SKU" items={session.catalog.skus} />
              <Field name="amount" label="Số lượng quota" type="number" min={workspaceToken ? '1' : '0.001'} step={workspaceToken ? '1' : '0.001'} required />
              <Button variant="primary" type="submit">Cấp quota B</Button>
            </fieldset>
          </form>
        </section>
        <section className="panel panel-content" aria-label="Nạp hàng B">
          <h2>Nạp hàng B</h2>
          {session.rights.length ? <ul className="c1-rows">{session.rights.map(right => <li key={right.id}>
            <div><strong>{right.warehouseCode} · {right.skuCode}</strong><p>
              Quota {displayAmount(right.granted)} {right.baseUom} · Còn giữ tại kho {displayAmount(right.warehouseHeld)} {right.baseUom}
              {' · '}Đã nạp {displayAmount(right.loaded)} {right.baseUom}
            </p></div>
          </li>)}</ul> : <p>Chưa có quota để nạp.</p>}
          {!!eligible.length && <form key={formVersion} onSubmit={event => submit(event, 'load')}>
            <fieldset className="sales-filter" disabled={off || !!pending}>
              <Choices name="right" label="Quota nạp" items={eligible.map(right => ({
                id: right.id, label: `${right.warehouseCode} · ${right.skuCode} · ${displayAmount(right.warehouseHeld)} ${right.baseUom}`,
              }))} />
              <Field name="amount" label="Số lượng nạp B" type="number" min={workspaceToken ? '1' : '0.001'} step={workspaceToken ? '1' : '0.001'} required />
              <Button variant="primary" type="submit">Nạp hàng B</Button>
            </fieldset>
          </form>}
        </section>
      </>}
      {trip?.canGrantCredit && <section className="panel panel-content" aria-label="Cấp hạn mức B">
        <h2>Cấp hạn mức B</h2>
        <form key={formVersion} onSubmit={event => submit(event, 'credit')}>
          <fieldset className="sales-filter" disabled={off || !!pending}>
            <Choices name="customer" label="Khách hàng" items={session.catalog.customers} value={customer} onChange={setCustomer} />
            <Choices key={customer} name="shipTo" label="Địa chỉ giao đã chốt"
              items={session.catalog.shipTos.filter(item => item.customerId === customer)} />
            <Field name="amount" label="Số tiền cấp" type="number" min="0.0001" step="0.0001" required />
            <Button variant="primary" type="submit">Cấp hạn mức B</Button>
          </fieldset>
        </form>
        <ul className="c1-rows">{session.catalog.creditGrants.filter(grant => grant.tripId === trip.id).map(grant => <li key={grant.id}>
          <div><strong>Đã cấp {displayAmount(grant.granted)} · Đã dùng {displayAmount(grant.consumed)}</strong>
            <p className="c3-source">Phiên bản duyệt {grant.approvalVersion} · {grant.approvalId}</p></div>
        </li>)}</ul>
      </section>}
    </>}
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ.</p>}
    {notice && <p role="status">{notice}</p>}
    {source && <p className="c3-source">Mã nguồn {source}</p>}
  </Page>
}
