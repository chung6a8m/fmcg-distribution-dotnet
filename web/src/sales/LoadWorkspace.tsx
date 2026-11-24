import { useEffect, useEffectEvent, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, readError, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02c3'
const ordersApi = '/api/synthetic/app02c2'
type Trip = { id: string; code: string }
type Right = { id: string; orderId: string; warehouseId: string; warehouseCode: string; skuCode: string;
  baseUom: string; granted: string; loaded: string; warehouseHeld: string
  lots?: { code: string; expiresOn: string; quantity: string }[]; eligibleWarehouseHeld?: string }
type LoadLine = { rightId: string; expectedWarehouseHeld: string; quantity: string }
type Command = { commandId: string; tripId: string; lines: LoadLine[] }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }
const rejections: Record<string, string> = {
  F03_LOT_STOCK_INSUFFICIENT: 'Số lượng lô đủ điều kiện không đủ. Tải lại chuyến trước khi nạp.',
  C3_STALE_WAREHOUSE_HELD: 'Số lượng giữ tại kho đã thay đổi. Hãy kiểm tra dữ liệu mới trước khi nạp.',
  C3_RESERVATION_NOT_ELIGIBLE: 'Có dòng giữ hàng không còn đủ điều kiện nạp.',
  C3_WAREHOUSE_STOCK_INSUFFICIENT: 'Kho không đủ lượng hàng đã giữ để nạp.',
  C3_WAREHOUSE_COUNTER_MISMATCH: 'Số dư giữ hàng và tồn vật lý của kho chưa khớp.',
  PHYSICAL_OPENING_REQUIRED: 'Kho hoặc xe chưa có số dư mở đầu cho SKU này.',
  TRIP_NOT_PREPARED: 'Chuyến không còn ở trạng thái chuẩn bị.',
}

function displayQuantity(quantity: string) {
  return quantity.includes('.') ? quantity.replace(/0+$/, '').replace(/\.$/, '') : quantity
}

function pendingKey(identity: Identity) {
  return 'app02c3.pending.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function restore(identity: Identity): Command | null {
  try {
    const raw = pendingJson(pendingKey(identity))
    if (!raw) return null
    const command = JSON.parse(raw) as Command
    const guid = /^[a-f0-9-]{36}$/
    return guid.test(command.commandId) && guid.test(command.tripId) && Array.isArray(command.lines) ? command : null
  } catch { /* Unreadable saved requests are ignored. */ }
  return null
}

async function get<T>(token: string, path: string, service = api): Promise<T> {
  const response = await requestSalesApi(service, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

export function LoadWorkspace({ workspaceToken, startTrip, onTripChange, startOrder, onOrderChange }: {
  workspaceToken?: string; startTrip?: string; onTripChange?: (trip: string) => void
  startOrder?: string; onOrderChange?: (order: string) => void
}) {
  const [session, setSession] = useState<{ token: string; identity: Identity } | null>(null)
  const [trips, setTrips] = useState<Trip[]>([])
  const [trip, setTrip] = useState('')
  const [order, setOrder] = useState('')
  const [rights, setRights] = useState<Right[]>([])
  const [warehouse, setWarehouse] = useState('')
  const [pending, setPending] = useState<Command | null>(null)
  const [notice, setNotice] = useState('')
  const [source, setSource] = useState<{ id: string; order: string } | null>(null)
  const [formVersion, setFormVersion] = useState(0)
  const [busy, setBusy] = useState(false)
  const [offline, setOffline] = useState(!navigator.onLine)
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

  async function work(run: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await run() }
    catch (error) { setNotice(readError(error)) }
    finally { active.current = false; setBusy(false) }
  }

  async function refresh(token: string, tripId: string, warehouseId = '', orderId = '') {
    setRights([])
    setTrip(tripId)
    setWarehouse('')
    setOrder(orderId)
    const rows = tripId ? await get<Right[]>(token, '/trips/' + tripId + '/rights') : []
    const scoped = orderId ? rows.filter(row => row.orderId === orderId) : rows
    setRights(rows)
    setWarehouse(scoped.some(r => r.warehouseId === warehouseId) ? warehouseId : scoped[0]?.warehouseId ?? '')
    setFormVersion(version => version + 1)
  }

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    void connectToken(token)
  }

  const openWorkspace = useEffectEvent((token: string) => { void connectToken(token) })
  useEffect(() => {
    if (workspaceToken) openWorkspace(workspaceToken)
  }, [workspaceToken])

  function connectToken(token: string) {
    void work(async () => {
      const [identity, available] = await Promise.all([get<Identity>(token, '/session', ordersApi), get<Trip[]>(token, '/trips')])
      const saved = restore(identity)
      setSession({ token, identity })
      setTrips(available)
      setPending(saved)
      setSource(null)
      const requested = available.find(t => t.id === startTrip)
      const effectiveTrip = startOrder ? requested?.id ?? ''
        : (available.find(t => t.id === saved?.tripId) ?? requested ?? available[0])?.id ?? ''
      if (effectiveTrip || !startOrder) onTripChange?.(effectiveTrip)
      await refresh(token, effectiveTrip, '', startOrder)
    })
  }

  async function resolve(command: Command, lookup: boolean) {
    if (!session) return
    const { token, identity } = session
    await work(async () => {
      if (!lookup) {
        rememberPending(pendingKey(identity), command)
        setPending(command)
      }
      let message: string
      try {
        setSource(null)
        const response = lookup
          ? await requestSalesApi(api, token, `/trips/${command.tripId}/commands/${command.commandId}`)
          : await requestSalesApi(api, token, '/load', JSON.stringify(command))
        if (lookup && response.status === 404) {
          setNotice('Chưa tìm thấy kết quả. Có thể gửi lại đúng đợt nạp này.')
          return
        }
        if (![200, 400, 409, 422].includes(response.status)) throw new HttpError(response.status)
        const outcome = await response.json() as Outcome
        if (outcome.status === 'CONFLICT') {
          setNotice('Mã yêu cầu đã được dùng cho nội dung khác. Hãy tra cứu kết quả nạp.')
          return
        }
        if (outcome.status === 'APPLIED' && outcome.sourceId) {
          const matches = command.tripId === trip && rights.some(right => right.orderId === order
            && command.lines.some(line => line.rightId === right.id))
          setSource({ id: outcome.sourceId, order: matches ? order : '' })
        }
        message = outcome.status === 'APPLIED' && outcome.sourceId ? 'Đã nạp hàng lên xe.'
          : outcome.status === 'REJECTED' ? rejections[outcome.reasonCode ?? ''] ?? 'Đợt nạp bị từ chối.'
            : ''
        if (!message) throw new Error('Missing command outcome')
      } catch (error) {
        setNotice(error instanceof HttpError && [401, 403].includes(error.status)
          ? 'Phiên hoặc quyền hiện hành không cho phép kiểm tra đợt nạp.'
          : 'Chưa rõ kết quả. Hãy tra cứu trước khi lập đợt nạp mới.')
        return
      }
      setPending(null)
      forgetPending(pendingKey(identity), command.commandId)
      try { await refresh(token, trip, warehouse, order) }
      catch { message += ' Hãy tải lại số lượng trước khi nạp tiếp.' }
      setNotice(message)
    })
  }

  const scoped = order ? rights.filter(r => r.orderId === order) : rights
  const warehouseOptions = [...new Map(scoped.map(r => [r.warehouseId, r.warehouseCode]))]
  const shown = scoped.filter(r => r.warehouseId === warehouse)
  const canRecoverHere = pending && (!order || (pending.tripId === trip && pending.lines.length > 0
    && pending.lines.every(line => scoped.some(right => right.id === line?.rightId))))
  const off = busy || offline
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending || off || !trip) return
    const data = new FormData(event.currentTarget)
    const lines = shown.map(r => ({
      rightId: r.id, expectedWarehouseHeld: r.warehouseHeld, quantity: String(data.get(r.id) ?? '').trim(),
    })).filter(l => l.quantity !== '')
    if (!lines.length || lines.length > 100) {
      setNotice('Nhập số lượng cho từ 1 đến 100 dòng trong đợt nạp.')
      return
    }
    if (lines.some(l => !/^\d{1,15}(\.\d{1,3})?$/.test(l.quantity) || Number(l.quantity) <= 0)) {
      setNotice('Nhập số lượng dương với tối đa ba chữ số thập phân.')
      return
    }
    void resolve({ commandId: crypto.randomUUID(), tripId: trip, lines }, false)
  }

  const Container = workspaceToken ? 'section' : 'main'
  return <Container className={workspaceToken ? 'sales-pane c1-page' : 'page c1-page'}>
    {workspaceToken ? <h2>Nạp hàng lên xe</h2> : <header className="page-heading">
      <h1>Nạp hàng lên xe</h1>
      <p>APP-02C.3 · DEV</p>
      <ThemeSelect />
      <a className="page-link" href="/sales/orders">Đơn A và giữ hàng</a>
      <a className="page-link" href="/sales/delivery">Giao hàng và công nợ A</a>
    </header>}
    {!session ? workspaceToken
      ? <Button disabled={busy || offline} onClick={() => void connectToken(workspaceToken)}>Tải màn hình</Button>
      : <form className="panel panel-content" onSubmit={connect}>
      <Field name="token" type="password" label="Mã truy cập DEV" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={off}>Kết nối</Button>
    </form> : <section className="panel panel-content" aria-label="Lập đợt nạp">
      {!workspaceToken && <Button disabled={busy} onClick={() => { setSession(null); setRights([]); setPending(null); setNotice(''); setSource(null) }}>
        Đổi phiên DEV
      </Button>}
      {order && <div className="button-row">
        <a className="page-link" href={`/work/orders?trip=${trip || startTrip || ''}&order=${order}`
          + (source?.order === order ? '&handoffSource=' + source.id : '')}>Xem đơn và lịch sử lô</a>
        {trip && <Button disabled={off || !!pending} onClick={() => {
          setSource(null)
          onOrderChange?.('')
          void work(() => refresh(session.token, trip, warehouse))
        }}>Tất cả đơn trên chuyến</Button>}
      </div>}
      {!trip ? order ? <div>
        <p>Chuyến trong liên kết không còn mở được với phân công hiện hành.</p>
        <a className="page-link" href="/work/load">Mở danh sách chuyến</a>
      </div> : <p>Chưa có chuyến được phân công.</p> : <>
        <label className="field">Chuyến
          <select value={trip} disabled={off || !!pending || !!order}
            onChange={event => {
              const id = event.target.value
              setSource(null)
              onTripChange?.(id)
              void work(() => refresh(session.token, id))
            }}>
            {trips.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}
          </select>
        </label>
        <Button disabled={off || !!pending} onClick={() => { void work(() => refresh(session.token, trip, warehouse, order)) }}>
          Tải lại số lượng
        </Button>
        {warehouseOptions.length === 0 ? <p>{order ? 'Đơn này không có dòng giữ hàng để nạp trên chuyến đã chọn.'
          : 'Chưa có dòng giữ hàng đủ điều kiện.'}</p> : <label className="field">Kho nạp
          <select value={warehouse} disabled={off || !!pending} onChange={event => setWarehouse(event.target.value)}>
            {warehouseOptions.map(([id, code]) => <option key={id} value={id}>{code}</option>)}
          </select>
        </label>}
        {shown.length > 0 && <form key={warehouse + formVersion} onSubmit={submit}>
          {shown.map((r, i) => <fieldset key={r.id} className="c1-fields">
            <legend>Dòng {i + 1} · {r.skuCode}</legend>
            <p className="c3-source">Mã đơn {r.orderId}</p>
            {workspaceToken && !order && <a className="page-link" href={`/work/orders?trip=${trip}&order=${r.orderId}`}>
              Mở đơn và lịch sử lô
            </a>}
            <p>Đã giữ {displayQuantity(r.granted)} {r.baseUom} · Đã nạp {displayQuantity(r.loaded)} {r.baseUom} · Còn giữ tại kho {displayQuantity(r.warehouseHeld)} {r.baseUom}</p>
            {r.lots && r.lots.length > 0 && <div>
              <p id={'c3-held-lots-' + r.id}>Lô giữ tại kho</p>
              <ul className="c1-rows" aria-labelledby={'c3-held-lots-' + r.id}>
                {r.lots.map(lot => <li key={lot.code}>
                  <strong>{lot.code}</strong>
                  <span>Hạn dùng <time dateTime={lot.expiresOn.slice(0, 10)}>
                    {lot.expiresOn.slice(0, 10).split('-').reverse().join('/')}
                  </time></span>
                  <span>{displayQuantity(lot.quantity)} {r.baseUom}</span>
                </li>)}
              </ul>
            </div>}
            {r.lots?.length === 0 && displayQuantity(r.warehouseHeld) !== '0' && <p>Không có lô đủ điều kiện nạp.</p>}
            {displayQuantity(r.eligibleWarehouseHeld ?? r.warehouseHeld) !== '0' && <Field name={r.id} label={`Số lượng nạp dòng ${i + 1}`} type="number"
              min="0.001" max={r.eligibleWarehouseHeld ?? r.warehouseHeld} step="0.001" disabled={off || !!pending} />}
          </fieldset>)}
          <Button type="submit" variant="primary" disabled={off || !!pending || !shown.some(r => displayQuantity(r.eligibleWarehouseHeld ?? r.warehouseHeld) !== '0')}>
            Nạp hàng
          </Button>
        </form>}
      </>}
      {pending && (canRecoverHere ? <>
        <p className="c3-source">Chuyến của lệnh nạp {trips.find(item => item.id === pending.tripId)?.code ?? pending.tripId}</p>
        <div className="button-row">
          <Button disabled={off} onClick={() => { void resolve(pending, true) }}>Tra cứu kết quả nạp</Button>
          <Button disabled={off} onClick={() => { void resolve(pending, false) }}>Gửi lại cùng yêu cầu</Button>
        </div>
      </> : <a className="page-link" href={'/work/load?trip=' + pending.tripId}>Mở chuyến của lệnh nạp đang chờ</a>)}
    </section>}
    {offline && <p role="status">Cần kết nối để nạp hàng.</p>}
    {notice && <p role="status">{notice}</p>}
    {source && <p className="c3-source">Mã nguồn nạp {source.id}</p>}
  </Container>
}
