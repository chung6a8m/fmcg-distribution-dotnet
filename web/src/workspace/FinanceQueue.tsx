import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { HttpError, forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from '../sales/customer-api'
import { ReconciliationResult, type Reconciliation } from '../sales/PartnerRehearsalWorkspace'

const api = '/api/synthetic/app02d4'
const timestamp = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'medium', timeZone: 'Asia/Bangkok' })
const amount = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
type Source = {
  deliveryId: string; sourceLineId: string; seller: string; occurredAt: string
  quantity: number; gross: number; ar: number; cost: number; customer: string; sku: string; trip: string
  receiptId: string | null; postingId: string | null; reconciliationStatus: string | null
}
type Report = { id: string; fromUtc: string; cutoff: string; asOf: string; status: string }
type Case = { id: string; reportId: string; deliveryId: string; reason: string; closedReportId: string | null }
type Correction = { id: string; caseId: string; deliveryId: string; period: string; postingId: string | null }
type Queue = { asOf: string; sources: Source[]; reports: Report[]; cases: Case[]; corrections: Correction[] }
type Pending = {
  commandId: string; deliveryId: string; sourceLineId: string; action: 'receive' | 'post'
  body: { transportId: string; scenario?: string }
}
type PendingReport = { commandId: string; from: string; cutoff: string; asOf: string }
const guid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i

function restore(key: string): Pending | null {
  try {
    const value = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    return value && guid.test(value.commandId) && guid.test(value.deliveryId) && guid.test(value.sourceLineId)
      && (value.action === 'receive' || value.action === 'post') && value.body?.transportId === value.commandId ? value : null
  } catch { return null }
}

function restoreReport(key: string): PendingReport | null {
  try {
    const value = JSON.parse(pendingJson(key) ?? 'null') as PendingReport | null
    return value && guid.test(value.commandId) && typeof value.from === 'string'
      && typeof value.cutoff === 'string' && typeof value.asOf === 'string' ? value : null
  } catch { return null }
}

async function request<T>(token: string, path: string, body?: object) {
  const response = await requestSalesApi(api, token, '/accounting' + path, body === undefined ? undefined : JSON.stringify(body))
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

const format = (date: string) => timestamp.format(new Date(date))
const sourceLabel = (source: Source) => `${source.trip} · ${source.customer} · ${source.sku} · ${amount.format(source.gross)} VND`
const reportLabel = (report: Report) => `${format(report.fromUtc)} → ${format(report.cutoff)} · ${report.status} · ${report.id}`

export function FinanceQueue({ token, identity, roles, offline }: {
  token: string; identity: Identity; roles: string[]; offline: boolean
}) {
  const key = 'app03.accounting.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
  const reportKey = key + '.report'
  const [pendingReport, setPendingReport] = useState(() => restoreReport(reportKey))
  const [pending, setPending] = useState(() => restore(key))
  const [queue, setQueue] = useState<Queue | null>(null)
  const [selection, setSelection] = useState('')
  const [reportId, setReportId] = useState('')
  const [caseId, setCaseId] = useState('')
  const [correctionId, setCorrectionId] = useState('')
  const [scenario, setScenario] = useState('')
  const [report, setReport] = useState<Reconciliation | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [revision, setRevision] = useState(0)
  const [loadedRevision, setLoadedRevision] = useState(-1)
  const active = useRef(false)
  const source = queue?.sources.find(row => row.deliveryId === selection)
  const selectedReport = queue?.reports.find(row => row.id === reportId)
  const selectedCase = queue?.cases.find(row => row.id === caseId && !row.closedReportId)
  const selectedCorrection = queue?.corrections.find(row => row.id === correctionId)
  const blocked = offline || busy || loadedRevision !== revision || !queue
  const canWrite = !blocked && !pending && !pendingReport

  useEffect(() => {
    if (offline) {
      setLoadedRevision(-1)
      return
    }
    let current = true
    void request<Queue>(token, '/queue').then(data => {
      if (!current) return
      setQueue(data)
      setLoadedRevision(revision)
    }).catch(() => {
      if (!current) return
      setLoadedRevision(-1)
      setNotice('Không tải được hàng đợi kế toán.')
    })
    return () => { current = false }
  }, [token, offline, revision])

  function refresh() {
    setRevision(value => value + 1)
  }

  async function work(run: () => Promise<void>, recovery = false) {
    if (active.current || !navigator.onLine || (!recovery && !canWrite)) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await run() }
    catch (error) {
      setNotice(error instanceof HttpError && error.status < 500
        ? `API từ chối yêu cầu (${error.status}).`
        : 'Chưa rõ kết quả. Tải lại hàng đợi hoặc tra cứu nguồn gốc.')
    } finally {
      active.current = false
      setBusy(false)
      refresh()
    }
  }

  function complete(command: Pending, id: string) {
    forgetPending(key, command.commandId)
    setPending(null)
    setNotice((command.action === 'receive' ? 'Đã tiếp nhận · ' : 'Đã ghi sổ · ') + id)
  }

  function resolve(command: Pending, lookup: boolean) {
    void work(async () => {
      try {
        const base = '/' + command.deliveryId
        const result = await request<{ id: string; sourceLineId: string }>(token,
          lookup ? base + '/transfers/' + command.action + '/' + command.commandId : base + '/' + command.action,
          lookup ? undefined : command.body)
        if (result.sourceLineId !== command.sourceLineId || !guid.test(result.id)) throw new Error('Invalid source acknowledgement')
        complete(command, result.id)
      } catch (error) {
        if (lookup && error instanceof HttpError && error.status === 404) {
          setNotice('Chưa có kết quả cho yêu cầu gốc. Có thể gửi lại cùng yêu cầu.')
          return
        }
        if (!lookup && error instanceof HttpError && [400, 404, 409].includes(error.status)) {
          forgetPending(key, command.commandId)
          setPending(null)
        }
        throw error
      }
    }, true)
  }

  function transfer(action: Pending['action']) {
    if (!canWrite || active.current || !navigator.onLine || !source) return
    const commandId = crypto.randomUUID()
    const command = { commandId, deliveryId: source.deliveryId, sourceLineId: source.sourceLineId, action,
      body: { transportId: commandId, scenario: action === 'post' && scenario ? scenario : undefined } }
    rememberPending(key, command)
    setPending(command)
    resolve(command, false)
  }

  function resolveReport(command: PendingReport, lookup: boolean) {
    void work(async () => {
      try {
        const result = lookup
          ? await request<Reconciliation>(token, '/reports/' + command.commandId)
          : await request<Reconciliation>(token, '/reconcile', {
            requestId: command.commandId, from: command.from, cutoff: command.cutoff, asOf: command.asOf,
          })
        if (result.report.id !== command.commandId) throw new Error('Invalid report acknowledgement')
        forgetPending(reportKey, command.commandId)
        setPendingReport(null)
        setReport(result)
        setReportId(result.report.id)
      } catch (error) {
        if (lookup && error instanceof HttpError && error.status === 404) {
          setNotice('Chưa có báo cáo cho yêu cầu gốc. Có thể gửi lại cùng yêu cầu.')
          return
        }
        if (!lookup && error instanceof HttpError && [400, 409].includes(error.status)) {
          forgetPending(reportKey, command.commandId)
          setPendingReport(null)
        }
        throw error
      }
    }, true)
  }

  function reconcile(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canWrite || active.current || !navigator.onLine) return
    const values = new FormData(event.currentTarget)
    const command = { commandId: crypto.randomUUID(), from: String(values.get('from') ?? ''),
      cutoff: String(values.get('cutoff') ?? ''), asOf: String(values.get('asOf') ?? '') }
    rememberPending(reportKey, command)
    setPendingReport(command)
    resolveReport(command, false)
  }

  function correct(action: 'propose' | 'approve' | 'close') {
    void work(async () => {
      const result = action === 'propose' && selectedCase
        ? await request<{ status: string; id: string }>(token, '/reports/' + selectedCase.reportId + '/corrections', {
          deliveryId: selectedCase.deliveryId,
        })
        : action === 'approve' && selectedCorrection
          ? await request<{ status: string; id: string }>(token, '/corrections/' + selectedCorrection.id + '/approve', {})
          : action === 'close' && selectedCase && selectedReport
            ? await request<{ status: string; id: string }>(token, '/cases/' + selectedCase.id + '/close', { reportId }) : null
      if (result) setNotice(result.status + ' · ' + result.id)
    })
  }

  const options = queue?.cases.filter(row => !row.closedReportId && (!roles.includes('maker') || row.reason === 'AMOUNT_MISMATCH')) ?? []
  return <>
    <section className="panel panel-content" aria-label="Hàng đợi kế toán">
      <h2>Hàng đợi kế toán</h2>
      <div className="button-row"><Button disabled={busy || offline} onClick={refresh}>Tải lại hàng đợi</Button></div>
      {queue?.asOf && <p>Ảnh số liệu lúc {format(queue.asOf)}</p>}
      {queue && queue.sources.length === 0 && <p>Chưa có nguồn giao hàng trong phiên.</p>}
      {queue && queue.sources.length > 0 && <ul className="c1-rows">{queue.sources.map(row => <li key={row.sourceLineId} data-delivery={row.deliveryId}>
        <Button aria-pressed={row.deliveryId === selection} disabled={busy || pending !== null} onClick={() => setSelection(row.deliveryId)}>
          {sourceLabel(row)}
        </Button>
        <p>{row.receiptId ? 'Đã tiếp nhận' : 'Chưa tiếp nhận'} · {row.postingId ? 'Đã ghi sổ' : 'Chưa ghi sổ'}
          {' · '}{row.reconciliationStatus ?? 'Chưa đối soát'}</p>
      </li>)}</ul>}
      {source && <dl className="sales-facts">
        <dt>Lần giao</dt><dd className="d4-identifier">{source.deliveryId}</dd>
        <dt>Thời điểm giao</dt><dd>{format(source.occurredAt)}</dd>
        <dt>Dòng nguồn</dt><dd className="d4-identifier">{source.sourceLineId}</dd>
        <dt>Số lượng</dt><dd>{amount.format(source.quantity)} EA</dd>
        <dt>Tiền hàng</dt><dd>{amount.format(source.gross)} VND</dd>
        <dt>Phải thu tại lần giao</dt><dd>{amount.format(source.ar)} VND</dd>
        <dt>Giá vốn gốc</dt><dd>{amount.format(source.cost)} VND</dd>
        <dt>Mã tiếp nhận</dt><dd className="d4-identifier">{source.receiptId ?? 'Chưa tiếp nhận'}</dd>
        <dt>Mã ghi sổ</dt><dd className="d4-identifier">{source.postingId ?? 'Chưa ghi sổ'}</dd>
      </dl>}
      {roles.includes('accountant') && <div className="sales-filter">
        <div className="field"><label htmlFor="finance-posting-scenario">Kịch bản ghi sổ DEV</label>
          <select id="finance-posting-scenario" value={scenario} disabled={!canWrite} onChange={event => setScenario(event.currentTarget.value)}>
            <option value="">Bình thường</option>
            <option value="wrong-amount">Sai tiền hàng và công nợ</option>
          </select>
        </div>
        <Button disabled={!canWrite || !source || !!source.receiptId} onClick={() => transfer('receive')}>Tiếp nhận nguồn</Button>
        <Button disabled={!canWrite || !source?.receiptId || !!source.postingId} onClick={() => transfer('post')}>Ghi sổ nguồn</Button>
      </div>}
      {pending && <div>
        <p>Chưa rõ kết quả · {pending.sourceLineId} · {pending.commandId}</p>
        <div className="button-row">
          <Button disabled={offline || busy} onClick={() => resolve(pending, true)}>Tra cứu nguồn gốc</Button>
          <Button disabled={offline || busy} onClick={() => resolve(pending, false)}>Gửi lại cùng yêu cầu</Button>
        </div>
      </div>}
      {pendingReport && <div>
        <p>Chưa rõ kết quả đối soát · {pendingReport.commandId} · {pendingReport.from} → {pendingReport.cutoff}</p>
        <div className="button-row">
          <Button disabled={offline || busy} onClick={() => resolveReport(pendingReport, true)}>Tra cứu báo cáo gốc</Button>
          <Button disabled={offline || busy} onClick={() => resolveReport(pendingReport, false)}>Gửi lại cùng báo cáo</Button>
        </div>
      </div>}
      {notice && <p role="status">{notice}</p>}
    </section>
    {roles.includes('reconciler') && <form className="panel panel-content" onSubmit={reconcile}>
      <h2>Đối soát sổ mô phỏng</h2>
      <fieldset disabled={!canWrite} className="sales-filter">
        <Field name="from" label="Từ (gồm)" help="Nhập ISO 8601 có múi giờ." required />
        <Field name="cutoff" label="Cutoff (loại trừ)" required />
        <Field name="asOf" label="Đối soát tính đến" required />
        <Button type="submit" variant="primary">Chạy đối soát</Button>
      </fieldset>
    </form>}
    {(roles.includes('maker') || roles.includes('checker') || roles.includes('reconciler')) && <section className="panel panel-content">
      <h2>Hồ sơ sai lệch và điều chỉnh</h2>
      <div className="sales-filter">
        {(roles.includes('maker') || roles.includes('reconciler')) && <div className="field"><label htmlFor="finance-case">Hồ sơ sai lệch</label>
          <select id="finance-case" value={selectedCase?.id ?? ''} disabled={busy} onChange={event => setCaseId(event.currentTarget.value)}>
            <option value="">Chọn hồ sơ</option>
            {options.map(row => <option key={row.id} value={row.id}>{row.reason} · {row.deliveryId} · {row.id}</option>)}
          </select>
        </div>}
        {roles.includes('checker') && <div className="field"><label htmlFor="finance-correction">Đề nghị điều chỉnh</label>
          <select id="finance-correction" value={selectedCorrection?.id ?? ''} disabled={busy} onChange={event => setCorrectionId(event.currentTarget.value)}>
            <option value="">Chọn đề nghị</option>
            {queue?.corrections.map(row => <option key={row.id} value={row.id}>{row.period} · {row.deliveryId} · {row.id}
              {' · '}{row.postingId ? 'Đã ghi điều chỉnh' : 'Chờ ghi điều chỉnh'}</option>)}
          </select>
        </div>}
        {roles.includes('reconciler') && <div className="field"><label htmlFor="finance-report">Báo cáo đối soát</label>
          <select id="finance-report" value={selectedReport?.id ?? ''} disabled={busy} onChange={event => setReportId(event.currentTarget.value)}>
            <option value="">Chọn báo cáo</option>
            {queue?.reports.map(row => <option key={row.id} value={row.id}>{reportLabel(row)}</option>)}
          </select>
        </div>}
      </div>
      <div className="button-row">
        {roles.includes('maker') && <Button disabled={!canWrite || !selectedCase} onClick={() => correct('propose')}>Đề nghị điều chỉnh</Button>}
        {roles.includes('checker') && <Button disabled={!canWrite || !selectedCorrection || !!selectedCorrection.postingId}
          onClick={() => correct('approve')}>Duyệt điều chỉnh</Button>}
        {roles.includes('reconciler') && <>
          <Button disabled={!canWrite || !selectedReport} onClick={() => void work(async () =>
            setReport(await request<Reconciliation>(token, '/reports/' + reportId)))}>Mở báo cáo</Button>
          <Button disabled={!canWrite || !selectedCase || !selectedReport} onClick={() => correct('close')}>Đóng hồ sơ sai lệch</Button>
        </>}
      </div>
    </section>}
    {report && <ReconciliationResult result={report} sources={queue?.sources ?? []} vnd />}
  </>
}
