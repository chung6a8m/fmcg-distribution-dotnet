import { StrictMode, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import { isLiveApiResponse } from './health'
import { Button, ThemeSelect } from './ui/controls'
import { FoundationsDemo } from './ui/FoundationsDemo'
import { DataEntryDemo } from './ui/DataEntryDemo'
import './style.css'

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
      <p><strong>Online-first.</strong> Chưa có business write hoặc offline command queue.</p>
    </section>
    <section className="panel panel-content">
      <h2>Nền tảng giao diện</h2>
      <ThemeSelect />
      <a className="page-link" href="/ui/foundations">Nền tảng giao diện</a>
      <a className="page-link" href="/ui/data-entry">UI-02 · Form & Grid Lab</a>
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
      : location.pathname === '/ui/data-entry' ? <DataEntryDemo /> : <HealthShell />}
  </>
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
