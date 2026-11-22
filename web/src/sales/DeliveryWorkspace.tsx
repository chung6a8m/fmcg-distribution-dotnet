import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Button, Field, ThemeSelect } from '../ui/controls'
import { HttpError, readError, requestSalesApi, type Identity } from './customer-api'
import './customers.css'

const api = '/api/synthetic/app02c4'
type Trip = { id: string; code: string }
type Line = { id: string; sku: string; customer: string; shipTo: string; shipToVersion: number; baseUom: string;
  loaded: string; delivered: string; available: string; held: string }
type Ar = { sourceId: string; customer: string; shipTo: string; businessDate: string; dueBusinessDate: string;
  quantity: string; amount: string; outstanding: string; cost: string }
type DeliveryCase = { commandId: string; manifestLineId: string; shipToVersion: number; quantity: string;
  reasonCode: string | null; canRetry: boolean }
type Pending = { commandId: string; tripId: string; manifestLineId: string; shipToVersion: number; quantity: string }
type Outcome = { status: string; reasonCode: string | null; sourceId: string | null }
const labels: Record<string, string> = {
  C4_QUANTITY_NOT_AVAILABLE: 'Số lượng vượt phần còn được giao.',
  C4_STALE_SHIPTO: 'Phiên bản địa chỉ đã đổi. Kiểm tra lại hồ sơ giao hàng.',
  C4_LOADED_LINE_NOT_DELIVERABLE: 'Dòng nạp không còn đủ điều kiện giao.',
  C4_FROZEN_POLICY_INVALID: 'Không xác minh được điều khoản đã đóng băng khi Confirm.',
  C4_EXISTING_DELIVERY_PENDING: 'Dòng này đã có lần giao chờ ghi sổ. Hãy dùng lại yêu cầu trong mục đối soát.',
}

function displayQuantity(value: string) {
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value
}

function storage(identity: Identity, pending?: Pending | null): Pending | null {
  const key = 'app02c4.delivery.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
  try {
    if (pending === undefined) {
      const raw = sessionStorage.getItem(key)
      return raw ? JSON.parse(raw) as Pending : null
    }
    if (pending) sessionStorage.setItem(key, JSON.stringify(pending))
    else sessionStorage.removeItem(key)
  } catch { /* The original command also remains in the open screen. */ }
  return null
}

async function get<T>(token: string, path: string, service = api): Promise<T> {
  const response = await requestSalesApi(service, token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

export function DeliveryWorkspace() {
  const [session, setSession] = useState<{ token: string; identity: Identity } | null>(null)
  const [trips, setTrips] = useState<Trip[]>([])
  const [trip, setTrip] = useState('')
  const [data, setData] = useState<{ lines: Line[]; ar: Ar[]; cases: DeliveryCase[] } | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [offline, setOffline] = useState(!navigator.onLine)
  const active = useRef(false)

  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function work(run: () => Promise<void>) {
    if (active.current || !navigator.onLine) return
    active.current = true
    setBusy(true)
    setNotice('')
    try { await run() } catch (error) { setNotice(readError(error)) }
    finally { active.current = false; setBusy(false) }
  }

  async function refresh(token: string, tripId: string) {
    setTrip(tripId)
    setData(null)
    if (!tripId) return
    const path = '/trips/' + tripId
    const [lines, ar, cases] = await Promise.all([
      get<Line[]>(token, path + '/lines'), get<Ar[]>(token, path + '/ar'), get<DeliveryCase[]>(token, path + '/cases'),
    ])
    setData({ lines, ar, cases })
  }

  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    event.currentTarget.reset()
    void work(async () => {
      const [identity, available] = await Promise.all([
        get<Identity>(token, '/session', '/api/synthetic/app02c2'), get<Trip[]>(token, '/trips'),
      ])
      const saved = storage(identity)
      setSession({ token, identity })
      setTrips(available)
      setPending(saved)
      await refresh(token, available.find(t => t.id === saved?.tripId)?.id ?? available[0]?.id ?? '')
    })
  }

  function resolve(command: Pending, lookup: boolean) {
    if (!session) return
    const { token, identity } = session
    void work(async () => {
      if (!lookup) {
        storage(identity, command)
        setPending(command)
      }
      let message: string
      try {
        const response = lookup
          ? await requestSalesApi(api, token, `/trips/${command.tripId}/commands/${command.commandId}`)
          : await requestSalesApi(api, token, '/deliver', JSON.stringify(command))
        if (lookup && response.status === 404) {
          await refresh(token, trip)
          setNotice('Chưa có kết quả ghi sổ. Có thể gửi lại đúng yêu cầu giao này.')
          return
        }
        if (![200, 400, 409, 422].includes(response.status)) throw new HttpError(response.status)
        const result = await response.json() as Outcome
        if (result.status === 'CONFLICT') {
          setNotice('Mã yêu cầu đã dùng cho nội dung khác. Hãy tra cứu kết quả.')
          return
        }
        if (result.status === 'APPLIED' && result.sourceId) message = 'Đã ghi giao hàng và công nợ.'
        else if (result.status === 'REJECTED') message = labels[result.reasonCode ?? ''] ?? 'Yêu cầu giao bị từ chối.'
        else throw new Error('Missing command outcome')
      } catch {
        setNotice('Chưa rõ kết quả ghi sổ. Hãy tra cứu trước khi ghi lần giao mới.')
        return
      }
      storage(identity, null)
      setPending(null)
      try { await refresh(token, trip) }
      catch { message += ' Hãy tải lại dữ liệu giao hàng.' }
      setNotice(message)
    })
  }

  const disabled = busy || offline || !!pending
  return <main className="page c1-page">
    <header className="page-heading c1-header">
      <div>
        <h1>Giao hàng và công nợ A</h1>
        <p>APP-02C.4 · DEV</p>
        <a className="page-link" href="/sales/load">Nạp hàng lên xe</a>
      </div>
      <ThemeSelect />
    </header>
    {!session ? <form className="panel panel-content c1-sign-in" onSubmit={connect}>
      <Field name="token" label="Mã truy cập DEV" type="password" autoComplete="off" required />
      <Button type="submit" variant="primary" disabled={disabled}>Kết nối</Button>
    </form> : <>
      <div className="button-row">
        <Button disabled={busy} onClick={() => { setSession(null); setData(null); setPending(null); setNotice('') }}>Đổi phiên DEV</Button>
        <Button disabled={disabled || !trip} onClick={() => { void work(() => refresh(session.token, trip)) }}>Tải lại chuyến</Button>
      </div>
      {trips.length > 0 ? <label className="field">Chuyến giao
        <select value={trip} disabled={disabled} onChange={event => { const id = event.target.value; void work(() => refresh(session.token, id)) }}>
          {trips.map(t => <option key={t.id} value={t.id}>{t.code}</option>)}
        </select>
      </label> : <p>Chưa có chuyến giao được phân công.</p>}
      {pending && <div className="button-row">
        <Button disabled={busy || offline} onClick={() => resolve(pending, true)}>Tra cứu kết quả giao</Button>
        <Button disabled={busy || offline} onClick={() => resolve(pending, false)}>Gửi lại cùng yêu cầu</Button>
      </div>}
      {data && <>
        <section className="panel panel-content" aria-label="Dòng đã nạp">
          <h2>Dòng đã nạp</h2>
          {data.lines.length === 0 && <p>Chưa có hàng đã nạp.</p>}
          {data.lines.map((line, index) => <form className="panel panel-content" key={line.id} onSubmit={event => {
            event.preventDefault()
            if (disabled) return
            const quantity = String(new FormData(event.currentTarget).get('quantity') ?? '').trim()
            if (!/^\d{1,15}(\.\d{1,3})?$/.test(quantity) || Number(quantity) <= 0) {
              setNotice('Nhập số lượng giao dương với tối đa ba chữ số thập phân.')
              return
            }
            resolve({ commandId: crypto.randomUUID(), tripId: trip, manifestLineId: line.id,
              shipToVersion: line.shipToVersion, quantity }, false)
          }}>
            <h3>{line.sku} · {line.customer}</h3>
            <p>ShipTo v{line.shipToVersion} · {line.shipTo}</p>
            <p>Đã nạp {displayQuantity(line.loaded)} {line.baseUom} · Đã giao {displayQuantity(line.delivered)} {line.baseUom} · Còn giữ {displayQuantity(line.held)} {line.baseUom}</p>
            {displayQuantity(line.available) !== '0' && !data.cases.some(item => item.manifestLineId === line.id && !item.reasonCode) && <>
              <Field name="quantity" label={`Số lượng đã giao dòng ${index + 1}`} type="number"
                min="0.001" max={line.available} step="0.001" required disabled={disabled} />
              <Button type="submit" variant="primary" disabled={disabled}>Xác nhận đã giao</Button>
            </>}
          </form>)}
        </section>
        <section className="panel panel-content" aria-label="Công nợ theo lần giao">
          <h2>Công nợ theo lần giao</h2>
          {data.ar.length === 0 && <p>Chưa phát sinh công nợ.</p>}
          {data.ar.map(item => <article className="panel panel-content" key={item.sourceId}>
            <h3>{item.customer} · {displayQuantity(item.amount)}</h3>
            <p>{item.shipTo}</p>
            <p>Ngày giao {item.businessDate.slice(0, 10)} · Hạn AR {item.dueBusinessDate.slice(0, 10)}</p>
            <p>Còn nợ {displayQuantity(item.outstanding)} · Giá vốn {displayQuantity(item.cost)}</p>
            <p className="c3-source">Nguồn {item.sourceId}</p>
          </article>)}
        </section>
        {data.cases.length > 0 && <section className="panel panel-content" aria-label="Giao hàng cần đối soát">
          <h2>Giao hàng cần đối soát</h2>
          {data.cases.map(item => <div key={item.commandId}>
            <p className="c3-source">
              {displayQuantity(item.quantity)} · {item.reasonCode ? labels[item.reasonCode] ?? 'Ghi sổ bị từ chối' : 'Chờ ghi sổ'} · {item.commandId}
            </p>
            {item.canRetry && <Button disabled={disabled} onClick={() => resolve({
              tripId: trip, commandId: item.commandId, manifestLineId: item.manifestLineId,
              shipToVersion: item.shipToVersion, quantity: item.quantity,
            }, false)}>Ghi lại lần giao đã lưu</Button>}
          </div>)}
        </section>}
      </>}
    </>}
    {offline && <p role="status">Cần kết nối để ghi giao hàng.</p>}
    {notice && <p role="status">{notice}</p>}
  </main>
}
