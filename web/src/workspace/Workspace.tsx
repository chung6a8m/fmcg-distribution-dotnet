import { lazy, Suspense, useEffect, useLayoutEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { readApiHealth } from '../health'
import { salesAuthorizationChanged, type Identity } from '../sales/customer-api'
import type { ReturnRole } from '../sales/ReturnWorkspace'
import './workspace.css'

const Customer = lazy(() => import('../sales/CustomerWorkspace').then(m => ({ default: m.CustomerWorkspace })))
const Order = lazy(() => import('../sales/OrderWorkspace').then(m => ({ default: m.OrderWorkspace })))
const Load = lazy(() => import('../sales/LoadWorkspace').then(m => ({ default: m.LoadWorkspace })))
const Delivery = lazy(() => import('../sales/DeliveryWorkspace').then(m => ({ default: m.DeliveryWorkspace })))
const Visit = lazy(() => import('../sales/VisitWorkspace').then(m => ({ default: m.VisitWorkspace })))
const Ar = lazy(() => import('../sales/ArReport').then(m => ({ default: m.ArReport })))
const Returns = lazy(() => import('../sales/ReturnWorkspace').then(m => ({ default: m.ReturnWorkspace })))

const api = '/api/synthetic/app03'
const tokenKey = 'app03.workspace.token.v1'
const lastIdentityKey = 'app03.workspace.identity.v1'
const guid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
type Access = Identity & {
  sales: boolean; warehouse: boolean; returnRole: ReturnRole | null; businessDate: string; expiresAt: number
}
type Session = { token: string; access: Access }
type Selection = 'customer' | 'order' | 'trip' | 'source'
const routes = [
  { id: 'customers', label: 'Khách hàng và địa chỉ giao', group: 'Bán hàng', show: (a: Access) => a.sales },
  { id: 'orders', label: 'Đơn A và giữ hàng', group: 'Bán hàng', show: (a: Access) => a.sales || a.warehouse },
  { id: 'ar', label: 'Báo cáo AR', group: 'Bán hàng', show: (a: Access) => a.sales },
  { id: 'load', label: 'Nạp hàng lên xe', group: 'Kho', show: (a: Access) => a.warehouse },
  { id: 'delivery', label: 'Giao hàng A', group: 'Hiện trường', show: (a: Access) => a.sales },
  { id: 'visits', label: 'Viếng thăm', group: 'Hiện trường', show: (a: Access) => a.sales },
  { id: 'returns', label: 'Trả hàng', group: '', show: (a: Access) => a.returnRole !== null },
] as const
const returnGroup = { sales: 'Bán hàng', warehouse: 'Kho', finance: 'Kế toán', case_checker: 'Kiểm tra chất lượng' }

function stored(key: string) {
  try { return sessionStorage.getItem(key) ?? '' } catch { return '' }
}

function store(key: string, value: string) {
  try {
    if (value) sessionStorage.setItem(key, value)
    else sessionStorage.removeItem(key)
  } catch { /* The current page remains usable. */ }
}

function identityKey(access: Access) {
  return JSON.stringify([access.sessionId, access.tenantId, access.actorId])
}

function valid(key: Selection, value: string) {
  return key === 'source' ? value.split(':').length <= 2 && value.split(':').every(part => guid.test(part)) : guid.test(value)
}

export function WorkspaceEntry({ fallback }: { fallback: (live: boolean) => ReactNode }) {
  const [profile, setProfile] = useState<'checking' | 'available' | 'foundation' | 'offline'>('checking')
  useEffect(() => {
    const request = new AbortController()
    void fetch('/health/live', { cache: 'no-store', signal: AbortSignal.any([request.signal, AbortSignal.timeout(5_000)]) })
      .then(readApiHealth)
      .then(health => {
        if (!request.signal.aborted)
          setProfile(health?.workspaceProfile === 'APP-03.1a-v1' ? 'available' : health ? 'foundation' : 'offline')
      })
      .catch(() => { if (!request.signal.aborted) setProfile('offline') })
    return () => request.abort()
  }, [])
  if (profile === 'checking') return <main className="page"><p role="status">Đang tải FMCG DMS…</p></main>
  if (profile === 'available') return <Workspace />
  return fallback(profile === 'foundation')
}

function Workspace() {
  const [token, setToken] = useState(() => stored(tokenKey))
  const [session, setSession] = useState<Session | null>(null)
  const [error, setError] = useState('')
  const [checking, setChecking] = useState(() => token !== '')
  const [attempt, setAttempt] = useState(0)
  const [offline, setOffline] = useState(() => !navigator.onLine)
  const activeToken = useRef(token)
  const login = useRef<HTMLInputElement>(null)
  const focusLogin = useRef(false)

  useEffect(() => {
    const original = document.title
    document.title = 'FMCG DMS — APP-03 DEV'
    return () => { document.title = original }
  }, [])

  useLayoutEffect(() => {
    if (!token && focusLogin.current) {
      focusLogin.current = false
      login.current?.focus()
    }
  }, [token])

  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  useEffect(() => {
    if (!token) return
    const lifetime = new AbortController()
    let inFlight = false
    let expiry: number | undefined
    const stale = () => lifetime.signal.aborted || activeToken.current !== token
    function reject(message: string) {
      if (stale()) return
      activeToken.current = ''
      store(tokenKey, '')
      setSession(null)
      setError(message)
      focusLogin.current = true
      setToken('')
    }
    async function validate() {
      if (inFlight || stale()) return
      inFlight = true
      try {
        const response = await fetch(api + '/session', {
          headers: { Authorization: 'Bearer ' + token, Accept: 'application/json' },
          cache: 'no-store', redirect: 'error',
          signal: AbortSignal.any([lifetime.signal, AbortSignal.timeout(10_000)]),
        })
        if (stale()) return
        if (response.status === 401) return reject('Mã truy cập không hợp lệ hoặc phiên DEV đã hết hạn.')
        if (response.status === 403) return reject('Danh tính này không còn quyền trong phiên DEV.')
        if (!response.ok) throw new Error('Session unavailable')
        const access = await response.json() as Access
        if (stale()) return
        if (!Number.isFinite(access.expiresAt)) throw new Error('Missing session expiry')
        if (access.expiresAt * 1000 <= Date.now()) return reject('Phiên DEV đã hết hạn.')
        const identity = identityKey(access)
        const previous = stored(lastIdentityKey)
        if (previous && previous !== identity) history.replaceState(null, '', location.pathname)
        store(lastIdentityKey, identity)
        store(tokenKey, token)
        setSession({ access, token })
        setError('')
        window.clearTimeout(expiry)
        expiry = window.setTimeout(() => reject('Phiên DEV đã hết hạn.'), access.expiresAt * 1000 - Date.now())
      } catch {
        if (!stale()) {
          setSession(null)
          setError('Chưa xác minh được phiên DEV. Kiểm tra kết nối rồi thử lại.')
        }
      } finally {
        inFlight = false
        if (!lifetime.signal.aborted) setChecking(false)
      }
    }
    const visible = () => { if (document.visibilityState === 'visible') void validate() }
    const denied = () => { void validate() }
    const hide = () => document.getElementById('workspace-data')?.setAttribute('hidden', '')
    const show = (event: PageTransitionEvent) => { if (event.persisted) location.reload() }
    void validate()
    const timer = window.setInterval(visible, 30_000)
    document.addEventListener('visibilitychange', visible)
    window.addEventListener('online', visible)
    window.addEventListener('focus', visible)
    window.addEventListener('pagehide', hide)
    window.addEventListener('pageshow', show)
    window.addEventListener(salesAuthorizationChanged, denied)
    return () => {
      lifetime.abort()
      window.clearInterval(timer)
      window.clearTimeout(expiry)
      document.removeEventListener('visibilitychange', visible)
      window.removeEventListener('online', visible)
      window.removeEventListener('focus', visible)
      window.removeEventListener('pagehide', hide)
      window.removeEventListener('pageshow', show)
      window.removeEventListener(salesAuthorizationChanged, denied)
    }
  }, [token, attempt])

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const input = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    if (!input || offline) return
    setError('')
    setChecking(true)
    activeToken.current = input
    setToken(input)
  }

  function changeIdentity() {
    activeToken.current = ''
    store(tokenKey, '')
    history.replaceState(null, '', location.pathname)
    setSession(null)
    setChecking(false)
    setError('')
    focusLogin.current = true
    setToken('')
  }

  const authorized = session?.token === token ? session : null
  return <main className="page c1-page app03-page">
    <header className="page-heading app03-header">
      <div>
        <h1>FMCG DMS · APP-03 DEV</h1>
        <p>Dữ liệu giả lập · A/F01/F03/F04/F05</p>
        {authorized && <p>{authorized.access.tenantId} · {authorized.access.actorId}</p>}
        {authorized?.access.businessDate && <p>Ngày nghiệp vụ <time dateTime={authorized.access.businessDate}>
          {authorized.access.businessDate.split('-').reverse().join('/')}
        </time></p>}
      </div>
      <div className="button-row">
        <ThemeSelect />
        {token && <Button onClick={changeIdentity}>Đổi danh tính</Button>}
      </div>
    </header>
    {offline && <p role="status">Đang ngoại tuyến. Kết nối lại để ghi nghiệp vụ.</p>}
    {checking && <p role="status">Đang xác minh phiên DEV…</p>}
    {error && <p role="alert">{error}</p>}
    {!token && <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required ref={login} />
      <Button type="submit" variant="primary" disabled={offline}>Kết nối</Button>
    </form>}
    {token && !authorized && !checking && <Button disabled={offline} onClick={() => {
      setChecking(true)
      setAttempt(n => n + 1)
    }}>Thử lại</Button>}
    {authorized && <WorkArea key={JSON.stringify([identityKey(authorized.access), authorized.access.sales,
      authorized.access.warehouse, authorized.access.returnRole])} {...authorized} />}
    <footer><a className="page-link" href="/dev">Công cụ DEV và UI Lab</a></footer>
  </main>
}

function WorkArea({ access, token }: Session) {
  const allowed = routes.filter(route => route.show(access))
  const current = allowed.find(route => '/work/' + route.id === location.pathname)
  const query = new URLSearchParams(location.search)
  const returnScope = access.returnRole ? '.' + access.returnRole : ''
  const contextPrefix = 'app03.workspace.selection.v1.' + identityKey(access) + '.'
  const contextKey = (key: Selection) => contextPrefix + key + (key === 'source' ? returnScope : '')
  function initial(key: Selection) {
    const value = query.get(key) || stored(contextKey(key))
    return valid(key, value) ? value : ''
  }

  const [customer, setCustomer] = useState(() => initial('customer'))
  const [order] = useState(() => initial('order'))
  const [trip, setTrip] = useState(() => initial('trip'))
  const [source] = useState(() => initial('source'))
  const area = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (location.pathname !== '/') area.current?.focus()
  }, [])

  function remember(key: Selection, value: string) {
    if (!area.current?.isConnected) return
    store(contextKey(key), value)
    const params = new URLSearchParams(location.search)
    if (value) params.set(key, value)
    else params.delete(key)
    if (key === 'source') params.delete('delivery')
    history.replaceState(null, '', location.pathname + (params.size ? '?' + params : ''))
  }
  function saveTrip(id: string) {
    setTrip(id)
    remember('trip', id)
  }
  function saveCustomer(id: string) {
    setCustomer(id)
    remember('customer', id)
  }
  function openAr(id: string) {
    store(contextKey('order'), id)
    location.assign('/work/ar?order=' + id)
  }

  const groupOf = (route: typeof routes[number]) => route.id === 'orders' && !access.sales ? 'Kho'
    : route.group || (access.returnRole ? returnGroup[access.returnRole] : '')
  const groups = [...new Set(allowed.map(groupOf))]
  const delivery = query.get('delivery') ?? ''
  const returnUrl = access.returnRole === 'sales' ? '/work/returns' : undefined
  return <div id="workspace-data" className="sales-layout">
    <nav className="panel panel-content sales-nav" aria-label="Công việc DEV">
      {groups.map(group => <div key={group} className="app03-nav-group">
        <strong>{group}</strong>
        {allowed.filter(route => groupOf(route) === group).map(route => <a
          key={route.id} className="button button-secondary" href={'/work/' + route.id
            + (trip && (route.id === 'delivery' || route.id === 'load') ? '?trip=' + trip
              : order && (route.id === 'orders' || route.id === 'ar') ? '?order=' + order : '')}
          aria-current={route.id === current?.id ? 'page' : undefined}>{route.label}</a>)}
      </div>)}
    </nav>
    <div className="sales-pane app03-area" ref={area} tabIndex={-1} role="region" aria-label={current?.label ?? 'Tổng quan'}>
      {!current && <h2>{location.pathname === '/' ? 'Công việc của bạn' : 'Không mở được màn hình'}</h2>}
      {!current && <p>{location.pathname === '/'
        ? 'Chọn công việc trong menu để tiếp tục.'
        : 'Màn hình này cần quyền phù hợp và profile DEV có hỗ trợ.'}</p>}
      <Suspense fallback={<p role="status">Đang tải màn hình…</p>}>
        {current?.id === 'customers' && <Customer workspaceToken={token} startCustomer={customer} onCustomerChange={saveCustomer} />}
        {current?.id === 'orders' && <Order workspaceToken={token} onOpenAr={openAr} selectedOrder={order}
          context={{ trip, customer, onCustomerChange: saveCustomer, onTripChange: saveTrip }} />}
        {current?.id === 'ar' && <Ar orderId={order} token={token} returnUrl={returnUrl} />}
        {current?.id === 'load' && <Load workspaceToken={token} startTrip={trip} onTripChange={saveTrip} />}
        {current?.id === 'delivery' && <Delivery workspaceToken={token} startTrip={trip} onTripChange={saveTrip} />}
        {current?.id === 'visits' && <Visit token={token} identity={access} startCustomer={customer} onCustomerChange={saveCustomer} />}
        {current?.id === 'returns' && access.returnRole && <Returns
          workspaceSession={{ token, identity: { ...access, role: access.returnRole } }}
          startSource={source} startDelivery={access.returnRole === 'sales' && guid.test(delivery) ? delivery : undefined}
          onSourceChange={id => remember('source', id)} />}
      </Suspense>
    </div>
  </div>
}
