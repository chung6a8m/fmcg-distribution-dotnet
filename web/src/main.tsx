import { lazy, StrictMode, Suspense, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import { isLiveApiResponse } from './health'
import { Button, ThemeSelect } from './ui/controls'
import { FoundationsDemo } from './ui/FoundationsDemo'
import { DataEntryDemo } from './ui/DataEntryDemo'
import { SourceCommandDemo } from './ui/SourceCommandDemo'
import './style.css'

const CustomerWorkspace = lazy(() => import('./sales/CustomerWorkspace')
  .then(module => ({ default: module.CustomerWorkspace })))
const OrderWorkspace = lazy(() => import('./sales/OrderWorkspace')
  .then(module => ({ default: module.OrderWorkspace })))
const LoadWorkspace = lazy(() => import('./sales/LoadWorkspace')
  .then(module => ({ default: module.LoadWorkspace })))
const DeliveryWorkspace = lazy(() => import('./sales/DeliveryWorkspace')
  .then(module => ({ default: module.DeliveryWorkspace })))
const ReturnWorkspace = lazy(() => import('./sales/ReturnWorkspace')
  .then(module => ({ default: module.ReturnWorkspace })))
const CostAdjustmentWorkspace = lazy(() => import('./sales/CostAdjustmentWorkspace')
  .then(module => ({ default: module.CostAdjustmentWorkspace })))
const GiftWorkspace = lazy(() => import('./sales/GiftWorkspace')
  .then(module => ({ default: module.GiftWorkspace })))
const SalesWorkspace = lazy(() => import('./sales/SalesWorkspace')
  .then(module => ({ default: module.SalesWorkspace })))

const BStockWorkspace = lazy(() => import('./sales/BStockWorkspace')
  .then(module => ({ default: module.BStockWorkspace })))
const BSaleWorkspace = lazy(() => import('./sales/BSaleWorkspace')
  .then(module => ({ default: module.BSaleWorkspace })))
const PartnerRehearsalWorkspace = lazy(() => import('./sales/PartnerRehearsalWorkspace')
  .then(module => ({ default: module.PartnerRehearsalWorkspace })))
const MixedTripWorkspace = lazy(() => import('./sales/MixedTripWorkspace')
  .then(module => ({ default: module.MixedTripWorkspace })))

type Health = 'Đang kiểm tra API…' | 'API hoạt động' | 'Không kết nối API'

function HealthShell() {
  const [status, setStatus] = useState<Health>('Đang kiểm tra API…')
  useEffect(() => {
    let active = true
    const controller = new AbortController()
    fetch('/health/live', { signal: controller.signal, cache: 'no-store' })
      .then(isLiveApiResponse)
      .then(live => { if (active) setStatus(live ? 'API hoạt động' : 'Không kết nối API') })
      .catch(() => { if (active) setStatus('Không kết nối API') })
    return () => {
      active = false
      controller.abort()
    }
  }, [])

  return <main className="page health-page">
    <header className="page-heading">
      <h1>FMCG Distribution · APP-00A</h1>
      <p>Application foundation — Synthetic / Nonproduction</p>
    </header>
    <section className="panel panel-content" aria-labelledby="connection">
      <h2 id="connection">Trạng thái kết nối</h2>
      <p role="status">{status}</p>
      <p><strong>Online-first.</strong> Cần kết nối để ghi nghiệp vụ.</p>
    </section>
    <section className="panel panel-content">
      <h2>Nền tảng giao diện</h2>
      <ThemeSelect />
      <a className="page-link" href="/ui/foundations">Nền tảng giao diện</a>
      <a className="page-link" href="/ui/data-entry">UI-02 · Form & Grid Lab</a>
      <a className="page-link" href="/ui/command-recovery">UI-03 · Source & Recovery Lab</a>
      <a className="page-link" href="/sales">Workspace Sales A · DEV</a>
      <a className="page-link" href="/sales/customers">Khách hàng và địa chỉ giao · DEV</a>
      <a className="page-link" href="/sales/orders">Đơn A và giữ hàng · DEV</a>
      <a className="page-link" href="/sales/load">Nạp hàng lên xe · DEV</a>
      <a className="page-link" href="/sales/mixed-trip">Đối soát chuyến Mixed · DEV</a>
      <a className="page-link" href="/sales/partner-rehearsal">Hóa đơn và kế toán mô phỏng · DEV</a>
      <a className="page-link" href="/sales/b-sale">Bán và giao hàng B · DEV</a>
      <a className="page-link" href="/sales/b-stock">Quota, hạn mức và nạp hàng B · DEV</a>
      <a className="page-link" href="/sales/delivery">Giao hàng và công nợ A · DEV</a>
      <a className="page-link" href="/sales/returns">Trả hàng · DEV</a>
      <a className="page-link" href="/sales/cost-adjustments">Điều chỉnh giá vốn · DEV</a>
      <a className="page-link" href="/sales/gifts">Quà khuyến mại · DEV</a>
    </section>
    <section className="panel panel-content">
      <h2>Increment tiếp theo</h2>
      <p>APP-01A: Tenant/Customer/SKU/Trip, source/command ledger và isolation tests.</p>
    </section>
  </main>
}

function App() {
  const [updateAvailable, setUpdateAvailable] = useState(false)
  const updateWorker = useRef<ReturnType<typeof registerSW> | null>(null)

  useEffect(() => {
    updateWorker.current = registerSW({
      onNeedRefresh() { setUpdateAvailable(true) }
    })
    return () => { updateWorker.current = null }
  }, [])

  return <>
    {updateAvailable && <section className="update-prompt panel panel-content" role="status">
      <h2>Có phiên bản mới</h2>
      <p>Ứng dụng đã sẵn sàng cập nhật. Các thay đổi chưa lưu cần được hoàn tất trước khi tải lại.</p>
      <Button variant="primary" onClick={() => { void updateWorker.current?.(true) }}>Cập nhật ứng dụng</Button>
    </section>}
    {location.pathname === '/ui/foundations' ? <FoundationsDemo />
      : location.pathname === '/ui/data-entry' ? <DataEntryDemo />
        : location.pathname === '/ui/command-recovery' ? <SourceCommandDemo />
          : location.pathname === '/sales'
            ? <Suspense fallback={<p className="page" role="status">Đang tải Sales A…</p>}><SalesWorkspace /></Suspense>
          : location.pathname === '/sales/returns'
            ? <Suspense fallback={<p className="page" role="status">Đang tải trả hàng…</p>}><ReturnWorkspace /></Suspense>
          : location.pathname === '/sales/cost-adjustments'
            ? <Suspense fallback={<p className="page" role="status">Đang tải điều chỉnh giá vốn…</p>}><CostAdjustmentWorkspace /></Suspense>
          : location.pathname === '/sales/gifts'
            ? <Suspense fallback={<p className="page" role="status">Đang tải quà khuyến mại…</p>}><GiftWorkspace /></Suspense>
          : location.pathname === '/sales/partner-rehearsal'
            ? <Suspense fallback={<p className="page" role="status">Đang tải mô phỏng…</p>}><PartnerRehearsalWorkspace /></Suspense>
          : location.pathname === '/sales/mixed-trip'
            ? <Suspense fallback={<p className="page" role="status">Đang tải chuyến Mixed…</p>}><MixedTripWorkspace /></Suspense>
          : location.pathname === '/sales/b-sale'
            ? <Suspense fallback={<p className="page" role="status">Đang tải màn hình bán B…</p>}><BSaleWorkspace /></Suspense>
          : location.pathname === '/sales/b-stock'
            ? <Suspense fallback={<p className="page" role="status">Đang tải nghiệp vụ B…</p>}><BStockWorkspace /></Suspense>
          : location.pathname === '/sales/customers'
            ? <Suspense fallback={<p className="page" role="status">Đang tải màn hình khách hàng…</p>}><CustomerWorkspace /></Suspense>
            : location.pathname === '/sales/orders'
              ? <Suspense fallback={<p className="page" role="status">Đang tải màn hình đơn hàng…</p>}><OrderWorkspace /></Suspense>
              : location.pathname === '/sales/load'
                ? <Suspense fallback={<p className="page" role="status">Đang tải màn hình nạp hàng…</p>}><LoadWorkspace /></Suspense>
                : location.pathname === '/sales/delivery'
                  ? <Suspense fallback={<p className="page" role="status">Đang tải màn hình giao hàng…</p>}><DeliveryWorkspace /></Suspense>
                  : <HealthShell />}
  </>
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
