import { useEffect, useId, useReducer, useState } from 'react'
import { Badge, Button, Field, Notice, ThemeSelect, type Density } from './controls'
import { parseDemoQuantity } from './decimal'
import {
  canCheck, canLookup, canSend, isLocked, newWorkflow, tryRestoreWorkflow, workflowReducer,
  type LookupFixture, type Phase, type Workflow
} from './command-recovery'

type SourceKey = 'partial' | 'zero' | 'unknown-total' | 'stale' | 'empty'
type Report = { id: string; amount: number }
type SourceFixture = {
  asOf: string
  clock: string
  scope: string
  expected: number | null
  reports: Report[]
  missing: string[]
  stale?: boolean
}
const sources: Record<SourceKey, SourceFixture> = {
  partial: {
    asOf: '2026-11-20T09:25:00+07:00', clock: '20/11/2026 09:25 +07:00',
    scope: '3 chuyến đã chọn · tenant giả lập A', expected: 3,
    reports: [{ id: 'SIM-TRIP-01', amount: 0 }, { id: 'SIM-TRIP-02', amount: 250000 }],
    missing: ['SIM-TRIP-03']
  },
  zero: {
    asOf: '2026-11-20T09:20:00+07:00', clock: '20/11/2026 09:20 +07:00',
    scope: '2 chuyến đã chọn · tenant giả lập A', expected: 2,
    reports: [{ id: 'SIM-TRIP-01', amount: 0 }, { id: 'SIM-TRIP-02', amount: 0 }], missing: []
  },
  'unknown-total': {
    asOf: '2026-11-20T09:25:00+07:00', clock: '20/11/2026 09:25 +07:00',
    scope: 'Nguồn chưa chốt danh sách · tenant giả lập A', expected: null,
    reports: [{ id: 'SIM-TRIP-01', amount: 0 }, { id: 'SIM-TRIP-02', amount: 250000 }], missing: []
  },
  stale: {
    asOf: '2026-11-19T17:15:00+07:00', clock: '19/11/2026 17:15 +07:00',
    scope: '3 chuyến đã chọn · tenant giả lập A', expected: 3,
    reports: [{ id: 'SIM-TRIP-01', amount: 0 }], missing: ['SIM-TRIP-02', 'SIM-TRIP-03'],
    stale: true
  },
  empty: {
    asOf: '2026-11-20T09:25:00+07:00', clock: '20/11/2026 09:25 +07:00',
    scope: 'Bộ lọc không có báo cáo · tenant giả lập A', expected: 0, reports: [], missing: []
  }
}
const formatter = new Intl.NumberFormat('vi-VN')
const phaseText: Record<Phase, { label: string; tone: 'info' | 'warning' | 'success' | 'danger'; explanation: string }> = {
  draft: { label: 'Chưa gửi', tone: 'info', explanation: 'Chưa có căn cứ ghi nhận nghiệp vụ.' },
  checked: { label: 'Đã kiểm tra trước gửi', tone: 'info', explanation: 'Chỉ preflight cục bộ; server phải kiểm lại tại commit.' },
  pending: { label: 'PENDING · chưa có ACK', tone: 'warning', explanation: 'Đã bấm gửi mô phỏng, chưa biết server có tiếp nhận hay chưa.' },
  unknown: { label: 'UNKNOWN · chưa rõ kết quả', tone: 'warning', explanation: 'Không suy rollback, rejected hoặc POSTED từ việc mất phản hồi.' },
  ack: { label: 'Đã ACK tiếp nhận', tone: 'info', explanation: 'ACK tiếp nhận bền không phải business POSTED.' },
  waiting: { label: 'Chờ dependency', tone: 'warning', explanation: 'Đã tiếp nhận nhưng chưa thể khẳng định POSTED.' },
  posted: { label: 'POSTED (mô phỏng)', tone: 'success', explanation: 'Chỉ là fixture lookup; chưa có chứng cứ đối soát, invoice hoặc kế toán.' },
  rejected: { label: 'Rejected (mô phỏng)', tone: 'danger', explanation: 'Kết quả từ chối trước hiệu ứng trong fixture, không phải lỗi mất mạng.' },
  conflict: { label: 'Identity conflict', tone: 'danger', explanation: 'Cùng ID nhưng nội dung khác. Không đổi ID để lách xung đột.' }
}
const scenarios: { value: LookupFixture; label: string }[] = [
  { value: 'no-result', label: 'Không tìm thấy business outcome → giữ UNKNOWN' },
  { value: 'ack', label: 'Durable intake ACK (chưa posted)' },
  { value: 'waiting', label: 'Đã nhận, chờ dependency' },
  { value: 'posted', label: 'POSTED (mô phỏng)' },
  { value: 'rejected', label: 'Rejected trước hiệu ứng' },
  { value: 'conflict', label: 'Cùng ID, payload khác → conflict' }
]

function SourcePanel({ permission }: { permission: Workflow['permission'] }) {
  const [mode, setMode] = useState<SourceKey>('partial')
  const [details, setDetails] = useState(false)
  const source = sources[mode]
  const accessible = permission !== 'none'
  const count = source.reports.length
  const knownTotal = source.reports.reduce((sum, report) => sum + report.amount, 0)
  const complete = source.expected !== null && count === source.expected && count > 0
  return <section className="panel" aria-labelledby="ui03-source-title">
    <div className="panel-heading">
      <h2 id="ui03-source-title">Nguồn, phạm vi và độ đầy đủ</h2>
      <Badge tone={accessible ? complete ? 'success' : 'warning' : 'danger'}>
        {!accessible ? 'Không có quyền đọc' : source.stale ? 'Bản chụp cũ' : count === 0 ? 'Chưa có dữ liệu'
          : complete ? 'Đủ nguồn mẫu' : 'Chưa đủ nguồn'}
      </Badge>
    </div>
    <div className="panel-content">
      <div className="field">
        <label htmlFor="ui03-source-scenario">Tình huống nguồn (mô phỏng)</label>
        <select id="ui03-source-scenario" value={mode} disabled={!accessible} onChange={e => {
          setMode(e.currentTarget.value as SourceKey)
          setDetails(false)
        }}>
          {!accessible ? <option value={mode}>Không có quyền xem kịch bản nguồn</option> : <>
            <option value="partial">2/3 báo cáo (một nguồn bằng 0)</option>
            <option value="zero">2/2 báo cáo, tổng thực bằng 0</option>
            <option value="unknown-total">N chưa biết; không hiển thị n/N</option>
            <option value="stale">Không làm mới được; giữ asOf cũ</option>
            <option value="empty">Chưa có dữ liệu (không phải tổng 0)</option>
          </>}
        </select>
      </div>
      {!accessible ? <Notice tone="danger" title="Quyền đọc bị từ chối">
        Không hiện giá trị hoặc danh sách nguồn khi quyền truy cập bị thu hồi (giả lập).
      </Notice> : <>
        <dl className="ui03-source-facts">
          <div><dt>Phạm vi</dt><dd>{source.scope}</dd></div>
          <div><dt>Nguồn</dt><dd>Báo cáo chuyến · dữ liệu fixture, không phải API</dd></div>
          <div><dt>As of (theo nguồn)</dt><dd><time dateTime={source.asOf}>{source.clock}</time></dd></div>
          <div><dt>Độ đầy đủ</dt><dd>{count === 0 && source.expected === 0
            ? 'Không có báo cáo trong phạm vi này'
            : source.expected === null ? count + ' nguồn đã biết · chưa xác định tổng số nguồn'
              : count + '/' + source.expected + ' báo cáo chuyến'}</dd></div>
          <div><dt>Doanh thu báo cáo</dt><dd>{count === 0 ? 'Chưa có dữ liệu'
            : complete ? formatter.format(knownTotal) + ' ₫ · tổng đầy đủ'
              : formatter.format(knownTotal) + ' ₫ · tổng phần đã biết'}</dd></div>
        </dl>
        {source.stale && <Notice tone="warning" title="Không làm mới được">
          Hiển thị bản chụp cũ đúng mốc nguồn nêu trên; không gán giờ thiết bị làm asOf mới.
        </Notice>}
        {!complete && count > 0 && <p className="field-help">
          {source.expected === null ? 'Không thể khẳng định tổng đầy đủ vì chưa biết mẫu số.'
            : 'Thiếu ' + source.missing.length + ' báo cáo; không coi phần thiếu bằng 0.'}
        </p>}
        <Button aria-expanded={details} aria-controls="ui03-source-details"
          onClick={() => setDetails(value => !value)}>
          {details ? 'Ẩn nguồn chi tiết' : 'Xem nguồn và phần còn thiếu'}
        </Button>
        <div id="ui03-source-details" className="ui03-detail" aria-label="Nguồn dữ liệu mẫu" hidden={!details}>
          {source.reports.length === 0 ? <p>Chưa nhận báo cáo nào trong phạm vi này.</p>
            : <ul>{source.reports.map(report => <li key={report.id}>
              {report.id}: {formatter.format(report.amount)} ₫ (đã biết)
            </li>)}</ul>}
          {source.missing.length > 0 && <p>Chờ nguồn: {source.missing.join(', ')}.</p>}
          {source.expected === null && <p>Tập nguồn đầy đủ chưa được xác định.</p>}
        </div>
      </>}
    </div>
  </section>
}

const sessionKey = 'fmcg.ui03.synthetic.v1'
function readSession(): { workflow: Workflow; corrupt: boolean; inaccessible: boolean } {
  try {
    const raw = sessionStorage.getItem(sessionKey)
    if (raw === null) return { workflow: newWorkflow(), corrupt: false, inaccessible: false }
    const workflow = tryRestoreWorkflow(raw)
    // A present but invalid snapshot could contain an unresolved request. Never
    // silently overwrite it with a fresh identity or enable a new send.
    return { workflow: workflow ?? newWorkflow(), corrupt: workflow === null, inaccessible: false }
  } catch {
    // Browser storage may be unavailable; this fixture then works in memory only.
    return { workflow: newWorkflow(), corrupt: false, inaccessible: true }
  }
}

function OutcomeRows({ state }: { state: Workflow }) {
  const phase = state.phase
  const intake = state.intakeAcknowledged
    ? 'Đã ACK tiếp nhận bền (mô phỏng)'
    : 'Chưa có xác nhận tiếp nhận bền'
  const business = phase === 'posted' ? 'POSTED (mô phỏng)'
    : phase === 'rejected' ? 'Rejected trước hiệu ứng (mô phỏng)'
      : phase === 'conflict' ? 'Conflict · cần xử lý'
        : phase === 'waiting' ? 'Chờ dependency'
          : 'Chưa xác định'
  return <dl className="ui03-source-facts ui03-result-grid">
    <div><dt>Tiếp nhận / ACK</dt><dd>{intake}</dd></div>
    <div><dt>Kết quả nghiệp vụ</dt><dd>{business}</dd></div>
    <div><dt>Hóa đơn / đối soát / kế toán</dt><dd>Chưa có bằng chứng; không suy từ ACK/POSTED</dd></div>
    <div><dt>Số dư sau lệnh</dt><dd>Chưa xác định; không lấy số trước gửi làm số sau</dd></div>
  </dl>
}

export function SourceCommandDemo() {
  const [density, setDensity] = useState<Density>('compact')
  const [initial] = useState(readSession)
  const [state, dispatch] = useReducer(workflowReducer, initial.workflow)
  const [scenario, setScenario] = useState<LookupFixture>('no-result')
  const [storageOk, setStorageOk] = useState(!initial.inaccessible)
  const recoveryBlocked = initial.corrupt
  const helpId = useId()
  useEffect(() => {
    if (recoveryBlocked) return // Preserve the invalid original for deliberate inspection.
    try {
      sessionStorage.setItem(sessionKey, JSON.stringify({ ...state, schema: 1 }))
      setStorageOk(true)
    } catch {
      setStorageOk(false)
    }
  }, [state, recoveryBlocked])
  const sent = isLocked(state)
  const quantityError = !sent && parseDemoQuantity(state.quantity) === null
    ? 'Nhập số dương hợp lệ, tối đa ba chữ số thập phân; không chấp nhận giá trị chưa nhập xong.'
    : undefined
  const currentPhase = phaseText[state.phase]
  // The status badge is part of the business snapshot; read revocation must
  // hide it just as it hides amounts/history, while retaining the opaque ID.
  const visiblePhase = state.permission === 'none'
    ? { label: 'Quyền đọc bị từ chối', tone: 'danger' as const,
      explanation: 'Thông tin trạng thái bị che vì không có quyền đọc.' }
    : currentPhase
  const preflightHelp = recoveryBlocked ? 'Không xác định được yêu cầu trước đó: các hành động command tạm khóa.'
    : state.permission === 'none' ? 'Thiếu quyền đọc và gửi trong fixture.'
    : state.permission === 'read' ? 'Chỉ có quyền đọc; không có assignment gửi.'
      : state.connection === 'offline' ? 'Offline: không gửi hoặc tra business command.'
        : quantityError ?? 'Preflight UI chỉ kiểm nháp và trạng thái mô phỏng, không xác nhận stock/credit/policy.'
  return <main className="page ui03-page" data-density={density}>
    <header className="page-heading">
      <nav className="button-row" aria-label="Điều hướng UI Lab">
        <a className="page-link" href="/">Trạng thái kết nối</a>
        <a className="page-link" href="/ui/foundations">UI-01 · Foundations</a>
        <a className="page-link" href="/ui/data-entry">UI-02 · Data Entry</a>
      </nav>
      <div className="title-row">
        <h1>Nguồn dữ liệu và phục hồi command</h1>
        <span className="environment">UI-03 · Synthetic / Nonproduction</span>
      </div>
      <p>Lab cục bộ để thử UX-B01…07. Không gọi API nghiệp vụ, không lưu lệnh ở server,
        không thực hiện giao dịch thật hoặc xếp hàng ghi offline.</p>
    </header>
    <div className="display-settings">
      <ThemeSelect />
      <div className="field">
        <label htmlFor="ui03-density">Mật độ</label>
        <select id="ui03-density" value={density}
          onChange={e => setDensity(e.currentTarget.value as Density)}>
          <option value="compact">Compact</option>
          <option value="comfortable">Comfortable</option>
          <option value="field">Field</option>
        </select>
      </div>
    </div>
    <div className="ui03-columns">
      <SourcePanel permission={state.permission} />
      <section className="panel" aria-labelledby="ui03-command-title">
        <div className="panel-heading">
          <h2 id="ui03-command-title">Command, preflight và quyền</h2>
          <Badge tone={visiblePhase.tone}>{visiblePhase.label}</Badge>
        </div>
        <div className="panel-content">
          <div className="ui03-guard-grid">
            <div className="field">
              <label htmlFor="ui03-permission">Quyền/assignment (mô phỏng)</label>
              <select id="ui03-permission" value={state.permission}
                onChange={e => dispatch({ type: 'permission', value: e.currentTarget.value as Workflow['permission'] })}>
                <option value="write">Được đọc và đề nghị gửi</option>
                <option value="read">Chỉ đọc / không được gửi</option>
                <option value="none">Thu hồi cả quyền đọc</option>
              </select>
            </div>
            <div className="field">
              <label htmlFor="ui03-network">Kết nối (mô phỏng)</label>
              <select id="ui03-network" value={state.connection}
                onChange={e => dispatch({ type: 'connection', value: e.currentTarget.value as Workflow['connection'] })}>
                <option value="online">Online</option><option value="offline">Offline</option>
              </select>
            </div>
          </div>
          {state.permission !== 'none' && <Field label="Số lượng trong payload mẫu" type="text" inputMode="decimal"
            value={sent ? state.sent!.quantity : state.quantity} disabled={sent}
            error={quantityError} aria-describedby={helpId}
            onChange={e => dispatch({ type: 'quantity', value: e.currentTarget.value })}
            help="Dữ liệu demo; chỉnh sửa trước gửi sẽ hủy preflight cũ. Không tự ghi nhận." />}
          {state.permission !== 'none' && <div className="field">
            <label htmlFor="ui03-revision">Phiên bản nguồn cho payload</label>
            <select id="ui03-revision" value={state.revision} disabled={sent}
              onChange={e => dispatch({ type: 'revision', value: e.currentTarget.value })}>
              <option value="rev-07">rev-07 (minh họa)</option><option value="rev-08">rev-08 (giả lập nguồn đổi)</option>
            </select>
          </div>}
          <p id={helpId} className="field-help">{preflightHelp}</p>
          {recoveryBlocked && <Notice tone="danger" title="Không thể khôi phục yêu cầu gốc">
            Bản lưu session không hợp lệ. Lab giữ nguyên bản lưu lỗi và khóa các hành động command,
            không tự tạo ID mới khi chưa rõ kết quả. Chỉ đặt lại fixture sau khi kiểm tra có chủ đích;
            đây không phải bằng chứng hay cơ chế khôi phục từ server.
          </Notice>}
          <div className="ui03-command-identity">
            <span>Mã yêu cầu gốc (session-only)</span>
            <strong data-testid="ui03-id">{recoveryBlocked ? "Không xác định" : state.id}</strong>
            {sent && state.permission !== 'none' && <p>Payload khóa: {state.sent!.quantity} đơn vị · {state.sent!.revision}.
              Không tự sinh request ID khác khi chưa rõ kết quả.</p>}
          </div>
          <div className="button-row">
            <Button onClick={() => dispatch({ type: 'check' })} disabled={recoveryBlocked || !canCheck(state)}>
              Kiểm tra điều kiện (demo)</Button>
            <Button variant="primary" onClick={() => dispatch({ type: 'send' })} disabled={recoveryBlocked || !canSend(state)}>
              Gửi command mẫu</Button>
          </div>
          {state.phase === 'checked' && <Notice tone="info" title="Preflight UI đã qua">
            Quyền, kết nối và nháp chỉ được kiểm tại client giả lập. Commit thật phải kiểm lại
            quyền, version, stock và policy trên server.
          </Notice>}
          {state.phase !== 'draft' && <p className="field-help" role="status">{visiblePhase.explanation}</p>}
          {state.phase === 'pending' && state.permission !== 'none' &&
            <Button onClick={() => dispatch({ type: 'lost-ack' })}>
            Mô phỏng mất ACK</Button>}
          {sent && <section className="ui03-outcome" aria-labelledby="ui03-outcome-title">
            <h3 id="ui03-outcome-title">Tra đúng lần gửi gốc</h3>
            {state.permission !== 'none' && <div className="field">
              <label htmlFor="ui03-scenario">Phản hồi tra cứu giả lập</label>
              <select id="ui03-scenario" value={scenario}
                disabled={recoveryBlocked || !canLookup(state)}
                onChange={e => setScenario(e.currentTarget.value as LookupFixture)}>
                {scenarios.map(x => <option key={x.value} value={x.value}>{x.label}</option>)}
              </select>
            </div>}
            <Button variant="primary" disabled={recoveryBlocked || !canLookup(state)}
              onClick={() => dispatch({ type: 'lookup', outcome: scenario })}>
              Kiểm tra kết quả lần gửi này</Button>
            {!canLookup(state) && <p className="field-help">
              {state.permission === 'none' ? 'Không có quyền tra cứu; giữ identity để chuyển người có thẩm quyền.'
                : state.connection === 'offline' ? 'Offline: không tra cứu hoặc phát lại, chờ online.'
                  : 'Kết quả đã được chốt trong fixture; không gửi lại.'}
            </p>}
            {state.permission !== 'none' && (state.phase === 'unknown' || state.phase === 'conflict') &&
              <Notice tone="warning"
              title={state.phase === 'conflict' ? 'Conflict cần xử lý' : 'Chưa rõ kết quả'}>
              Không gửi lệnh mới/ID mới để vượt trạng thái này. Nếu sự việc đã xảy ra ngoài đời,
              chuyển case kèm bằng chứng cho người có thẩm quyền; Lab không tạo case thật.
            </Notice>}
          </section>}
        </div>
      </section>
    </div>
    <section className="panel" aria-labelledby="ui03-result-title">
      <div className="panel-heading"><h2 id="ui03-result-title">Kết quả theo lớp và lịch sử nguồn</h2>
        <Badge tone={visiblePhase.tone}>{visiblePhase.label}</Badge></div>
      <div className="panel-content">
        {recoveryBlocked ? <Notice tone="danger" title="Không có trạng thái phục hồi hợp lệ">
          Không diễn giải bản lưu session lỗi như một kết quả server. Identity và lịch sử trước đó chưa thể xác minh.
        </Notice> : state.permission === 'none' ? <Notice tone="danger" title="Đã thu hồi quyền đọc">
          Giá trị và lịch sử yêu cầu bị che trong Lab; không dùng trạng thái UI làm quyền truy cập.
        </Notice> : <>
          <p>Yêu cầu <strong>{state.id}</strong> · phiên bản nguồn {state.sent?.revision ?? state.revision}.
            Mọi phản hồi dưới đây đều là lựa chọn fixture, không phải bằng chứng server.</p>
          <OutcomeRows state={state} />
          <h3>Lịch sử thao tác trong session (không phải audit server)</h3>
          {state.history.length === 0 ? <p>Chưa có sự kiện trong session.</p>
            : <ol className="ui03-history">{state.history.map((entry, index) =>
              <li key={index}>{entry}</li>)}</ol>}
        </>}
        {!storageOk && <Notice tone="warning" title="Không lưu được session">
          Trình duyệt từ chối sessionStorage; dữ liệu demo chỉ còn trong trang đang mở.
        </Notice>}
        <p className="field-help">Lịch sử sessionStorage chỉ giúp xem lại mô phỏng trong cùng tab khi reload;
          không phải durable inbox, audit, bản ghi quyền hoặc offline business queue.
          Nếu trình duyệt chặn/đã xóa storage, identity mẫu không còn được giữ.</p>
        <div className="button-row">
          <Button disabled={recoveryBlocked || !['posted', 'rejected'].includes(state.phase)}
            onClick={() => { dispatch({ type: 'next' }); setScenario('no-result') }}>
            Tạo nháp demo tiếp theo sau kết quả cuối</Button>
        </div>
      </div>
    </section>
  </main>
}
