import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

type Role = 'sales' | 'warehouse' | 'finance'
type Action = 'ISSUE' | 'RETURN' | 'PROPOSE' | 'APPROVE'
type Session = { token: string; identity: Identity & { role: Role } }
type Position = { customerId: string; customer: string; netPurchases: number; entitlement: number
  giftHeld: number; waivedHeld: number; toDeliver: number }
type Issue = { id: string; customerId: string; customer: string; quantity: number; returned: number; waivedHeld: number }
type Waiver = { id: string; issueId: string; customer: string; quantity: number; status: 'PROPOSED' | 'APPROVED' | 'EXPIRED'
  reason: string; evidence: string }
type Ledger = { positions: Position[]; issues: Issue[]; waivers: Waiver[]; refresh: number }
type Pending = { commandId: string; body: string; conflict?: boolean }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }

const api = '/api/synthetic/dev-f07'
const quantities = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })
const labels: Record<Action, string> = {
  ISSUE: 'Giao quà', RETURN: 'Nhận quà trả', PROPOSE: 'Đề nghị miễn thu hồi quà', APPROVE: 'Duyệt miễn thu hồi quà',
}
const actions: Record<Role, Action[]> = {
  sales: ['ISSUE', 'PROPOSE'], warehouse: ['RETURN'], finance: ['APPROVE'],
}

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new Error('Gift workspace read rejected')
  return response.json() as Promise<T>
}

function restore(key: string): Pending | null {
  try {
    const pending = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!pending || !/^[a-f0-9-]{36}$/i.test(pending.commandId)) return null
    return JSON.parse(pending.body).commandId === pending.commandId ? pending : null
  } catch { return null }
}

export function GiftWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [loginError, setLoginError] = useState('')
  const [connecting, setConnecting] = useState(false)
  const [offline, setOffline] = useState(() => !navigator.onLine)
  const loginOperation = useRef(false)

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
    if (loginOperation.current || !navigator.onLine) return
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    loginOperation.current = true
    setConnecting(true)
    setLoginError('')
    try { setSession({ token, identity: await get<Session['identity']>(token, '/session') }) }
    catch { setLoginError('Không kết nối được phiên quà DEV. Kiểm tra mã truy cập và quyền hiện hành.') }
    finally {
      loginOperation.current = false
      setConnecting(false)
    }
  }

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Quà khuyến mại</h1><p>DEV{session && ' · ' + session.identity.tenantId + ' · ' + session.identity.actorId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales/returns">Trả hàng</a>
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ quà.</p>}
    {loginError && <p role="alert">{loginError}</p>}
    {session ? <GiftPanel session={session} offline={offline} />
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
        <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
        <Button type="submit" disabled={connecting || offline} variant="primary">Kết nối</Button>
      </form>}
  </main>
}

function GiftPanel({ session: { token, identity }, offline }: { session: Session; offline: boolean }) {
  const roleActions = actions[identity.role]
  const key = 'dev-f07.gift.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, identity.role])
  const [action, setAction] = useState<Action>(roleActions[0])
  const [ledger, setLedger] = useState<Ledger | null>(null)
  const [selection, setSelection] = useState('')
  const [pending, setPending] = useState(() => restore(key))
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [loadError, setLoadError] = useState('')
  const [refresh, setRefresh] = useState(0)
  const operation = useRef(false)
  const form = useRef<HTMLFormElement>(null)
  const id = useId()
  const options = choices(action, ledger)
  const selected = options.find(option => option.id === selection)
  const sourceLabel = action === 'ISSUE' ? 'Khách hàng'
    : action === 'APPROVE' ? 'Đề nghị miễn thu hồi' : 'Lần giao quà'
  const formDisabled = offline || busy || pending !== null || loadError !== ''
    || ledger?.refresh !== refresh || options.length === 0

  useEffect(() => {
    if (offline) return
    let current = true
    void Promise.all([
      get<Position[]>(token, '/positions'),
      get<Issue[]>(token, '/issues'),
      get<Waiver[]>(token, '/waivers'),
    ]).then(([positions, issues, waivers]) => {
      if (current) {
        setLedger({ positions, issues, waivers, refresh })
        setLoadError('')
      }
    }).catch(() => {
      if (current) setLoadError('Không tải được sổ quà.')
    })
    return () => { current = false }
  }, [offline, refresh, token])

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
      if (!response.ok && ![400, 404, 409, 422].includes(response.status)) throw new Error('Unknown gift outcome')
      const outcome = await response.json() as Outcome
      if (outcome.status === 'CONFLICT') {
        const conflict = { ...command, conflict: true }
        rememberPending(key, conflict)
        setPending(conflict)
        return
      }
      if (outcome.status !== 'APPLIED' && outcome.status !== 'REJECTED') throw new Error('Unresolved gift outcome')
      forgetPending(key, command.commandId)
      setPending(null)
      if (outcome.status === 'APPLIED') {
        form.current?.reset()
        setSelection('')
        setMessage('Đã ghi nghiệp vụ quà.')
      } else {
        setMessage(outcome.reasonCode ? `Yêu cầu quà bị từ chối (${outcome.reasonCode}).` : 'Yêu cầu quà bị từ chối.')
      }
      setRefresh(value => value + 1)
    } catch { setMessage('Chưa rõ kết quả ghi nghiệp vụ quà.') }
    finally {
      operation.current = false
      setBusy(false)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (formDisabled || operation.current || !navigator.onLine || !selected) return
    const values = new FormData(event.currentTarget)
    const reason = String(values.get('reason') ?? '').trim()
    const evidence = String(values.get('evidence') ?? '').trim()
    if (!reason || !evidence) {
      setMessage('Nhập lý do và chứng từ DEV.')
      return
    }
    const quantity = Number(values.get('quantity'))
    if (action !== 'APPROVE' && (!Number.isSafeInteger(quantity) || quantity < 1)) {
      setMessage('Nhập số lượng quà nguyên lớn hơn 0.')
      return
    }
    const details = action === 'APPROVE' ? { waiverId: selected.id }
      : action === 'ISSUE' ? { customerId: selected.id, quantity } : { issueId: selected.id, quantity }
    const commandId = crypto.randomUUID()
    const command = { commandId, body: JSON.stringify({ commandId, action, reason, evidence, ...details }) }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  return <>
    <section className="panel panel-content sales-pane" aria-label="Thao tác khuyến mại">
      <h2>{labels[action]}</h2>
      {loadError && <p role="alert">{loadError}</p>}
      <div className="button-row"><Button disabled={offline || busy || pending !== null}
        onClick={() => setRefresh(value => value + 1)}>Tải lại danh sách</Button></div>
      {roleActions.length > 1 && <div className="field">
        <label htmlFor={id + '-action'}>Thao tác quà</label>
        <select id={id + '-action'} value={action} disabled={busy || pending !== null}
          onChange={event => {
            const next = roleActions.find(value => value === event.currentTarget.value)
            if (!next) return
            form.current?.reset()
            setSelection('')
            setMessage('')
            setAction(next)
          }}>
          {roleActions.map(value => <option key={value} value={value}>{labels[value]}</option>)}
        </select>
      </div>}
      {ledger && options.length === 0 && <p>Chưa có nguồn đủ điều kiện cho thao tác này.</p>}
      <form ref={form} onSubmit={submit}>
        <fieldset className="c1-fields" disabled={formDisabled}>
          <div className="field">
            <label htmlFor={id + '-selection'}>{sourceLabel}</label>
            <select id={id + '-selection'} value={selected ? selection : ''} required
              onChange={event => setSelection(event.currentTarget.value)}>
              <option value="">Chọn nguồn</option>
              {options.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
            </select>
          </div>
          {action !== 'APPROVE' && <Field label={action === 'RETURN' ? 'Số lượng quà thực nhận' : 'Số lượng quà'}
            name="quantity" type="number" min="1" step="1" max={selected?.quantity} disabled={!selected} required />}
          {action === 'APPROVE' && selected && <p>Số lượng đề nghị miễn thu hồi {quantities.format(selected.quantity)} EA</p>}
          <Field name="reason" label="Lý do" maxLength={200} required />
          <Field name="evidence" label="Chứng từ DEV" maxLength={120} required />
          <div className="button-row"><Button type="submit" variant="primary" disabled={!selected}>{labels[action]}</Button></div>
        </fieldset>
      </form>
      {message && <p role="status">{message}</p>}
      {pending?.conflict && <p role="alert">Xung đột nội dung với yêu cầu {pending.commandId}.</p>}
      {pending && !pending.conflict && <div className="button-row">
        <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
        <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
      </div>}
    </section>
    {ledger && <GiftHistory ledger={ledger} />}
  </>
}

function choices(action: Action, ledger: Ledger | null) {
  if (!ledger) return []
  if (action === 'ISSUE') return ledger.positions.filter(row => row.toDeliver > 0)
    .map(row => ({ id: row.customerId, label: row.customer, quantity: row.toDeliver }))
  if (action === 'APPROVE') return ledger.waivers.filter(row => row.status === 'PROPOSED')
    .map(row => ({ id: row.id, label: `${row.customer} · ${quantities.format(row.quantity)} EA · ${row.id}`, quantity: row.quantity }))
  return ledger.issues.map(row => ({
    id: row.id, label: `${row.customer} · ${row.id}`,
    quantity: row.quantity - row.returned - (action === 'PROPOSE' ? row.waivedHeld : 0),
  })).filter(row => row.quantity > 0)
}

function GiftHistory({ ledger }: { ledger: Ledger }) {
  return <>
    <section className="panel panel-content sales-pane" aria-label="Quyền quà theo khách hàng">
      <h2>Quyền quà theo khách hàng</h2>
      {ledger.positions.length === 0 && <p>Chưa có quyền quà.</p>}
      {ledger.positions.map(row => <section className="panel panel-content" key={row.customerId} aria-label={`Quyền quà ${row.customer}`}>
        <h3>{row.customer}</h3>
        <dl className="sales-facts">
          <dt>Mua ròng</dt><dd>{quantities.format(row.netPurchases)} EA</dd>
          <dt>Quyền nhận</dt><dd>{quantities.format(row.entitlement)} EA</dd>
          <dt>Khách còn giữ</dt><dd>{quantities.format(row.giftHeld)} EA</dd>
          <dt>Miễn thu hồi đang giữ</dt><dd>{quantities.format(row.waivedHeld)} EA</dd>
          <dt>Còn có thể giao</dt><dd>{quantities.format(row.toDeliver)} EA</dd>
        </dl>
      </section>)}
    </section>
    <section className="panel panel-content sales-pane" aria-label="Lịch sử giao và nhận quà">
      <h2>Lịch sử giao và nhận quà</h2>
      {ledger.issues.length === 0 ? <p>Chưa giao quà.</p> : <ul className="c1-rows">
        {ledger.issues.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer}</strong>
          <p>Đã giao {quantities.format(row.quantity)} EA · Đã nhận lại {quantities.format(row.returned)} EA
            · Miễn thu hồi đang giữ {quantities.format(row.waivedHeld)} EA</p>
          <p>Mã lần giao quà {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
    <section className="panel panel-content sales-pane" aria-label="Danh sách miễn thu hồi">
      <h2>Danh sách miễn thu hồi</h2>
      {ledger.waivers.length === 0 ? <p>Chưa có đề nghị miễn thu hồi.</p> : <ul className="c1-rows">
        {ledger.waivers.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer} · {{ PROPOSED: 'Chờ duyệt', APPROVED: 'Đã duyệt', EXPIRED: 'Hết hiệu lực' }[row.status]}</strong>
          <p>{quantities.format(row.quantity)} EA · {row.reason} · Chứng từ {row.evidence}</p>
          <p>Mã lần giao quà {row.issueId}</p>
          <p>Mã đề nghị {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
  </>
}
