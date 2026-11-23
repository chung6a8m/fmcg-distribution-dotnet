import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

type Role = 'finance' | 'case_checker'
type Session = { token: string; identity: Identity & { role: Role } }
type Source = { deliveryId: string; sku: string; originalCost: number; priorReturned: number
  previousRefund: number; period: string; adjustedCost: number | null }
type Proposal = { id: string; deliveryId: string; sku: string; originalCost: number; adjustedCost: number
  previousRefund: number; priorAdjustment: number | null; status: 'PROPOSED' | 'APPROVED' }
type Ledger = { sources: Source[]; proposals: Proposal[]; refresh: number }
type Pending = { commandId: string; body: string; conflict?: boolean }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }

const api = '/api/synthetic/dev-f06'
const vnd = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 0 })
const difference = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 0, signDisplay: 'exceptZero' })
const quantity = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new Error('Cost adjustment workspace read rejected')
  return response.json() as Promise<T>
}

function restore(key: string): Pending | null {
  try {
    const pending = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!pending || !/^[a-f0-9-]{36}$/i.test(pending.commandId)) return null
    return JSON.parse(pending.body).commandId === pending.commandId ? pending : null
  } catch { return null }
}

export function CostAdjustmentWorkspace() {
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
    catch { setLoginError('Không kết nối được phiên điều chỉnh DEV. Kiểm tra mã truy cập và quyền hiện hành.') }
    finally {
      loginOperation.current = false
      setConnecting(false)
    }
  }

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Điều chỉnh giá vốn</h1><p>DEV{session && ' · ' + session.identity.tenantId + ' · ' + session.identity.actorId}</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales/returns">Trả hàng</a>
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {offline && <p role="status">Cần kết nối để ghi điều chỉnh giá vốn.</p>}
    {loginError && <p role="alert">{loginError}</p>}
    {session ? <AdjustmentPanel session={session} offline={offline} />
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
        <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
        <Button type="submit" disabled={connecting || offline} variant="primary">Kết nối</Button>
      </form>}
  </main>
}

function AdjustmentPanel({ session: { token, identity }, offline }: { session: Session; offline: boolean }) {
  const mayPropose = identity.role === 'finance'
  const action = mayPropose ? 'Đề nghị điều chỉnh' : 'Duyệt điều chỉnh'
  const key = 'dev-f06.adjustment.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, identity.role])
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
  const occupiedSources = new Set(ledger?.proposals.map(row => row.deliveryId))
  const sources = ledger?.sources.filter(row => row.adjustedCost == null && !occupiedSources.has(row.deliveryId)) ?? []
  const proposals = ledger?.proposals.filter(row => row.status === 'PROPOSED') ?? []
  const source = sources.find(row => row.deliveryId === selection)
  const proposal = proposals.find(row => row.id === selection)
  const formDisabled = offline || busy || pending !== null || loadError !== '' || ledger?.refresh !== refresh
    || (mayPropose ? sources.length === 0 : proposals.length === 0)

  useEffect(() => {
    if (offline) return
    let current = true
    void Promise.all([
      get<Proposal[]>(token, '/proposals'),
      mayPropose ? get<Source[]>(token, '/sources') : Promise.resolve([]),
    ]).then(([loadedProposals, loadedSources]) => {
      if (current) {
        setLedger({ proposals: loadedProposals, sources: loadedSources, refresh })
        setLoadError('')
      }
    }).catch(() => {
      if (current) setLoadError('Không tải được nguồn điều chỉnh giá vốn.')
    })
    return () => { current = false }
  }, [mayPropose, offline, refresh, token])

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
      if (!response.ok && ![400, 404, 409, 422].includes(response.status)) throw new Error('Unknown adjustment outcome')
      const outcome = await response.json() as Outcome
      if (outcome.status === 'CONFLICT') {
        const conflict = { ...command, conflict: true }
        rememberPending(key, conflict)
        setPending(conflict)
        return
      }
      if (outcome.status !== 'APPLIED' && outcome.status !== 'REJECTED') throw new Error('Unresolved adjustment outcome')
      forgetPending(key, command.commandId)
      setPending(null)
      if (outcome.status === 'APPLIED') {
        form.current?.reset()
        setSelection('')
        setMessage(mayPropose ? 'Đã ghi đề nghị điều chỉnh.' : 'Đã ghi điều chỉnh giá vốn.')
      } else {
        setMessage(outcome.reasonCode
          ? `Yêu cầu điều chỉnh bị từ chối (${outcome.reasonCode}).` : 'Yêu cầu điều chỉnh bị từ chối.')
      }
      setRefresh(value => value + 1)
    } catch { setMessage('Chưa rõ kết quả ghi điều chỉnh giá vốn.') }
    finally {
      operation.current = false
      setBusy(false)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (formDisabled || operation.current || !navigator.onLine || (!source && !proposal)) return
    const values = new FormData(event.currentTarget)
    const reason = String(values.get('reason') ?? '').trim()
    const evidence = String(values.get('evidence') ?? '').trim()
    if (!reason || !evidence) {
      setMessage('Nhập lý do và chứng từ DEV.')
      return
    }
    const adjustedCost = Number(values.get('adjustedCost'))
    if (mayPropose && (!Number.isSafeInteger(adjustedCost) || adjustedCost < 0)) {
      setMessage('Nhập giá vốn bằng số VND nguyên không âm.')
      return
    }
    const details = source ? { action: 'PROPOSE', deliveryId: source.deliveryId, adjustedCost }
      : { action: 'APPROVE', proposalId: proposal?.id }
    const commandId = crypto.randomUUID()
    const command = { commandId, body: JSON.stringify({ commandId, reason, evidence, ...details }) }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  return <>
    <section className="panel panel-content sales-pane" aria-label={action}>
      <h2>{action}</h2>
      {loadError && <p role="alert">{loadError}</p>}
      <div className="button-row"><Button disabled={offline || busy || pending !== null}
        onClick={() => setRefresh(value => value + 1)}>Tải lại danh sách</Button></div>
      {ledger && (mayPropose ? sources.length === 0 : proposals.length === 0)
        && <p>{mayPropose ? 'Chưa có lần giao đủ điều kiện đề nghị.' : 'Chưa có đề nghị chờ duyệt.'}</p>}
      <form ref={form} onSubmit={submit}>
        <fieldset className="c1-fields" disabled={formDisabled}>
          <div className="field">
            <label htmlFor={id + '-selection'}>{mayPropose ? 'Lần giao nguồn' : 'Đề nghị chờ duyệt'}</label>
            <select id={id + '-selection'} required value={source || proposal ? selection : ''}
              onChange={event => setSelection(event.currentTarget.value)}>
              <option value="">{mayPropose ? 'Chọn lần giao' : 'Chọn đề nghị'}</option>
              {mayPropose ? sources.map(row => <option key={row.deliveryId} value={row.deliveryId}>
                {row.sku} · Kỳ {row.period} · {row.deliveryId}
              </option>) : proposals.map(row => <option key={row.id} value={row.id}>
                {row.sku} · {vnd.format(row.adjustedCost)} VND · {row.id}
              </option>)}
            </select>
          </div>
          {source && <dl className="sales-facts">
            <dt>Kỳ nguồn</dt><dd>{source.period}</dd>
            <dt>Giá vốn gốc</dt><dd>{vnd.format(source.originalCost)} VND</dd>
            <dt>Đã trả</dt><dd>{quantity.format(source.priorReturned)} EA</dd>
            <dt>Giá vốn đã hoàn</dt><dd>{vnd.format(source.previousRefund)} VND</dd>
          </dl>}
          {proposal && <dl className="sales-facts">
            <dt>Giá vốn gốc</dt><dd>{vnd.format(proposal.originalCost)} VND</dd>
            <dt>Giá vốn đề nghị</dt><dd>{vnd.format(proposal.adjustedCost)} VND</dd>
            <dt>Chênh giá vốn nguồn</dt><dd>{difference.format(proposal.adjustedCost - proposal.originalCost)} VND</dd>
            <dt>Giá vốn đã hoàn</dt><dd>{vnd.format(proposal.previousRefund)} VND</dd>
          </dl>}
          {mayPropose && <Field label="Giá vốn sau điều chỉnh (VND)" name="adjustedCost" type="number" min="0"
            step="1" required disabled={!source} />}
          <Field label="Lý do" name="reason" maxLength={200} required />
          <Field label="Chứng từ DEV" name="evidence" maxLength={120} required />
          <div className="button-row"><Button variant="primary" type="submit" disabled={!source && !proposal}>
            {action}
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
    <section className="panel panel-content sales-pane" aria-label="Danh sách điều chỉnh">
      <h2>Danh sách điều chỉnh</h2>
      {ledger && ledger.proposals.length === 0 && <p>Chưa có đề nghị điều chỉnh.</p>}
      {ledger && ledger.proposals.length > 0 && <ul className="c1-rows">
        {ledger.proposals.map(row => <li key={row.id}><div className="c3-source">
          <strong>{row.sku} · {row.status === 'APPROVED' ? 'Đã duyệt' : 'Chờ duyệt'}</strong>
          <dl className="sales-facts">
            <dt>Giá vốn gốc</dt><dd>{vnd.format(row.originalCost)} VND</dd>
            <dt>{row.status === 'APPROVED' ? 'Giá vốn đã duyệt' : 'Giá vốn đề nghị'}</dt><dd>{vnd.format(row.adjustedCost)} VND</dd>
            <dt>Chênh giá vốn nguồn</dt><dd>{difference.format(row.adjustedCost - row.originalCost)} VND</dd>
            <dt>Giá vốn đã hoàn</dt><dd>{vnd.format(row.previousRefund)} VND</dd>
            {row.priorAdjustment != null && <>
              <dt>Chênh phần đã hoàn</dt><dd>{difference.format(row.priorAdjustment)} VND</dd>
            </>}
          </dl>
          <p>Mã lần giao {row.deliveryId}</p>
          <p>Mã đề nghị {row.id}</p>
        </div></li>)}
      </ul>}
    </section>
  </>
}
