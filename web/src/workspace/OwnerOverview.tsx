import { useEffect, useRef, useState } from 'react'
import { Button } from '../ui/controls'
import { requestSalesApi } from '../sales/customer-api'

const sections = [
  ['Đơn và chuyến', [
    ['orders', 'Đơn A', 'Đơn nháp và đơn đã xác nhận'], ['trips', 'Chuyến', 'Mỗi mã chuyến một dòng'],
  ]],
  ['Tồn và giá vốn', [
    ['main-qty', 'Số lượng MAIN', 'Kho và SKU'], ['main-value', 'Giá trị MAIN', 'Kho và SKU'],
    ['trip-qty', 'Số lượng TRIP', 'Chuyến và SKU'], ['trip-value', 'Giá trị TRIP', 'Chuyến và SKU, phân bổ theo giá trị xe'],
    ['vehicle-qty', 'Số lượng trên xe', 'Xe và SKU'], ['vehicle-value', 'Giá trị trên xe', 'Xe và SKU'],
    ['qa-qty', 'Số lượng cách ly', 'Khu QA và SKU'], ['qa-value', 'Giá trị cách ly', 'Khu QA và SKU'],
    ['main-lots', 'Lô tại MAIN', 'Kho, SKU và lô'], ['vehicle-lots', 'Lô trên xe', 'Xe, SKU và lô'],
    ['qa-lots', 'Lô trong khu QA', 'Khu QA, SKU và lô'],
    ['a-hold', 'Quyền giữ A', 'Quyền còn lại theo chuyến và SKU'],
    ['b-quota', 'Quota B', 'Quyền còn lại theo chuyến và SKU'],
    ['cogs', 'Giá vốn thuần', 'Hiệu ứng giá vốn giao, trả và điều chỉnh'],
  ]],
  ['Công nợ và tiền', [
    ['ar-a', 'Dư nợ A', 'Khoản phải thu theo lần giao A'], ['ar-b', 'Dư nợ B', 'Khoản phải thu theo lần giao B'],
    ['customer-credit', 'Credit tiền khách', 'Sổ tiền khách D.3'],
    ['custody', 'Tiền ngoài thị trường', 'Hiệu ứng custody của chuyến'],
    ['fund', 'Tiền đã vào quỹ', 'Hiệu ứng fund của chuyến'],
    ['advance', 'Tạm ứng còn lại F02', 'Chứng từ tạm ứng theo nhân viên'],
    ['return-credit', 'Credit còn lại F08', 'Credit trả hàng trừ khoản đã thực chi'],
    ['refund-treasury', 'Quỹ hoàn tiền F08', 'Sổ quỹ hoàn tiền của tenant'],
    ['refund-paid', 'Hoàn tiền đã chi F08', 'Chứng từ thực chi'],
  ]],
  ['Công việc chờ', [
    ['confirm-pending', 'Đơn A chờ xác nhận', 'Đơn A theo chuyến'],
    ['cash-pending', 'Đề nghị nộp tiền chờ nhận', 'Đề nghị bàn giao tiền'],
    ['advance-pending', 'Tạm ứng chờ nhận tiền hoàn', 'Chứng từ tạm ứng'],
    ['return-pending', 'Hồ sơ trả hàng còn mở', 'Hồ sơ trả hàng và QA'],
    ['refund-pending', 'Đề nghị hoàn tiền chưa chi', 'Đề nghị hoàn tiền F08'],
    ['cost-pending', 'Đề nghị điều chỉnh giá vốn chưa ghi', 'Đề nghị giá vốn F06'],
    ['finance-pending', 'Nguồn kế toán chưa khớp', 'Nguồn thuộc phạm vi D.4'],
    ['finance-cases', 'Hồ sơ chênh lệch còn mở', 'Hồ sơ kế toán D.4'],
  ]],
] as const

const units: Record<string, string> = {
  orders: 'đơn', trips: 'chuyến', cases: 'hồ sơ', advances: 'chứng từ', refunds: 'đề nghị',
  offers: 'đề nghị', sources: 'nguồn', SYNTHETIC_UNITLESS: 'đơn vị DEV',
}
const time = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'medium', timeZone: 'Asia/Bangkok' })

type Source = { id: string | null; relatedId: string | null; label: string; state: string; value: string | null;
  ownerRole: string | null; nextAction: string | null }
type Metric = { metric: string; unit: string; known: number; expected: number; value: string | null; sources: Source[] }
type Report = { tenantId: string; asOf: string; businessCutoff: string; metrics: Metric[] }
type Selection = { label: string; metric: Metric }

function formatValue(value: string, unit: string) {
  const [integer, fraction] = value.split('.')
  const decimals = fraction?.replace(/0+$/, '')
  const sign = integer.startsWith('-') && BigInt(integer) === 0n ? '−' : ''
  return sign + BigInt(integer).toLocaleString('vi-VN') + (decimals ? ',' + decimals : '') + ' ' + (units[unit] ?? unit)
}

export function OwnerOverview({ token }: { token: string }) {
  const [report, setReport] = useState<Report | null>(null)
  const [failure, setFailure] = useState(false)
  const [refresh, setRefresh] = useState(0)
  const [selection, setSelection] = useState<Selection | null>(null)
  const detail = useRef<HTMLElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let active = true
    requestSalesApi('/api/synthetic/app03', token, '/owner-overview')
      .then(async response => {
        if (!response.ok) throw new Error('Owner report unavailable')
        const next = await response.json() as Report
        if (active) setReport(next)
      })
      .catch(() => { if (active) setFailure(true) })
    return () => { active = false }
  }, [token, refresh])

  useEffect(() => {
    if (selection) detail.current?.focus()
  }, [selection])

  return <section className="panel panel-content">
    <header className="app03-header">
      <h2>Tổng quan Chủ NPP</h2>
      <Button onClick={() => {
        setSelection(null)
        setReport(null)
        setFailure(false)
        setRefresh(value => value + 1)
      }}>Tải lại số liệu</Button>
    </header>
    {failure ? <p role="alert">Không tải được số liệu. Kiểm tra kết nối hoặc tải lại.</p>
      : !report ? <p role="status">Đang tải số liệu…</p>
      : <>
        <p>Tenant {report.tenantId} · Đọc lúc {time.format(new Date(report.asOf))} ICT
          {' · '}Mốc nghiệp vụ {time.format(new Date(report.businessCutoff))} ICT</p>
        {selection && <section className="app03-owner-detail" ref={detail} tabIndex={-1} aria-label={'Nguồn ' + selection.label}>
          <header className="app03-header">
            <h3>Nguồn {selection.label}</h3>
            <Button onClick={() => { setSelection(null); trigger.current?.focus() }}>Đóng chi tiết</Button>
          </header>
          <div className="app03-table-scroll">
            <table className="app03-handoff-table" aria-label={'Nguồn ' + selection.label}>
              <thead><tr><th scope="col">Nguồn</th><th scope="col">Trạng thái</th><th scope="col">Giá trị</th></tr></thead>
              <tbody>{selection.metric.sources.map((source, index) => <tr key={index}>
                <td><span className="app03-lot-label">Nguồn</span><div>{source.label}
                  {source.id && <small>Mã nguồn {source.id}</small>}
                  {source.relatedId && <small>Mã liên quan {source.relatedId}</small>}
                </div></td>
                <td><span className="app03-lot-label">Trạng thái</span><div>{source.state}
                  {source.ownerRole && <small>Vai xử lý {source.ownerRole}</small>}
                  {source.nextAction && <small>{source.nextAction}</small>}
                </div></td>
                <td><span className="app03-lot-label">Giá trị</span>
                  {source.value === null ? 'Chưa đủ dữ liệu' : formatValue(source.value, selection.metric.unit)}</td>
              </tr>)}</tbody>
            </table>
          </div>
        </section>}
        {sections.map(([section, definitions]) => <section key={section}>
          <h3>{section}</h3>
          <ul className="app03-owner-metrics">
            {definitions.map(([id, label, scope]) => {
              const metrics = report.metrics.filter(metric => metric.metric === id)
              return <li key={id}>
                <strong>{label}</strong>
                <small>{scope}</small>
                {metrics.length === 0 && <span>Chưa đủ dữ liệu</span>}
                {metrics.map(metric => <div key={metric.unit}>
                  <Button aria-label={label + ' · ' + (units[metric.unit] ?? metric.unit)}
                    onClick={event => { trigger.current = event.currentTarget; setSelection({ label, metric }) }}>
                    {metric.value === null ? 'Chưa đủ dữ liệu'
                      : (metric.known < metric.expected ? 'Phần đã biết ' : '') + formatValue(metric.value, metric.unit)}
                  </Button>
                  <small>{metric.known}/{metric.expected} nguồn có số liệu
                    {metric.known < metric.expected && ' · Chưa đủ dữ liệu'}</small>
                </div>)}
              </li>
            })}
          </ul>
        </section>)}
      </>}
  </section>
}
