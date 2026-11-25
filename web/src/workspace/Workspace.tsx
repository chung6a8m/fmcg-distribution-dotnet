import { lazy, Suspense, useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { readApiHealth } from '../health'
import { requestSalesApi, salesAuthorizationChanged, type Identity } from '../sales/customer-api'
import type { ReturnRole } from '../sales/ReturnWorkspace'
import './workspace.css'

const Customer = lazy(() => import('../sales/CustomerWorkspace').then(m => ({ default: m.CustomerWorkspace })))
const Order = lazy(() => import('../sales/OrderWorkspace').then(m => ({ default: m.OrderWorkspace })))
const Load = lazy(() => import('../sales/LoadWorkspace').then(m => ({ default: m.LoadWorkspace })))
const Delivery = lazy(() => import('../sales/DeliveryWorkspace').then(m => ({ default: m.DeliveryWorkspace })))
const Visit = lazy(() => import('../sales/VisitWorkspace').then(m => ({ default: m.VisitWorkspace })))
const BStock = lazy(() => import('../sales/BStockWorkspace').then(m => ({ default: m.BStockWorkspace })))
const BSale = lazy(() => import('../sales/BSaleWorkspace').then(m => ({ default: m.BSaleWorkspace })))
const Ar = lazy(() => import('../sales/ArReport').then(m => ({ default: m.ScopedArReport })))
const Returns = lazy(() => import('../sales/ReturnWorkspace').then(m => ({ default: m.ReturnWorkspace })))

const TripClose = lazy(() => import('../sales/TripCloseWorkspace').then(m => ({ default: m.TripCloseWorkspace })))
const Settlement = lazy(() => import('../sales/MixedTripWorkspace').then(m => ({ default: m.MixedTripWorkspace })))
const Advances = lazy(() => import('../sales/AdvanceWorkspace').then(m => ({ default: m.AdvancePanel })))
const Gifts = lazy(() => import('../sales/GiftWorkspace').then(m => ({ default: m.GiftPanel })))

const Adjustment = lazy(() => import('../sales/CostAdjustmentWorkspace').then(m => ({ default: m.AdjustmentPanel })))
const Refunds = lazy(() => import('../sales/RefundWorkspace').then(m => ({ default: m.RefundPanel })))
const Accounting = lazy(() => import('./FinanceQueue').then(m => ({ default: m.FinanceQueue })))
const Owner = lazy(() => import('./OwnerOverview').then(m => ({ default: m.OwnerOverview })))

type HandoffRole = 'sales' | 'warehouse' | 'cashier'
type AdvanceRole = 'sales' | 'finance' | 'cashier'
type GiftRole = 'sales' | 'warehouse' | 'finance'

const api = '/api/synthetic/app03'
const tokenKey = 'app03.workspace.token.v1'
const lastIdentityKey = 'app03.workspace.identity.v1'
const guid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
type Access = Identity & {
  handoffRoles?: HandoffRole[]; advanceRole?: AdvanceRole | null; giftRole?: GiftRole | null
  adjustmentRole?: 'finance' | 'case_checker' | null
  refundRole?: 'finance' | 'cashier' | 'case_checker' | null
  accountingRoles?: string[]
  owner?: boolean
  tripClose?: boolean
  sales: boolean; warehouse: boolean; fieldB?: boolean; returnRole: ReturnRole | null; businessDate: string; expiresAt: number
}
type Session = { token: string; access: Access }
type Selection = 'customer' | 'order' | 'trip' | 'source'
const routes = [
  { id: 'customers', label: 'Khách hàng và địa chỉ giao', group: 'Bán hàng', show: (a: Access) => a.sales },
  { id: 'orders', label: 'Đơn A và giữ hàng', group: 'Bán hàng', show: (a: Access) => a.sales || a.warehouse },
  { id: 'ar', label: 'Báo cáo AR', group: 'Bán hàng', show: (a: Access) => a.sales },
  { id: 'load', label: 'Nạp hàng lên xe', group: 'Kho', show: (a: Access) => a.warehouse },
  { id: 'b-stock', label: 'Quota và nạp hàng B', group: 'Kho', show: (a: Access) => !!a.fieldB && a.warehouse },
  { id: 'field', label: 'Chuyến và công việc', group: 'Hiện trường', show: (a: Access) => !!a.fieldB && a.sales },
  { id: 'delivery', label: 'Giao hàng A', group: 'Hiện trường', show: (a: Access) => a.sales },
  { id: 'b-sale', label: 'Bán và giao hàng B', group: 'Hiện trường', show: (a: Access) => !!a.fieldB && a.sales },
  { id: 'visits', label: 'Viếng thăm', group: 'Hiện trường', show: (a: Access) => a.sales },
  { id: 'trip-close', label: 'Chuẩn bị đóng chuyến', group: 'Vận hành', show: (a: Access) => a.tripClose === true },
  { id: 'settlement', label: 'Đối soát chuyến và nộp tiền', group: '', show: (a: Access) => !!a.handoffRoles?.length },
  { id: 'advances', label: 'Tạm ứng và hoàn ứng', group: '', show: (a: Access) => !!a.advanceRole },
  { id: 'gifts', label: 'Quà khuyến mại', group: '', show: (a: Access) => !!a.giftRole },
  { id: 'finance', label: 'Hàng đợi kế toán', group: 'Kế toán', show: (a: Access) => !!a.accountingRoles?.length },
  { id: 'cost-adjustments', label: 'Điều chỉnh giá vốn', group: '', show: (a: Access) => !!a.adjustmentRole },
  { id: 'refunds', label: 'Credit và hoàn tiền', group: '', show: (a: Access) => !!a.refundRole },
  { id: 'returns', label: 'Trả hàng', group: '', show: (a: Access) => a.returnRole !== null },
  { id: 'owner', label: 'Tổng quan Chủ NPP', group: 'Chủ NPP', show: (a: Access) => a.owner === true },
] as const
const handoffGroup = { sales: 'Hiện trường', warehouse: 'Kho', finance: 'Kế toán', cashier: 'Thủ quỹ' }
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

function bindHistoryContext(access: Access) {
  const identity = identityKey(access)
  const scope = location.pathname === '/work/returns' ? identity + '.' + access.returnRole : identity
  const entryScope = history.state?.app03Context
  const previous = stored(lastIdentityKey)
  if (entryScope !== scope) {
    const discard = (entryScope && entryScope !== scope) || (previous && previous !== identity)
    history.replaceState({ ...history.state, app03Context: scope }, '', discard ? location.pathname : location.href)
  }
  store(lastIdentityKey, identity)
}

function valid(key: Selection, value: string) {
  return key === 'source' ? value.split(':').length <= 2 && value.split(':').every(part => guid.test(part)) : guid.test(value)
}

export function WorkspaceEntry({ fallback, fieldRoute, settlementRoute }: {
  fallback: (live: boolean) => ReactNode; fieldRoute?: string; settlementRoute?: string
}) {
  const [profile, setProfile] = useState<'checking' | 'available' | 'foundation' | 'offline'>('checking')
  useEffect(() => {
    const request = new AbortController()
    void fetch('/health/live', { cache: 'no-store', signal: AbortSignal.any([request.signal, AbortSignal.timeout(5_000)]) })
      .then(readApiHealth)
      .then(health => {
        if (request.signal.aborted) return
        const settlement = health?.workspaceProfile === 'APP-03.6-v1' || health?.workspaceProfile === 'APP-03.7-v1'
          || health?.workspaceProfile === 'APP-05.1-v1'
        const field = health?.workspaceProfile === 'APP-03.5-v1' || settlement
        if (settlement && settlementRoute) {
          location.replace(settlementRoute)
          return
        }
        if (fieldRoute && field) {
          location.replace(fieldRoute)
          return
        }
        setProfile(!fieldRoute && !settlementRoute && (health?.workspaceProfile === 'APP-03.1a-v1' || field)
          ? 'available' : health ? 'foundation' : 'offline')
      })
      .catch(() => { if (!request.signal.aborted) setProfile('offline') })
    return () => request.abort()
  }, [fieldRoute, settlementRoute])
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
      if (inFlight || stale() || !navigator.onLine) return
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
        bindHistoryContext(access)
        store(tokenKey, token)
        setSession({ access, token })
        setError('')
        window.clearTimeout(expiry)
        expiry = window.setTimeout(() => reject('Phiên DEV đã hết hạn.'), access.expiresAt * 1000 - Date.now())
      } catch {
        if (!stale() && navigator.onLine) {
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
    history.replaceState(history.state, '', location.pathname)
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
        <p>Dữ liệu giả lập{authorized?.access.fieldB ? ' · A/B/Mixed · VND' : ' · A/F01/F03/F04/F05'}</p>
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
      authorized.access.warehouse, authorized.access.returnRole, authorized.access.fieldB,
      authorized.access.handoffRoles, authorized.access.advanceRole, authorized.access.giftRole,
      authorized.access.accountingRoles, authorized.access.adjustmentRole, authorized.access.refundRole, authorized.access.owner,
      authorized.access.tripClose])} {...authorized} offline={offline} />}
    <footer><a className="page-link" href="/dev">Công cụ DEV và UI Lab</a></footer>
  </main>
}

function WorkArea({ access, token, offline }: Session & { offline: boolean }) {
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

  const handoffRoles = JSON.stringify(access.handoffRoles ?? [])
  const { sessionId, actorId, tenantId } = access
  const settlementSession = useMemo(() => ({ token, identity: {
    sessionId, actorId, tenantId, roles: JSON.parse(handoffRoles) as HandoffRole[],
  } }), [token, sessionId, actorId, tenantId, handoffRoles])

  const [customer, setCustomer] = useState(() => initial('customer'))
  const [order, setOrder] = useState(() => initial('order'))
  const [trip, setTrip] = useState(() => initial('trip'))
  const [source] = useState(() => initial('source'))
  const area = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (location.pathname !== '/') area.current?.focus()
  }, [])

  function remember(key: Selection, value: string, persist = true) {
    if (!area.current?.isConnected) return
    if (persist) store(contextKey(key), value)
    const params = new URLSearchParams(location.search)
    if (value) params.set(key, value)
    else params.delete(key)
    if (key === 'source') params.delete('delivery')
    history.replaceState(history.state, '', location.pathname + (params.size ? '?' + params : ''))
  }
  function saveTrip(id: string) {
    setTrip(id)
    remember('trip', id)
  }
  function saveLoadTrip(id: string) {
    if (id) saveTrip(id)
    else {
      const previous = stored(contextKey('trip'))
      setTrip(guid.test(previous) ? previous : '')
      remember('trip', '', false)
    }
  }
  function saveCustomer(id: string) {
    setCustomer(id)
    remember('customer', id)
  }
  function openAr(id: string, tripId?: string) {
    store(contextKey('order'), id)
    if (tripId) store(contextKey('trip'), tripId)
    location.assign('/work/ar?order=' + id + (tripId ? '&trip=' + tripId : ''))
  }
  function saveArOrder(id: string) {
    setOrder(id)
    remember('order', id)
  }

  const groupOf = (route: typeof routes[number]) => {
    if (route.id === 'cost-adjustments') return access.adjustmentRole === 'finance' ? 'Kế toán' : returnGroup.case_checker
    if (route.id === 'refunds') return access.refundRole === 'cashier' ? 'Thủ quỹ'
      : access.refundRole === 'finance' ? 'Kế toán' : returnGroup.case_checker
    if (route.id === 'orders' && !access.sales) return 'Kho'
    if (route.id === 'settlement') {
      const roles = access.handoffRoles ?? []
      return handoffGroup[roles.includes('cashier') ? 'cashier' : roles.includes('sales') ? 'sales' : 'warehouse']
    }
    if (route.id === 'advances' && access.advanceRole) return handoffGroup[access.advanceRole]
    if (route.id === 'gifts' && access.giftRole) return handoffGroup[access.giftRole]
    return route.group || (access.returnRole ? returnGroup[access.returnRole] : '')
  }
  const groups = [...new Set(allowed.map(groupOf))]
  const delivery = query.get('delivery') ?? ''
  const requestedOrder = query.get('order') ?? ''
  const caseId = query.get('case') ?? ''
  const linkedSource = query.get('source') ?? ''
  const linkedId = guid.test(linkedSource) ? linkedSource : undefined
  const requestedSource = query.get('handoffSource') ?? ''
  const requestedTrace = query.get('handoffTrace') ?? ''
  const requestedTrip = query.get('trip') ?? ''
  const loadOrder = guid.test(requestedOrder) ? requestedOrder : ''
  const returnUrl = access.returnRole === 'sales' ? '/work/returns' : undefined
  const field = current ? groupOf(current) === 'Hiện trường' : false
  return <div id="workspace-data" className="sales-layout">
    <nav className="panel panel-content sales-nav" aria-label="Công việc DEV">
      {groups.map(group => <div key={group} className="app03-nav-group">
        <strong>{group}</strong>
        {allowed.filter(route => groupOf(route) === group).map(route => <a
          key={route.id} className="button button-secondary" href={'/work/' + route.id
            + (trip && ['delivery', 'load', 'b-sale', 'b-stock', 'settlement', 'trip-close'].includes(route.id) ? '?trip=' + trip : '')}
          aria-current={route.id === current?.id ? 'page' : undefined}>{route.label}</a>)}
      </div>)}
    </nav>
    <div className={'sales-pane app03-area' + (field ? ' app03-field' : '')} ref={area} tabIndex={-1}
      role="region" aria-label={current?.label ?? 'Tổng quan'}>
      {!current && <h2>{location.pathname === '/' ? 'Công việc của bạn' : 'Không mở được màn hình'}</h2>}
      {!current && <p>{location.pathname === '/'
        ? 'Chọn công việc trong menu để tiếp tục.'
        : 'Màn hình này cần quyền phù hợp và profile DEV có hỗ trợ.'}</p>}
      <Suspense fallback={<p role="status">Đang tải màn hình…</p>}>
        {current?.id === 'owner' && (offline ? <p role="status">Kết nối lại để đọc số liệu Chủ NPP.</p> : <Owner key={token} token={token} />)}
        {current?.id === 'trip-close' && <TripClose token={token} identity={access} offline={offline}
          tripId={trip} onTripChange={saveTrip} access={{
            orders: access.sales || access.warehouse, settlement: !!access.handoffRoles?.length,
            finance: !!access.accountingRoles?.length, returns: access.returnRole !== null,
            costs: !!access.adjustmentRole, refunds: !!access.refundRole,
          }} />}
        {current?.id === 'finance' && <Accounting token={token} identity={access}
          roles={access.accountingRoles ?? []} offline={offline}
          startSource={linkedId} startDelivery={guid.test(delivery) ? delivery : undefined}
          startCase={guid.test(caseId) ? caseId : undefined} />}
        {current?.id === 'cost-adjustments' && access.adjustmentRole && <Adjustment offline={offline} linkedProposal={linkedId}
          session={{ token, identity: { ...access, role: access.adjustmentRole } }} />}
        {current?.id === 'refunds' && access.refundRole && <Refunds offline={offline} integrated canOpenReturn={access.returnRole != null} linkedRefund={linkedId}
          session={{ token, identity: { ...access, role: access.refundRole } }} />}
        {current?.id === 'customers' && <Customer workspaceToken={token} startCustomer={customer} onCustomerChange={saveCustomer} />}
        {current?.id === 'orders' && <Order workspaceToken={token} onOpenAr={openAr}
          selectedOrder={requestedOrder} selectedSource={guid.test(requestedSource) ? requestedSource : ''}
          selectedTrace={requestedTrace} recovering={query.get('recover') === '1'}
          onSelectOrder={(id, tripId) => { if (tripId) saveTrip(tripId); saveArOrder(id) }}
          context={{ trip, customer, onCustomerChange: saveCustomer, onTripChange: saveTrip }} />}
        {current?.id === 'ar' && <Ar orderId={order} token={token} returnUrl={returnUrl} onCheckOrder={saveArOrder} />}
        {current?.id === 'load' && <Load workspaceToken={token} onTripChange={saveLoadTrip}
          startTrip={loadOrder ? (guid.test(requestedTrip) ? requestedTrip : '') : trip} startOrder={loadOrder}
          onOrderChange={id => remember('order', id)} />}
        {current?.id === 'delivery' && <Delivery workspaceToken={token} startTrip={trip} onTripChange={saveTrip} />}
        {current?.id === 'field' && <FieldTrips token={token} settlement={!!access.handoffRoles?.length} />}
        {current?.id === 'b-stock' && <BStock workspaceToken={token} startTrip={trip} onTripChange={saveTrip} />}
        {current?.id === 'b-sale' && <BSale workspaceToken={token} startTrip={trip} requestedTrip={query.get('trip')}
          onTripChange={saveTrip} />}
        {current?.id === 'visits' && <Visit token={token} identity={access} startCustomer={customer} onCustomerChange={saveCustomer} />}
        {current?.id === 'settlement' && access.handoffRoles && <>
          {access.tripClose && trip && <a className="button button-secondary" href={'/work/trip-close?trip=' + trip}>
            Chuẩn bị đóng chuyến
          </a>}
          <Settlement workspaceSession={settlementSession} startTrip={trip} onTripChange={saveTrip} />
        </>}
        {current?.id === 'advances' && access.advanceRole && <><h2>Tạm ứng và hoàn ứng</h2>
          <Advances session={{ token, identity: { ...access, role: access.advanceRole } }} /></>}
        {current?.id === 'gifts' && access.giftRole && <><h2>Quà khuyến mại</h2>
          <Gifts session={{ token, identity: { ...access, role: access.giftRole } }} offline={offline} /></>}
        {current?.id === 'returns' && access.returnRole && <Returns
          workspaceSession={{ token, identity: { ...access, role: access.returnRole } }}
          startSource={source} linkedCase={linkedId} startDelivery={access.returnRole === 'sales' && guid.test(delivery) ? delivery : undefined}
          onSourceChange={id => remember('source', id)} />}
      </Suspense>
    </div>
  </div>
}

function FieldTrips({ token, settlement }: { token: string; settlement: boolean }) {
  const [trips, setTrips] = useState<{ id: string; code: string; b?: { canSell: boolean } }[] | null>(null)
  const [error, setError] = useState(false)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let current = true
    void Promise.all([
      requestSalesApi('/api/synthetic/app02c4', token, '/trips'),
      requestSalesApi('/api/synthetic/app02d2', token, '/trips'),
    ]).then(async ([a, b]) => {
      if (!a.ok || !b.ok) throw new Error('Trip list unavailable')
      const [aRows, bRows] = await Promise.all([
        a.json() as Promise<{ id: string; code: string }[]>,
        b.json() as Promise<{ tripId: string; canSell: boolean }[]>,
      ])
      const bTrips = new Map(bRows.map(trip => [trip.tripId, trip]))
      if (current) setTrips(aRows.map(trip => ({ ...trip, b: bTrips.get(trip.id) })))
    }).catch(() => { if (current) setError(true) })
    return () => { current = false }
  }, [token, attempt])
  return <section className="panel panel-content">
    <h2>Chuyến và công việc</h2>
    {error ? <><p role="alert">Chưa tải được chuyến được phân công.</p>
      <Button onClick={() => { setError(false); setAttempt(n => n + 1) }}>Tải lại danh sách</Button></>
      : trips === null ? <p role="status">Đang tải chuyến…</p>
        : trips.length === 0 ? <p>Chưa có chuyến được phân công.</p>
          : <ul className="c1-rows">{trips.map(trip => <li key={trip.id}>
            <div><h3>{trip.code}</h3>
              <div className="button-row">
                <a className="button button-secondary" href={'/work/delivery?trip=' + trip.id}>Giao A</a>
                {trip.b && <a className="button button-secondary" href={'/work/b-sale?trip=' + trip.id}>
                  {trip.b.canSell ? 'Bán B' : 'Lịch sử B'}
                </a>}
                {settlement && <a className="button button-secondary" href={'/work/settlement?trip=' + trip.id}>Đối soát chuyến</a>}
              </div>
            </div>
          </li>)}</ul>}
    <a className="button button-secondary" href="/work/visits">Viếng thăm khách hàng</a>
  </section>
}
