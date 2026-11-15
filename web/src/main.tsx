import { StrictMode, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import { isLiveApiResponse } from './health'
import './style.css'

type Health = 'Đang kiểm tra API…' | 'API hoạt động' | 'Không kết nối API'

function App() {
  const [status, setStatus] = useState<Health>('Đang kiểm tra API…')
  const [updateAvailable, setUpdateAvailable] = useState(false)
  const updateWorker = useRef<ReturnType<typeof registerSW> | null>(null)

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

  useEffect(() => {
    updateWorker.current = registerSW({
      onNeedRefresh() { setUpdateAvailable(true) }
    })
    return () => { updateWorker.current = null }
  }, [])

  return <main>
    <h1>FMCG Distribution · APP-00A</h1>
    <p className="muted">Application foundation — Synthetic / Nonproduction</p>
    <section className="panel">
      <h2>Trạng thái kết nối</h2>
      <p role="status">{status}</p>
      <p><strong>Online-first.</strong> Chưa có business write hoặc offline command queue.</p>
    </section>
    {updateAvailable && <section className="panel" role="status">
      <h2>Có phiên bản mới</h2>
      <p>Ứng dụng đã sẵn sàng cập nhật. Các thay đổi chưa lưu cần được hoàn tất trước khi tải lại.</p>
      <button type="button" onClick={() => { void updateWorker.current?.(true) }}>
        Cập nhật ứng dụng
      </button>
    </section>}
    <section className="panel">
      <h2>Increment tiếp theo</h2>
      <p>APP-01A: Tenant/Customer/SKU/Trip, source/command ledger và isolation tests.</p>
    </section>
  </main>
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
