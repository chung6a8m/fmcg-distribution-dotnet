import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending, requestSalesApi, type Identity } from './customer-api'
import { readSuccessorOutcome, successorUnits, type SuccessorCommand } from './ASuccessorForm'

type Binding = {
  bindingId: string; destinationTripId: string; code: string; quantity: number
  transferred: number; reserved: number; unfilled: number
}
type Options = { followUpId: string; confirmSourceId: string; revision: string; unheld: number; step: number; bindings: Binding[] }

const api = '/api/synthetic/app05/trip-close'
const number = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })

export function ASuccessorReserve({ token, identity, tripId, lineId, unit, warehouses, offline, onApplied }: {
  token: string; identity: Identity; tripId: string; lineId: string; unit: string
  warehouses: { id: string; label: string }[]; offline: boolean; onApplied: () => void
}) {
  const id = useId()
  const path = '/trips/' + tripId + '/a-remainders/' + lineId
  const storageKey = 'app05.successor-reserve.v1.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId, tripId, lineId])
  const [pending, setPending] = useState<SuccessorCommand | null>(() => {
    try { return JSON.parse(pendingJson(storageKey) ?? 'null') as SuccessorCommand | null }
    catch { return null }
  })
  const [open, setOpen] = useState(pending !== null)
  const [options, setOptions] = useState<Options>()
  const [bindingId, setBinding] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const sending = useRef(false)

  useEffect(() => {
    if (!open || offline) return
    let live = true
    void requestSalesApi(api, token, path + '/reserve-options').then(async response => {
      if (!response.ok) throw new Error('Successor reserve options unavailable')
      const data = await response.json() as Options
      if (live) { setOptions(data); setError(false) }
    }).catch(() => {
      if (live) { setOptions(undefined); setError(true) }
    })
    return () => { live = false }
  }, [open, offline, token, path, attempt])

  const binding = options?.bindings.find(row => row.bindingId === bindingId)
  const limit = binding && options ? Math.min(binding.unfilled, options.unheld) : 0
  const amount = (n: number) => number.format(n) + ' ' + unit
  function reload() {
    setOptions(undefined)
    setError(false)
    setAttempt(value => value + 1)
  }

  async function send(command: SuccessorCommand) {
    if (offline || !navigator.onLine || sending.current) return
    sending.current = true
    setBusy(true)
    setNotice('')
    rememberPending(storageKey, command)
    setPending(command)
    try {
      const response = await requestSalesApi(api, token, path + '/reserve-unheld', JSON.stringify(command))
      const result = await readSuccessorOutcome(response, command, { originTripId: tripId, orderLineId: lineId })
      if (result === 'applied' || typeof result === 'object') {
        forgetPending(storageKey, command.commandId)
        setPending(null)
      }
      if (result === 'applied') {
        setNotice('Đã giữ bổ sung cho chuyến kế nhiệm.')
        onApplied()
      } else if (typeof result === 'object') {
        setNotice(['STOCK_INSUFFICIENT', 'F03_LOT_STOCK_INSUFFICIENT'].includes(result.rejected)
          ? 'Không đủ hàng đủ điều kiện. Phần chưa giữ vẫn đang chờ xử lý.'
          : 'Yêu cầu giữ bị từ chối. Kiểm tra chuyến, kho và số lượng hiện tại.')
      } else if (result === 'access') {
        setNotice('Chưa đủ quyền xác nhận kết quả. Gửi lại cùng yêu cầu khi có quyền.')
      } else {
        setNotice('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')
      }
    } catch {
      setNotice('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')
    } finally {
      sending.current = false
      setBusy(false)
      reload()
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!options || !binding || pending || busy) return
    const data = new FormData(event.currentTarget)
    const quantity = String(data.get('quantity') ?? '')
    const warehouseId = String(data.get('warehouse') ?? '')
    if (!successorUnits(quantity) || Number(quantity) > limit || !warehouses.some(w => w.id === warehouseId)) return
    void send({
      commandId: crypto.randomUUID(), destinationTripId: binding.destinationTripId,
      bindingId: binding.bindingId, warehouseId, quantity, confirmSourceId: options.confirmSourceId,
      followUpId: options.followUpId, expectedRevision: options.revision,
    })
  }

  if (!open) return <Button disabled={offline} onClick={() => setOpen(true)}>Giữ hàng trên chuyến kế nhiệm</Button>
  return <section aria-label="Giữ hàng trên chuyến kế nhiệm">
    {notice && <p role="status">{notice}</p>}
    {pending && <Button disabled={offline || busy} onClick={() => void send(pending)}>Gửi lại yêu cầu giữ bổ sung</Button>}
    {error ? <p role="alert">Không tải được chuyến kế nhiệm. <Button onClick={reload}>Tải lại</Button></p>
      : !options && <p role="status">Đang tải phần hàng chưa giữ…</p>}
    {options && <>
      <p>Còn chờ giữ {amount(options.unheld)}</p>
      {options.bindings.map(row => <p key={row.bindingId}>
        {row.code} · Đã xếp {amount(row.quantity)} · Đã giữ {amount(row.transferred + row.reserved)}
      </p>)}
      {options.bindings.length > 0 && <p>Nạp lên xe trên chuyến kế nhiệm sẽ có ở APP-05.4b.</p>}
      {options.bindings.length === 0 && <p>Chưa có chuyến kế nhiệm đủ điều kiện.</p>}
      {!pending && options.unheld > 0 && options.bindings.some(row => row.unfilled > 0) &&
        <form aria-label="Giữ bổ sung" onSubmit={submit}>
          <fieldset className="sales-filter" disabled={offline || busy}>
            <label className="field" htmlFor={id}>Chuyến kế nhiệm
              <select id={id} value={bindingId} onChange={event => setBinding(event.target.value)} required>
                <option value="">Chọn chuyến</option>
                {options.bindings.filter(row => row.unfilled > 0)
                  .map(row => <option key={row.bindingId} value={row.bindingId}>{row.code}</option>)}
              </select>
            </label>
            <label className="field">Kho giữ
              <select name="warehouse" defaultValue="" required>
                <option value="">Chọn kho</option>
                {warehouses.map(row => <option key={row.id} value={row.id}>{row.label}</option>)}
              </select>
            </label>
            <Field name="quantity" label="Lượng giữ bổ sung" type="number" min={options.step} step={options.step} max={limit} required />
            <Button variant="primary" type="submit" disabled={limit <= 0}>Giữ bổ sung</Button>
          </fieldset>
        </form>}
    </>}
  </section>
}
