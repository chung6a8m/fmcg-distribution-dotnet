import { useEffect, useEffectEvent, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

const d2Api = '/api/synthetic/app02d2'
type Trip = { tripId: string; label: string; canSell?: boolean }
type Line = {
  tripManifestLineId: string; skuId: string; availableQuantity: number | string; unitPrice: number | string | null
  skuLabel: string
}
type Selection = { selectionId: string; shipToSnapshotId: string; outletId: string | null; label: string; outletLabel?: string }
type Customer = { customerId: string; label: string; selections: Selection[] }
type Cash = { evidenceRef: string; customerId: string; shipToSnapshotId: string; amount: number }
type SaleSource = { sourceId: string; commandId: string; quantity: number; payable: number }
type Catalog = {
  tripId: string; lines: Line[]; customers: Customer[]; verifiedCash?: Cash[]; recentSales?: SaleSource[]; currentDay?: boolean
}
type Session = { identity: Identity; token: string; trips: Trip[] }
type SaleInput = {
  producerId: string; commandId: string; sourceNumber: string
  tripId: string; customerId: string; shipToSnapshotId: string; selectionId: string
  outletId: string | null; tripManifestLineId: string; skuId: string; baseQuantity: number
  receivedCashAmount: number; verificationEvidenceRef: string
  pendingBankEvidenceRef: string | null; customerCreditReservationId: string | null
}
type Preview = {
  payable: number; receiptApplied: number; creditApplied: number; receivable: number; creditIssued: number; pendingBank: boolean
}
type Review = { command: SaleInput; preview: Preview }
type Outcome = { status: string; outcomeCode: string | null; reasonCode: string | null; sourceId: string | null }
type Option = { id: string; label: string }

function Choices({ name, label, options, value, onChange }: {
  name: string; label: string; options: Option[]; value?: string; onChange?: (id: string) => void
}) {
  const id = useId()
  return <div className="field">
    <label htmlFor={id}>{label}</label>
    <select id={id} name={name} required value={value} onChange={onChange ? event => onChange(event.currentTarget.value) : undefined}>
      <option value="">Chọn {label.toLocaleLowerCase('vi')}</option>
      {options.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
    </select>
  </div>
}

const guidPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i
function pendingKey(identity: Identity) {
  return 'app02d2.pending.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function resultKey(identity: Identity, tripId: string) {
  return pendingKey(identity) + '.result.' + tripId
}

function restore(identity: Identity, resultTrip?: string): SaleInput | null {
  try {
    const stored = pendingJson(resultTrip ? resultKey(identity, resultTrip) : pendingKey(identity))
    if (!stored) return null
    const command = JSON.parse(stored) as SaleInput
    return command.producerId === identity.actorId && guidPattern.test(command.commandId) &&
      guidPattern.test(command.tripId) && (!resultTrip || command.tripId === resultTrip) ? command : null
  } catch { return null }
}

function value(data: FormData, key: string) {
  return String(data.get(key) ?? '').trim()
}

function validPositive(input: string, decimals: number) {
  return new RegExp(`^\\d{1,15}(\\.\\d{1,${decimals}})?$`).test(input) && Number(input) > 0
}

function reasonMessage(reason: string | null | undefined) {
  return reason ? `Yêu cầu bị từ chối · ${reason}` : 'Yêu cầu bị từ chối.'
}

function requestError(error: unknown) {
  if (error instanceof HttpError) {
    if (error.status === 401 || error.status === 403) return 'Phiên DEV hoặc quyền bán hiện hành không hợp lệ.'
    if (error.status === 404) return 'Không tìm thấy dữ liệu bán B trong phiên DEV.'
  }
  return 'Không tải được dữ liệu bán B. Hãy thử lại khi có kết nối.'
}

async function read<T>(token: string, path: string, base = d2Api) {
  const response = await requestSalesApi(base, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

export function BSaleWorkspace({ workspaceToken, startTrip, requestedTrip, onTripChange }: {
  workspaceToken?: string; startTrip?: string; requestedTrip?: string | null; onTripChange?: (id: string) => void
} = {}) {
  const field = !!workspaceToken
  const money = (amount: number) => field ? amount.toLocaleString('vi-VN') + ' VND' : String(amount)
  const [session, setSession] = useState<Session | null>(null)
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [customerId, setCustomerId] = useState('')
  const [selectionId, setSelectionId] = useState('')
  const [cashRef, setCashRef] = useState('')
  const [paymentMode, setPaymentMode] = useState('cash')
  const [review, setReview] = useState<Review | null>(null)
  const [pending, setPending] = useState<SaleInput | null>(null)
  const [latest, setLatest] = useState<SaleInput | null>(null)
  const [source, setSource] = useState('')
  const [tripError, setTripError] = useState(false)
  const [notice, setNotice] = useState('')
  const [offline, setOffline] = useState(!navigator.onLine)
  const [busy, setBusy] = useState(false)
  const active = useRef(false)

  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function work(operation: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await operation() }
    catch (error) { setNotice(requestError(error)) }
    finally { active.current = false; setBusy(false) }
  }

  function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = value(new FormData(event.currentTarget), 'token')
    event.currentTarget.reset()
    if (!token) return
    connectToken(token)
  }

  function connectToken(token: string) {
    void work(async () => {
      const [identity, trips] = await Promise.all([
        read<Identity>(token, '/session', '/api/synthetic/app02c1'), read<Trip[]>(token, '/trips'),
      ])
      const unresolved = restore(identity)
      setSession({ identity, token, trips })
      setPending(unresolved)
      if (!field) return
      const candidates = requestedTrip != null ? [requestedTrip.toLowerCase()] : [unresolved?.tripId, startTrip, trips[0]?.tripId]
      const tripId = candidates.find(id => trips.some(trip => trip.tripId === id))
      setTripError(requestedTrip != null && !tripId)
      if (tripId) await loadTrip({ identity, token, trips }, tripId)
    })
  }

  const openWorkspace = useEffectEvent(connectToken)
  useEffect(() => {
    if (workspaceToken) openWorkspace(workspaceToken)
  }, [workspaceToken])

  async function loadTrip(current: Session, tripId: string) {
    try {
      setCatalog(await read<Catalog>(current.token, `/trips/${encodeURIComponent(tripId)}`))
    } catch (error) {
      if (!field || !(error instanceof HttpError) || ![403, 404].includes(error.status)) throw error
      setTripError(true)
      return
    }
    setLatest(field ? restore(current.identity, tripId) : null)
    setTripError(false)
    onTripChange?.(tripId)
  }

  function openTrip(tripId: string) {
    if (!session || !tripId) return
    void work(async () => {
      setCatalog(null)
      setSource('')
      setLatest(null)
      setCustomerId('')
      setSelectionId('')
      setCashRef('')
      setPaymentMode('cash')
      await loadTrip(session, tripId)
    })
  }

  function showPreview(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!session || !catalog || pending || review || active.current || !navigator.onLine) return
    const data = new FormData(event.currentTarget)
    const customer = catalog.customers.find(item => item.customerId === customerId)
    const selection = customer?.selections.find(item => item.selectionId === value(data, 'selection'))
    const line = catalog.lines.find(item => item.tripManifestLineId === value(data, 'manifest'))
    const quantity = value(data, 'quantity')
    const witness = catalog.verifiedCash?.find(item => item.evidenceRef === value(data, 'cashRef')
      && item.customerId === customerId && item.shipToSnapshotId === selection?.shipToSnapshotId)
    const cash = field ? String(witness?.amount ?? '') : value(data, 'cash')
    const bankRef = paymentMode === 'bank' ? value(data, 'bankRef') : null
    const cashRef = paymentMode === 'cash' ? value(data, 'cashRef') : ''
    const creditRef = paymentMode === 'cash' ? value(data, 'creditRef') || null : null
    if (!customer || !selection || !line || !validPositive(quantity, 3) ||
        (field && (!witness || !Number.isInteger(Number(quantity)))) ||
        (paymentMode === 'bank' ? !bankRef : !cashRef || !validPositive(cash, 4)) ||
        (creditRef && !guidPattern.test(creditRef))) {
      setNotice('Kiểm tra dòng hàng, địa chỉ giao, số lượng và chứng cứ thanh toán.')
      return
    }
    const commandId = crypto.randomUUID()
    const command: SaleInput = {
      producerId: session.identity.actorId, commandId, sourceNumber: 'D2-' + commandId,
      tripId: catalog.tripId, customerId: customer.customerId, shipToSnapshotId: selection.shipToSnapshotId,
      selectionId: selection.selectionId, outletId: selection.outletId,
      tripManifestLineId: line.tripManifestLineId, skuId: line.skuId, baseQuantity: Number(quantity),
      receivedCashAmount: paymentMode === 'bank' ? 0 : Number(cash), verificationEvidenceRef: cashRef,
      pendingBankEvidenceRef: bankRef, customerCreditReservationId: creditRef,
    }
    const token = session.token
    void work(async () => {
      setSource('')
      const response = await requestSalesApi(d2Api, token, '/preview', JSON.stringify(command))
      if ([401, 403, 404].includes(response.status)) throw new HttpError(response.status)
      const result = await response.json() as Preview & { status?: string; reasonCode?: string }
      if (!response.ok || result.status === 'REJECTED') {
        setNotice(reasonMessage(result.reasonCode))
        return
      }
      if (typeof result.payable !== 'number' || typeof result.receivable !== 'number')
        throw new Error('Invalid sale preview')
      setReview({ command, preview: result })
    })
  }

  function resolve(command: SaleInput, lookup: boolean) {
    if (!session) return
    const { identity, token } = session
    void work(async () => {
      if (!lookup) {
        rememberPending(pendingKey(identity), command)
        setPending(command)
        setReview(null)
      }
      setSource('')
      try {
        const path = lookup ?
          `/sales/${encodeURIComponent(command.producerId)}/${encodeURIComponent(command.commandId)}?tripId=${encodeURIComponent(command.tripId)}` : '/sales'
        const response = await requestSalesApi(d2Api, token, path, lookup ? undefined : JSON.stringify(command))
        if (response.status === 401 || response.status === 403) {
          setNotice('Quyền bán hiện hành không hợp lệ. Yêu cầu gốc được giữ để tra cứu.')
          return
        }
        if (lookup && response.status === 404) {
          setNotice('Chưa có kết quả. Có thể gửi lại cùng yêu cầu khi có quyền.')
          return
        }
        const result = await response.json() as Outcome
        if (result.status === 'CONFLICT') {
          setNotice('Mã yêu cầu đã dùng cho nội dung khác. Hãy tra cứu yêu cầu gốc.')
          return
        }
        if (!(result.status === 'APPLIED' && result.sourceId) && result.status !== 'REJECTED')
          throw new Error('Unknown sale outcome')
        setPending(null)
        if (field) setCashRef('')
        forgetPending(pendingKey(identity), command.commandId)
        const activeTrip = !field || catalog?.tripId === command.tripId
        if (result.status === 'APPLIED') {
          if (activeTrip) setLatest(command)
          if (field) rememberPending(resultKey(identity, command.tripId), command)
        }
        const disposition = result.outcomeCode === 'D2_BANK_PENDING_POSTED' ? ' Ngân hàng chờ xác minh.' : ''
        const message = result.status === 'APPLIED' ? 'Đã ghi bán và giao.' + disposition : reasonMessage(result.reasonCode)
        if (!activeTrip) {
          setNotice(`${message} Chuyến nguồn: ${tripLabel(command.tripId)}.${result.sourceId ? ' Mã nguồn ' + result.sourceId : ''}`)
          return
        }
        setSource(result.sourceId ?? '')
        try {
          setCatalog(await read<Catalog>(token, `/trips/${encodeURIComponent(command.tripId)}`))
          onTripChange?.(command.tripId)
        }
        catch { setNotice(message + ' Hãy mở lại chuyến để cập nhật số liệu.'); return }
        setNotice(message)
      } catch {
        setNotice('Chưa rõ kết quả. Hãy tra cứu trước khi ghi yêu cầu mới.')
      }
    })
  }

  const blocked = busy || offline
  const tripLabel = (id: string) => session?.trips.find(trip => trip.tripId === id)?.label ?? id
  const customer = catalog?.customers.find(item => item.customerId === customerId)
  const selection = customer?.selections.find(item => item.selectionId === selectionId)
  const cashOptions = catalog?.verifiedCash?.filter(item => item.customerId === customerId
    && item.shipToSnapshotId === selection?.shipToSnapshotId) ?? []
  const Page = field ? 'section' : 'main'

  return <Page className={field ? 'sales-pane' : 'page c1-page sales-pane'}>
    {!field ? <header className="page-heading c1-header">
      <div><h1>Bán và giao hàng B</h1><p>DEV tổng hợp{session && ' · ' + session.identity.tenantId}</p></div>
      <div className="button-row">
        <ThemeSelect />
        <a className="page-link" href="/sales">Workspace Sales</a>
        <a className="page-link" href="/sales/b-stock">Kho B</a>
        {session && <Button disabled={busy} onClick={() => {
          setSession(null); setCatalog(null); setPending(null); setLatest(null); setReview(null); setSource(''); setNotice('')
        }}>Đổi phiên DEV</Button>}
      </div>
    </header> : <header><h2>Bán và giao hàng B</h2>
      <p>EA nguyên, hàng không quản lý lô. Thanh toán đủ bằng tiền mặt đã xác minh, theo giá VND và thuế suất 0% của dữ liệu DEV.</p>
    </header>}
    {!session ? workspaceToken ? <Button disabled={blocked} onClick={() => connectToken(workspaceToken)}>Tải chuyến bán B</Button>
      : <form className="panel panel-content c1-sign-in" onSubmit={signIn}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button variant="primary" type="submit" disabled={blocked}>Kết nối</Button>
    </form> : <>
      <section className="panel panel-content" aria-label="Chuyến bán">
        <fieldset disabled={blocked || !!pending || !!review}>
          <Choices name="trip" label="Chuyến" value={catalog?.tripId ?? ''}
            options={session.trips.map(trip => ({ id: trip.tripId, label: trip.label }))} onChange={openTrip} />
        </fieldset>
        {tripError && <p role="alert">Chuyến được yêu cầu không còn khả dụng. Hãy chọn chuyến khác.</p>}
        {!session.trips.length && <p>Chưa có chuyến được phân công bán.</p>}
        {latest && catalog?.tripId === latest.tripId && !pending && !review &&
          <Button disabled={blocked} onClick={() => resolve(latest, true)}>Mở lại kết quả</Button>}
      </section>
      {catalog && <section className="panel panel-content" aria-label="Thông tin giao dịch">
        {catalog.lines.length ? <form key={catalog.tripId} onSubmit={showPreview}>
          <fieldset className="sales-filter" disabled={busy || !!pending || !!review}>
            <Choices name="manifest" label="Dòng hàng B" options={catalog.lines.map(line => ({
              id: line.tripManifestLineId,
              label: `${line.skuLabel} · Còn ${line.availableQuantity} EA` + (field ? '' : ` · Giá ${line.unitPrice}`),
            }))} />
            <Choices name="customer" label="Khách hàng" value={customerId}
              options={catalog.customers.map(item => ({ id: item.customerId, label: item.label }))}
              onChange={id => { setCustomerId(id); setSelectionId(''); setCashRef('') }} />
            <Choices key={field ? 'field' : customerId} name="selection" label="ShipTo đã chốt"
              value={field ? selectionId : undefined}
              onChange={field ? id => { setSelectionId(id); setCashRef('') } : undefined}
              options={(customer?.selections ?? []).map(item => ({
              id: item.selectionId,
              label: item.outletId ? `${item.label} · Outlet ${item.outletLabel ?? item.outletId}` : item.label,
            }))} />
            <Field name="quantity" label="Số lượng bán" type="number" min={field ? '1' : '0.001'} step={field ? '1' : '0.001'} required />
            {field ? <Choices name="cashRef" label="Tiền mặt đã xác minh" value={cashRef} onChange={setCashRef}
              options={cashOptions.map(item => ({ id: item.evidenceRef, label: money(item.amount) + ' · ' + item.evidenceRef }))} />
              : <Choices name="payment" label="Nguồn thanh toán" value={paymentMode} onChange={setPaymentMode}
              options={[{ id: 'cash', label: 'Tiền mặt đã xác minh' }, { id: 'bank', label: 'Ngân hàng chờ xác minh' }]} />
            }
            {!field && (paymentMode === 'cash' ? <>
              <Field name="cash" label="Số tiền mặt thực nhận" type="number" min="0.0001" step="0.0001" required />
              <Field name="cashRef" label="Chứng cứ tiền mặt đã xác minh" maxLength={128} required />
              <Field name="creditRef" label="Mã CustomerCredit đã giữ" maxLength={36} />
            </> : <Field name="bankRef" label="Chứng cứ ngân hàng chờ xác minh" maxLength={128} required />)}
            <Button type="submit" disabled={offline}>Xem trước giao dịch</Button>
          </fieldset>
        </form> : <p>{session.trips.find(trip => trip.tripId === catalog.tripId)?.canSell === false || catalog.currentDay === false
          ? 'Chuyến chỉ mở để tra cứu giao dịch đã ghi.'
          : 'Chuyến chưa có dòng hàng B khả dụng.'}</p>}
      </section>}
      {review && <section className="panel panel-content" aria-labelledby="b-sale-review">
        <h2 id="b-sale-review">Xác nhận giao dịch</h2>
        <ul>
          <li>Phải trả {money(review.preview.payable)}</li>
          {review.preview.pendingBank && <li>Ngân hàng chờ xác minh</li>}
          <li>Tiền đã xác minh áp vào giao dịch {money(review.preview.receiptApplied)}</li>
          {!field && <li>CustomerCredit áp vào giao dịch {review.preview.creditApplied}</li>}
          <li>Công nợ phát sinh {money(review.preview.receivable)}</li>
          {!field && <li>CustomerCredit phát sinh từ tiền dư {review.preview.creditIssued}</li>}
        </ul>
        <div className="button-row">
          <Button variant="primary" disabled={blocked} onClick={() => resolve(review.command, false)}>Ghi bán và giao</Button>
          <Button disabled={busy} onClick={() => setReview(null)}>Sửa giao dịch</Button>
        </div>
      </section>}
      {pending && <section className="panel panel-content" aria-labelledby="b-sale-recovery">
        <h2 id="b-sale-recovery">Khôi phục yêu cầu</h2>
        {field && <p>Chuyến nguồn: {tripLabel(pending.tripId)}</p>}
        <p className="c3-source">Mã yêu cầu {pending.commandId}</p>
        <div className="button-row">
          <Button disabled={blocked} onClick={() => resolve(pending, true)}>Tra cứu yêu cầu</Button>
          <Button disabled={blocked} onClick={() => resolve(pending, false)}>Gửi lại cùng yêu cầu</Button>
        </div>
      </section>}
      {!!catalog?.recentSales?.length && <section className="panel panel-content" aria-labelledby="b-sale-history">
        <h2 id="b-sale-history">Giao dịch B đã ghi trên chuyến</h2>
        <ul className="c1-rows">{catalog.recentSales.map(sale => <li key={sale.sourceId}>
          <div><strong>{sale.quantity} EA · {money(sale.payable)}</strong>
            <p className="c3-source">Mã nguồn {sale.sourceId}</p>
            <p className="c3-source">Mã yêu cầu {sale.commandId}</p>
          </div>
        </li>)}</ul>
      </section>}
    </>}
    {offline && <p role="status">Cần kết nối để ghi nghiệp vụ.</p>}
    {notice && <p role="status">{notice}</p>}
    {source && <p className="c3-source">Mã nguồn {source}</p>}
  </Page>
}
