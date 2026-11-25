import { useEffect, useEffectEvent, useState } from 'react'
import { Button, Field } from '../ui/controls'
import { HttpError, read, readError, requestSalesApi, type Customer } from './customer-api'

type Source = {
  commandId: string; orderId: string; orderLineId: string; customer: string; originTrip: string; trip: string
  owner: string; occurredAtUtc: string
  sourceId: string | null; deliveryId: string | null; loadSourceId: string | null; confirmSourceId: string | null
  shipToSnapshotId: string | null; shipToVersion: number; shipTo: string | null
  policyVersion: number; termsDays: number; businessDate: string | null; dueBusinessDate: string | null
  status: string; reason: string | null; overdue: boolean | null; quantity: string
  amount: string | null; applied: string | null; balance: string | null; cost: string | null
}
type CustomerTotal = {
  customerId: string; customer: string; reconciled: number; expected: number
  knownBalance: string | null; knownOverdue: string | null
}
type Report = {
  currency?: 'VND'
  asOf: string; expected: number | null; reconciled: number; knownBalance: string | null
  knownOverdue: string | null; customers: CustomerTotal[]; sources: Source[]
}
type Filter = { asOf: string; customerId: string; orderId: string; orderLineId: string;
  deliveryId: string; due: string; overdue: string }
const api = '/api/synthetic/app02c5'
const statusText: Record<string, string> = {
  RECONCILED: 'Đã đối soát', PENDING: 'Chờ ghi sổ', REJECTED: 'Ghi sổ bị từ chối',
  RECONCILIATION_REQUIRED: 'Cần kiểm tra chứng từ và sổ',
}

function decimal(value: string | null) {
  if (value === null) return 'Chưa xác định'
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value
}

function queryFor(filter: Filter) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value)
  return query
}

function SourceDetail({ source, returnUrl }: { source: Source; returnUrl?: string }) {
  return <details className="panel panel-content">
    <summary>{source.customer} · {source.trip} · {statusText[source.status]}</summary>
    <dl className="sales-facts">
      <dt>Chuyến gốc</dt><dd>{source.originTrip}</dd>
      <dt>Chuyến giao</dt><dd>{source.trip}</dd>
      <dt>Số lượng giao</dt><dd>{decimal(source.quantity)}</dd>
      {source.status === 'RECONCILED' ? <>
        <dt>Ngày giao</dt><dd>{source.businessDate?.slice(0, 10)}</dd>
        <dt>Hạn thanh toán</dt><dd>{source.overdue ? 'Quá hạn' : 'Đến hạn'} {source.dueBusinessDate?.slice(0, 10)}</dd>
        <dt>Gốc / đã phân bổ / còn nợ</dt><dd>{decimal(source.amount)} / {decimal(source.applied)} / {decimal(source.balance)}</dd>
        <dt>Giá vốn</dt><dd>{decimal(source.cost)}</dd>
        <dt>Địa chỉ giao v{source.shipToVersion}</dt><dd>{source.shipTo}</dd>
      </> : <>
        <dt>Người phụ trách</dt><dd>{source.owner}</dd>
        <dt>Lần giao ghi nhận (UTC)</dt><dd>{source.occurredAtUtc}</dd>
        {source.reason && <><dt>Lý do</dt><dd>{source.reason}</dd></>}
        <dt>Bước tiếp theo</dt><dd>{source.status === 'PENDING'
          ? 'Tra cứu mã yêu cầu gốc trong Giao hàng.'
          : 'Đối chiếu lần giao và chứng từ với người phụ trách.'}</dd>
      </>}
      <dt>Chính sách đã Confirm</dt><dd>v{source.policyVersion} · {source.termsDays} ngày</dd>
      <dt>Mã đơn</dt><dd>{source.orderId}</dd>
      <dt>Mã dòng đơn A</dt><dd>{source.orderLineId}</dd>
      <dt>Mã yêu cầu</dt><dd>{source.commandId}</dd>
      <dt>Nguồn Confirm</dt><dd>{source.confirmSourceId}</dd>
      <dt>Nguồn nạp</dt><dd>{source.loadSourceId}</dd>
      {source.sourceId && <><dt>Nguồn giao hàng</dt><dd>{source.sourceId}</dd></>}
      {source.deliveryId && <><dt>Mã lần giao</dt><dd>{source.deliveryId}</dd></>}
      {source.shipToSnapshotId && <><dt>Bản chụp địa chỉ</dt><dd>{source.shipToSnapshotId}</dd></>}
    </dl>
    {returnUrl && source.deliveryId && <a className="page-link" href={returnUrl + '?delivery=' + source.deliveryId}>Trả hàng từ lần giao này</a>}
  </details>
}

type ReportProps = { token: string; orderId: string; returnUrl?: string }

export function ScopedArReport({ onCheckOrder, ...props }: ReportProps & { onCheckOrder: (id: string) => void }) {
  const { orderId, token } = props
  const [checked, setChecked] = useState('')
  const [error, setError] = useState('')
  const publish = useEffectEvent((id: string) => onCheckOrder(id))

  useEffect(() => {
    if (!orderId) return
    let active = true
    async function load<T>(api: string, path: string): Promise<T[]> {
      const response = await requestSalesApi(api, token, path)
      if (!response.ok) throw new HttpError(response.status)
      return response.json() as Promise<T[]>
    }
    void Promise.all([
      load<{ id: string; trip: string }>('/api/synthetic/app02c2', '/orders'),
      load<{ code: string }>('/api/synthetic/app02c4', '/trips'),
    ]).then(([orders, trips]) => {
      if (!active) return
      const order = orders.find(row => row.id === orderId)
      const allowed = order && trips.some(trip => trip.code === order.trip)
      publish(allowed ? orderId : '')
      if (allowed) setChecked(orderId)
    }).catch(error => { if (active) setError(readError(error)) })
    return () => { active = false }
  }, [token, orderId])

  if (!orderId || checked === orderId) return <ArReport {...props} />
  if (error) return <p role="alert">{error}</p>
  return <p role="status">Đang kiểm tra phạm vi của đơn…</p>
}

export function ArReport({ token, orderId, returnUrl }: ReportProps) {
  const [filter, setFilter] = useState<Filter>({
    asOf: '', customerId: '', orderId, orderLineId: '', deliveryId: '', due: '', overdue: '',
  })
  const [report, setReport] = useState<Report | null>(null)
  const [customers, setCustomers] = useState<Customer[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(true)
  const [exporting, setExporting] = useState(false)
  useEffect(() => {
    let active = true
    read<Customer[]>(token, '/customers').then(rows => { if (active) setCustomers(rows) })
      .catch(() => { /* The authoritative report can still be read without Customer options. */ })
    return () => { active = false }
  }, [token])
  useEffect(() => {
    let active = true
    requestSalesApi(api, token, '/ar?' + queryFor(filter))
      .then(async response => {
        if (!response.ok) throw new HttpError(response.status)
        const value = await response.json() as Report
        if (active) setReport(value)
      })
      .catch(error => {
        if (active) setError(error instanceof HttpError && error.status === 400
          ? 'Nhập mốc UTC theo dạng YYYY-MM-DDTHH:mm:ssZ và không vượt giờ máy chủ.' : readError(error))
      })
      .finally(() => { if (active) setBusy(false) })
    return () => { active = false }
  }, [token, filter])

  async function download(asOf: string) {
    setExporting(true)
    setError('')
    try {
      const query = queryFor({ ...filter, asOf })
      query.set('download', 'true')
      const response = await requestSalesApi(api, token, '/ar?' + query)
      if (!response.ok) throw new HttpError(response.status)
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url
      link.download = 'sales-a-ar.json'
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (error) { setError(readError(error)) }
    finally { setExporting(false) }
  }

  return <section className="sales-pane" aria-labelledby="ar-title">
    <h2 id="ar-title">Báo cáo AR</h2>
    <form className="panel panel-content sales-filter" onSubmit={event => {
      event.preventDefault()
      const data = new FormData(event.currentTarget)
      const orderLineId = String(data.get('orderLineId') ?? '').trim()
      if (orderLineId && !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(orderLineId)) {
        setError('Nhập mã dòng đơn A theo định dạng UUID.')
        return
      }
      setBusy(true)
      setReport(null)
      setError('')
      setFilter({ asOf: String(data.get('asOf') ?? '').trim(), customerId: String(data.get('customerId') ?? ''),
        orderId: String(data.get('orderId') ?? '').trim(), deliveryId: String(data.get('deliveryId') ?? '').trim(),
        orderLineId,
        due: String(data.get('due') ?? ''), overdue: String(data.get('overdue') ?? '') })
    }}>
      <label className="field">Khách hàng
        <select name="customerId"><option value="">Tất cả trong phạm vi</option>
          {customers.map(c => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
        </select>
      </label>
      <Field name="orderId" label="Mã đơn A" defaultValue={orderId} />
      <Field name="orderLineId" label="Mã dòng đơn A" />
      <Field name="deliveryId" label="Mã lần giao" />
      <Field name="due" label="Ngày đến hạn" type="date" />
      <label className="field">Trạng thái hạn
        <select name="overdue"><option value="">Tất cả</option><option value="true">Quá hạn</option>
          <option value="false">Chưa quá hạn</option></select>
      </label>
      <Field name="asOf" label="Mốc UTC (ISO 8601)" placeholder="Hiện tại theo SQL" />
      <Button type="submit" disabled={busy || exporting}>Tải báo cáo</Button>
    </form>
    {busy && <p role="status">Đang tải báo cáo…</p>}
    {error && <p role="alert">{error}</p>}
    {report && <section className="panel panel-content" aria-label="Kết quả báo cáo AR">
      <p>Mốc UTC <time dateTime={report.asOf}>{report.asOf}</time></p>
      <p>Phạm vi chuyến được phân công · {report.currency ?? 'SYNTHETIC_UNITLESS'}</p>
      <Button disabled={exporting} onClick={() => void download(report.asOf)}>Xuất JSON</Button>
      {report.expected === null ? <p>Chưa có bằng chứng giao hàng trong phạm vi này.</p> : <>
        <p>Nguồn đã đối soát {report.reconciled}/{report.expected} lần giao đã ghi nhận</p>
        {report.reconciled < report.expected && <p role="status">Công nợ chưa đầy đủ. Xem các nguồn chờ đối soát bên dưới.</p>}
        <p>Tổng còn nợ đã đối soát {decimal(report.knownBalance)} · Quá hạn {decimal(report.knownOverdue)}</p>
        <ul className="c1-rows" aria-label="Số dư theo khách hàng">
          {report.customers.map(c => <li key={c.customerId}>
            <strong>{c.customer}</strong>
            <span>{c.reconciled}/{c.expected} · Còn nợ {decimal(c.knownBalance)} · Quá hạn {decimal(c.knownOverdue)}</span>
          </li>)}
        </ul>
        {report.sources.map(source => <SourceDetail key={JSON.stringify([source.owner, source.commandId])} source={source} returnUrl={returnUrl} />)}
      </>}
    </section>}
  </section>
}
