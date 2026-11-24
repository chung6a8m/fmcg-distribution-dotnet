import { Fragment, useEffect, useEffectEvent, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { Button, Field, Notice, ThemeSelect } from '../ui/controls'
import { forgetPending, HttpError, pendingJson, read, rememberPending, requestSalesApi, type Detail, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02c2'
const handoffApi = '/api/synthetic/app03'
type Authority = Identity & { sales: boolean; warehouse: boolean }
type Option = { id: string; label: string }
type CatalogUnit = { skuId: string; uomCode: string; factor: number; step: number }
type Catalog = { customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[]
  units?: CatalogUnit[] }
type Pricing = { mode: 'EXCLUSIVE' | 'INCLUSIVE'; currency: 'VND'; base: number; discount: number
  net: number; tax: number; gross: number; version: number }
type LotStep = { stage: 1 | 2 | 3; sourceId: string; lotCode: string; expiresOn: string; quantity: number }
type LineHandoff = { warehouseHeld: number; unreserved: number
  quantity: { uomCode: string; enteredQuantity: number; factor: number } | null; lots: LotStep[] }
type Line = { lineId: string | null; sku: string; baseUom: string; ordered: number; reserved: number;
  delivered: number; unitPrice: number | null; policyVersion: number | null; termsDays: number | null
  loaded: number; heldOnTrip: number; policySourceHash: string | null; pricing?: Pricing | null; handoff?: LineHandoff }
type Order = { id: string; customer: string; trip: string; shipTo: string; businessDate: string | null;
  confirmSourceId: string | null; status: string; lines: Line[]; shipToSnapshotId: string | null; shipToOrigin: string
  tripId?: string; actions?: { confirm: boolean; reserve: boolean; load: boolean } }
type Command = { path: '/drafts' | '/confirm' | '/reserve'; id: string; body: string }
type Session = { token: string; identity: Authority }

const labels: Record<string, string> = {
  DRAFT: 'Đơn nháp',
  CONFIRMED_UNRESERVED: 'Đã Confirm · Chưa giữ hàng',
  CONFIRMED_PARTIALLY_RESERVED: 'Đã giữ một phần',
  CONFIRMED_FULLY_RESERVED: 'Đã giữ đủ',
  PARTIALLY_DELIVERED: 'Đã giao một phần',
  FULFILLED: 'Đã giao đủ',
}
const rejections: Record<string, string> = {
  F03_DRAFT_QUANTITY_INVALID: 'Thiếu snapshot quy đổi. Hãy lập lại đơn nháp DEV.',
  C2_POLICY_REQUIRED: 'Thiếu chính sách thương mại được duyệt và còn hiệu lực.',
  C2_POLICY_SCOPE_CONFLICT: 'Điều khoản duyệt giữa các dòng không nhất quán.',
  C2_CREDIT_CAP_EXCEEDED: 'Giá trị đơn vượt giới hạn DEV được duyệt.',
  C2_SHIP_TO_STALE: 'Địa chỉ đăng ký đã đổi phiên bản. Hãy chọn lại địa chỉ trong màn khách hàng.',
  C2_SHIP_TO_ALREADY_BOUND: 'Lựa chọn địa chỉ đã được gắn với một đơn.',
  C2_SHIP_TO_INVALID: 'Không tìm thấy địa chỉ đã chọn cho khách hàng.',
  A_LINE_OVER_RESERVED: 'Số lượng yêu cầu vượt phần còn thiếu của dòng đơn.',
  STOCK_INSUFFICIENT: 'Kho không đủ lượng khả dụng. Đơn đã Confirm vẫn được giữ.',
  C2_STALE_CONFIRM: 'Nguồn Confirm đã thay đổi. Hãy tải lại đơn.',
  C2_BASE_UOM_CHANGED: 'Đơn vị cơ sở của SKU đã đổi. Hãy lập lại đơn nháp.',
  C2_FROZEN_LINE_INVALID: 'Nguồn Confirm hoặc đơn vị của dòng hàng không còn khớp.',
  QUANTITY_INVALID: 'Số lượng phải dương, tối đa ba chữ số thập phân.',
  F03_UOM_INVALID: 'Đơn vị hoặc bước số lượng không hợp lệ.',
  F03_LOT_STOCK_INSUFFICIENT: 'Kho không đủ hàng từ các lô đủ điều kiện để giữ.',
  F04_PRICE_PLAN_REQUIRED: 'Đơn chưa có phương án giá được duyệt và còn hiệu lực.',
  F04_PRICE_INVALID: 'Giá, chiết khấu hoặc thuế trên đơn không hợp lệ.',
}

const vnd = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 0 })
const vndUnit = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 4 })
const steps = { confirm: 'Cần Confirm', reserve: 'Cần giữ hàng', load: 'Cần nạp' }
const lotStages = { 1: 'Đã giữ', 2: 'Đã nạp', 3: 'Đã giao' }

function orderLocation(screen: 'orders' | 'load', order: Order, source?: string) {
  return `/work/${screen}?trip=${order.tripId}&order=${order.id}` + (source ? '&handoffSource=' + source : '')
}

function LotCell({ label, children }: { label: string; children: ReactNode }) {
  return <td role="cell"><span className="app03-lot-label" aria-hidden="true">{label}</span>{children}</td>
}

function HandoffLine({ line, order }: { line: Line; order: Order }) {
  const { handoff, baseUom } = line
  if (!handoff) return null
  const { quantity, lots, warehouseHeld, unreserved } = handoff
  return <>
    {quantity && <p>Quy đổi {quantity.enteredQuantity} {quantity.uomCode} × {quantity.factor} = {line.ordered} {baseUom}</p>}
    {line.lineId && <>
      <p>Đã nạp {line.loaded} {baseUom} · Còn thiếu giữ {unreserved} {baseUom}</p>
      <p>Còn ở kho {warehouseHeld} {baseUom} · Còn trên xe {line.heldOnTrip} {baseUom}</p>
      {lots.length > 0 && <div className="app03-table-scroll" role="region" aria-label={`Lô của ${line.sku}`}>
        <table className="app03-handoff-table" role="table">
          <caption>Lịch sử lô</caption>
          <thead role="rowgroup"><tr role="row"><th scope="col">Bước</th><th scope="col">Lô</th><th scope="col">Hạn dùng</th>
            <th scope="col">Số lượng</th><th scope="col">Mã nguồn</th></tr></thead>
          <tbody role="rowgroup">{lots.map((lot, i) => <tr key={`${lot.stage}:${lot.sourceId}:${lot.lotCode}:${i}`}
            role="row" data-source={lot.sourceId} tabIndex={-1}>
            <LotCell label="Bước">{lotStages[lot.stage]}</LotCell>
            <LotCell label="Lô">{lot.lotCode}</LotCell>
            <LotCell label="Hạn dùng"><time dateTime={lot.expiresOn.slice(0, 10)}>
              {lot.expiresOn.slice(0, 10).split('-').reverse().join('/')}
            </time></LotCell>
            <LotCell label="Số lượng">{lot.quantity} {baseUom}</LotCell>
            <LotCell label="Mã nguồn"><a className="page-link" href={orderLocation('orders', order, lot.sourceId)}>
              <code>{lot.sourceId}</code></a></LotCell>
          </tr>)}</tbody>
        </table>
      </div>}
    </>}
  </>
}

function PriceAmounts({ amounts }: { amounts: [string, number][] }) {
  return <dl className="sales-facts">
    {amounts.map(([label, value]) => <Fragment key={label}><dt>{label}</dt><dd>{vnd.format(value)} VND</dd></Fragment>)}
  </dl>
}

function OrderPriceTotals({ lines }: { lines: Line[] }) {
  const prices = lines.flatMap(line => line.pricing ? [line.pricing] : [])
  if (prices.length === 0 || prices.length !== lines.length) return null

  const totals = prices.reduce((sum, { net, tax, gross }) => ({
    net: sum.net + net, tax: sum.tax + tax, gross: sum.gross + gross,
  }), { net: 0, tax: 0, gross: 0 })

  return <section aria-label="Tổng tiền đơn đã chốt">
    <h4>Tổng tiền đơn đã chốt</h4>
    <PriceAmounts amounts={[
      ['Tiền chưa thuế', totals.net], ['Thuế', totals.tax], ['Tổng thanh toán', totals.gross],
    ]} />
  </section>
}

function key(identity: Authority) {
  return 'app02c2.pending.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function restore(identity: Authority): Command | null {
  try {
    const raw = pendingJson(key(identity))
    if (!raw) return null
    const item = JSON.parse(raw) as Command
    if (['/drafts', '/confirm', '/reserve'].includes(item.path) && /^[a-f0-9-]{36}$/.test(item.id)
        && JSON.parse(item.body).commandId === item.id) return item
  } catch { /* The identity remains isolated by owned DEV session. */ }
  return null
}


async function get<T>(token: string, path: string, service = api): Promise<T> {
  const response = await requestSalesApi(service, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function failure(error: unknown) {
  if (error instanceof HttpError && [401, 403].includes(error.status))
    return 'Phiên hoặc quyền hiện hành không cho phép thao tác.'
  return 'Không tải được dữ liệu từ API.'
}

function DraftLineFields({ row, index, catalog, onRemove }: {
  row: string; index: number; catalog: Catalog; onRemove?: () => void
}) {
  const [skuId, setSkuId] = useState('')
  const [uomCode, setUomCode] = useState('')
  const units = catalog.units?.filter(unit => unit.skuId === skuId) ?? []
  const selected = units.find(unit => unit.uomCode === uomCode)
    ?? units.find(unit => unit.factor === 1) ?? units[0]
  const formatUnit = (value: number) => value.toLocaleString('vi-VN', { maximumFractionDigits: 6 })
  const quantityHelp = selected
    ? `Bước nhập ${formatUnit(selected.step)} ${selected.uomCode} · Hệ số quy đổi ${formatUnit(selected.factor)}`
    : undefined

  return <fieldset className="c1-fields">
    <legend>Dòng {index}</legend>
    <label className="field">SKU dòng {index}
      <select name={'sku-' + row} value={skuId} onChange={event => {
        setSkuId(event.currentTarget.value)
        setUomCode('')
      }} required>
        <option value="">Chọn SKU</option>
        {catalog.skus.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </select>
    </label>
    {selected && <label className="field">Đơn vị dòng {index}
      <select name={'uom-' + row} value={selected.uomCode}
        onChange={event => setUomCode(event.currentTarget.value)}>
        {units.map(unit => <option key={unit.uomCode} value={unit.uomCode}>{unit.uomCode}</option>)}
      </select>
    </label>}
    <Field name={'qty-' + row} label={'Số lượng dòng ' + index} help={quantityHelp}
      type="number" min="0.001" step="0.001" required />
    {onRemove && <Button onClick={onRemove}>Xóa dòng {index}</Button>}
  </fieldset>
}

type DraftContext = {
  customer: string; trip: string
  onCustomerChange: (id: string) => void; onTripChange: (id: string) => void
}

function DraftForm({ session, catalog, disabled, save, context }: {
  session: Session; catalog: Catalog; disabled: boolean; save: (path: Command['path'], fields: object) => void
  context?: DraftContext
}) {
  const [customer, setCustomer] = useState(() => catalog.customers.find(row => row.id === context?.customer)?.id ?? '')
  const [detail, setDetail] = useState<Detail | null>(null)
  const [selectionError, setSelectionError] = useState('')
  const [rows, setRows] = useState(() => [crypto.randomUUID()])
  const token = session.token
  useEffect(() => {
    if (!customer) return
    let active = true
    read<Detail>(token, '/customers/' + customer)
      .then(value => { if (active) { setDetail(value); setSelectionError('') } })
      .catch(error => { if (active) setSelectionError(failure(error)) })
    return () => { active = false }
  }, [customer, token])
  const current = detail?.customer.id === customer ? detail : null
  return <form className="panel panel-content" aria-label="Tạo đơn A nháp" onSubmit={event => {
    event.preventDefault()
    const values = new FormData(event.currentTarget)
    save('/drafts', {
      customerId: customer, tripId: values.get('trip'), selectionId: values.get('selection'),
      lines: rows.map(row => ({
        skuId: values.get('sku-' + row),
        quantity: Number(values.get('qty-' + row)),
        uomCode: values.get('uom-' + row) ?? undefined,
      })),
    })
  }}>
    <h2>Tạo đơn A nháp</h2>
    <label className="field">Khách hàng
      <select required value={customer} onChange={event => {
        setCustomer(event.target.value)
        context?.onCustomerChange(event.target.value)
        setSelectionError('')
      }}>
        <option value="">Chọn khách hàng</option>
        {catalog.customers.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </select>
    </label>
    <label className="field">ShipTo đã chọn
      <select name="selection" key={customer} required>
        <option value="">Chọn địa chỉ giao</option>
        {current?.selections.map(row => <option key={row.id} value={row.id}>
          {row.address.line} · {row.address.locality} · {row.origin === 'DIRECT' ? 'Một lần' : 'Bản ' + row.registeredRevision}
        </option>)}
      </select>
    </label>
    {selectionError && <p role="alert">{selectionError}</p>}
    {current?.selections.length === 0 && <a className="page-link" href={context ? '/work/customers?customer=' + customer : '/sales/customers'}>
      Chọn địa chỉ trong hồ sơ khách hàng
    </a>}
    <label className="field">Chuyến
      <select name="trip" defaultValue={catalog.trips.find(row => row.id === context?.trip)?.id ?? ''} required
        onChange={event => context?.onTripChange(event.target.value)}>
        <option value="">Chọn chuyến</option>
        {catalog.trips.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </select>
    </label>
    {rows.map((row, i) => <DraftLineFields key={row} row={row} index={i + 1} catalog={catalog}
      onRemove={rows.length > 1 ? () => setRows(items => items.filter(item => item !== row)) : undefined} />)}
    <div className="button-row">
      <Button disabled={rows.length === 24} onClick={() => setRows(items => [...items, crypto.randomUUID()])}>Thêm dòng</Button>
      <Button variant="primary" type="submit" disabled={disabled}>Lưu đơn nháp</Button>
    </div>
  </form>
}

export function OrderWorkspace({ workspaceToken, onOpenAr, context, selectedOrder, selectedSource, onSelectOrder }: {
  workspaceToken?: string; onOpenAr?: (orderId: string, tripId?: string) => void; context?: DraftContext
  selectedOrder?: string; selectedSource?: string; onSelectOrder?: (orderId: string, tripId?: string) => void
}) {
  const [session, setSession] = useState<Session | null>(null)
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [step, setStep] = useState<keyof typeof steps | 'all'>('all')
  const [tripFilter, setTripFilter] = useState('')
  const [pending, setPending] = useState<Command | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [offline, setOffline] = useState(!navigator.onLine)
  const active = useRef(false)
  const connecting = useRef(false)
  const orderSection = useRef<HTMLElement>(null)
  const focusedSource = useRef('')
  const readOrders = (token: string) => context ? get<Order[]>(token, '/handoff', handoffApi) : get<Order[]>(token, '/orders')
  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    void connectToken(token)
  }

  const openWorkspace = useEffectEvent((token: string) => { void connectToken(token) })
  useEffect(() => {
    if (workspaceToken) openWorkspace(workspaceToken)
  }, [workspaceToken])

  async function connectToken(token: string) {
    if (connecting.current) return
    connecting.current = true
    setBusy(true)
    setNotice('')
    try {
      const [identity, data, existing] = await Promise.all([
        get<Authority>(token, '/session'), get<Catalog>(token, '/catalog'), readOrders(token),
      ])
      setSession({ token, identity })
      setCatalog(data)
      setOrders(existing)
      setPending(restore(identity))
      if (selectedOrder) {
        const match = existing.find(order => order.id === selectedOrder)
        if (match) onSelectOrder?.(match.id, match.tripId)
      }
    } catch (error) { setNotice(failure(error)) }
    finally {
      connecting.current = false
      setBusy(false)
    }
  }

  async function resolve(command: Command, lookup: boolean) {
    if (!session || active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try {
      const response = await requestSalesApi(api, session.token,
        lookup ? '/commands/' + command.id : command.path, lookup ? undefined : command.body)
      if (lookup && response.status === 404) {
        setNotice('Chưa tìm thấy kết quả. Có thể gửi lại đúng yêu cầu này.')
        return
      }
      if ([401, 403].includes(response.status)) throw new HttpError(response.status)
      const result = await response.json() as { status?: string; reasonCode?: string }
      if (result.status === 'CONFLICT') {
        setNotice('Mã lệnh đã được dùng cho nội dung khác. Hãy tra cứu yêu cầu gốc.')
        return
      }
      if (result.status !== 'APPLIED' && result.status !== 'REJECTED') throw new Error('Unknown outcome')
      forgetPending(key(session.identity), command.id)
      setPending(null)
      setNotice(result.status === 'APPLIED' ? 'Đã ghi vào cơ sở dữ liệu.'
        : rejections[result.reasonCode ?? ''] ?? `Yêu cầu bị từ chối (${result.reasonCode ?? 'UNKNOWN'}).`)
      try { setOrders(await readOrders(session.token)) }
      catch { setNotice('Đã có kết quả lệnh. Chưa tải lại được danh sách đơn.') }
    } catch (error) {
      setNotice(error instanceof HttpError ? failure(error) : 'Chưa rõ kết quả. Tra cứu hoặc gửi lại cùng mã lệnh.')
    } finally {
      setBusy(false)
      active.current = false
    }
  }

  function save(path: Command['path'], fields: object) {
    if (!session || busy || pending || active.current || !navigator.onLine) return
    const id = crypto.randomUUID()
    const command = { path, id, body: JSON.stringify({ ...fields, commandId: id }) }
    setPending(command)
    rememberPending(key(session.identity), command)
    void resolve(command, false)
  }

  const disabled = busy || offline || pending !== null
  const selected = context && orders.find(order => order.id === selectedOrder)
  useEffect(() => {
    if (!selected || !selectedSource || focusedSource.current === selectedSource) return
    const target = orderSection.current?.querySelector<HTMLElement>(`[data-source="${CSS.escape(selectedSource)}"]`)
    if (target) {
      target.focus()
      focusedSource.current = selectedSource
    }
  }, [selected, selectedSource])
  const pinned = !!context && !!selectedOrder
  const trips = [...new Map(orders.map(order => [order.tripId, order.trip]))]
  const queued = orders.filter(order => (!tripFilter || order.tripId === tripFilter) && (step === 'all' || order.actions?.[step]))
  const shown = pinned ? selected ? [selected] : [] : queued
  const Container = workspaceToken ? 'section' : 'main'
  return <Container className={workspaceToken ? 'sales-pane c1-page' : 'page c1-page'}>
    {workspaceToken ? <h2>Đơn A và giữ hàng</h2> : <header className="page-heading c1-header">
      <div>
        <a className="page-link" href="/">Trang chính</a>
        <h1>Đơn A và giữ hàng</h1>
        <p className="section-detail">APP-02C.2 · DEV tổng hợp{session && ' · ' + session.identity.tenantId}</p>
        <a className="page-link" href="/sales/customers">Khách hàng và địa chỉ giao</a>
        <a className="page-link" href="/sales/load">Nạp hàng lên xe</a>
      </div>
      <ThemeSelect />
    </header>}
    {offline && <div role="status"><Notice title="Đang ngoại tuyến" tone="warning">Kết nối mạng để gửi yêu cầu.</Notice></div>}
    {!session ? workspaceToken
      ? <Button disabled={busy || offline} onClick={() => void connectToken(workspaceToken)}>Tải màn hình</Button>
      : <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <h2>Kết nối phiên DEV</h2>
      <Field label="Mã truy cập DEV" name="token" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={busy || offline}>Kết nối</Button>
    </form> : catalog && <>
      <div className="button-row">
        {!workspaceToken && <Button disabled={busy} onClick={() => { setSession(null); setOrders([]); setPending(null); setNotice('') }}>Đổi phiên DEV</Button>}
        <Button disabled={busy || offline} onClick={() => {
          setBusy(true)
          void readOrders(session.token).then(setOrders)
            .catch(error => setNotice(failure(error))).finally(() => setBusy(false))
        }}>Tải lại đơn</Button>
      </div>
      {pending && <section className="panel panel-content" aria-label="Khôi phục lệnh">
        <p>Mã lệnh <code>{pending.id}</code></p>
        <div className="button-row">
          <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
          <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
        </div>
      </section>}
      {session.identity.sales && !pinned && <DraftForm session={session} catalog={catalog} disabled={disabled} save={save} context={context} />}
      {context && !pinned && <section className="panel panel-content" aria-labelledby="app03-handoff">
        <h2 id="app03-handoff">Hàng đợi bàn giao</h2>
        <div className="sales-filter">
          <label className="field">Lọc theo chuyến
            <select value={tripFilter} onChange={event => setTripFilter(event.currentTarget.value)}>
              <option value="">Tất cả chuyến</option>
              {trips.map(([id, code]) => <option value={id} key={id}>{code}</option>)}
            </select>
          </label>
          <label className="field">Bước xử lý
          <select value={step} onChange={event => setStep(event.currentTarget.value as typeof step)}>
            <option value="all">Tất cả đơn</option>
            {session.identity.sales && <option value="confirm">{steps.confirm}</option>}
            {session.identity.warehouse && <>
              <option value="reserve">{steps.reserve}</option>
              <option value="load">{steps.load}</option>
            </>}
          </select>
          </label>
        </div>
        {queued.length === 0 ? <p>Chưa có đơn ở bước này.</p> : <ul className="c1-rows">
          {queued.map(order => <li key={order.id}>
            <a className="page-link" href={orderLocation('orders', order)}>{order.trip} · {order.customer} · {order.id.slice(0, 8)}</a>
            <span>{(Object.keys(steps) as (keyof typeof steps)[]).filter(key => order.actions?.[key])
              .map(key => steps[key]).join(' · ') || labels[order.status]}</span>
          </li>)}
        </ul>}
      </section>}
      <section ref={orderSection} className="panel panel-content" aria-labelledby="c2-orders">
        <h2 id="c2-orders">Đơn A</h2>
        {pinned && <a className="page-link" href="/work/orders">Tất cả đơn</a>}
        {pinned && !selected && <p>Không tìm thấy đơn trong phạm vi hiện hành.</p>}
        {selected && selectedSource && selected.confirmSourceId !== selectedSource &&
          !selected.lines.some(line => line.handoff?.lots.some(lot => lot.sourceId === selectedSource)) &&
          <p>Không tìm thấy nguồn này trên đơn.</p>}
        {!pinned && orders.length === 0 && <p>Chưa có đơn A.</p>}
        {shown.map(order => <article className="panel panel-content" key={order.id}>
          <h3>{order.customer} · {order.trip}</h3>
          <p>{order.shipTo}</p>
          <p><strong>{labels[order.status] ?? order.status}</strong>{order.businessDate && ' · ' + order.businessDate.slice(0, 10)}</p>
          {workspaceToken && order.confirmSourceId && <details open={selectedOrder === order.id || undefined}>
            <summary>Nguồn và địa chỉ đã Confirm</summary>
            <p className="c3-source" data-source={order.confirmSourceId} tabIndex={-1}>
              Nguồn Confirm {context ? <a className="page-link" href={orderLocation('orders', order, order.confirmSourceId)}>
                {order.confirmSourceId}
              </a> : order.confirmSourceId}
            </p>
            <p className="c3-source">Mã đơn {order.id}</p>
            <p className="c3-source">Bản chụp địa chỉ {order.shipToSnapshotId} · {order.shipToOrigin === 'DIRECT' ? 'Một lần' : 'Đã đăng ký'}</p>
            {onOpenAr && session.identity.sales && <Button onClick={() => onOpenAr(order.id, order.tripId)}>Công nợ theo đơn</Button>}
          </details>}
          {context && order.actions?.load && <a className="page-link" href={orderLocation('load', order)}>Nạp đơn lên xe</a>}
          {(context ? order.actions?.confirm : order.status === 'DRAFT' && session.identity.sales) &&
            <Button disabled={disabled} variant="primary" onClick={() => save('/confirm', { orderId: order.id })}>Confirm đơn</Button>}
          {order.lines.map(line => <div className="panel panel-content" key={line.sku}>
            <p>{line.sku} · Đặt {line.ordered} {line.baseUom} · Đã giữ {line.reserved} {line.baseUom}</p>
            {line.delivered > 0 && <p>Đã giao {line.delivered} {line.baseUom}</p>}
            {line.handoff ? <HandoffLine line={line} order={order} />
              : workspaceToken && order.confirmSourceId && <p>Đã nạp {line.loaded} {line.baseUom} · Còn giữ trên xe {line.heldOnTrip} {line.baseUom}</p>}
            {line.policyVersion !== null && <p>Chính sách v{line.policyVersion} · Giá {
              line.pricing && line.unitPrice !== null ? `${vndUnit.format(line.unitPrice)} VND` : line.unitPrice
            } · Hạn {line.termsDays} ngày</p>}
            {line.pricing && <section aria-label={`Giá đã chốt ${line.sku}`}>
              <h4>Giá đã chốt</h4>
              <p>{line.pricing.mode === 'INCLUSIVE' ? 'Giá gồm VAT' : 'Giá chưa VAT'} · Phương án giá v{line.pricing.version}</p>
              <PriceAmounts amounts={[
                ['Giá dòng trước chiết khấu', line.pricing.base],
                ['Chiết khấu', line.pricing.discount],
                ['Tiền chưa thuế', line.pricing.net],
                ['Thuế', line.pricing.tax],
                ['Tổng thanh toán', line.pricing.gross],
              ]} />
            </section>}
            {workspaceToken && line.policySourceHash && <details>
              <summary>Nguồn chính sách</summary><p className="c3-source">{line.policySourceHash}</p>
            </details>}
            {(context ? order.actions?.reserve : session.identity.warehouse) && order.confirmSourceId && line.lineId
              && (line.handoff?.unreserved ?? line.ordered - line.reserved - line.delivered) > 0 &&
              <form className="c1-fields" aria-label={`Giữ hàng ${line.sku}`} onSubmit={event => {
                event.preventDefault()
                const values = new FormData(event.currentTarget)
                save('/reserve', { orderId: order.id, lineId: line.lineId, confirmSourceId: order.confirmSourceId,
                  locationId: values.get('warehouse'), quantity: Number(values.get('quantity')) })
              }}>
                <label className="field">Kho
                  <select name="warehouse" defaultValue="" required>
                    <option value="">Chọn kho</option>
                    {catalog.warehouses.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
                  </select>
                </label>
                <Field label="Số lượng giữ" name="quantity" type="number" min="0.001" step="0.001" required />
                <Button variant="primary" type="submit" disabled={disabled}>Reserve hàng</Button>
              </form>}
          </div>)}
          <OrderPriceTotals lines={order.lines} />
        </article>)}
      </section>
    </>}
    {notice && <p role="status">{notice}</p>}
  </Container>
}
