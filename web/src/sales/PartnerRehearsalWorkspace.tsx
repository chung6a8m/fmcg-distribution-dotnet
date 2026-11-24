import { useRef, useState, useSyncExternalStore, type FormEvent } from 'react'
import { Badge, Button, Field, ThemeSelect } from '../ui/controls'
import { HttpError, requestSalesApi } from './customer-api'
import './customers.css'
import './partner-rehearsal.css'

const api = '/api/synthetic/app02d4'
const uuid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i
const amounts = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
const differences = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4, signDisplay: 'exceptZero' })
const instant = new Intl.DateTimeFormat('vi-VN', {
  dateStyle: 'short', timeStyle: 'short', timeZone: 'Asia/Bangkok',
})
type Role = 'invoice' | 'accountant' | 'reconciler' | 'maker' | 'checker'
type Source = {
  deliveryId: string; sourceLineId: string; seller: string; channel: string; occurredAt: string
  quantity: number; gross: number; ar: number; cost: number; hash: string
}
type Session = { roles: Role[]; tenantId?: string; token: string; sources: Source[] }
type Intent = { intentId: string; deliveryId: string; status: string; stableKey?: string; factId?: string; caseId?: string }
type Outcome = { status: string; id: string; postingId?: string }
type Line = {
  deliveryId: string; sourceLineId: string; status: string; reason?: string; caseId?: string
  expectedQuantity: number; expectedGross: number; expectedAr: number; expectedCost: number
  actualQuantity: number | null; actualGross: number | null; actualAr: number | null; actualCost: number | null
  diffQuantity: number | null; diffGross: number | null; diffAr: number | null; diffCost: number | null
}
export type Reconciliation = {
  report: {
    id: string; status: string; expectedCount: number | null; receivedCount: number; postedCount: number
    okCount: number; diffCount: number; pendingCount: number; outsideCount: number
    fromUtc?: string; cutoff?: string; asOf?: string; reason?: string
  }
  lines: Line[]
}

function watchOnline(update: () => void) {
  window.addEventListener('online', update)
  window.addEventListener('offline', update)
  return () => {
    window.removeEventListener('online', update)
    window.removeEventListener('offline', update)
  }
}

function describe(error: unknown) {
  if (error instanceof HttpError) {
    if (error.status === 401) return 'Phiên DEV đã hết hạn hoặc mã truy cập không hợp lệ.'
    if (error.status === 403) return 'Bạn không còn quyền thực hiện thao tác trong phiên DEV này.'
    if (error.status === 404) return 'Dữ liệu không có trong phiên DEV này.'
    if (error.status === 409) return 'Yêu cầu xung đột với dữ liệu hiện tại.'
  }
  return 'Không lấy được xác nhận từ API. Kiểm tra trạng thái trước khi thao tác lại.'
}

async function request<T>(token: string, path: string, body?: object) {
  const response = await requestSalesApi(api, token, path, body === undefined ? undefined : JSON.stringify(body))
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function formattedInstant(value: string) {
  const timestamp = new Date(value)
  return Number.isNaN(timestamp.getTime()) ? value : instant.format(timestamp)
}

function values(quantity: number | null, gross: number | null, ar: number | null, cost: number | null, formatter = amounts) {
  return [quantity, gross, ar, cost].map(item => item == null ? '—' : formatter.format(item)).join(' / ')
}

export function ReconciliationResult({ result, sources, vnd = false }: {
  result: Reconciliation; sources: Pick<Source, 'deliveryId' | 'seller'>[]; vnd?: boolean
}) {
  const { report, lines } = result
  const sourceById = new Map(sources.map(source => [source.deliveryId, source]))
  return <section className="panel panel-content" aria-label="Kết quả đối soát">
    <h2>Kết quả đối soát</h2>
    <Badge tone={report.status === 'RECONCILED_OK' ? 'success' : 'warning'}>{report.status}</Badge>
    <p className="d4-identifier">Mã báo cáo <code>{report.id}</code></p>
    {report.reason && <p>{report.reason}</p>}
    <dl className="sales-filter">
      {report.fromUtc && <div><dt>Từ (gồm)</dt><dd>{formattedInstant(report.fromUtc)}</dd></div>}
      {report.cutoff && <div><dt>Cutoff (loại trừ)</dt><dd>{formattedInstant(report.cutoff)}</dd></div>}
      {report.asOf && <div><dt>Ảnh số liệu lúc</dt><dd>{formattedInstant(report.asOf)}</dd></div>}
      <div><dt>Khớp / dự kiến</dt><dd>{report.expectedCount == null ? 'Chưa biết phạm vi' : report.okCount + ' / ' + report.expectedCount}</dd></div>
      <div><dt>Tiếp nhận</dt><dd>{report.receivedCount}</dd></div>
      <div><dt>Đã ghi sổ</dt><dd>{report.postedCount}</dd></div>
      <div><dt>Lệch</dt><dd>{report.diffCount}</dd></div>
      <div><dt>Chờ</dt><dd>{report.pendingCount}</dd></div>
      <div><dt>Ngoài cửa sổ</dt><dd>{report.outsideCount}</dd></div>
    </dl>
    {lines.length > 0 && <div className="d4-table" role="region" aria-label="Dòng đối soát" tabIndex={0}>
      <table className="sample-table">
        <caption>{vnd ? "Số lượng (EA) / Tiền hàng, công nợ và giá vốn (VND)" : "Số lượng / Tiền hàng / Công nợ / Giá vốn"}</caption>
        <thead><tr><th scope="col">Nguồn</th><th scope="col">Trạng thái</th><th scope="col">Dự kiến</th>
          <th scope="col">Thực tế</th><th scope="col">Chênh lệch</th><th scope="col">Hồ sơ</th></tr></thead>
        <tbody>{lines.map(line => {
          const source = sourceById.get(line.deliveryId)
          return <tr key={line.sourceLineId}>
            <th scope="row" className="d4-identifier">{source?.seller ?? line.deliveryId}<br /><code>{line.sourceLineId}</code></th>
            <td><span className="card-label">Trạng thái</span><span>{line.status}{line.reason && <><br />{line.reason}</>}</span></td>
            <td className="numeric"><span className="card-label">Dự kiến</span>{values(line.expectedQuantity, line.expectedGross, line.expectedAr, line.expectedCost)}</td>
            <td className="numeric"><span className="card-label">Thực tế</span>{values(line.actualQuantity, line.actualGross, line.actualAr, line.actualCost)}</td>
            <td className="numeric"><span className="card-label">Chênh lệch</span>{values(line.diffQuantity, line.diffGross, line.diffAr, line.diffCost, differences)}</td>
            <td className="d4-identifier"><span className="card-label">Hồ sơ</span>{line.caseId ?? '—'}</td>
          </tr>
        })}</tbody>
      </table>
    </div>}
  </section>
}

export function PartnerRehearsalWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [deliveryId, setDeliveryId] = useState('')
  const [intentId, setIntentId] = useState('')
  const [intents, setIntents] = useState<Record<string, Intent>>({})
  const [invoiceFault, setInvoiceFault] = useState('')
  const [postingScenario, setPostingScenario] = useState('')
  const [reportId, setReportId] = useState('')
  const [correctionId, setCorrectionId] = useState('')
  const [caseId, setCaseId] = useState('')
  const [reconciliation, setReconciliation] = useState<Reconciliation | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const active = useRef(false)
  const online = useSyncExternalStore(watchOnline, () => navigator.onLine)
  const blocked = busy || !online
  const roles = session?.roles ?? []
  const source = session?.sources.find(item => item.deliveryId === deliveryId)
  const intent = intents[deliveryId]

  async function work(operation: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await operation() }
    catch (error) { setNotice(describe(error)) }
    finally {
      setBusy(false)
      active.current = false
    }
  }

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    void work(async () => {
      const [identity, sources] = await Promise.all([
        request<Omit<Session, 'token' | 'sources'>>(token, '/session'),
        request<Source[]>(token, '/sources'),
      ])
      setSession({ ...identity, token, sources })
      setDeliveryId(sources[0]?.deliveryId ?? '')
    })
  }

  function disconnect() {
    setSession(null)
    setDeliveryId('')
    setIntentId('')
    setIntents({})
    setInvoiceFault('')
    setPostingScenario('')
    setReportId('')
    setCorrectionId('')
    setCaseId('')
    setReconciliation(null)
    setNotice('')
  }

  function track(next: Intent) {
    setIntents(current => ({ ...current, [next.deliveryId]: next }))
    setIntentId(next.intentId)
  }

  function invoice(kind: 'prepare' | 'send' | 'lookup' | 'read') {
    if (!session) return
    const { token } = session
    const path = '/invoice/' + encodeURIComponent(intent?.intentId ?? intentId)
    void work(async () => {
      if (kind === 'prepare') {
        track(await request<Intent>(token, '/invoice/prepare', { deliveryId }))
      } else if (kind === 'read') {
        const next = await request<Intent>(token, '/invoice/' + encodeURIComponent(intentId))
        setDeliveryId(next.deliveryId)
        track(next)
      } else if (kind === 'lookup') {
        track(await request<Intent>(token, path + '/lookup', {}))
      } else if (intent) {
        try {
          track(await request<Intent>(token, path + '/send', { transportId: crypto.randomUUID(), fault: invoiceFault || undefined }))
        } catch (error) {
          if (!(error instanceof HttpError) || error.status >= 500) track({ ...intent, status: 'UNKNOWN' })
          throw error
        }
      }
    })
  }

  function accounting(kind: 'receive' | 'post') {
    if (!session || !deliveryId) return
    void work(async () => {
      const result = await request<Outcome>(session.token, '/accounting/' + encodeURIComponent(deliveryId) + '/' + kind, {
        transportId: crypto.randomUUID(), scenario: kind === 'post' ? postingScenario || undefined : undefined,
      })
      setNotice(result.status + ' · ' + result.id)
    })
  }

  function reconcile(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!session) return
    const form = new FormData(event.currentTarget)
    void work(async () => {
      const result = await request<Reconciliation>(session.token, '/accounting/reconcile', {
        from: form.get('from'), cutoff: form.get('cutoff'), asOf: form.get('asOf'),
      })
      setReconciliation(result)
      setReportId(result.report.id)
    })
  }

  function readReport() {
    if (!session) return
    void work(async () => setReconciliation(await request<Reconciliation>(
      session.token, '/accounting/reports/' + encodeURIComponent(reportId))))
  }

  function correct(kind: 'propose' | 'approve' | 'close') {
    if (!session) return
    void work(async () => {
      let result: Outcome
      if (kind === 'propose') {
        result = await request<Outcome>(session.token, '/accounting/reports/' + encodeURIComponent(reportId) + '/corrections', { deliveryId })
        setCorrectionId(result.id)
      } else if (kind === 'approve') {
        result = await request<Outcome>(session.token, '/accounting/corrections/' + encodeURIComponent(correctionId) + '/approve', {})
      } else {
        result = await request<Outcome>(session.token, '/accounting/cases/' + encodeURIComponent(caseId) + '/close', { reportId })
      }
      setNotice(result.status + ' · ' + result.id)
    })
  }

  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Hóa đơn và kế toán mô phỏng</h1><p>APP-02D.4 · Windows LocalDB · DEV</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales">Workspace Sales</a></div>
    </header>
    {!session ? <form className="panel panel-content sales-filter" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={blocked}>Kết nối</Button>
    </form> : <>
      <section className="panel panel-content" aria-label="Nguồn giao hàng DEV">
        {session.tenantId && <p>Tenant DEV <code>{session.tenantId}</code></p>}
        <div className="sales-filter">
          <label className="field">
            <span>Giao hàng</span>
            <select value={deliveryId} disabled={busy} onChange={event => {
              setDeliveryId(event.currentTarget.value)
              setIntentId(intents[event.currentTarget.value]?.intentId ?? '')
              setNotice('')
            }}>
              <option value="">Chọn giao hàng</option>
              {session.sources.map(item => <option key={item.deliveryId} value={item.deliveryId}>
                {item.quantity} · {item.seller} · {formattedInstant(item.occurredAt)}
              </option>)}
            </select>
          </label>
          <Button disabled={busy} onClick={disconnect}>Đổi phiên DEV</Button>
        </div>
        {source && <dl className="sales-filter">
          <div><dt>Mã giao hàng</dt><dd className="d4-identifier"><code>{source.deliveryId}</code></dd></div>
          <div><dt>Mã dòng nguồn</dt><dd className="d4-identifier"><code>{source.sourceLineId}</code></dd></div>
          <div><dt>Số lượng / Tiền hàng / Công nợ / Giá vốn</dt>
            <dd>{[source.quantity, source.gross, source.ar, source.cost].join(' / ')}</dd></div>
          <div><dt>Kênh mô phỏng</dt><dd>{source.channel}</dd></div>
        </dl>}
      </section>
      {roles.includes('invoice') && <section className="panel panel-content" aria-label="Hóa đơn mô phỏng">
        <h2>Hóa đơn mô phỏng</h2>
        {intent && <p><Badge tone={intent.status === 'MOCK_RECORDED' ? 'success' : 'warning'}>{intent.status}</Badge></p>}
        {intent?.stableKey && <p className="d4-identifier">Định danh hóa đơn gốc <code>{intent.stableKey}</code></p>}
        {intent?.factId && <p className="d4-identifier">Mã chứng từ mô phỏng <code>{intent.factId}</code></p>}
        {intent?.caseId && <p className="d4-identifier">Mã hồ sơ <code>{intent.caseId}</code></p>}
        <div className="sales-filter">
          <Field label="Mã yêu cầu hóa đơn" value={intentId} disabled={busy}
            onChange={event => setIntentId(event.target.value.trim())} />
          <label className="field">
            <span>Kịch bản gửi DEV</span>
            <select value={invoiceFault} disabled={busy} onChange={event => setInvoiceFault(event.target.value)}>
              <option value="">Bình thường</option>
              <option value="BEFORE_COMMIT">Mất ACK trước commit</option>
              <option value="AFTER_COMMIT">Mất ACK sau commit</option>
              <option value="CRASH_AFTER_COMMIT">Dừng tiến trình sau commit</option>
            </select>
          </label>
        </div>
        <div className="button-row">
          <Button disabled={blocked || !source} onClick={() => invoice('prepare')}>Tạo yêu cầu hóa đơn</Button>
          <Button disabled={blocked || !uuid.test(intentId)} onClick={() => invoice('read')}>Tải yêu cầu gốc</Button>
          <Button disabled={blocked || intent?.status !== 'PREPARED'} onClick={() => invoice('send')}>Gửi hóa đơn mô phỏng</Button>
          <Button disabled={blocked || intent?.status !== 'UNKNOWN'} onClick={() => invoice('lookup')}>Tra cứu hóa đơn gốc</Button>
        </div>
      </section>}
      {roles.includes('accountant') && <section className="panel panel-content" aria-label="Ghi sổ mô phỏng">
        <h2>Ghi sổ mô phỏng</h2>
        <label className="field">
          <span>Kịch bản ghi sổ DEV</span>
          <select value={postingScenario} disabled={busy} onChange={event => setPostingScenario(event.target.value)}>
            <option value="">Bình thường</option>
            <option value="wrong-amount">Sai tiền hàng và công nợ</option>
          </select>
        </label>
        <div className="button-row">
          <Button disabled={blocked || !source} onClick={() => accounting('receive')}>Tiếp nhận nguồn</Button>
          <Button disabled={blocked || !source} onClick={() => accounting('post')}>Ghi sổ nguồn</Button>
        </div>
      </section>}
      {roles.includes('reconciler') && <form className="panel panel-content" onSubmit={reconcile}>
        <h2>Đối soát sổ mô phỏng</h2>
        <fieldset disabled={blocked} className="sales-filter">
          <Field name="from" label="Từ (gồm)" placeholder="2026-11-23T03:10:00Z" help="Nhập ISO 8601 có múi giờ." required />
          <Field name="cutoff" label="Cutoff (loại trừ)" placeholder="2026-11-23T04:00:00Z" required />
          <Field name="asOf" label="Đối soát tính đến" placeholder="2026-11-23T04:10:00Z" required />
          <Button type="submit" variant="primary">Chạy đối soát</Button>
        </fieldset>
      </form>}
      {(roles.includes('reconciler') || roles.includes('maker') || roles.includes('checker')) &&
        <section className="panel panel-content" aria-label="Hồ sơ điều chỉnh">
          <h2>Hồ sơ điều chỉnh</h2>
          <div className="sales-filter">
            <Field label="Mã báo cáo" value={reportId} disabled={busy} onChange={event => setReportId(event.target.value.trim())} />
            {roles.includes('checker') && <Field label="Mã đề nghị điều chỉnh" value={correctionId} disabled={busy}
              onChange={event => setCorrectionId(event.target.value.trim())} />}
            {roles.includes('reconciler') && <Field label="Mã hồ sơ sai lệch" value={caseId} disabled={busy}
              onChange={event => setCaseId(event.target.value.trim())} />}
          </div>
          <div className="button-row">
            {roles.includes('reconciler') && <Button disabled={blocked || !uuid.test(reportId)} onClick={readReport}>Mở báo cáo</Button>}
            {roles.includes('maker') && <Button disabled={blocked || !source || !uuid.test(reportId)}
              onClick={() => correct('propose')}>Đề nghị điều chỉnh</Button>}
            {roles.includes('checker') && <Button disabled={blocked || !uuid.test(correctionId)}
              onClick={() => correct('approve')}>Duyệt điều chỉnh</Button>}
            {roles.includes('reconciler') && <Button disabled={blocked || !uuid.test(caseId) || !uuid.test(reportId)}
              onClick={() => correct('close')}>Đóng hồ sơ sai lệch</Button>}
          </div>
        </section>}
      {reconciliation && <ReconciliationResult result={reconciliation} sources={session.sources} />}
    </>}
    {!online && <p role="status">Cần kết nối để ghi nghiệp vụ.</p>}
    {notice && <p role="status" className="c3-source">{notice}</p>}
  </main>
}
