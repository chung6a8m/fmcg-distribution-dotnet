import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Button, Field, Notice, ThemeSelect } from '../ui/controls'
import { HttpError, read, requestSalesApi, type Detail, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02c2'
type Authority = Identity & { sales: boolean; warehouse: boolean }
type Option = { id: string; label: string }
type Catalog = { customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }
type Line = { lineId: string | null; sku: string; baseUom: string; ordered: number; reserved: number;
  unitPrice: number | null; policyVersion: number | null; termsDays: number | null }
type Order = { id: string; customer: string; trip: string; shipTo: string; businessDate: string | null;
  confirmSourceId: string | null; status: string; lines: Line[] }
type Command = { path: '/drafts' | '/confirm' | '/reserve'; id: string; body: string }
type Session = { token: string; identity: Authority }

const labels: Record<string, string> = {
  DRAFT: 'Đơn nháp',
  CONFIRMED_UNRESERVED: 'Đã Confirm · Chưa giữ hàng',
  CONFIRMED_PARTIALLY_RESERVED: 'Đã giữ một phần',
  CONFIRMED_FULLY_RESERVED: 'Đã giữ đủ',
}
const rejections: Record<string, string> = {
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
}

function key(identity: Authority) {
  return 'app02c2.pending.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

function restore(identity: Authority): Command | null {
  try {
    const raw = sessionStorage.getItem(key(identity))
    if (!raw) return null
    const item = JSON.parse(raw) as Command
    if (['/drafts', '/confirm', '/reserve'].includes(item.path) && /^[a-f0-9-]{36}$/.test(item.id)
        && JSON.parse(item.body).commandId === item.id) return item
  } catch { /* The identity remains isolated by owned DEV session. */ }
  return null
}

function persist(identity: Authority, command: Command | null) {
  try {
    if (command) sessionStorage.setItem(key(identity), JSON.stringify(command))
    else sessionStorage.removeItem(key(identity))
  } catch { /* Preserve the in-memory request when browser storage is unavailable. */ }
}

async function get<T>(token: string, path: string): Promise<T> {
  const response = await requestSalesApi(api, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function failure(error: unknown) {
  if (error instanceof HttpError && [401, 403].includes(error.status))
    return 'Phiên hoặc quyền hiện hành không cho phép thao tác.'
  return 'Không tải được dữ liệu từ API.'
}

function DraftForm({ session, catalog, disabled, save }: {
  session: Session; catalog: Catalog; disabled: boolean; save: (path: Command['path'], fields: object) => void
}) {
  const [customer, setCustomer] = useState('')
  const [detail, setDetail] = useState<Detail | null>(null)
  const [selectionError, setSelectionError] = useState('')
  const [rows, setRows] = useState([crypto.randomUUID()])
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
      lines: rows.map(row => ({ skuId: values.get('sku-' + row), quantity: Number(values.get('qty-' + row)) })),
    })
  }}>
    <h2>Tạo đơn A nháp</h2>
    <label className="field">Khách hàng
      <select required value={customer} onChange={event => { setCustomer(event.target.value); setSelectionError('') }}>
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
    {current?.selections.length === 0 && <a className="page-link" href="/sales/customers">Chọn địa chỉ trong hồ sơ khách hàng</a>}
    <label className="field">Chuyến
      <select name="trip" defaultValue="" required>
        <option value="">Chọn chuyến</option>
        {catalog.trips.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </select>
    </label>
    {rows.map((row, i) => <fieldset key={row} className="c1-fields">
      <legend>Dòng {i + 1}</legend>
      <label className="field">SKU dòng {i + 1}
        <select name={'sku-' + row} defaultValue="" required>
          <option value="">Chọn SKU</option>
          {catalog.skus.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
        </select>
      </label>
      <Field name={'qty-' + row} label={'Số lượng dòng ' + (i + 1)} type="number" min="0.001" step="0.001" required />
      {rows.length > 1 && <Button onClick={() => setRows(items => items.filter(item => item !== row))}>Xóa dòng {i + 1}</Button>}
    </fieldset>)}
    <div className="button-row">
      <Button disabled={rows.length === 24} onClick={() => setRows(items => [...items, crypto.randomUUID()])}>Thêm dòng</Button>
      <Button variant="primary" type="submit" disabled={disabled}>Lưu đơn nháp</Button>
    </div>
  </form>
}

export function OrderWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [pending, setPending] = useState<Command | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [offline, setOffline] = useState(!navigator.onLine)
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

  async function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    setBusy(true)
    setNotice('')
    try {
      const [identity, data, existing] = await Promise.all([
        get<Authority>(token, '/session'), get<Catalog>(token, '/catalog'), get<Order[]>(token, '/orders'),
      ])
      setSession({ token, identity })
      setCatalog(data)
      setOrders(existing)
      setPending(restore(identity))
    } catch (error) { setNotice(failure(error)) }
    finally { setBusy(false) }
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
      persist(session.identity, null)
      setPending(null)
      setNotice(result.status === 'APPLIED' ? 'Đã ghi vào cơ sở dữ liệu.'
        : rejections[result.reasonCode ?? ''] ?? `Yêu cầu bị từ chối (${result.reasonCode ?? 'UNKNOWN'}).`)
      try { setOrders(await get<Order[]>(session.token, '/orders')) }
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
    persist(session.identity, command)
    void resolve(command, false)
  }

  const disabled = busy || offline || pending !== null
  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div>
        <a className="page-link" href="/">Trang chính</a>
        <h1>Đơn A và giữ hàng</h1>
        <p className="section-detail">APP-02C.2 · DEV tổng hợp{session && ' · ' + session.identity.tenantId}</p>
        <a className="page-link" href="/sales/customers">Khách hàng và địa chỉ giao</a>
      </div>
      <ThemeSelect />
    </header>
    {offline && <div role="status"><Notice title="Đang ngoại tuyến" tone="warning">Kết nối mạng để gửi yêu cầu.</Notice></div>}
    {!session ? <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <h2>Kết nối phiên DEV</h2>
      <Field label="Mã truy cập DEV" name="token" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={busy || offline}>Kết nối</Button>
    </form> : catalog && <>
      <div className="button-row">
        <Button disabled={busy} onClick={() => { setSession(null); setOrders([]); setPending(null); setNotice('') }}>Đổi phiên DEV</Button>
        <Button disabled={busy || offline} onClick={() => {
          setBusy(true)
          void get<Order[]>(session.token, '/orders').then(setOrders)
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
      {session.identity.sales && <DraftForm session={session} catalog={catalog} disabled={disabled} save={save} />}
      <section className="panel panel-content" aria-labelledby="c2-orders">
        <h2 id="c2-orders">Đơn A</h2>
        {orders.length === 0 && <p>Chưa có đơn A.</p>}
        {orders.map(order => <article className="panel panel-content" key={order.id}>
          <h3>{order.customer} · {order.trip}</h3>
          <p>{order.shipTo}</p>
          <p><strong>{labels[order.status] ?? order.status}</strong>{order.businessDate && ' · ' + order.businessDate.slice(0, 10)}</p>
          {order.status === 'DRAFT' && session.identity.sales &&
            <Button disabled={disabled} variant="primary" onClick={() => save('/confirm', { orderId: order.id })}>Confirm đơn</Button>}
          {order.lines.map(line => <div className="panel panel-content" key={line.sku}>
            <p>{line.sku} · Đặt {line.ordered} {line.baseUom} · Đã giữ {line.reserved} {line.baseUom}</p>
            {line.policyVersion !== null && <p>Chính sách v{line.policyVersion} · Giá {line.unitPrice} · Hạn {line.termsDays} ngày</p>}
            {session.identity.warehouse && order.confirmSourceId && line.lineId && line.reserved < line.ordered &&
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
        </article>)}
      </section>
    </>}
    {notice && <p role="status">{notice}</p>}
  </main>
}
