import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Button, Field, Notice, ThemeSelect, type Density } from './controls'
import { demoQuantityError, parseDemoQuantity } from './decimal'
import { EditableGrid, type DraftLine, type GridSourceState } from './EditableGrid'
import { LookupField, searchKey, type LookupOption, type LookupSourceState } from './LookupField'

const customers: LookupOption[] = [
  { id: 'customer-101', code: 'KH-101', name: 'Tạp hóa Minh Anh', detail: 'Điểm bán Quận 1 · TP. Hồ Chí Minh' },
  { id: 'customer-102', code: 'KH-102', name: 'Tạp hóa Minh Anh', detail: 'Điểm bán Quận 3 · TP. Hồ Chí Minh' },
  { id: 'customer-103', code: 'KH-103', name: 'Cửa hàng Bách hóa Nguyễn Đình Chiểu', detail: 'Điểm bán dài tên · Hà Nội' },
  { id: 'customer-104', code: 'KH-104', name: 'Đại lý Đồng Tâm', detail: 'Điểm bán quận Hải Châu · Đà Nẵng' }
]

const originalLines: DraftLine[] = [
  { id: 'line-001', code: 'SP-001', name: 'Sữa tươi tiệt trùng không đường — thùng 48 hộp × 180 ml', unit: 'thùng', quantity: '12,5' },
  { id: 'line-002', code: 'SP-002', name: 'Nước khoáng thiên nhiên — chai 500 ml', unit: 'thùng', quantity: '8' },
  { id: 'line-003', code: 'SP-003', name: 'Trà xanh vị chanh — lốc 6 chai', unit: 'lốc', quantity: '3.25' },
  { id: 'line-004', code: 'SP-004', name: 'Bột giặt hương thiên nhiên — loại túi lớn dành cho khách hàng dài tên', unit: 'túi', quantity: '1' }
]

type Order = 'code' | 'quantity'

export function DataEntryDemo() {
  const [density, setDensity] = useState<Density>('compact')
  const [lookupState, setLookupState] = useState<LookupSourceState>('ready')
  const [lookupKey, setLookupKey] = useState(0)
  const [customer, setCustomer] = useState<LookupOption | null>(null)
  const [quantity, setQuantity] = useState('')
  const [touched, setTouched] = useState(false)
  const [attempted, setAttempted] = useState(false)
  const [reviewed, setReviewed] = useState(false)
  const customerRef = useRef<HTMLInputElement>(null)
  const quantityRef = useRef<HTMLInputElement>(null)

  // Reset remounts the uncontrolled lookup query; move focus after React mounts its new input.
  useLayoutEffect(() => {
    if (lookupKey !== 0) customerRef.current?.focus()
  }, [lookupKey])

  const [lines, setLines] = useState<DraftLine[]>(originalLines)
  const [selection, setSelection] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [appliedFilter, setAppliedFilter] = useState('')
  const [sortChoice, setSortChoice] = useState<Order>('code')
  const [appliedSort, setAppliedSort] = useState<Order>('code')
  const [editing, setEditing] = useState(false)
  const [gridState, setGridState] = useState<GridSourceState>('ready')
  const filterRef = useRef<HTMLInputElement>(null)
  const gridSourceRef = useRef<HTMLSelectElement>(null)
  const exitRef = useRef<HTMLButtonElement>(null)
  // Stable focus callbacks are important: changing onExit during a parent render would
  // re-run the grid's layout effect and steal focus from a filter being typed mid-edit.
  const focusAfterGrid = useCallback(() => exitRef.current?.focus(), [])
  const focusBeforeGrid = useCallback(() => gridSourceRef.current?.focus(), [])

  // A filter/sort request during editing is staged, never silently unmounting the draft editor.
  useEffect(() => {
    if (editing || (filter === appliedFilter && sortChoice === appliedSort)) return
    setAppliedFilter(filter)
    setAppliedSort(sortChoice)
    filterRef.current?.focus()
  }, [editing, filter, appliedFilter, sortChoice, appliedSort])

  const visible = useMemo(() => {
    const needle = searchKey(appliedFilter.trim())
    return lines.filter(line => searchKey(line.code + ' ' + line.name).includes(needle))
      .sort((a, b) => appliedSort === 'code' ? a.code.localeCompare(b.code)
        : (parseDemoQuantity(a.quantity) ?? 0) - (parseDemoQuantity(b.quantity) ?? 0) || a.code.localeCompare(b.code))
  }, [lines, appliedFilter, appliedSort])

  const pendingView = editing && (filter !== appliedFilter || sortChoice !== appliedSort)
  const selectedLine = lines.find(line => line.id === selection)
  const selectedVisible = visible.some(line => line.id === selection)
  const quantityInvalid = parseDemoQuantity(quantity) === null
  const formInvalid = attempted && (!customer || lookupState !== 'ready')
  const quantityError = (touched || attempted) && quantityInvalid ? demoQuantityError : undefined

  function checkForm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setAttempted(true)
    setTouched(true)
    setReviewed(false)
    if (!customer || lookupState !== 'ready') { customerRef.current?.focus(); return }
    if (quantityInvalid) { quantityRef.current?.focus(); return }
    setReviewed(true)
  }

  function resetForm() {
    setCustomer(null)
    setQuantity('')
    setAttempted(false)
    setTouched(false)
    setReviewed(false)
    setLookupKey(key => key + 1)
  }

  return <main className="page entry-page" data-density={density}>
    <header className="page-heading">
      <nav className="button-row" aria-label="Điều hướng UI Lab">
        <a className="page-link" href="/">Trạng thái kết nối</a>
        <a className="page-link" href="/ui/foundations">UI-01 · Foundations</a>
        <a className="page-link" href="/ui/command-recovery">UI-03 · Source & Recovery</a>
      </nav>
      <div className="title-row">
        <h1>Biểu mẫu và bảng tương tác</h1>
        <span className="environment">UI-02 · Synthetic / Nonproduction</span>
      </div>
      <p>Chỉ là dữ liệu cục bộ để kiểm thử component. Không tạo đơn, không gọi API nghiệp vụ và không lưu qua reload.</p>
    </header>
    <div className="display-settings">
      <ThemeSelect />
      <div className="field">
        <label htmlFor="entry-density">Mật độ</label>
        <select id="entry-density" value={density} onChange={event => setDensity(event.currentTarget.value as Density)}>
          <option value="compact">Compact</option>
          <option value="comfortable">Comfortable</option>
          <option value="field">Field</option>
        </select>
      </div>
    </div>
    <section className="panel" aria-labelledby="entry-form-title">
      <div className="panel-heading">
        <h2 id="entry-form-title">Form mẫu · identity và số thập phân</h2>
        <span className="section-detail">Không phải nghiệp vụ ghi nhận</span>
      </div>
      <form className="panel-content" onSubmit={checkForm} noValidate>
        <div className="field entry-source">
          <label htmlFor="lookup-state">Nguồn danh mục (mô phỏng)</label>
          <select id="lookup-state" value={lookupState}
            onChange={event => {
              setLookupState(event.currentTarget.value as LookupSourceState)
              setReviewed(false)
            }}>
            <option value="ready">Sẵn sàng</option><option value="loading">Đang tải</option>
            <option value="error">Lỗi đọc</option><option value="forbidden">Không đủ quyền</option>
          </select>
        </div>
        <div className="entry-form-grid">
          <LookupField key={lookupKey} label="Khách hàng" options={customers} selected={customer}
            onSelect={value => { setCustomer(value); setReviewed(false) }}
            onCommit={() => quantityRef.current?.focus()} inputRef={customerRef}
            error={formInvalid ? (lookupState === 'ready'
              ? 'Hãy chọn một khách hàng bằng mã từ danh sách.'
              : 'Nguồn danh mục chưa sẵn sàng để kiểm chứng identity.') : undefined}
            sourceState={lookupState} />
          <Field label="Số lượng minh họa (thùng)" ref={quantityRef} type="text"
            inputMode="decimal" value={quantity} error={quantityError}
            help="Chỉ quy tắc thử nghiệm UI: số dương, tối đa 3 chữ số thập phân; dấu phẩy hoặc dấu chấm."
            onChange={event => { setQuantity(event.currentTarget.value); setReviewed(false) }}
            onBlur={() => setTouched(true)}
            onKeyDown={event => {
              if (event.key === 'Enter') event.preventDefault()
            }} />
        </div>
        <div className="button-row">
          <Button variant="primary" type="submit">Kiểm tra nháp mẫu</Button>
          <Button onClick={resetForm}>Đặt lại form</Button>
        </div>
        {attempted && (!customer || lookupState !== 'ready' || quantityInvalid) && <p className="field-error" role="alert">
          Nháp chưa hợp lệ. Sửa các trường có thông báo; dữ liệu đã nhập được giữ nguyên.
        </p>}
        {reviewed && customer && <Notice tone="success" title="Dữ liệu mẫu hợp lệ">
          Đã kiểm tra tại trình duyệt: {customer.code} · {customer.name}; số lượng {quantity} thùng. Không có ghi sổ hay request.
        </Notice>}
      </form>
    </section>
    <section className="panel" aria-labelledby="entry-grid-title">
      <div className="panel-heading">
        <h2 id="entry-grid-title">Grid nháp · điều hướng và sửa ô</h2>
        <span className="section-detail">{visible.length} / {lines.length} dòng trong bộ lọc</span>
      </div>
      <div className="panel-content">
        <div className="entry-tools">
          <div className="field">
            <label htmlFor="grid-filter">Lọc mặt hàng (mã hoặc tên)</label>
            <input id="grid-filter" ref={filterRef} type="search" value={filter}
              onChange={event => setFilter(event.currentTarget.value)} />
          </div>
          <div className="field">
            <label htmlFor="grid-sort">Sắp xếp</label>
            <select id="grid-sort" value={sortChoice} onChange={event => setSortChoice(event.currentTarget.value as Order)}>
              <option value="code">Theo mã hàng</option><option value="quantity">Theo số lượng tăng dần</option>
            </select>
          </div>
          <div className="field">
            <label htmlFor="grid-state">Nguồn dòng (mô phỏng)</label>
            <select id="grid-state" ref={gridSourceRef} value={gridState} disabled={editing}
              onChange={event => setGridState(event.currentTarget.value as GridSourceState)}>
              <option value="ready">Sẵn sàng</option><option value="loading">Đang tải</option>
              <option value="error">Lỗi đọc</option><option value="forbidden">Không đủ quyền</option>
              <option value="empty">Chưa có dữ liệu</option>
            </select>
          </div>
        </div>
        {pendingView && <p className="entry-warning" role="status">
          Bộ lọc hoặc sắp xếp đang chờ. Hoàn tất Enter/Tab hoặc hủy Escape ở ô đang sửa trước khi áp dụng.
        </p>}
        <p className="table-note">Tab đi qua vùng điều khiển; dùng mũi tên để di chuyển ô, Enter/F2 để sửa Số lượng, Escape để hủy.
          Trong ô nhập, mũi tên chỉ di chuyển con trỏ; Tab xác nhận và chuyển đến ô số lượng tiếp theo.</p>
      </div>
      <EditableGrid label="Dòng hàng mẫu" rows={visible} selectedId={selection}
        onRowsChange={changed => setLines(current => current.map(line => changed.find(item => item.id === line.id) ?? line))}
        onSelect={setSelection} onEditingChange={setEditing}
        onExit={focusAfterGrid} onExitPrevious={focusBeforeGrid}
        state={gridState} filtered={Boolean(appliedFilter.trim())} />
      <div className="panel-content entry-footer">
        <p role="status">{selectedLine
          ? 'Đang chọn: ' + selectedLine.code + ' (' + selectedLine.id + ').' +
            (selectedVisible ? '' : ' Dòng đang ẩn bởi bộ lọc; lựa chọn chưa bị chuyển sang dòng khác.')
          : 'Chưa chọn dòng. Enter hoặc Space ở ô đọc để chọn đúng identity.'}</p>
        <div className="button-row">
          <Button ref={exitRef} disabled={editing} onClick={() => {
            setLines(originalLines)
            setSelection(null)
            setFilter('')
            setAppliedFilter('')
            setSortChoice('code')
            setAppliedSort('code')
            setGridState('ready')
          }}>Đặt lại các dòng mẫu</Button>
          <Button onClick={() => { setFilter(''); setSortChoice('code') }}>Xóa bộ lọc</Button>
        </div>
        {editing && <p className="field-help" role="status">
          Hoàn tất Enter/Tab hoặc hủy Escape ở ô số lượng trước khi đặt lại các dòng mẫu.
        </p>}
      </div>
    </section>
  </main>
}
