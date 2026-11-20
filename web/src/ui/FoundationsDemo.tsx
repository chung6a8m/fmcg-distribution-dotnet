import { useEffect, useRef, useState } from 'react'
import { Badge, Button, DemoDialog, Field, Notice, ThemeSelect, type Density } from './controls'

const products = [
  { code: 'SP-001', name: 'Sữa tươi tiệt trùng không đường — thùng 48 hộp × 180 ml', quantity: 24, amount: '1.248.000 ₫', fontSample: 'regular' },
  { code: 'SP-002', name: 'Nước khoáng thiên nhiên — chai 500 ml', quantity: 12, amount: '216.000 ₫', fontSample: 'bold' },
  { code: 'SP-003', name: 'Trà xanh vị chanh — lốc 6 chai', quantity: 6, amount: '144.000 ₫' }
]

function ProductSample() {
  return <section className="panel sample-panel" aria-labelledby="products-title">
    <div className="panel-heading">
      <h2 id="products-title">Danh mục mẫu</h2>
      <span className="section-detail">3 mặt hàng</span>
    </div>
    <table className="sample-table" aria-labelledby="products-title" role="table">
      <thead role="rowgroup">
        <tr role="row">{['Mã hàng', 'Tên mặt hàng', 'Số lượng', 'Thành tiền'].map(label =>
          <th key={label} scope="col" role="columnheader">{label}</th>)}</tr>
      </thead>
      <tbody role="rowgroup">{products.map(product =>
        <tr role="row" key={product.code}>
          <td role="cell"><span className="card-label" aria-hidden="true">Mã hàng</span>{product.code}</td>
          <td role="cell"><span className="card-label" aria-hidden="true">Tên mặt hàng</span>
            <span data-font-sample={product.fontSample}>{product.name}</span>
          </td>
          <td role="cell" className="numeric"><span className="card-label" aria-hidden="true">Số lượng</span>
            {product.code === 'SP-003'
              ? <input type="number" min="0" step="1" defaultValue={product.quantity} aria-label={'Số lượng ' + product.code} />
              : product.quantity}
          </td>
          <td role="cell" className="numeric"><span className="card-label" aria-hidden="true">Thành tiền</span>{product.amount}</td>
        </tr>)}</tbody>
    </table>
    <p className="table-note">Có thể sửa số lượng ở dòng SP-003. Các giá trị tiền là số liệu minh họa cố định.</p>
  </section>
}

function DemoActions({ density }: { density: Density }) {
  const [pending, setPending] = useState(false)
  const [completed, setCompleted] = useState(0)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => () => { if (timer.current !== null) clearTimeout(timer.current) }, [])
  function run() {
    if (timer.current !== null) return
    setPending(true)
    timer.current = setTimeout(() => {
      timer.current = null
      setCompleted(count => count + 1)
      setPending(false)
    }, 1000)
  }
  return <div className="demo-actions">
    <div className="button-row">
      <Button variant="primary" disabled={pending} onClick={run}>Chạy tác vụ mẫu</Button>
      <DemoDialog density={density} />
    </div>
    <p className="action-result" role="status">{pending ? 'Đang chạy mô phỏng…'
      : completed > 0 ? 'Đã hoàn tất mô phỏng lần ' + completed + '.' : ''}</p>
  </div>
}

export function FoundationsDemo() {
  const [density, setDensity] = useState<Density>('compact')
  const [quantity, setQuantity] = useState('0')
  const quantityError = /^0*[1-9]\d*$/.test(quantity) ? undefined
    : 'Giá trị mẫu chưa hợp lệ.\nNhập số nguyên dương, lớn hơn 0 và không có phần thập phân.'
  return <main className="page demo-page" data-density={density}>
    <header className="page-heading">
      <nav className="button-row" aria-label="Điều hướng trang"><a className="page-link" href="/">Trạng thái kết nối</a><a className="entry-link" href="/ui/data-entry">UI-02 · Form & Grid Lab</a></nav>
      <div className="title-row">
        <h1>Nền tảng giao diện</h1>
        <span className="environment">UI-01 · Synthetic / Nonproduction</span>
      </div>
      <p>Dữ liệu minh họa. Các thao tác mẫu chỉ thay đổi nội dung trên trang này.</p>
    </header>
    <div className="display-settings">
      <ThemeSelect />
      <div className="field">
        <label htmlFor="demo-density">Mật độ</label>
        <select id="demo-density" value={density} onChange={event => setDensity(event.currentTarget.value as Density)}>
          <option value="compact">Compact</option>
          <option value="comfortable">Comfortable</option>
          <option value="field">Field</option>
        </select>
      </div>
    </div>
    <div className="demo-grid">
      <section className="panel" aria-labelledby="fields-title">
        <div className="panel-heading"><h2 id="fields-title">Trường nhập liệu</h2></div>
        <div className="panel-content">
          <Field label="Tên điểm bán" defaultValue="Cửa hàng bách hóa Nguyễn Đình Chiểu, phường Bàn Cờ, Thành phố Hồ Chí Minh" />
          <div className="field-pair">
            <Field label="Mã tham chiếu (chỉ đọc)" value="UI-01-DEMO" readOnly />
            <Field label="Mã nội bộ (không khả dụng)" value="NB-001" disabled help="Mã này được đặt sẵn trong bản minh họa." />
          </div>
          <Field label="Số lượng mẫu" value={quantity} inputMode="numeric" error={quantityError}
            onChange={event => setQuantity(event.currentTarget.value)} />
        </div>
        <DemoActions density={density} />
      </section>
      <section className="panel" aria-labelledby="states-title">
        <div className="panel-heading"><h2 id="states-title">Trạng thái mẫu</h2></div>
        <div className="panel-content">
          <div className="badges">
            <Badge tone="success">Thành công</Badge><Badge tone="info">Thông tin</Badge>
            <Badge tone="warning">Cảnh báo</Badge><Badge tone="danger">Lỗi</Badge>
          </div>
          <div className="notices">
            <Notice tone="info" title="Đang tải">Đang đọc danh mục minh họa…</Notice>
            <Notice tone="warning" title="Trống">Danh sách mẫu chưa có mặt hàng.</Notice>
            <Notice tone="danger" title="Có lỗi">Không thể hiển thị dữ liệu trong ví dụ này.</Notice>
          </div>
        </div>
      </section>
    </div>
    <ProductSample />
  </main>
}
