import { useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { ArReport } from './ArReport'
import { CustomerWorkspace } from './CustomerWorkspace'
import { DeliveryWorkspace } from './DeliveryWorkspace'
import { LoadWorkspace } from './LoadWorkspace'
import { OrderWorkspace } from './OrderWorkspace'
import { HttpError, readError, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

type Session = { token: string; identity: Identity & { sales: boolean; warehouse: boolean } }
const sections = [
  { id: 'customers', label: 'Khách hàng và địa chỉ giao', authority: 'sales' },
  { id: 'orders', label: 'Đơn A và giữ hàng', authority: null },
  { id: 'load', label: 'Nạp hàng', authority: 'warehouse' },
  { id: 'delivery', label: 'Giao hàng', authority: 'sales' },
  { id: 'ar', label: 'Báo cáo AR', authority: 'sales' },
] as const
type Section = typeof sections[number]['id']

export function SalesWorkspace() {
  const [session, setSession] = useState<Session | null>(null)
  const [section, setSection] = useState<Section>('customers')
  const [orderFilter, setOrderFilter] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const connecting = useRef(false)

  async function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (connecting.current) return
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    connecting.current = true
    setBusy(true)
    setError('')
    try {
      const response = await requestSalesApi('/api/synthetic/app02c2', token, '/session')
      if (!response.ok) throw new HttpError(response.status)
      const identity = await response.json() as Session['identity']
      setSection(identity.sales ? 'customers' : 'orders')
      setOrderFilter('')
      setSession({ token, identity })
    } catch (error) { setError(readError(error)) }
    finally { connecting.current = false; setBusy(false) }
  }

  return <main className="page c1-page sales-workspace">
    <header className="page-heading c1-header">
      <div><h1>Sales A</h1><p>DEV tổng hợp{session && ' · ' + session.identity.tenantId}</p></div>
      <div className="button-row"><ThemeSelect />
        {session && <Button onClick={() => setSession(null)}>Đổi phiên DEV</Button>}
      </div>
    </header>
    {error && <p role="alert">{error}</p>}
    {!session ? <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={busy}>Kết nối</Button>
    </form> : <div className="sales-layout">
      <nav aria-label="Sales A" className="panel panel-content sales-nav">
        {sections.map(item => <Button key={item.id} disabled={item.authority !== null && !session.identity[item.authority]}
          aria-current={section === item.id ? 'page' : undefined}
          onClick={() => { setOrderFilter(''); setSection(item.id) }}>{item.label}</Button>)}
      </nav>
      {section === 'customers' && <CustomerWorkspace workspaceToken={session.token} />}
      {section === 'orders' && <OrderWorkspace workspaceToken={session.token}
        onOpenAr={orderId => { setOrderFilter(orderId); setSection('ar') }} />}
      {section === 'load' && <LoadWorkspace workspaceToken={session.token} />}
      {section === 'delivery' && <DeliveryWorkspace workspaceToken={session.token} />}
      {section === 'ar' && <ArReport key={orderFilter} token={session.token} orderId={orderFilter} />}
    </div>}
  </main>
}
