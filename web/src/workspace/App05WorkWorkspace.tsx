import { Fragment, useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from '../sales/customer-api'
import { SourceLink, dateTimeLabel, ictInput, type LinkAccess } from '../sales/TripCloseWorkspace'

const api = '/api/synthetic/app05/trip-close/trips'
const guid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
const localDateTime = /^\d{4}-\d\d-\d\dT\d\d:\d\d$/

type Assignment = {
  followUpId: string; assignee: string; dueAt: string; nextAction: string; reason: string
  commandId: string; actorId: string; assignedAt: string
}
type Work = {
  tripId: string; tripCode: string; sourceKind: string; sourceId: string; relatedId?: string; caseId?: string
  label: string; status: 'OPEN' | 'COMPLETED'; overdue: boolean; closingAssignment: Assignment
  assignment: Assignment; assignmentRevision: string; assignmentHistory: Assignment[]; canReassign: boolean; assignees: string[]
  aLine?: { remaining: number; held: number; scheduled: number; loaded: number; delivered: number; closedRemainder: number; unit: string }
  completionReason?: string; completionSourceIds: string[]
  successorTrips: { tripId: string; code: string }[]; reconciliationReportId?: string
}
type Command = {
  commandId: string; tripId: string; tripCode: string; kind: string; status: string
  resultId?: string; sourceId?: string; orderId?: string; code?: string; recordedAt?: string
}
type Reassign = {
  commandId: string; expectedAssignmentRevision: string; assignee: string; dueAt: string; reason: string
}
type Props = { token: string; identity: Identity; offline: boolean; access: LinkAccess; canReadWork: boolean }

const quantity = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
const completionLabels: Record<string, string> = {
  F06_ADJUSTED_BY_ANOTHER_PROPOSAL: 'Lần giao đã được điều chỉnh giá vốn theo đề nghị khác.',
  F08_CREDIT_SPENT_BY_OTHER_REFUNDS: 'Các khoản hoàn khác đã sử dụng số tiền có thể hoàn của credit này.',
}
const kindLabels: Record<string, string> = {
  TRIP_CLOSE: 'Đóng chuyến', FOLLOW_UP_ASSIGNMENT: 'Phân công',
  A_ASSIGN_SUCCESSOR: 'Chọn chuyến kế nhiệm', A_TRANSFER_HELD: 'Chuyển HELD',
  A_RESERVE_UNHELD: 'Giữ hàng cho chuyến kế nhiệm', A_CLOSURE_PROPOSAL: 'Đề nghị đóng phần A',
  A_CLOSURE_APPROVAL: 'Duyệt đóng phần A',
  A_SUCCESSOR_LOAD: 'Nạp hàng chuyến kế nhiệm', A_SUCCESSOR_DELIVERY: 'Giao hàng chuyến kế nhiệm',
}

async function read<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new Error('Không đọc được dữ liệu')
  return response.json() as Promise<T>
}

export function App05WorkWorkspace({ token, identity, offline, access, canReadWork }: Props) {
  const [refresh, setRefresh] = useState(0)
  const reload = () => setRefresh(value => value + 1)

  return <div className="sales-pane trip-close">
    {offline ? <section className="panel panel-content"><p role="status">Kết nối lại để xem việc và tra cứu lệnh.</p></section>
      : <>
        {canReadWork && <WorkBoard token={token} identity={identity} access={access} refresh={refresh} reload={reload} />}
        <CommandHistory token={token} refresh={refresh} />
      </>}
  </div>
}

function WorkBoard({ token, identity, access, refresh, reload }: Pick<Props, 'token' | 'identity' | 'access'> & {
  refresh: number; reload: () => void
}) {
  const [filter, setFilter] = useState('')
  const [loaded, setLoaded] = useState<{ work: Work[]; asOf: string; filter: string; refresh: number } | null>(null)
  const [error, setError] = useState(false)
  const fresh = loaded?.filter === filter && loaded.refresh === refresh

  useEffect(() => {
    let current = true
    void read<{ work: Work[]; asOf: string }>(token, '/work' + filter).then(result => {
      if (current) { setLoaded({ ...result, filter, refresh }); setError(false) }
    }).catch(() => { if (current) { setLoaded(null); setError(true) } })
    return () => { current = false }
  }, [token, filter, refresh])

  function applyFilter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const query = new URLSearchParams()
    for (const key of ['assignee', 'status']) {
      const value = String(form.get(key) ?? '').trim()
      if (value) query.set(key, value)
    }
    const due = String(form.get('dueBefore') ?? '')
    if (localDateTime.test(due)) query.set('dueBefore', due + ':00+07:00')
    setFilter('?' + query.toString())
    reload()
  }

  return <section className="panel panel-content">
    <h2>Bàn công việc ngày sau</h2>
    <form className="sales-filter" onSubmit={applyFilter}>
      <Field label="Người phụ trách" name="assignee" maxLength={128} />
      <Field label="Hạn trước (ICT)" name="dueBefore" type="datetime-local" />
      <div className="field">
        <label htmlFor="app05-work-status">Trạng thái việc</label>
        <select id="app05-work-status" name="status" defaultValue="">
          <option value="">Tất cả</option>
          <option value="OPEN">Đang mở</option>
          <option value="COMPLETED">Đã hoàn tất</option>
        </select>
      </div>
      <Button type="submit">Lọc công việc</Button>
    </form>
    {error ? <p role="alert">Không tải được công việc trong phạm vi phân công.</p> : !fresh
      ? <p role="status">Đang tải công việc…</p>
      : <>
        <p role="status">{loaded.work.length} việc · Tính đến <time dateTime={loaded.asOf}>{dateTimeLabel(loaded.asOf)}</time></p>
        <ul className="c1-rows">{loaded.work.map(item => <WorkRow key={item.assignment.followUpId}
          item={item} token={token} identity={identity} access={access} reload={reload} />)}</ul>
      </>}
  </section>
}

function WorkRow({ item, token, identity, access, reload }: Pick<Props, 'token' | 'identity' | 'access'> & {
  item: Work; reload: () => void
}) {
  const { assignment, aLine } = item
  const storageKey = 'app05.reassign.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, assignment.followUpId])
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const inFlight = useRef(false)
  const [notice, setNotice] = useState('')
  const [pending, setPending] = useState<Reassign | null>(() => {
    try {
      const value = JSON.parse(pendingJson(storageKey) ?? 'null')
      return value && guid.test(value.commandId) && /^[a-f0-9]{64}$/i.test(value.expectedAssignmentRevision)
        && ['assignee', 'dueAt', 'reason'].every(key => typeof value[key] === 'string') ? value : null
    } catch { return null }
  })
  useEffect(() => {
    if (!pending || pending.commandId !== assignment.commandId) return
    forgetPending(storageKey, pending.commandId)
    setPending(null)
    setEditing(false)
    setNotice('Đã tra được kết quả phân công từ máy chủ.')
  }, [assignment.commandId, pending, storageKey])
  const assigneeId = useId()
  const assigneeField = useRef<HTMLSelectElement>(null)
  useEffect(() => { if (editing) assigneeField.current?.focus() }, [editing])

  async function send(command: Reassign) {
    if (!navigator.onLine || inFlight.current || !item.canReassign) return
    inFlight.current = true
    setBusy(true)
    setNotice('')
    rememberPending(storageKey, command)
    setPending(command)
    try {
      const response = await requestSalesApi(api, token, '/' + item.tripId + '/work/' + assignment.followUpId + '/reassign', JSON.stringify(command))
      if (response.ok && (await response.json()).followUpId === assignment.followUpId) {
        forgetPending(storageKey, command.commandId)
        setPending(null)
        setEditing(false)
        reload()
      } else if ([400, 404, 409, 422].includes(response.status)) {
        const result = await response.json().catch(() => null)
        if (result?.code === 'COMMAND_PAYLOAD_CONFLICT') {
          setNotice('Mã lệnh đã được dùng với dữ liệu khác. Tra lịch sử lệnh để đối chiếu.')
        } else {
          forgetPending(storageKey, command.commandId)
          setPending(null)
          setNotice('Không lưu được phân công. Tải lại công việc và kiểm tra dữ liệu hiện tại.')
        }
      } else {
        setNotice('Chưa rõ kết quả. Tra lịch sử hoặc gửi lại đúng lệnh gốc.')
      }
    } catch {
      setNotice('Chưa rõ kết quả. Tra lịch sử hoặc gửi lại đúng lệnh gốc.')
    } finally {
      setBusy(false)
      inFlight.current = false
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const due = String(form.get('due') ?? '')
    const reason = String(form.get('reason') ?? '').trim()
    if (!localDateTime.test(due) || !reason) return
    void send({ commandId: crypto.randomUUID(), expectedAssignmentRevision: item.assignmentRevision,
      assignee: String(form.get('assignee') ?? ''), dueAt: due + ':00+07:00', reason })
  }

  return <li className="c3-source" data-testid={'app05-work-' + assignment.followUpId}>
    <div>
      <h3>{item.label}</h3>
      <p>{item.tripCode} · {item.status === 'COMPLETED' ? 'Đã hoàn tất' : item.overdue ? 'Đang mở · Quá hạn' : 'Đang mở'}</p>
      <p>{assignment.assignee} · <time dateTime={assignment.dueAt}>{dateTimeLabel(assignment.dueAt)}</time></p>
      <p>{assignment.nextAction} · Lý do phân công {assignment.reason}</p>
      {aLine && <dl className="sales-facts">
        {([
          ['remaining', 'Còn phải giao'], ['held', 'Đang giữ (HELD)'], ['scheduled', 'Đã xếp lịch'],
          ['loaded', 'Đã nạp'], ['delivered', 'Đã giao'], ['closedRemainder', 'Đã duyệt đóng'],
        ] as const).map(([field, label]) => <Fragment key={field}>
          <dt>{label}</dt><dd>{quantity.format(aLine[field])} {aLine.unit}</dd>
        </Fragment>)}
      </dl>}
      {item.completionReason && <p>{completionLabels[item.completionReason] ?? item.completionReason}</p>}
      {!!item.completionSourceIds.length && <details><summary>Nguồn hoàn tất</summary>
        <ul>{item.completionSourceIds.map(id => <li key={id}>{id}</li>)}</ul>
      </details>}
      <div className="button-row">
        <SourceLink source={item} tripId={item.tripId} access={access} />
        <a className="page-link" href={'/work/trip-close?trip=' + item.tripId + '&source=' + item.sourceId + '#work-' + item.sourceId}>Mở chuyến gốc</a>
        {item.successorTrips.map(trip => <a key={trip.tripId} className="page-link"
          href={'/work/trip-close?trip=' + trip.tripId}>Chuyến kế nhiệm {trip.code}</a>)}
      </div>
      {item.reconciliationReportId && <p>Mã báo cáo đối soát {item.reconciliationReportId}</p>}
      <details>
        <summary>Lịch sử phân công</summary>
        <p>Lúc đóng chuyến {item.closingAssignment.assignee} · {dateTimeLabel(item.closingAssignment.dueAt)}</p>
        <ul>{item.assignmentHistory.map(event => <li key={event.commandId}>
          {event.actorId} · {dateTimeLabel(event.assignedAt)} · {event.assignee} · Hạn {dateTimeLabel(event.dueAt)} · {event.reason}
        </li>)}</ul>
      </details>
      {notice && <p role="status">{notice}</p>}
      {pending ? <div className="button-row">
        <a className="page-link" href={'/work/app05-work?command=' + pending.commandId}>Tra lệnh {pending.commandId}</a>
        <Button disabled={busy || !item.canReassign} onClick={() => void send(pending)}>Gửi lại cùng lệnh phân công</Button>
      </div> : item.canReassign && (editing ? <form onSubmit={submit}>
        <fieldset className="sales-filter" disabled={busy}>
          <div className="field">
            <label htmlFor={assigneeId}>Người phụ trách mới</label>
            <select id={assigneeId} name="assignee" required ref={assigneeField}
              defaultValue={item.assignees.includes(assignment.assignee) ? assignment.assignee : ''}>
              <option value="">Chọn người phụ trách</option>
              {item.assignees.map(actor => <option key={actor}>{actor}</option>)}
            </select>
          </div>
          <Field label="Hạn xử lý (ICT)" name="due" type="datetime-local" required
            defaultValue={ictInput.format(new Date(assignment.dueAt)).replace(' ', 'T')} />
          <Field label="Lý do thay đổi" name="reason" required maxLength={240} />
          <div className="button-row">
            <Button type="submit" variant="primary">Lưu phân công</Button>
            <Button onClick={() => setEditing(false)}>Hủy</Button>
          </div>
        </fieldset>
      </form> : <Button onClick={() => setEditing(true)}>Đổi người hoặc hạn xử lý</Button>)}
    </div>
  </li>
}

function CommandHistory({ token, refresh }: { token: string; refresh: number }) {
  const linked = new URLSearchParams(location.search).get('command') ?? ''
  const [filter, setFilter] = useState(guid.test(linked) ? linked : '')
  const [trip, setTrip] = useState('')
  const [page, setPage] = useState(0)
  const [reload, setReload] = useState(0)
  const [result, setResult] = useState<{ items: Command[]; filter: string; trip: string; page: number; reload: number; refresh: number } | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    let current = true
    const query = new URLSearchParams({ page: String(page) })
    if (filter) query.set('commandId', filter)
    if (trip) query.set('tripId', trip)
    void read<Command[]>(token, '/commands?' + query.toString()).then(items => {
      if (current) { setResult({ items, filter, trip, page, reload, refresh }); setError('') }
    }).catch(() => { if (current) { setResult(null); setError('Không tải được lịch sử lệnh trong phạm vi hiện tại.') } })
    return () => { current = false }
  }, [token, filter, trip, page, reload, refresh])

  function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const id = String(form.get('command') ?? '').trim()
    const tripId = String(form.get('trip') ?? '').trim()
    if ((id && !guid.test(id)) || (tripId && !guid.test(tripId))) {
      setError('Kiểm tra định dạng mã lệnh và mã chuyến.')
      return
    }
    setError('')
    setFilter(id)
    setTrip(tripId)
    setPage(0)
    setReload(value => value + 1)
  }
  const fresh = result && result.filter === filter && result.trip === trip && result.page === page && result.reload === reload && result.refresh === refresh
  return <section className="panel panel-content">
    <h2>Lịch sử lệnh của tôi</h2>
    <form className="sales-filter" onSubmit={search}>
      <Field label="Mã lệnh" name="command" defaultValue={filter} maxLength={36} />
      <Field label="Mã chuyến" name="trip" defaultValue={trip} maxLength={36} />
      <Button type="submit">Tra lịch sử lệnh</Button>
    </form>
    {!filter && (page > 0 || (fresh && result.items.length === 50)) && <div className="button-row">
      <Button disabled={page === 0} onClick={() => setPage(value => value - 1)}>Trang trước</Button>
      <Button disabled={!fresh || result.items.length < 50} onClick={() => setPage(value => value + 1)}>Trang sau</Button>
    </div>}
    {error ? <p role="alert">{error}</p> : !fresh ? <p role="status">Đang tải lịch sử lệnh…</p>
      : result.items.length === 0 ? <p role="status">Chưa tìm thấy lệnh trong phạm vi hiện tại.
        Nếu kết quả chưa rõ, đăng nhập lại bằng đúng danh tính và chỉ gửi lại mã lệnh cùng dữ liệu gốc.</p>
      : <>
        <p role="status">{result.items.length} kết quả{filter ? '' : ' gần nhất'}</p>
        <ul className="c1-rows">{result.items.map(command => <li key={command.kind + ':' + command.tripId + ':' + command.commandId} className="c3-source"
          data-testid={'app05-command-' + command.commandId}>
          <div>
            <strong>{kindLabels[command.kind] ?? command.kind} · {command.status === 'APPLIED' ? 'Đã áp dụng'
              : command.status === 'REJECTED' ? 'Đã từ chối' : 'Đang chờ xử lý'}</strong>
            <p>{command.tripCode}{command.recordedAt && ' · ' + dateTimeLabel(command.recordedAt)}</p>
            <p>Mã lệnh {command.commandId}</p>
            {command.sourceId && <p>Mã nguồn {command.sourceId}</p>}
            {command.resultId && <p>Mã kết quả {command.resultId}</p>}
            {command.code && <p>{command.code}</p>}
            {command.kind === 'A_SUCCESSOR_LOAD' ? <a className="page-link" href={'/work/load?trip=' + command.tripId}>Mở chuyến nạp</a>
              : command.kind === 'A_SUCCESSOR_DELIVERY' ? <a className="page-link" href={'/work/delivery?trip=' + command.tripId + (command.resultId ? '&source=' + command.resultId : '')}>
                  {command.resultId ? 'Mở nguồn giao' : 'Mở chuyến giao'}</a>
              : command.kind === 'A_RESERVE_UNHELD' ? command.orderId && <a className="page-link"
                href={'/work/orders?order=' + command.orderId + '&trip=' + command.tripId}>Mở đơn A</a>
              : <a className="page-link" href={'/work/trip-close?trip=' + command.tripId}>Mở chuyến gốc</a>}
          </div>
        </li>)}</ul>
      </>}
  </section>
}
