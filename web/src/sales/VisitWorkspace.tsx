import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { forgetPending, outcomeMessage, pendingJson, read, readError, rememberPending, request,
  type Customer, type Detail, type Identity, type Outcome } from './customer-api'

type Visit = {
  id: string; outletId: string | null; outcome: string; reason: string; observation: string | null
  actorId: string; completedAtUtc: string
}
type Pending = { commandId: string; customerId: string; body: string; conflict?: boolean }
const labels: Record<string, string> = { NO_ORDER: 'Không có đơn', NOT_MET: 'Không gặp' }

function restore(key: string): Pending | null {
  try {
    const item = JSON.parse(pendingJson(key) ?? 'null') as Pending | null
    if (!item || !/^[a-f0-9-]{36}$/i.test(item.commandId) || typeof item.body !== 'string') return null
    const body = JSON.parse(item.body)
    return body.commandId === item.commandId && body.customerId === item.customerId ? item : null
  } catch { return null }
}

export function VisitWorkspace({ token, identity, startCustomer, onCustomerChange }: {
  token: string; identity: Identity; startCustomer?: string; onCustomerChange?: (id: string) => void
}) {
  const key = 'dev-f01.visit.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
  const [pending, setPending] = useState(() => restore(key))
  const [customers, setCustomers] = useState<Customer[]>([])
  const [selectedCustomer, setCustomerId] = useState(() => pending?.customerId ?? startCustomer ?? '')
  const customerId = customers.some(row => row.id === selectedCustomer) ? selectedCustomer : ''
  const [loaded, setLoaded] = useState<{ customerId: string; detail: Detail; visits: Visit[] }>()
  const [refresh, setRefresh] = useState(0)
  const [offline, setOffline] = useState(() => !navigator.onLine)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [customerError, setCustomerError] = useState('')
  const [visitError, setVisitError] = useState('')
  const operation = useRef(false)
  const form = useRef<HTMLFormElement>(null)
  const id = useId()

  useEffect(() => {
    function sync() { setOffline(!navigator.onLine) }
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  useEffect(() => {
    let current = true
    if (!offline) {
      void read<Customer[]>(token, '/visits/customers')
        .then(rows => {
          if (current) {
            setCustomers(rows)
            setCustomerError('')
          }
        })
        .catch(error => { if (current) setCustomerError(readError(error)) })
    }
    return () => { current = false }
  }, [token, offline])

  useEffect(() => {
    let current = true
    if (customerId && !offline) {
      void Promise.all([
        read<Detail>(token, '/customers/' + customerId),
        read<Visit[]>(token, '/visits?customerId=' + customerId),
      ]).then(([detail, visits]) => {
        if (current) {
          setLoaded({ customerId, detail, visits })
          setVisitError('')
        }
      }).catch(error => { if (current) setVisitError(readError(error)) })
    }
    return () => { current = false }
  }, [token, customerId, refresh, offline])

  async function resolve(command: Pending, lookup: boolean) {
    if (!navigator.onLine || operation.current || command.conflict) return
    operation.current = true
    setBusy(true)
    try {
      const response = lookup
        ? await request(token, '/visits/commands/' + command.commandId)
        : await request(token, '/visits', command.body)
      if (lookup && response.status === 404) {
        setMessage('Chưa tìm thấy kết quả. Có thể gửi lại cùng yêu cầu.')
        return
      }
      if (response.status === 401 || response.status === 403) {
        setMessage('Phiên DEV không có quyền Sales hiện hành. Kết quả gốc vẫn được giữ để tra cứu.')
        return
      }
      if (!response.ok && ![400, 404, 409].includes(response.status)) throw new Error('Unknown outcome')
      const result = await response.json() as Outcome
      if (result.code === 'COMMAND_PAYLOAD_CONFLICT') {
        const conflicted = { ...command, conflict: true }
        rememberPending(key, conflicted)
        setPending(conflicted)
        return
      }
      forgetPending(key, command.commandId)
      setPending(null)
      if (result.code === 'APPLIED') {
        setMessage('Đã hoàn tất viếng thăm.')
        form.current?.reset()
        setRefresh(value => value + 1)
      } else {
        setMessage(result.code === 'VISIT_FIELDS_INVALID' ? 'Kiểm tra kết quả, lý do và nội dung quan sát.' : outcomeMessage(result.code))
      }
    } catch { setMessage('Chưa rõ kết quả viếng thăm.') }
    finally {
      operation.current = false
      setBusy(false)
    }
  }

  function complete(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending || operation.current || !navigator.onLine || !data?.detail.customer.isActive) return
    const values = new FormData(event.currentTarget)
    const commandId = crypto.randomUUID()
    const body = JSON.stringify({ commandId, customerId, outletId: values.get('outlet') || null,
      outcome: values.get('outcome'), reason: values.get('reason'), observation: String(values.get('observation')).trim() || null })
    const command = { commandId, customerId, body }
    rememberPending(key, command)
    setPending(command)
    void resolve(command, false)
  }

  const data = loaded?.customerId === customerId ? loaded : undefined
  return <section className="panel panel-content sales-pane" aria-labelledby={id + '-title'}>
    <h2 id={id + '-title'}>Viếng thăm</h2>
    {offline && <p role="status">Đang ngoại tuyến. Cần kết nối để hoàn tất viếng thăm.</p>}
    {(customerError || visitError) && <p role="alert">{customerError || visitError}</p>}
    <div className="field">
      <label htmlFor={id + '-customer'}>Khách hàng</label>
      <select id={id + '-customer'} value={customerId} disabled={busy || pending !== null}
        onChange={event => {
          setLoaded(undefined)
          setCustomerId(event.currentTarget.value)
          onCustomerChange?.(event.currentTarget.value)
          setVisitError('')
          setMessage('')
        }}>
        <option value="">Chọn khách hàng</option>
        {customers.map(customer => <option key={customer.id} value={customer.id}>{customer.code} · {customer.name}</option>)}
      </select>
    </div>
    {message && <p role="status">{message}</p>}
    {pending?.conflict && <p role="alert">Mã yêu cầu {pending.commandId} đã được dùng cho nội dung khác.</p>}
    {pending && !pending.conflict && <div className="button-row">
      <Button onClick={() => void resolve(pending, true)} disabled={busy || offline}>Tra cứu kết quả</Button>
      <Button onClick={() => void resolve(pending, false)} disabled={busy || offline}>Gửi lại yêu cầu</Button>
    </div>}
    {data && <>
      {data.detail.customer.isActive && <form key={customerId} ref={form} onSubmit={complete}>
        <fieldset className="c1-fields" disabled={busy || offline || pending !== null}>
          <div className="field">
            <label htmlFor={id + '-outlet'}>Điểm bán</label>
            <select name="outlet" id={id + '-outlet'} defaultValue="">
              <option value="">Không chọn điểm bán</option>
              {data.detail.outlets.map(outlet => <option key={outlet.id} value={outlet.id}>{outlet.name}</option>)}
            </select>
          </div>
          <div className="field">
            <label htmlFor={id + '-outcome'}>Kết quả viếng thăm</label>
            <select name="outcome" id={id + '-outcome'} defaultValue="NO_ORDER">
              {Object.entries(labels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
            </select>
          </div>
          <Field name="reason" label="Lý do" required maxLength={160} />
          <Field name="observation" label="Quan sát" maxLength={500} />
          <div className="button-row"><Button variant="primary" type="submit">Hoàn tất viếng thăm</Button></div>
        </fieldset>
      </form>}
      <h3>Lịch sử viếng thăm</h3>
      {data.visits.length === 0 ? <p>Chưa có viếng thăm.</p> : <ul className="c1-rows">
        {data.visits.map(visit => <li key={visit.id}><div className="c3-source">
          <strong>{labels[visit.outcome]}</strong> · {visit.reason}
          {visit.observation && <p>{visit.observation}</p>}
          <p>{data.detail.outlets.find(outlet => outlet.id === visit.outletId)?.name ?? 'Khách hàng'} · {visit.actorId} · {' '}
            <time dateTime={visit.completedAtUtc}>{new Date(visit.completedAtUtc).toLocaleString('vi-VN')}</time></p>
        </div></li>)}
      </ul>}
    </>}
  </section>
}
