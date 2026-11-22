import { useRef, useState, useSyncExternalStore, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02d3'
type Role = 'sales' | 'cashier' | 'warehouse'
type Kind = 'offer' | 'accept' | 'unload'
type Trip = { tripId: string; label: string }
type Session = { identity: Identity & { roles: Role[] }; token: string; trips: Trip[] }
type Report = Trip & {
  tripStatus: string; custody: number; fund: number; arA: number; arB: number
  mainQty: number; mainCost: number; tripQty: number; tripCost: number; aHold: number; bQuota: number
  soldQty: number; cogs: number; customerCredit: number; advance: number; totalAllocated: number; asOfUtc: string
  pendingOfferId?: string | null; pendingOfferAmount?: number | null
}
type Base = { producerId: string; commandId: string; sourceNumber: string; tripId: string }
type Command =
  | (Base & { kind: 'offer'; offeredAmount: number })
  | (Base & { kind: 'accept'; offerId: string; countEvidenceRef: string })
  | (Base & { kind: 'unload' })
type Outcome = { status: string; reasonCode?: string | null; sourceId?: string | null }

const disposition: Record<Kind, string> = {
  offer: 'Đã gửi đề nghị nộp tiền.',
  accept: 'Đã ghi nhận thủ quỹ nhận tiền.',
  unload: 'Đã trả hàng tồn xe về kho.',
}
const rejection: Record<string, string> = {
  CUSTODY_INSUFFICIENT: 'Số tiền đề nghị vượt số tiền thực giữ.',
  CUSTODY_NOT_SETTLED: 'Còn tiền chưa nộp quỹ.',
  CASHIER_COUNT_NOT_VERIFIED: 'Chứng cứ kiểm đếm chưa khớp đề nghị.',
  CASHIER_MUST_BE_INDEPENDENT: 'Cần thủ quỹ độc lập với người gửi đề nghị.',
  OFFER_ALREADY_ACCEPTED: 'Đề nghị đã được nhận.',
  TRIP_ALREADY_UNLOADED: 'Chuyến đã trả hàng tồn xe.',
  TRIP_NOT_ELIGIBLE: 'Chuyến hiện không cho phép thao tác này.',
}
const tripStatus: Record<string, string> = {
  PREPARED: 'Đã chuẩn bị', IN_PROGRESS: 'Đang thực hiện', RECONCILING: 'Đang đối soát',
  CLOSED: 'Đã đóng', CLOSED_WITH_OPEN_CASES: 'Đã đóng, còn hồ sơ xử lý',
}
const amount = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
const instant = new Intl.DateTimeFormat('vi-VN', {
  dateStyle: 'short', timeStyle: 'short', timeZone: 'Asia/Bangkok',
})
const uuid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i

function watchOnline(update: () => void) {
  window.addEventListener('online', update)
  window.addEventListener('offline', update)
  return () => {
    window.removeEventListener('online', update)
    window.removeEventListener('offline', update)
  }
}

function pendingKey(identity: Identity) {
  return 'app02d3.pending.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function restore(identity: Identity): Command | null {
  try {
    const saved = pendingJson(pendingKey(identity))
    if (!saved) return null
    const command = JSON.parse(saved) as Command
    if (command.producerId !== identity.actorId || !uuid.test(command.commandId)
      || !uuid.test(command.sourceNumber) || !uuid.test(command.tripId)) return null
    switch (command.kind) {
      case 'offer': return Number.isFinite(command.offeredAmount) && command.offeredAmount > 0 ? command : null
      case 'accept': return uuid.test(command.offerId) && typeof command.countEvidenceRef === 'string'
        && command.countEvidenceRef.length > 0 && command.countEvidenceRef.length <= 128 ? command : null
      case 'unload': return command
      default: return null
    }
  } catch { return null }
}

async function read<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function failure(error: unknown, unknown: boolean) {
  if (error instanceof HttpError) {
    if (error.status === 401) return 'Phiên DEV đã hết hạn hoặc mã truy cập không hợp lệ.'
    if (error.status === 403) return 'Quyền đã thay đổi hoặc bạn không được phân công cho chuyến này.'
    if (!unknown && error.status === 404) return 'Chuyến không còn khả dụng trong phiên DEV này.'
  }
  return unknown ? 'Chưa rõ kết quả. Tra cứu trước khi tạo yêu cầu khác.' : 'Không tải được dữ liệu chuyến.'
}

export function MixedTripWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [trip, setTrip] = useState('')
  const [report, setReport] = useState<Report | null>(null)
  const [pending, setPending] = useState<Command | null>(null)
  const [busy, setBusy] = useState(false)
  const [source, setSource] = useState('')
  const [notice, setNotice] = useState('')
  const active = useRef(false)
  const online = useSyncExternalStore(watchOnline, () => navigator.onLine)
  const blocked = busy || !online

  async function work(operation: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    try { await operation() }
    catch (error) { setNotice(failure(error, false)) }
    finally {
      setBusy(false)
      active.current = false
    }
  }

  async function showTrip(current: Session, id: string) {
    setTrip(id)
    setReport(null)
    if (id) setReport(await read<Report>(current.token, '/trips/' + encodeURIComponent(id)))
  }

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    void work(async () => {
      const [identity, trips] = await Promise.all([
        read<Session['identity']>(token, '/session'), read<Trip[]>(token, '/trips'),
      ])
      const next = { identity, token, trips }
      const saved = restore(identity)
      setSession(next)
      setPending(saved)
      setSource('')
      setNotice(trips.length ? '' : 'Phiên này chưa được phân công chuyến.')
      await showTrip(next, trips.find(item => item.tripId === saved?.tripId)?.tripId ?? trips[0]?.tripId ?? '')
    })
  }

  function resolve(command: Command, lookup: boolean, fresh = false) {
    if (!session) return
    const { token, identity } = session
    void work(async () => {
      if (!lookup) {
        rememberPending(pendingKey(identity), command)
        setPending(command)
      }
      setSource('')
      try {
        const { kind, ...body } = command
        const path = lookup
          ? ['/trips', encodeURIComponent(command.tripId), 'commands', encodeURIComponent(command.producerId), encodeURIComponent(command.commandId)].join('/')
          : '/' + kind
        const response = await requestSalesApi(api, token, path, lookup ? undefined : JSON.stringify(body))
        if (lookup && response.status === 404) {
          setNotice('Chưa có kết quả. Có thể gửi lại đúng yêu cầu đã lưu.')
          return
        }
        if (!response.ok && response.status !== 409) throw new HttpError(response.status)
        const result = await response.json() as Outcome
        if (result.status === 'CONFLICT') {
          setNotice('Mã yêu cầu đã được dùng với nội dung khác. Hãy tra cứu kết quả.')
          return
        }
        if (result.status !== 'REJECTED' && !(result.status === 'APPLIED' && result.sourceId))
          throw new Error('Missing durable command outcome')
        forgetPending(pendingKey(identity), command.commandId)
        setPending(null)
        const message = result.status === 'APPLIED' ? disposition[command.kind]
          : rejection[result.reasonCode ?? ''] ?? 'Yêu cầu bị từ chối' + (result.reasonCode ? ' · ' + result.reasonCode : '.')
        if (result.sourceId) setSource(result.sourceId)
        try {
          await showTrip(session, command.tripId)
          setNotice(message)
        } catch { setNotice(message + ' Hãy tải lại số liệu chuyến.') }
      } catch (error) {
        if (fresh && error instanceof HttpError && [401, 403].includes(error.status)) {
          forgetPending(pendingKey(identity), command.commandId)
          setPending(null)
        }
        setNotice(failure(error, true))
      }
    })
  }

  function submit(event: FormEvent<HTMLFormElement>, kind: Kind) {
    event.preventDefault()
    if (!session || !report || pending || active.current || !navigator.onLine) return
    const base = {
      producerId: session.identity.actorId, commandId: crypto.randomUUID(),
      sourceNumber: crypto.randomUUID(), tripId: report.tripId,
    }
    const form = new FormData(event.currentTarget)
    if (kind === 'offer') {
      const value = String(form.get('offeredAmount') ?? '').trim()
      const offeredAmount = Number(value)
      if (!/^\d+(?:\.\d{1,4})?$/.test(value) || !Number.isFinite(offeredAmount) || offeredAmount <= 0) {
        setNotice('Nhập số tiền dương với tối đa bốn chữ số thập phân.')
        return
      }
      resolve({ ...base, kind, offeredAmount }, false, true)
    } else if (kind === 'accept') {
      const countEvidenceRef = String(form.get('countEvidenceRef') ?? '').trim()
      if (!report.pendingOfferId || !countEvidenceRef || countEvidenceRef.length > 128) return
      resolve({ ...base, kind, offerId: report.pendingOfferId, countEvidenceRef }, false, true)
    } else resolve({ ...base, kind }, false, true)
  }

  const canPost = report?.tripStatus === 'PREPARED' || report?.tripStatus === 'IN_PROGRESS'
  const roles = session?.identity.roles ?? []
  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div><h1>Đối soát chuyến Mixed</h1><p>Windows LocalDB · DEV</p></div>
      <div className="button-row"><ThemeSelect /><a className="page-link" href="/sales">Workspace Sales</a></div>
    </header>
    {!session ? <form className="panel panel-content sales-filter" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={blocked}>Kết nối</Button>
    </form> : <section className="panel panel-content" aria-label="Chuyến được phân công">
      <div className="sales-filter">
        <label className="field">
          <span>Chuyến</span>
          <select value={trip} disabled={blocked || Boolean(pending)} onChange={event => {
            const id = event.currentTarget.value
            setSource('')
            setNotice('')
            void work(() => showTrip(session, id))
          }}>
            <option value="">Chọn chuyến</option>
            {session.trips.map(item => <option key={item.tripId} value={item.tripId}>{item.label}</option>)}
          </select>
        </label>
        <div className="button-row">
          <Button disabled={blocked || !trip} onClick={() => { void work(() => showTrip(session, trip)) }}>Tải lại chuyến</Button>
          <Button disabled={busy} onClick={() => {
            setSession(null)
            setTrip('')
            setReport(null)
            setPending(null)
            setSource('')
            setNotice('')
          }}>Đổi phiên DEV</Button>
        </div>
      </div>
    </section>}
    {report && <section className="panel panel-content" aria-label="Đối soát chuyến">
      <h2>{report.label}</h2>
      <p>{tripStatus[report.tripStatus] ?? report.tripStatus}</p>
      {report.asOfUtc && <p>Số liệu đến <time dateTime={report.asOfUtc}>{instant.format(new Date(report.asOfUtc))}</time></p>}
      <dl className="sales-filter">
        <div><dt>Kho chính</dt><dd>{amount.format(report.mainQty)} EA / {amount.format(report.mainCost)}</dd></div>
        <div><dt>Trên xe</dt><dd>{amount.format(report.tripQty)} EA / {amount.format(report.tripCost)}</dd></div>
        <div><dt>Quyền A còn giữ</dt><dd>{amount.format(report.aHold)} EA</dd></div>
        <div><dt>Quota B còn lại</dt><dd>{amount.format(report.bQuota)} EA</dd></div>
        <div><dt>Công nợ A</dt><dd>{amount.format(report.arA)}</dd></div>
        <div><dt>Công nợ B</dt><dd>{amount.format(report.arB)}</dd></div>
        <div><dt>Tiền thực giữ</dt><dd>{amount.format(report.custody)}</dd></div>
        <div><dt>Quỹ đã nhận</dt><dd>{amount.format(report.fund)}</dd></div>
        <div><dt>Đã bán và giao</dt><dd>{amount.format(report.soldQty)} EA</dd></div>
        <div><dt>Giá vốn</dt><dd>{amount.format(report.cogs)}</dd></div>
        <div><dt>Đã phân bổ</dt><dd>{amount.format(report.totalAllocated)}</dd></div>
        <div><dt>CustomerCredit</dt><dd>{amount.format(report.customerCredit)}</dd></div>
        <div><dt>Tạm ứng</dt><dd>{amount.format(report.advance)}</dd></div>
      </dl>
      {report.pendingOfferId && <p>Đề nghị đang chờ nhận {amount.format(report.pendingOfferAmount ?? 0)}.</p>}
    </section>}
    {report && canPost && roles.includes('sales') && !report.pendingOfferId &&
      <form className="panel panel-content" onSubmit={event => submit(event, 'offer')}>
        <h2>Đề nghị nộp tiền</h2>
        <fieldset className="sales-filter" disabled={blocked || Boolean(pending)}>
          <Field name="offeredAmount" label="Số tiền đề nghị nộp" type="number" min="0.0001" step="0.0001" required />
          <Button type="submit" variant="primary">Gửi đề nghị nộp tiền</Button>
        </fieldset>
      </form>}
    {report && canPost && roles.includes('cashier') && report.pendingOfferId &&
      <form className="panel panel-content" onSubmit={event => submit(event, 'accept')}>
        <h2>Thủ quỹ nhận tiền</h2>
        <fieldset className="sales-filter" disabled={blocked || Boolean(pending)}>
          <Field name="countEvidenceRef" label="Chứng cứ kiểm đếm" maxLength={128} required />
          <Button type="submit" variant="primary">Xác nhận đã nhận tiền</Button>
        </fieldset>
      </form>}
    {report && canPost && roles.includes('warehouse') &&
      <form className="panel panel-content" onSubmit={event => submit(event, 'unload')}>
        <h2>Trả hàng về kho</h2>
        {(report.custody !== 0 || report.pendingOfferId) && <p>Hoàn tất nộp và nhận tiền trước khi trả xe.</p>}
        <Button type="submit" variant="primary" disabled={blocked || Boolean(pending) || report.custody !== 0 || !!report.pendingOfferId}>
          Trả hàng tồn xe
        </Button>
      </form>}
    {pending && <section className="panel panel-content" aria-label="Khôi phục yêu cầu">
      <h2>Khôi phục yêu cầu</h2>
      <p className="c3-source">Mã yêu cầu {pending.commandId}</p>
      <div className="button-row">
        <Button disabled={blocked} onClick={() => resolve(pending, true)}>Tra cứu yêu cầu</Button>
        <Button disabled={blocked} onClick={() => resolve(pending, false)}>Gửi lại cùng yêu cầu</Button>
      </div>
    </section>}
    {!online && <p role="status">Cần kết nối để ghi nghiệp vụ.</p>}
    {notice && <p role="status">{notice}</p>}
    {source && <p className="c3-source">Mã nguồn {source}</p>}
  </main>
}
