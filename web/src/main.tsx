import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './style.css'

type Health = 'Đang kiểm tra API…' | 'API hoạt động' | 'Không kết nối API'

function App() {
  const [status, setStatus] = useState<Health>('Đang kiểm tra API…')
  useEffect(() => {
    const controller = new AbortController()
    fetch('/health/live', { signal: controller.signal, cache: 'no-store' })
      .then(r => setStatus(r.ok ? 'API hoạt động' : 'Không kết nối API'))
      .catch(e => { if ((e as Error).name !== 'AbortError') setStatus('Không kết nối API') })
    return () => controller.abort()
  }, [])

  return <main>
    <h1>FMCG Distribution · APP-00A</h1>
    <p className="muted">Application foundation — Synthetic / Nonproduction</p>
    <section className="panel">
      <h2>Trạng thái kết nối</h2>
      <p role="status">{status}</p>
      <p><strong>Online-first.</strong> Chưa có business write hoặc offline command queue.</p>
    </section>
    <section className="panel">
      <h2>Increment tiếp theo</h2>
      <p>APP-01A: Tenant/Customer/SKU/Trip, source/command ledger và isolation tests.</p>
    </section>
  </main>
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
