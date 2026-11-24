import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

type Role = 'finance' | 'cashier' | 'case_checker'
type Action = 'SETTLE' | 'CREDIT' | 'PROPOSE' | 'APPROVE' | 'DISBURSE' | 'WITNESS_PAYMENT' | 'WITNESS_PAYOUT'
type Session = { token: string; identity: Identity & { role: Role } }
type Delivery = { id: string; customer: string; sku: string; gross: number; outstanding: number; paymentWitnessed?: boolean }
type Returned = { id: string; deliveryId: string; customer: string; sku: string; quantity: number; creditId: string | null; eligibility?: 'UNSUPPORTED_SOURCE' | 'AWAITING_QA' | 'AWAITING_PAYMENT' | 'ELIGIBLE' }
type Credit = { id: string; caseId: string; customer: string; net: number; tax: number
  gross: number; discount: number; available: number }
type Refund = { id: string; creditId: string; customer: string; amount: number; status: 'PROPOSED' | 'APPROVED' | 'DISBURSED'; payoutWitnessed?: boolean }
type Ledger = { deliveries: Delivery[]; returns: Returned[]; credits: Credit[]; refunds: Refund[]; refresh: number }
type Choice = { id: string; label: string; amount?: number; max?: number }
type Pending = { commandId: string; body: string; conflict?: boolean }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }

const api = '/api/synthetic/dev-f08'
const vnd = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 0 })
const quantities = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })
const actions: Record<Role, Action[]> = {
  finance: ['SETTLE', 'CREDIT', 'PROPOSE', 'APPROVE'],
  cashier: ['DISBURSE'],
  case_checker: ['WITNESS_PAYMENT', 'WITNESS_PAYOUT'],
}
const descriptors = {
  SETTLE: { label: 'Đối soát tiền vào', source: 'Lần giao nguồn', amount: 'Số tiền đối soát (VND)', done: 'Đã đối soát tiền vào.' },
  CREDIT: { label: 'Ghi credit', source: 'Hồ sơ trả đã giải phóng', amount: null, done: 'Đã ghi credit.' },
  PROPOSE: { label: 'Đề nghị chi hoàn tiền', source: 'Nguồn credit', amount: 'Số tiền đề nghị (VND)', done: 'Đã ghi đề nghị chi hoàn tiền.' },
  APPROVE: { label: 'Duyệt chi hoàn tiền', source: 'Đề nghị chi', amount: null, done: 'Đã duyệt chi hoàn tiền.' },
  DISBURSE: { label: 'Ghi thực chi hoàn tiền', source: 'Đề nghị đã duyệt', amount: 'Số tiền thực chi (VND)', done: 'Đã ghi thực chi hoàn tiền.' },
  WITNESS_PAYMENT: { label: 'Xác minh tiền vào', source: 'Lần giao nguồn', amount: 'Số tiền thực nhận (VND)', done: 'Đã xác minh chứng từ tiền vào.' },
  WITNESS_PAYOUT: { label: 'Xác minh thực chi', source: 'Đề nghị đã duyệt', amount: 'Số tiền thực chi (VND)', done: 'Đã xác minh chứng từ thực chi.' },
} as const
const statuses: Record<Refund['status'], string> = {
  PROPOSED: 'Đã đề nghị', APPROVED: 'Đã duyệt', DISBURSED: 'Đã thực chi',
}

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new Error('Refund workspace read rejected')
  return response.json() as Promise<T>
}

function restore(key: string): Pending | null {
  try {
    const pending = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!pending || !/^[a-f0-9-]{36}$/i.test(pending.commandId)) return null
    return JSON.parse(pending.body).commandId === pending.commandId ? pending : null
  } catch { return null }
}

export function RefundWorkspace() {
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
    catch { setLoginError('Không kết nối được phiên hoàn tiền DEV. Kiểm tra mã truy cập và quyền hiện hành.') }
    finally {
      loginOperation.current = false
      setConnecting(false)
    }
  }

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Credit và hoàn tiền</h1><p>DEV{session && ' · ' + session.identity.tenantId + ' · ' + session.identity.actorId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales/returns">Trả hàng</a>
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ hoàn tiền.</p>}
    {loginError && <p role="alert">{loginError}</p>}
    {session ? <RefundPanel session={session} offline={offline} />
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
        <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
        <Button type="submit" disabled={connecting || offline} variant="primary">Kết nối</Button>
      </form>}
  </main>
}

export function RefundPanel({ session: { token, identity }, offline, integrated = false, canOpenReturn = false }: {
  session: Session; offline: boolean; integrated?: boolean; canOpenReturn?: boolean
}) {
  const roleActions = actions[identity.role]
  const key = 'dev-f08.refund.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, identity.role])
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
  const descriptor = descriptors[action]
  const payout = action === 'DISBURSE' || action === 'WITNESS_PAYOUT'
  const options = choices(action, ledger, integrated)
  const selected = options.find(option => option.id === selection)
  const formDisabled = offline || busy || pending !== null || loadError !== ''
    || ledger?.refresh !== refresh || options.length === 0

  useEffect(() => {
    if (offline) return
    let current = true
    void Promise.all([
      get<Delivery[]>(token, '/deliveries'),
      get<Returned[]>(token, '/returns'),
      get<Credit[]>(token, '/credits'),
      get<Refund[]>(token, '/refunds'),
    ]).then(([deliveries, returns, credits, refunds]) => {
      if (current) {
        setLedger({ deliveries, returns, credits, refunds, refresh })
        setLoadError('')
      }
    }).catch(() => {
      if (current) setLoadError('Không tải được sổ credit và hoàn tiền.')
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
      if (!response.ok && ![400, 404, 409, 422].includes(response.status)) throw new Error('Unknown refund outcome')
      const outcome = await response.json() as Outcome
      if (outcome.status === 'CONFLICT') {
        const conflict = { ...command, conflict: true }
        rememberPending(key, conflict)
        setPending(conflict)
        return
      }
      if (outcome.status !== 'APPLIED' && outcome.status !== 'REJECTED') throw new Error('Unresolved refund outcome')
      forgetPending(key, command.commandId)
      setPending(null)
      if (outcome.status === 'APPLIED') {
        form.current?.reset()
        setSelection('')
        const submitted = JSON.parse(command.body) as { action: Action }
        setMessage(descriptors[submitted.action]?.done ?? 'Đã ghi nghiệp vụ hoàn tiền.')
      } else {
        setMessage(outcome.reasonCode ? `Yêu cầu hoàn tiền bị từ chối (${outcome.reasonCode}).` : 'Yêu cầu hoàn tiền bị từ chối.')
      }
      setRefresh(value => value + 1)
    } catch { setMessage('Chưa rõ kết quả ghi nghiệp vụ hoàn tiền.') }
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
    const amount = Number(values.get('amount'))
    if (descriptor.amount && (!Number.isSafeInteger(amount) || amount < 1)) {
      setMessage('Nhập số tiền bằng số VND nguyên lớn hơn 0.')
      return
    }
    const commandId = crypto.randomUUID()
    const command = { commandId, body: JSON.stringify({ commandId, action, reason, evidence,
      ...commandSource(action, selected.id, amount) }) }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  return <>
    <section className="panel panel-content sales-pane" aria-label="Thao tác credit và hoàn tiền">
      <h2>{descriptor.label}</h2>
      {loadError && <p role="alert">{loadError}</p>}
      <div className="button-row"><Button disabled={offline || busy || pending !== null}
        onClick={() => setRefresh(value => value + 1)}>Tải lại danh sách</Button></div>
      {roleActions.length > 1 && <div className="field">
        <label htmlFor={id + '-action'}>Thao tác hoàn tiền</label>
        <select id={id + '-action'} value={action} disabled={busy || pending !== null}
          onChange={event => {
            const next = roleActions.find(value => value === event.currentTarget.value)
            if (!next) return
            form.current?.reset()
            setSelection('')
            setMessage('')
            setAction(next)
          }}>
          {roleActions.map(value => <option key={value} value={value}>{descriptors[value].label}</option>)}
        </select>
      </div>}
      {ledger && options.length === 0 && <p>Chưa có nguồn đủ điều kiện cho thao tác này.</p>}
      <form ref={form} onSubmit={submit}>
        <fieldset className="c1-fields" disabled={formDisabled}>
          <div className="field">
            <label htmlFor={id + '-selection'}>{descriptor.source}</label>
            <select id={id + '-selection'} value={selected ? selection : ''} required
              onChange={event => setSelection(event.currentTarget.value)}>
              <option value="">Chọn nguồn</option>
              {options.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
            </select>
          </div>
          {payout && selected?.amount != null && <p>Số tiền đã duyệt {vnd.format(selected.amount)} VND</p>}
          {descriptor.amount && <Field label={descriptor.amount} name="amount" type="number" min="1"
            step="1" max={selected?.max} disabled={!selected} required />}
          <Field name="reason" label="Lý do" maxLength={200} required />
          <Field name="evidence" label={payout ? 'Chứng từ thực chi DEV' : 'Chứng từ DEV'} maxLength={120} required />
          <div className="button-row"><Button type="submit" variant="primary" disabled={!selected}>{descriptor.label}</Button></div>
        </fieldset>
      </form>
      {message && <p role="status">{message}</p>}
      {pending?.conflict && <p role="alert">Xung đột nội dung với yêu cầu {pending.commandId}.</p>}
      {pending && !pending.conflict && <div className="button-row">
        <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
        <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
      </div>}
    </section>
    {ledger && <RefundHistory ledger={ledger} integrated={integrated} canOpenReturn={canOpenReturn} />}
  </>
}

function commandSource(action: Action, id: string, amount: number) {
  switch (action) {
    case 'SETTLE':
    case 'WITNESS_PAYMENT': return { deliveryId: id, amount }
    case 'CREDIT': return { caseId: id }
    case 'PROPOSE': return { creditId: id, amount }
    case 'APPROVE': return { refundId: id }
    case 'DISBURSE':
    case 'WITNESS_PAYOUT': return { refundId: id, amount }
  }
}

function choices(action: Action, ledger: Ledger | null, integrated: boolean): Choice[] {
  if (!ledger) return []
  if (action === 'SETTLE' || action === 'WITNESS_PAYMENT') return ledger.deliveries.filter(row => row.outstanding > 0 && (!integrated || (action === 'SETTLE' ? row.paymentWitnessed : !row.paymentWitnessed)))
    .map(row => ({ id: row.id, label: `${row.customer} · ${row.sku} · ${row.id}`, max: row.outstanding }))
  if (action === 'CREDIT') {
    const outstanding = new Map(ledger.deliveries.map(row => [row.id, row.outstanding]))
    return ledger.returns.filter(row => {
      const receivable = outstanding.get(row.deliveryId)
      return row.creditId == null && (integrated ? row.eligibility === 'ELIGIBLE' : (receivable === undefined || receivable === 0))
    })
      .map(row => ({ id: row.id, label: `${row.customer} · ${row.sku} · ${quantities.format(row.quantity)} EA · ${row.id}` }))
  }
  if (action === 'PROPOSE') return ledger.credits.filter(row => row.available > 0)
    .map(row => ({ id: row.id, label: `${row.customer} · ${vnd.format(row.available)} VND · ${row.id}`, max: row.available }))
  const status = action === 'APPROVE' ? 'PROPOSED' : 'APPROVED'
  return ledger.refunds.filter(row => row.status === status && (!integrated || action === 'APPROVE' || (action === 'DISBURSE' ? row.payoutWitnessed : !row.payoutWitnessed)))
    .map(row => ({ id: row.id, label: `${row.customer} · ${vnd.format(row.amount)} VND · ${row.id}`, amount: row.amount }))
}

const caseStatus = {
  UNSUPPORTED_SOURCE: 'Nguồn ngoài phạm vi F08 v1',
  AWAITING_QA: 'Chờ giải phóng hàng qua QA',
  AWAITING_PAYMENT: 'Chờ đối soát đủ tiền',
  ELIGIBLE: 'Chờ ghi credit',
} as const

function RefundHistory({ ledger, integrated, canOpenReturn }: { ledger: Ledger; integrated: boolean; canOpenReturn: boolean }) {
  return <>
    <section className="panel panel-content sales-pane" aria-label="Công nợ theo lần giao">
      <h2>Công nợ theo lần giao</h2>
      {ledger.deliveries.length === 0 ? <p>Chưa có lần giao để đối soát.</p> : <ul className="c1-rows">
        {ledger.deliveries.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer} · {row.sku}</strong>
          <dl className="sales-facts">
            <dt>Tổng thanh toán</dt><dd>{vnd.format(row.gross)} VND</dd>
            <dt>Nợ còn lại</dt><dd>{vnd.format(row.outstanding)} VND</dd>
          </dl>
          <p>Mã lần giao {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
    <section className="panel panel-content sales-pane" aria-label={integrated ? "Hồ sơ trả hàng" : "Hồ sơ trả đã giải phóng"}>
      <h2>{integrated ? "Hồ sơ trả hàng" : "Hồ sơ trả đã giải phóng"}</h2>
      {ledger.returns.length === 0 ? <p>Chưa có hồ sơ trả đã giải phóng.</p> : <ul className="c1-rows">
        {ledger.returns.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer} · {row.sku}</strong>
          <p>{quantities.format(row.quantity)} EA · {row.creditId ? 'Đã ghi credit' : caseStatus[row.eligibility ?? 'ELIGIBLE']}</p>
          <p>Mã hồ sơ trả {row.id}</p>
          <p>Mã lần giao {row.deliveryId}</p>
          {canOpenReturn && <a className="page-link" href={'/work/returns?source=' + row.id + '#return-' + row.id}>
            Mở hồ sơ trả hàng
          </a>}
          {row.creditId && <p>Mã credit {row.creditId}</p>}
        </div></li>)}
      </ul>}
    </section>
    <section className="panel panel-content sales-pane" aria-label="Credit theo hồ sơ trả">
      <h2>Credit theo hồ sơ trả</h2>
      {ledger.credits.length === 0 ? <p>Chưa có credit.</p> : <ul className="c1-rows">
        {ledger.credits.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer}</strong>
          <dl className="sales-facts">
            <dt>Tiền chưa thuế</dt><dd>{vnd.format(row.net)} VND</dd>
            <dt>Thuế</dt><dd>{vnd.format(row.tax)} VND</dd>
            <dt>Chiết khấu phân bổ</dt><dd>{vnd.format(row.discount)} VND</dd>
            <dt>Tổng credit</dt><dd>{vnd.format(row.gross)} VND</dd>
            <dt>Credit còn lại</dt><dd>{vnd.format(row.available)} VND</dd>
          </dl>
          <p>Mã hồ sơ trả {row.caseId}</p>
          <p>Mã credit {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
    <section className="panel panel-content sales-pane" aria-label="Danh sách hoàn tiền">
      <h2>Danh sách hoàn tiền</h2>
      {ledger.refunds.length === 0 ? <p>Chưa có đề nghị chi hoàn tiền.</p> : <ul className="c1-rows">
        {ledger.refunds.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.customer} · {statuses[row.status]}</strong>
          {integrated && row.status === 'APPROVED' && <p>{row.payoutWitnessed ? 'Đã xác minh chứng từ thực chi' : 'Chờ xác minh chứng từ thực chi'}</p>}
          <p>{vnd.format(row.amount)} VND</p>
          <p>Mã credit {row.creditId}</p>
          <p>Mã đề nghị chi {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
  </>
}
