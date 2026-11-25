import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import { ASuccessorForm } from './ASuccessorForm'
import './customers.css'

const api = '/api/synthetic/app05/trip-close'
const revisionPattern = /^[0-9a-f]{64}$/i
const idPattern = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
const quantity = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
const ict = new Intl.DateTimeFormat('vi-VN', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Bangkok',
})
const ictInput = new Intl.DateTimeFormat('sv-SE', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Bangkok',
})
const states: Record<string, string> = {
  UNUSED: 'Chuyến chưa phát sinh nghiệp vụ',
  BLOCKED: 'Còn điều kiện cần xử lý',
  NEEDS_ASSIGNMENT: 'Còn việc cần giao người phụ trách',
  READY_WITH_WORK: 'Đủ điều kiện chuẩn bị, còn việc tồn',
  READY: 'Đủ điều kiện chuẩn bị',
}
const tripStates: Record<string, string> = {
  PREPARED: 'Đã chuẩn bị', IN_PROGRESS: 'Đang thực hiện', RECONCILING: 'Đang đối soát',
  CLOSED: 'Đã đóng', CLOSED_WITH_OPEN_CASES: 'Đã đóng với việc tồn lúc chốt', UNUSED: 'Chưa phát sinh nghiệp vụ',
}
const quantityColumns = [
  ['confirmed', 'Đã xác nhận'], ['remaining', 'Còn phải giao'], ['held', 'Đang giữ (HELD)'],
  ['scheduled', 'Đã xếp lịch'], ['loaded', 'Đã nạp'], ['delivered', 'Đã giao'], ['closedRemainder', 'Đã đóng phần còn'],
] as const

type Trip = { tripId: string; code: string; businessDate: string; status: string }
type Source = { sourceKind?: string | null; sourceId?: string | null; relatedId?: string | null; caseId?: string | null }
type FollowUp = { followUpId: string; assignee: string; dueAt: string; nextAction: string; reason: string }
type Work = Source & { sourceKind: string; sourceId: string; label: string; action: string; followUp: FollowUp | null }
type ALine = Record<typeof quantityColumns[number][0], number> & {
  orderId: string; orderLineId: string; confirmSourceId: string; skuId: string; unit: string; skuCode: string; onVehicle: number
}
type Readiness = Trip & {
  asOf: string; state: string; revision: string; coverage: { known: number | null; expected: number | null }
  blockers: (Source & { code: string; action: string })[]; aLines: ALine[]
  receivables: { deliveryId: string; amount: number | null; dueBusinessDate: string | null; unit: string; proven: boolean }[]
  work: Work[]; assignees: string[]; canAssign: boolean; canClose?: boolean
}
type CloseCommand = { commandId: string; expectedRevision: string }
type Closure = {
  closureId: string; tripId: string; commandId: string; handoffId: string
  status: 'CLOSED' | 'CLOSED_WITH_OPEN_CASES'; closedAt: string; closedBy: string; openWorkCount: number
  snapshot: Pick<Readiness, 'businessDate' | 'asOf' | 'revision' | 'coverage' | 'aLines' | 'receivables' | 'work'>
}
type ClosureHistory = { closure: Closure | null; currentOpenWorkCount: number | null }
type Assignment = Pick<FollowUp, 'assignee' | 'dueAt' | 'nextAction' | 'reason'> & {
  commandId: string; sourceKind: string; sourceId: string; expectedRevision: string
}
type LinkAccess = {
  orders: boolean; settlement: boolean; finance: boolean; returns: boolean; costs: boolean; refunds: boolean
}

const dateLabel = (date: string) => date.slice(0, 10).split('-').reverse().join('/')
const dateTimeLabel = (date: string) => ict.format(new Date(date)) + ' ICT'
const workKey = (source: Work) => source.sourceKind + ':' + source.sourceId

function restoreAssignment(key: string): Assignment | null {
  try {
    const item = JSON.parse(pendingJson(key) ?? 'null') as Assignment | null
    return item && idPattern.test(item.commandId) && idPattern.test(item.sourceId)
      && [item.sourceKind, item.assignee, item.dueAt, item.nextAction, item.reason, item.expectedRevision]
        .every(value => typeof value === 'string' && value.length > 0) ? item : null
  } catch { return null }
}

function restoreClose(key: string): CloseCommand | null {
  try {
    const item = JSON.parse(pendingJson(key) ?? 'null') as CloseCommand | null
    return item && idPattern.test(item.commandId) && revisionPattern.test(item.expectedRevision)
      ? { commandId: item.commandId, expectedRevision: item.expectedRevision } : null
  } catch { return null }
}

class TripCloseHttpError extends HttpError {
  constructor(status: number, readonly code?: string) { super(status) }
}

async function request<T>(token: string, path: string, body?: object): Promise<T> {
  const response = await requestSalesApi(api, token, path, body && JSON.stringify(body))
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { code?: string } | null
    throw new TripCloseHttpError(response.status, problem?.code)
  }
  return response.json() as Promise<T>
}

function coverageLabel({ expected, known }: Readiness['coverage']) {
  return expected == null || known == null ? 'chưa xác định' : known + '/' + expected
}

function SourceLink({ source, tripId, access }: { source: Source; tripId: string; access: LinkAccess }) {
  const { sourceId, relatedId, caseId, sourceKind } = source
  function link(href: string, label: string, authorized: boolean) {
    return <a className="page-link" href={href} target={authorized ? undefined : '_blank'}
      rel={authorized ? undefined : 'noopener'}
      title={authorized ? undefined : 'Mở tab mới và đăng nhập bằng danh tính có quyền xem hồ sơ.'}>{label}</a>
  }

  switch (sourceKind) {
    case 'A_ORDER':
    case 'A_REMAINDER': {
      const orderId = sourceKind === 'A_ORDER' ? sourceId : relatedId
      return orderId ? link('/work/orders?order=' + orderId + '&trip=' + tripId, 'Mở đơn A', access.orders) : null
    }
    case 'D3_SETTLEMENT':
      return link('/work/settlement?trip=' + tripId, 'Mở đối soát chuyến', access.settlement)
    case 'D4_RECONCILIATION':
      return relatedId && sourceId ? link('/work/finance?delivery=' + relatedId + '&source=' + sourceId
        + (caseId ? '&case=' + caseId : ''), caseId ? 'Mở hồ sơ đối soát' : 'Mở dòng nguồn kế toán', access.finance) : null
    case 'F05_RETURN':
      return sourceId ? link('/work/returns?source=' + sourceId + '#return-' + sourceId,
        'Mở hồ sơ trả hàng', access.returns) : null
    case 'F06_COST':
      return sourceId ? link('/work/cost-adjustments?source=' + sourceId, 'Mở đề nghị giá vốn', access.costs) : null
    case 'F08_REFUND':
      return sourceId ? link('/work/refunds?source=' + sourceId, 'Mở đề nghị hoàn tiền', access.refunds) : null
    default: return null
  }
}

export function TripCloseWorkspace({ token, identity, offline, tripId, onTripChange, access, tripClosure, aSuccessor }: {
  token: string; identity: Identity; offline: boolean; tripId: string; onTripChange: (id: string) => void; access: LinkAccess
  tripClosure: boolean; aSuccessor: boolean
}) {
  const [trips, setTrips] = useState<Trip[] | null>(null)
  const [error, setError] = useState(false)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    if (offline) return
    let current = true
    void request<Trip[]>(token, '/trips').then(items => {
      if (current) {
        setTrips(items)
        setError(false)
      }
    }).catch(() => {
      if (current) {
        setTrips(null)
        setError(true)
      }
    })
    return () => { current = false }
  }, [attempt, offline, token])

  const selected = trips?.find(trip => trip.tripId === tripId)
  return <div className="sales-pane trip-close">
    <section className="panel panel-content">
      <h2>Chuẩn bị đóng chuyến</h2>
      {offline ? <p role="status">Kết nối lại để xem điều kiện cuối chuyến.</p>
        : error ? <>
          <p role="alert">Không tải được chuyến trong phạm vi phân công.</p>
          <Button onClick={() => { setError(false); setAttempt(value => value + 1) }}>Tải lại danh sách</Button>
        </> : trips === null ? <p role="status">Đang tải danh sách chuyến…</p>
          : trips.length === 0 ? <p>Chưa có chuyến trong phạm vi phân công.</p>
            : <div className="field">
              <label htmlFor="trip-close-trip">Chuyến</label>
              <select id="trip-close-trip" value={selected?.tripId ?? ''} onChange={event => onTripChange(event.currentTarget.value)}>
                <option value="">Chọn chuyến</option>
                {trips.map(trip => <option key={trip.tripId} value={trip.tripId}>
                  {trip.code || trip.tripId}
                </option>)}
              </select>
            </div>}
    </section>
    {!offline && selected && <TripDetails key={selected.tripId} token={token} identity={identity} trip={selected}
      access={access} tripClosure={tripClosure} aSuccessor={aSuccessor} />}
  </div>
}

function TripDetails({ token, identity, trip, access, tripClosure, aSuccessor }: {
  token: string; identity: Identity; trip: Trip; access: LinkAccess; tripClosure: boolean; aSuccessor: boolean
}) {
  const { tripId } = trip
  const path = '/trips/' + tripId
  const storageKey = 'app05.trip-close.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, tripId])
  const closeKey = storageKey + '.close'
  const [pending, setPending] = useState(() => restoreAssignment(storageKey))
  const [pendingClose, setPendingClose] = useState(() => tripClosure ? restoreClose(closeKey) : null)
  const [confirmation, setConfirmation] = useState<CloseCommand['expectedRevision'] | null>(null)
  const [history, setHistory] = useState<{ data: ClosureHistory; attempt: number } | null>(null)
  const [receipt, setReceipt] = useState<Closure | null>(null)
  const [historyError, setHistoryError] = useState(false)
  const [closeNotice, setCloseNotice] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [result, setResult] = useState<{ data: Readiness; attempt: number } | null>(null)
  const [error, setError] = useState(false)
  const [notice, setNotice] = useState('')
  const [editor, setEditor] = useState<{ key: string; revision: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const inFlight = useRef(false)
  const data = result?.data
  const fresh = result?.attempt === attempt
  const closure = history?.data.closure ?? receipt
  const historyFresh = !tripClosure || (history?.attempt === attempt && !historyError)
  const closed = !!closure || data?.status === 'CLOSED' || data?.status === 'CLOSED_WITH_OPEN_CASES'
  const canEdit = !!data?.canAssign && fresh && historyFresh && !closed && !busy && !pending && !pendingClose && !error
  const canClose = tripClosure && data?.canClose && fresh && historyFresh && !closed && !busy && !pending && !pendingClose
  const canReplayClose = tripClosure && fresh && historyFresh && !closed && !busy && !pending

  useEffect(() => {
    let current = true
    void request<Readiness>(token, path).then(data => {
      if (current) {
        setResult({ data, attempt })
        setError(false)
      }
    }).catch(() => {
      if (current) {
        setResult(null)
        setError(true)
      }
    })
    if (tripClosure) {
      void request<ClosureHistory>(token, path + '/closure').then(data => {
        if (!current) return
        setHistory({ data, attempt })
        setHistoryError(false)
        if (data.closure) {
          const original = restoreClose(closeKey)
          if (original) {
            forgetPending(closeKey, original.commandId)
            setPendingClose(null)
            setCloseNotice(original.commandId === data.closure.commandId
              ? 'Đã tra được kết quả của yêu cầu đóng chuyến.'
              : 'Chuyến đã đóng theo yêu cầu khác. Xem biên bản để đối chiếu.')
          }
        }
      }).catch(failure => {
        if (!current) return
        setHistory(null)
        setHistoryError(true)
        if (failure instanceof HttpError && [401, 403].includes(failure.status)) setReceipt(null)
      })
    }
    return () => { current = false }
  }, [attempt, closeKey, path, token, tripClosure])

  function refresh() {
    setError(false)
    setHistoryError(false)
    setAttempt(value => value + 1)
  }

  async function send(command: Assignment) {
    if (!navigator.onLine || inFlight.current || !data?.canAssign || closed || pendingClose) return
    inFlight.current = true
    setBusy(true)
    setNotice('')
    rememberPending(storageKey, command)
    setPending(command)
    try {
      const receipt = await request<{ followUpId: string }>(token, path + '/follow-ups', command)
      if (!idPattern.test(receipt.followUpId)) throw new Error('Invalid follow-up acknowledgement')
      forgetPending(storageKey, command.commandId)
      setPending(null)
      setEditor(null)
      setNotice('Đã lưu giao việc.')
    } catch (failure) {
      if (failure instanceof HttpError && [400, 404, 409, 422].includes(failure.status)) {
        forgetPending(storageKey, command.commandId)
        setPending(null)
        if (failure.status === 409) setEditor(null)
        setNotice(failure.status === 409
          ? 'Dữ liệu hoặc mã yêu cầu đã thay đổi. Kiểm tra lại nguồn trước khi giao việc.'
          : 'API từ chối giao việc. Kiểm tra người phụ trách, hạn và nguồn.')
      } else {
        setNotice('Chưa xác nhận được kết quả. Kiểm tra quyền và kết nối trước khi gửi lại cùng yêu cầu.')
      }
    } finally {
      inFlight.current = false
      setBusy(false)
      refresh()
    }
  }

  async function closeTrip(command: CloseCommand) {
    if (!navigator.onLine || inFlight.current || !(pendingClose ? canReplayClose : canClose)) return
    inFlight.current = true
    setBusy(true)
    setConfirmation(null)
    setCloseNotice('')
    rememberPending(closeKey, command)
    setPendingClose(command)
    try {
      const outcome = await request<Closure>(token, path + '/close', command)
      if (outcome.commandId !== command.commandId || outcome.tripId !== tripId
        || !idPattern.test(outcome.closureId) || !['CLOSED', 'CLOSED_WITH_OPEN_CASES'].includes(outcome.status))
        throw new Error('Invalid closure acknowledgement')
      setReceipt(outcome)
      forgetPending(closeKey, command.commandId)
      setPendingClose(null)
      setCloseNotice('Đã đóng chuyến.')
    } catch (failure) {
      if (failure instanceof TripCloseHttpError && failure.code && [400, 409, 422].includes(failure.status)
        && failure.code !== 'COMMAND_PAYLOAD_CONFLICT') {
        forgetPending(closeKey, command.commandId)
        setPendingClose(null)
        setCloseNotice(failure.status === 409
          ? 'Dữ liệu hoặc trạng thái chuyến đã thay đổi. Kiểm tra lại trước khi đóng.'
          : 'Yêu cầu đóng chuyến bị từ chối. Kiểm tra điều kiện hiện tại.')
      } else {
        setCloseNotice('Chưa xác nhận được kết quả đóng chuyến. Tra cứu biên bản hoặc gửi lại đúng yêu cầu gốc.')
      }
    } finally {
      inFlight.current = false
      setBusy(false)
      refresh()
    }
  }

  return <>
    <section className="panel panel-content">
      <h3>Điều kiện cần xử lý</h3>
      <div className="button-row"><Button disabled={busy || (!fresh && !error)} onClick={refresh}>Tải lại đối soát</Button></div>
      {error ? <p role="alert">Không tải được dữ liệu đối soát chuyến.</p>
        : !fresh && <p role="status">Đang tải đối soát chuyến…</p>}
      {data && <>
        <p>{closed ? <strong>{tripStates[closure?.status ?? data.status]}</strong>
          : <><strong>{states[data.state] ?? data.state}</strong> · {tripStates[data.status] ?? data.status}</>}</p>
        <p>Ngày nghiệp vụ {dateLabel(data.businessDate)} · Tính đến <time dateTime={data.asOf}>{dateTimeLabel(data.asOf)}</time></p>
        <p>Độ phủ nguồn {coverageLabel(data.coverage)}</p>
        {data.blockers.length ? <ul className="c1-rows">
          {data.blockers.map((blocker, index) => <li key={blocker.code + (blocker.sourceId ?? index)} data-blocker-code={blocker.code}>
            <div className="c3-source">
              <strong>{blocker.action}</strong>
              {blocker.sourceId && <p>Mã nguồn {blocker.sourceId}</p>}
              <SourceLink source={blocker} tripId={tripId} access={access} />
            </div>
          </li>)}
        </ul> : !closed && <p>Không có điều kiện chặn trong dữ liệu đã đọc.</p>}
        {tripClosure && data.canClose && !closed && !pendingClose && <div className="button-row">
          <Button variant="primary" disabled={!canClose} onClick={() => setConfirmation(data.revision)}>Đóng chuyến</Button>
        </div>}
      </>}
    </section>
    {tripClosure && historyError && <section className="panel panel-content">
      <p role="alert">Không tải được biên bản đóng chuyến.</p>
      <div className="button-row"><Button disabled={busy} onClick={refresh}>Tải lại biên bản</Button></div>
    </section>}
    {closure && <TripClosureHistory closure={closure}
      currentOpenWorkCount={historyFresh ? history?.data.currentOpenWorkCount : undefined} access={access} />}
    {closeNotice && <p role="status">{closeNotice}</p>}
    {pendingClose && !closed && <section className="panel panel-content">
      <h3>Khôi phục yêu cầu đóng chuyến</h3>
      <p className="c3-source">Mã yêu cầu {pendingClose.commandId}</p>
      <div className="button-row">
        <Button disabled={busy} onClick={refresh}>Tra cứu kết quả đóng chuyến</Button>
        <Button disabled={!canReplayClose} onClick={() => void closeTrip(pendingClose)}>Gửi lại cùng yêu cầu đóng chuyến</Button>
      </div>
    </section>}
    {confirmation && data && <ConfirmTripClosure code={data.code} openWorkCount={data.work.length}
      enabled={canClose === true && confirmation === data.revision} onDismiss={() => setConfirmation(null)}
      onConfirm={() => void closeTrip({ commandId: crypto.randomUUID(), expectedRevision: confirmation })} />}
    {data && <>
      <section className="panel panel-content">
        <h3>{closed ? 'Số lượng hiện tại theo dòng A' : 'Số lượng theo dòng A'}</h3>
        <TripCloseQuantities lines={data.aLines} tripId={tripId} access={access} />
      </section>
      {data.receivables.length > 0 && <section className="panel panel-content">
        <h3>{closed ? 'Phải thu hiện tại theo lần giao' : 'Phải thu theo lần giao'}</h3>
        <TripCloseReceivables rows={data.receivables} />
      </section>}
      <section className="panel panel-content">
        <h3>Việc tồn</h3>
        {notice && <p role="status">{notice}</p>}
        {pending && <div className="c3-source">
          <p>Yêu cầu chờ xác nhận {pending.commandId}</p>
          <Button disabled={!data.canAssign || !fresh || busy} onClick={() => void send(pending)}>Gửi lại cùng yêu cầu</Button>
        </div>}
        {data.work.length ? <ul className="c1-rows">
          {data.work.map(source => <li key={workKey(source)} data-source-kind={source.sourceKind} data-source-id={source.sourceId}>
            <div className="c3-source">
              <h4>{source.label}</h4>
              <p>{source.action}</p>
              <p>Mã nguồn {source.sourceId}{source.caseId && ' · Mã hồ sơ ' + source.caseId}</p>
              <SourceLink source={source} tripId={tripId} access={access} />
              {source.followUp ? <dl className="sales-facts">
                <dt>Người phụ trách</dt><dd>{source.followUp.assignee}
                  {data.canAssign && !data.assignees.includes(source.followUp.assignee) && ' · Ngoài phân công hiện hành'}</dd>
                <dt>Hạn xử lý</dt><dd><time dateTime={source.followUp.dueAt}>{dateTimeLabel(source.followUp.dueAt)}</time></dd>
                <dt>Hành động tiếp theo</dt><dd>{source.followUp.nextAction}</dd>
                <dt>Lý do</dt><dd>{source.followUp.reason}</dd>
              </dl> : <p>Chưa giao người phụ trách và hạn xử lý.</p>}
              {aSuccessor && closed && source.sourceKind === 'A_REMAINDER' && source.followUp &&
                <ASuccessorSection token={token} originTripId={tripId} orderLineId={source.sourceId}
                  storageKey={storageKey + '.successor.' + source.sourceId}
                  unit={data.aLines.find(line => line.orderLineId === source.sourceId)?.unit ?? ''}
                  onApplied={refresh} />}
              {data.canAssign && !closed && (editor?.key === workKey(source) && editor.revision === data.revision ? <AssignmentForm
                source={source} assignees={data.assignees} enabled={canEdit} revision={editor.revision}
                cancel={() => setEditor(null)} submit={command => void send(command)} />
                : <Button disabled={!canEdit} onClick={() => setEditor({ key: workKey(source), revision: data.revision })}>
                  {source.followUp ? 'Cập nhật giao việc' : 'Giao việc'}
                </Button>)}
            </div>
          </li>)}
        </ul> : <p>Không có việc tồn theo nguồn đã đọc.</p>}
      </section>
    </>}
  </>
}

function ASuccessorSection({ token, originTripId, orderLineId, storageKey, unit, onApplied }: {
  token: string; originTripId: string; orderLineId: string; storageKey: string; unit: string; onApplied: () => void
}) {
  const path = '/trips/' + originTripId + '/a-remainders/' + orderLineId
  type Props = Parameters<typeof ASuccessorForm>[0]
  const load = useCallback<Props['load']>(() => request(token, path + '/successors'), [token, path])
  const apply = useCallback<Props['apply']>(command =>
    requestSalesApi(api, token, path + (command.bindingId ? '/transfer-held' : '/assign-successor'), JSON.stringify(command)),
  [token, path])
  return <ASuccessorForm load={load} apply={apply} storageKey={storageKey} unit={unit} onApplied={onApplied}
    source={{ originTripId, orderLineId }} />
}

function TripCloseQuantities({ lines, tripId, access, atClose = false }: {
  lines: ALine[]; tripId: string; access: LinkAccess; atClose?: boolean
}) {
  if (lines.length === 0) return <p>Chưa có dòng A đã xác nhận.</p>
  return <div className="app03-table-scroll">
    <table className="app03-handoff-table trip-close-table" aria-label={atClose ? 'Số lượng dòng A lúc đóng' : 'Số lượng theo dòng A'}>
      <thead><tr><th scope="col">Dòng đơn và nguồn</th>
        {quantityColumns.map(([column, label]) => <th scope="col" key={column}>{label}</th>)}
      </tr></thead>
      <tbody>{lines.map(line => <tr key={line.orderLineId} data-order-line-id={atClose ? undefined : line.orderLineId}>
        <td className="c3-source"><div>
          <strong>{line.skuCode ?? line.skuId}</strong>
          <p>Mã dòng {line.orderLineId}</p>
          <p>Xác nhận {line.confirmSourceId}</p>
          <SourceLink source={{ sourceKind: 'A_REMAINDER', relatedId: line.orderId }} tripId={tripId} access={access} />
        </div></td>
        {quantityColumns.map(([column, label]) => <td key={column}>
          <span className="app03-lot-label" aria-hidden="true">{label}</span>
          <div>
            <span data-quantity={column}>{quantity.format(line[column])} {line.unit}</span>
            {column === 'loaded' && <p>Trên xe {quantity.format(line.onVehicle)} {line.unit}</p>}
          </div>
        </td>)}
      </tr>)}</tbody>
    </table>
  </div>
}

function TripCloseReceivables({ rows }: { rows: Readiness['receivables'] }) {
  return <ul className="c1-rows">{rows.map(row => <li key={row.deliveryId}><div className="c3-source">
    <strong>{row.amount === null || !row.proven ? 'Chưa đủ nguồn xác nhận phải thu'
      : quantity.format(row.amount) + ' ' + row.unit}</strong>
    {row.dueBusinessDate && <p>Đến hạn {dateLabel(row.dueBusinessDate)}</p>}
    <p>Mã lần giao {row.deliveryId}</p>
  </div></li>)}</ul>
}

function TripClosureHistory({ closure, currentOpenWorkCount, access }: {
  closure: Closure; currentOpenWorkCount?: number | null; access: LinkAccess
}) {
  const snapshot = closure.snapshot
  return <section className="panel panel-content" data-closure-id={closure.closureId} data-closure-status={closure.status}>
    <h3>Biên bản đóng chuyến</h3>
    <p><strong>{tripStates[closure.status]}</strong></p>
    <dl className="sales-facts c3-source">
      <dt>Mã biên bản</dt><dd>{closure.closureId}</dd>
      <dt>Mã yêu cầu</dt><dd>{closure.commandId}</dd>
      <dt>Biên nhận bàn giao</dt><dd>{closure.handoffId}</dd>
      <dt>Người đóng chuyến</dt><dd>{closure.closedBy}</dd>
      <dt>Thời điểm đóng</dt><dd><time dateTime={closure.closedAt}>{dateTimeLabel(closure.closedAt)}</time></dd>
      <dt>Việc mở lúc đóng</dt><dd data-testid="closure-open-at-close">{closure.openWorkCount}</dd>
      <dt>Việc đang mở</dt><dd data-testid="closure-open-current">{currentOpenWorkCount ?? 'Chưa xác định'}</dd>
    </dl>
    <details>
      <summary>Ảnh chốt và nguồn</summary>
      <p>Ngày nghiệp vụ {dateLabel(snapshot.businessDate)} · Độ phủ nguồn {coverageLabel(snapshot.coverage)}</p>
      <p>Tính đến <time dateTime={snapshot.asOf}>{dateTimeLabel(snapshot.asOf)}</time></p>
      <p className="c3-source">Mã phiên bản nguồn {snapshot.revision}</p>
      <h4>Số lượng dòng A lúc đóng</h4>
      <TripCloseQuantities lines={snapshot.aLines} tripId={closure.tripId} access={access} atClose />
      {snapshot.receivables.length > 0 && <>
        <h4>Phải thu lúc đóng</h4>
        <TripCloseReceivables rows={snapshot.receivables} />
      </>}
      {snapshot.work.length > 0 && <ul className="c1-rows">
        {snapshot.work.map(item => <li key={workKey(item)}><div className="c3-source">
          <h4>{item.label}</h4>
          <p>Mã nguồn {item.sourceId}</p>
          {item.followUp && <>
            <p>{item.followUp.assignee} · <time dateTime={item.followUp.dueAt}>{dateTimeLabel(item.followUp.dueAt)}</time></p>
            <p>{item.followUp.nextAction}</p>
            <p>Lý do {item.followUp.reason}</p>
          </>}
          <SourceLink source={item} tripId={closure.tripId} access={access} />
        </div></li>)}
      </ul>}
    </details>
  </section>
}

function ConfirmTripClosure({ code, openWorkCount, enabled, onDismiss, onConfirm }: {
  code: string; openWorkCount: number; enabled: boolean; onDismiss: () => void; onConfirm: () => void
}) {
  const title = useId()
  const dialog = useRef<HTMLDialogElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  useLayoutEffect(() => {
    if (dialog.current && !dialog.current.open) dialog.current.showModal()
    cancel.current?.focus()
  }, [])
  return <dialog className="demo-dialog" ref={dialog} aria-labelledby={title} onClose={onDismiss}>
    <form className="panel-content" onSubmit={event => {
      event.preventDefault()
      if (enabled && navigator.onLine) onConfirm()
    }}>
      <h2 id={title}>Xác nhận đóng chuyến</h2>
      <p>Chuyến {code} · {openWorkCount} việc còn mở</p>
      <p>Biên bản lưu nguồn và phân công tại thời điểm đóng. Các thao tác bán, nạp và giao trên chuyến sẽ kết thúc.</p>
      <div className="button-row">
        <Button ref={cancel} onClick={() => dialog.current?.close()}>Quay lại</Button>
        <Button variant="primary" type="submit" disabled={!enabled}>Xác nhận đóng chuyến</Button>
      </div>
    </form>
  </dialog>
}

function AssignmentForm({ source, assignees, enabled, revision, cancel, submit }: {
  source: Work; assignees: string[]; enabled: boolean; revision: string; cancel: () => void; submit: (value: Assignment) => void
}) {
  const id = useId()
  const assigneeField = useRef<HTMLSelectElement>(null)
  const [error, setError] = useState('')
  useEffect(() => { assigneeField.current?.focus() }, [])

  function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!enabled || !navigator.onLine) return
    const values = new FormData(event.currentTarget)
    const text = (name: string) => String(values.get(name) ?? '').trim()
    const localDue = text('due')
    const assignee = text('assignee')
    const nextAction = text('nextAction')
    const reason = text('reason')
    if (!assignees.includes(assignee) || !/^\d{4}-\d\d-\d\dT\d\d:\d\d$/.test(localDue) || !nextAction || !reason) {
      setError('Kiểm tra người phụ trách, hạn xử lý, hành động và lý do.')
      return
    }
    setError('')
    submit({
      commandId: crypto.randomUUID(), sourceKind: source.sourceKind, sourceId: source.sourceId, assignee,
      dueAt: localDue + ':00+07:00', nextAction, reason, expectedRevision: revision,
    })
  }

  const followUp = source.followUp
  return <form onSubmit={save} aria-label={'Giao việc · ' + source.label}>
    {error && <p role="alert">{error}</p>}
    <fieldset className="sales-filter" disabled={!enabled}>
      <div className="field">
        <label htmlFor={id}>Người phụ trách</label>
        <select id={id} name="assignee" ref={assigneeField} required defaultValue={
          followUp && assignees.includes(followUp.assignee) ? followUp.assignee : ''}>
          <option value="">Chọn người phụ trách</option>
          {assignees.map(actor => <option key={actor}>{actor}</option>)}
        </select>
      </div>
      <Field name="due" label="Hạn xử lý (ICT)" type="datetime-local" required
        defaultValue={followUp ? ictInput.format(new Date(followUp.dueAt)).replace(' ', 'T') : undefined} />
      <Field name="nextAction" label="Hành động tiếp theo" required maxLength={240}
        defaultValue={followUp?.nextAction ?? source.action} />
      <Field name="reason" label="Lý do" required maxLength={240} defaultValue={followUp?.reason} />
      <div className="button-row">
        <Button type="submit" variant="primary">Lưu giao việc</Button>
        <Button onClick={cancel}>Hủy</Button>
      </div>
    </fieldset>
  </form>
}
