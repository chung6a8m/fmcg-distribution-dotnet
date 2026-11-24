import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, rememberPending, requestSalesApi, type Identity, type Outcome } from './customer-api'
import './customers.css'

type Role = 'finance' | 'cashier' | 'sales'
type Session = { token: string; identity: Identity & { role: Role } }
type Advance = { id: string; employeeId: string; issued: number; outstanding: number; currency: string;
  evidence: string; approvedExpenses: number; acceptedReturns: number; pendingReturns: number }
type Entry = { id: string; kind: string; referenceId: string | null; amount: number; actorId: string; evidence: string;
  decisionReason: string | null }
type Detail = { id: string; outstanding: number; entries: Entry[] }
type Pending = { commandId: string; body: string; conflict?: boolean }
const api = '/api/synthetic/dev-f02'
const amounts = new Intl.NumberFormat('vi-VN')
const actions: Record<string, string> = {
  ISSUE: 'Phát ứng', EXPENSE_REQUEST: 'Đề nghị quyết toán chi', EXPENSE_APPROVE: 'Duyệt chi',
  RETURN_OFFER: 'Đề nghị hoàn ứng', RETURN_ACCEPT: 'Xác nhận đã nhận hoàn ứng',
}
const byRole = { finance: ['ISSUE', 'EXPENSE_APPROVE'], cashier: ['RETURN_ACCEPT'],
  sales: ['EXPENSE_REQUEST', 'RETURN_OFFER'] }
const explanations: Record<string, string> = {
  ADVANCE_FIELDS_INVALID: 'Kiểm tra số tiền, chứng từ và nguồn.',
  ADVANCE_NOT_FOUND: 'Không tìm thấy khoản tạm ứng trong phạm vi.',
  ADVANCE_REFERENCE_NOT_FOUND: 'Chứng từ nguồn không khả dụng.',
  ADVANCE_EMPLOYEE_NOT_FOUND: 'Nhân viên không còn quyền trong phạm vi.',
  ADVANCE_AMOUNT_EXCEEDS_BALANCE: 'Số tiền vượt phần còn lại.',
  ADVANCE_SELF_CHECK: 'Người nhận tạm ứng cần người duyệt hoặc thủ quỹ khác.',
  ADVANCE_EVIDENCE_IN_USE: 'Chứng từ đã được sử dụng.',
}

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function restore(key: string): Pending | null {
  try {
    const pending = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!pending || !/^[a-f0-9-]{36}$/i.test(pending.commandId)) return null
    return JSON.parse(pending.body).commandId === pending.commandId ? pending : null
  } catch { return null }
}

export function AdvanceWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [loginError, setLoginError] = useState('')
  const [connecting, setConnecting] = useState(false)

  async function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (connecting) return
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    setConnecting(true)
    setLoginError('')
    try { setSession({ token, identity: await get<Session['identity']>(token, '/session') }) }
    catch { setLoginError('Không kết nối được phiên tạm ứng DEV với quyền hiện hành.') }
    finally { setConnecting(false) }
  }

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Tạm ứng nhân viên</h1><p>DEV{session && ' · ' + session.identity.tenantId + ' · ' + session.identity.actorId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales">Sales A</a>
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {loginError && <p role="alert">{loginError}</p>}
    {session ? <AdvancePanel session={session} /> : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button type="submit" disabled={connecting} variant="primary">Kết nối</Button>
    </form>}
  </main>
}

export function AdvancePanel({ session: { token, identity } }: { session: Session }) {
  const key = 'dev-f02.advance.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
  const [entries, setEntries] = useState<Advance[]>([])
  const [employees, setEmployees] = useState<string[]>([])
  const [selected, setSelected] = useState('')
  const [action, setAction] = useState(() => byRole[identity.role][0])
  const [detail, setDetail] = useState<Detail | null>(null)
  const [pending, setPending] = useState(() => restore(key))
  const [busy, setBusy] = useState(false)
  const [offline, setOffline] = useState(() => !navigator.onLine)
  const [message, setMessage] = useState('')
  const [loadError, setLoadError] = useState('')
  const [refresh, setRefresh] = useState(0)
  const operation = useRef(false)
  const form = useRef<HTMLFormElement>(null)
  const id = useId()
  const advanceId = selected || entries[0]?.id || ''
  const advance = entries.find(row => row.id === advanceId)
  const needsReference = action === 'EXPENSE_APPROVE' || action === 'RETURN_ACCEPT'
  const openSources = detail?.id === advanceId ? detail.entries.filter(entry => {
    if (entry.kind !== (action === 'EXPENSE_APPROVE' ? 'EXPENSE_REQUEST' : 'RETURN_OFFER')) return false
    const used = detail.entries.reduce((sum, child) => child.referenceId === entry.id ? sum + child.amount : sum, 0)
    return used < entry.amount
  }) : []
  const entryReady = action === 'ISSUE'
    ? employees.length > 0 : Boolean(advance) && (!needsReference || openSources.length > 0)

  useEffect(() => {
    function sync() { setOffline(!navigator.onLine) }
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  useEffect(() => {
    let current = true
    if (!offline) void Promise.all([
      get<Advance[]>(token, '/advances'),
      identity.role === 'finance' ? get<string[]>(token, '/employees') : Promise.resolve([]),
    ]).then(([advances, roster]) => {
      if (current) {
        setEntries(advances)
        setEmployees(roster)
        setLoadError('')
      }
    }).catch(() => { if (current) setLoadError('Không tải được sổ tạm ứng.') })
    return () => { current = false }
  }, [token, identity.role, refresh, offline])

  useEffect(() => {
    let current = true
    if (advanceId && !offline) void get<Detail>(token, '/advances/' + advanceId)
      .then(value => { if (current) setDetail(value) })
      .catch(() => {
        if (current) {
          setDetail(null)
          setLoadError('Không tải được chi tiết khoản tạm ứng.')
        }
      })
    return () => { current = false }
  }, [token, advanceId, refresh, offline])

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
      if (!response.ok && ![400, 404, 409].includes(response.status)) throw new Error('Unknown outcome')
      const outcome = await response.json() as Outcome
      if (outcome.code === 'COMMAND_PAYLOAD_CONFLICT') {
        const conflict = { ...command, conflict: true }
        rememberPending(key, conflict)
        setPending(conflict)
        return
      }
      forgetPending(key, command.commandId)
      setPending(null)
      if (outcome.code === 'APPLIED') {
        if (JSON.parse(command.body).action === 'ISSUE' && outcome.id) setSelected(outcome.id)
        form.current?.reset()
        setMessage('Đã ghi sổ tạm ứng.')
        setRefresh(value => value + 1)
      } else {
        setMessage(explanations[outcome.code] ?? 'Yêu cầu không hợp lệ.')
      }
    } catch { setMessage('Chưa rõ kết quả ghi sổ tạm ứng.') }
    finally {
      operation.current = false
      setBusy(false)
    }
  }

  function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!entryReady || pending || operation.current || !navigator.onLine) return
    const values = new FormData(event.currentTarget)
    const commandId = crypto.randomUUID()
    const command = { commandId, body: JSON.stringify({ commandId, action,
      amount: Number(values.get('amount')), evidence: values.get('evidence'),
      employeeId: action === 'ISSUE' ? values.get('employee') : null,
      advanceId: action === 'ISSUE' ? null : advanceId, referenceId: values.get('reference') || null,
      decisionReason: values.get('decisionReason') || null,
    }) }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  return <section className="panel panel-content sales-pane" aria-labelledby={id + '-heading'}>
    <h2 id={id + '-heading'}>Sổ tạm ứng</h2>
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ tạm ứng.</p>}
    {loadError && <p role="alert">{loadError}</p>}
    <div className="field">
      <label htmlFor={id + '-advance'}>Khoản tạm ứng</label>
      <select id={id + '-advance'} value={advanceId} disabled={busy || pending !== null || entries.length === 0}
        onChange={event => setSelected(event.currentTarget.value)}>
        {entries.length === 0 && <option value="">Chưa có khoản tạm ứng</option>}
        {entries.map(row => <option key={row.id} value={row.id}>{row.evidence} · {row.employeeId}</option>)}
      </select>
    </div>
    {advance && <dl className="sales-facts">
      <dt>Đã phát</dt><dd>{amounts.format(advance.issued)} {advance.currency}</dd>
      <dt>Chi đã duyệt</dt><dd>{amounts.format(advance.approvedExpenses)} {advance.currency}</dd>
      <dt>Đề nghị hoàn còn chờ</dt><dd>{amounts.format(advance.pendingReturns)} {advance.currency}</dd>
      <dt>Đã nhận hoàn</dt><dd>{amounts.format(advance.acceptedReturns)} {advance.currency}</dd>
      <dt>Số dư tạm ứng</dt><dd data-testid="advance-outstanding">{amounts.format(advance.outstanding)} {advance.currency}</dd>
    </dl>}
    <div className="field">
      <label htmlFor={id + '-action'}>Thao tác</label>
      <select id={id + '-action'} value={action} disabled={pending !== null} onChange={event => setAction(event.currentTarget.value)}>
        {byRole[identity.role].map(kind => <option key={kind} value={kind}>{actions[kind]}</option>)}
      </select>
    </div>
    <form key={action === 'ISSUE' ? action : action + advanceId} ref={form} onSubmit={save}>
      <fieldset className="c1-fields" disabled={!entryReady || offline || busy || pending !== null}>
        {action === 'ISSUE' && <div className="field">
          <label htmlFor={id + '-employee'}>Nhân viên</label>
          <select id={id + '-employee'} name="employee" required defaultValue="">
            <option value="">Chọn nhân viên</option>
            {employees.map(employee => <option key={employee}>{employee}</option>)}
          </select>
        </div>}
        {needsReference && <div className="field">
          <label htmlFor={id + '-reference'}>Nguồn đề nghị</label>
          <select id={id + '-reference'} name="reference" required defaultValue="">
            <option value="">Chọn đề nghị</option>
            {openSources.map(entry => <option value={entry.id} key={entry.id}>{entry.evidence}</option>)}
          </select>
        </div>}
        {needsReference && <Field label="Lý do xác nhận" name="decisionReason" maxLength={200} required />}
        <Field label="Số tiền (VND)" name="amount" type="number" min={1} max={1000000000} required />
        <Field label="Chứng từ DEV" name="evidence" maxLength={120} required />
        <div className="button-row"><Button variant="primary" type="submit">{actions[action]}</Button></div>
      </fieldset>
    </form>
    {message && <p role="status">{message}</p>}
    {pending?.conflict && <p role="alert">Xung đột nội dung với yêu cầu {pending.commandId}.</p>}
    {pending && !pending.conflict && <div className="button-row">
      <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
      <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
    </div>}
    {detail?.id === advanceId && detail.entries.length > 0 && <ul className="c1-rows">
      {detail.entries.map(entry => <li key={entry.id}>
        <strong>{actions[entry.kind]}</strong><span>{entry.evidence} · {entry.actorId} · {amounts.format(entry.amount)} VND
          {entry.decisionReason && ' · ' + entry.decisionReason}</span>
      </li>)}
    </ul>}
  </section>
}
