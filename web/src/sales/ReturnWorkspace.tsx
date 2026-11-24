import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

export type ReturnRole = 'sales' | 'warehouse' | 'finance' | 'case_checker'
type Session = { token: string; identity: Identity & { role: ReturnRole } }
type Delivery = { deliveryId: string; lotId: string; lotCode: string; customer: string; sku: string
  issued: number; available: number }
type ReturnCase = { id: string; deliveryId: string; lotId: string; lotCode: string; customer: string; sku: string
  quantity: number; state: 'REQUESTED' | 'QUARANTINED' | 'APPROVED' | 'RELEASED'; releasedCost: number | null }
type Ledger = { deliveries: Delivery[]; cases: ReturnCase[]; refresh: number }
type Pending = { commandId: string; body: string; conflict?: boolean }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }

const api = '/api/synthetic/dev-f05'
const amounts = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })
const phases = {
  sales: { action: 'REQUEST', label: 'Đề nghị trả hàng', state: null },
  warehouse: { action: 'RECEIVE', label: 'Nhận vào cách ly', state: 'REQUESTED' },
  finance: { action: 'APPROVE', label: 'Duyệt trả hàng', state: 'QUARANTINED' },
  case_checker: { action: 'RELEASE', label: 'Giải phóng hàng', state: 'APPROVED' },
} as const
const states: Record<ReturnCase['state'], string> = {
  REQUESTED: 'Đã đề nghị',
  QUARANTINED: 'Đang cách ly',
  APPROVED: 'Đã duyệt',
  RELEASED: 'Đã giải phóng',
}

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new Error('Return workspace read rejected')
  return response.json() as Promise<T>
}

function restore(key: string): Pending | null {
  try {
    const pending = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!pending || !/^[a-f0-9-]{36}$/i.test(pending.commandId)) return null
    return JSON.parse(pending.body).commandId === pending.commandId ? pending : null
  } catch { return null }
}

function sourceKey(delivery: Delivery) {
  return delivery.deliveryId + ':' + delivery.lotId
}

export function ReturnWorkspace({ workspaceSession, startSource, startDelivery, onSourceChange }: {
  workspaceSession?: Session; startSource?: string; startDelivery?: string; onSourceChange?: (source: string) => void
} = {}) {
  const [session, setSession] = useState<Session | null>(null)
  const [loginError, setLoginError] = useState('')
  const [connecting, setConnecting] = useState(false)
  const [offline, setOffline] = useState(() => !navigator.onLine)
  const loginInFlight = useRef(false)

  useEffect(() => {
    function sync() { setOffline(!navigator.onLine) }
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (loginInFlight.current || !navigator.onLine) return
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    loginInFlight.current = true
    setConnecting(true)
    setLoginError('')
    try { setSession({ token, identity: await get<Session['identity']>(token, '/session') }) }
    catch { setLoginError('Không kết nối được phiên trả hàng DEV. Kiểm tra mã truy cập và quyền hiện hành.') }
    finally {
      loginInFlight.current = false
      setConnecting(false)
    }
  }

  if (workspaceSession)
    return <ReturnPanel session={workspaceSession} offline={offline}
      startSource={startSource} startDelivery={startDelivery} onSourceChange={onSourceChange} />

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Trả hàng</h1><p>DEV{session && ' · ' + session.identity.tenantId + ' · ' + session.identity.actorId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales">Sales A</a>
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ trả hàng.</p>}
    {loginError && <p role="alert">{loginError}</p>}
    {session ? <ReturnPanel session={session} offline={offline} />
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
        <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
        <Button type="submit" disabled={connecting || offline} variant="primary">Kết nối</Button>
      </form>}
  </main>
}

function ReturnPanel({ session: { token, identity }, offline, startSource = '', startDelivery, onSourceChange }: {
  session: Session; offline: boolean; startSource?: string; startDelivery?: string; onSourceChange?: (source: string) => void
}) {
  const phase = phases[identity.role]
  const request = phase.action === 'REQUEST'
  const receive = phase.action === 'RECEIVE'
  const key = 'dev-f05.return.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, identity.role])
  const [ledger, setLedger] = useState<Ledger | null>(null)
  const [pickedSource, setSelection] = useState<string | null>(null)
  const [linkedDelivery, setLinkedDelivery] = useState(startDelivery)
  const [receivedQuantity, setReceivedQuantity] = useState('')
  const [pending, setPending] = useState(() => restore(key))
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [loadError, setLoadError] = useState('')
  const [refresh, setRefresh] = useState(0)
  const operation = useRef(false)
  const form = useRef<HTMLFormElement>(null)
  const id = useId()
  const deliveries = ledger?.deliveries.filter(row => row.available > 0
    && (!linkedDelivery || row.deliveryId === linkedDelivery)) ?? []
  const cases = ledger?.cases.filter(row => row.state === phase.state) ?? []
  const linkedSource = deliveries.length === 1 ? sourceKey(deliveries[0]) : ''
  const selection = pickedSource ?? (linkedDelivery ? linkedSource : startSource)
  const delivery = deliveries.find(row => sourceKey(row) === selection)
  const returnCase = cases.find(row => row.id === selection)
  const formDisabled = offline || busy || pending !== null || loadError !== '' || ledger?.refresh !== refresh
    || (request ? deliveries.length === 0 : cases.length === 0)

  useEffect(() => {
    if (offline) return
    let current = true
    void Promise.all([
      get<ReturnCase[]>(token, '/cases'),
      request ? get<Delivery[]>(token, '/deliveries') : Promise.resolve([]),
    ]).then(([loadedCases, loadedDeliveries]) => {
      if (current) {
        setLedger({ cases: loadedCases, deliveries: loadedDeliveries, refresh })
        setLoadError('')
      }
    }).catch(() => {
      if (current) setLoadError('Không tải được danh sách trả hàng.')
    })
    return () => { current = false }
  }, [offline, refresh, request, token])

  async function resolve(command: Pending, lookup: boolean) {
    if (operation.current || command.conflict || !navigator.onLine) return
    operation.current = true
    setBusy(true)
    try {
      const response = lookup
        ? await requestSalesApi(api, token, '/commands/' + command.commandId)
        : await requestSalesApi(api, token, '/commands', command.body)
      if (lookup && response.status === 404) {
        setMessage('Chưa tìm thấy kết quả. Có thể gửi lại cùng yêu cầu.')
        return
      }
      if (response.status === 401 || response.status === 403) {
        setMessage('Phiên DEV không còn quyền cho yêu cầu này.')
        return
      }
      if (!response.ok && ![400, 404, 409, 422].includes(response.status)) throw new Error('Unknown return outcome')
      const outcome = await response.json() as Outcome
      if (outcome.status === 'CONFLICT') {
        const conflict = { ...command, conflict: true }
        rememberPending(key, conflict)
        setPending(conflict)
        return
      }
      if (outcome.status !== 'APPLIED' && outcome.status !== 'REJECTED') throw new Error('Unresolved return outcome')
      forgetPending(key, command.commandId)
      setPending(null)
      if (outcome.status === 'APPLIED') {
        form.current?.reset()
        setSelection('')
        onSourceChange?.('')
        setReceivedQuantity('')
        setMessage('Đã ghi hồ sơ trả hàng.')
      } else if (outcome.reasonCode === 'F05_COUNT_MISMATCH') {
        setMessage('Số lượng thực nhận phải khớp số lượng theo hồ sơ.')
      } else {
        setMessage(outcome.reasonCode
          ? `Yêu cầu trả hàng bị từ chối (${outcome.reasonCode}).` : 'Yêu cầu trả hàng bị từ chối.')
      }
      setRefresh(value => value + 1)
    } catch { setMessage('Chưa rõ kết quả ghi hồ sơ trả hàng.') }
    finally {
      operation.current = false
      setBusy(false)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (formDisabled || operation.current || !navigator.onLine || (!delivery && !returnCase)) return
    const values = new FormData(event.currentTarget)
    const reason = String(values.get('reason') ?? '').trim()
    const evidence = String(values.get('evidence') ?? '').trim()
    if (!reason || (!request && !evidence)) {
      setMessage('Nhập lý do và chứng từ khi có yêu cầu.')
      return
    }
    const quantity = Number(request ? values.get('quantity') : receivedQuantity)
    if (receive && (receivedQuantity === '' || !Number.isFinite(quantity) || quantity < 0)) {
      setMessage('Nhập số lượng thực nhận.')
      return
    }
    const details = delivery
      ? { deliveryId: delivery.deliveryId, lotId: delivery.lotId, quantity }
      : { caseId: returnCase?.id, evidence, ...(receive ? { quantity } : {}) }
    const commandId = crypto.randomUUID()
    const command = { commandId, body: JSON.stringify({ commandId, action: phase.action, reason, ...details }) }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  return <>
    <section className="panel panel-content sales-pane" aria-label={phase.label}>
      <h2>{phase.label}</h2>
      {loadError && <p role="alert">{loadError}</p>}
      <div className="button-row"><Button disabled={offline || busy || pending !== null}
        onClick={() => setRefresh(value => value + 1)}>Tải lại danh sách</Button>
        {request && linkedDelivery && <Button onClick={() => {
          setLinkedDelivery(undefined)
          setSelection('')
          onSourceChange?.('')
        }}>Xem tất cả lần giao</Button>}
      </div>
      {request && linkedDelivery && ledger && !loadError && <p role="status">{deliveries.length
        ? 'Đang xem các lô của lần giao đã chọn.'
        : 'Không có lô còn được trả trong phạm vi của lần giao này.'}</p>}
      <form ref={form} onSubmit={submit}>
        <fieldset className="c1-fields" disabled={formDisabled}>
          <div className="field">
            <label htmlFor={id + '-selection'}>{request ? 'Nguồn giao và lô' : 'Hồ sơ trả hàng'}</label>
            <select id={id + '-selection'} required value={delivery || returnCase ? selection : ''}
              onChange={event => {
                setSelection(event.currentTarget.value)
                onSourceChange?.(event.currentTarget.value)
                setReceivedQuantity('')
              }}>
              <option value="">{request ? 'Chọn nguồn giao' : 'Chọn hồ sơ'}</option>
              {request ? deliveries.map(row => <option key={sourceKey(row)} value={sourceKey(row)}>
                {row.customer} · {row.sku} · {row.lotCode} · {row.deliveryId}
              </option>) : cases.map(row => <option key={row.id} value={row.id}>
                {row.customer} · {row.sku} · {row.lotCode} · {amounts.format(row.quantity)} EA · {row.id}
              </option>)}
            </select>
          </div>
          {request && <Field label="Số lượng trả" name="quantity" type="number" min="0.001"
            max={delivery?.available} step="0.001" required
            help={delivery && `Đã giao ${amounts.format(delivery.issued)} EA · Còn có thể trả ${amounts.format(delivery.available)} EA`} />}
          {returnCase && <p>Số lượng theo hồ sơ {amounts.format(returnCase.quantity)} EA</p>}
          {receive && <Field label="Số lượng thực nhận" name="receivedQuantity" type="number" min="0"
            step="0.001" value={receivedQuantity} onChange={event => setReceivedQuantity(event.currentTarget.value)}
            disabled={!returnCase} required />}
          <Field label="Lý do" name="reason" maxLength={200} required />
          {!request && <Field label="Chứng từ DEV" name="evidence" maxLength={120} required />}
          <div className="button-row"><Button variant="primary" type="submit" disabled={!delivery && !returnCase}>
            {phase.label}
          </Button></div>
        </fieldset>
      </form>
      {message && <p role="status">{message}</p>}
      {pending?.conflict && <p role="alert">Xung đột nội dung với yêu cầu {pending.commandId}.</p>}
      {pending && !pending.conflict && <div className="button-row">
        <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
        <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
      </div>}
    </section>
    <section className="panel panel-content sales-pane" aria-label="Danh sách hồ sơ trả hàng">
      <h2>Danh sách hồ sơ trả hàng</h2>
      {ledger && ledger.cases.length === 0 && <p>Chưa có hồ sơ trả hàng.</p>}
      {ledger && ledger.cases.length > 0 && <ul className="c1-rows">
        {ledger.cases.map(row => <li key={row.id} id={'return-' + row.id}><div className="c3-source">
          <strong>{row.customer} · {row.sku} · {row.lotCode}</strong>
          <p>{states[row.state]} · {amounts.format(row.quantity)} EA</p>
          {row.releasedCost != null && <p>Giá vốn hoàn {amounts.format(row.releasedCost)} VND</p>}
          <p>Mã lần giao {row.deliveryId}</p>
          <p>Mã hồ sơ {row.id}</p>
          {onSourceChange && row.state === phase.state && <Button disabled={formDisabled} onClick={() => {
            setSelection(row.id)
            onSourceChange(row.id)
            setReceivedQuantity('')
            document.getElementById(id + '-selection')?.focus()
          }}>Mở hồ sơ</Button>}
        </div></li>)}
      </ul>}
    </section>
  </>
}
