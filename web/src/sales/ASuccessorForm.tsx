import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { forgetPending, pendingJson, rememberPending } from './customer-api'

type Trip = { tripId: string; code: string; businessDate: string }
type Successors = {
  confirmSourceId: string; followUpId: string; revision: string; remaining: number; unheld: number; unscheduled: number; canWrite: boolean
  trips: Trip[]
  bindings: { bindingId: string | null; destinationTripId: string | null; code: string | null; quantity: number; transferred: number }[]
  rights: { rightId: string; held: number; neverLoaded: number; returned: number }[]
}
type Command = {
  commandId: string; destinationTripId: string; confirmSourceId: string; quantity: string
  followUpId: string; expectedRevision: string
  bindingId?: string; originalRightId?: string
}

const format = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 3 })

export function ASuccessorForm({ storageKey, load, apply, onApplied, unit }: {
  storageKey: string
  load: () => Promise<Successors>
  apply: (command: Command) => Promise<void>
  onApplied: () => void
  unit: string
}) {
  const id = useId()
  const [data, setData] = useState<Successors>()
  const [attempt, setAttempt] = useState(0)
  const [loadError, setLoadError] = useState(false)
  const [notice, setNotice] = useState('')
  const [target, setTarget] = useState('')
  const [bindingId, setBinding] = useState('')
  const [rightId, setRight] = useState('')
  const [busy, setBusy] = useState(false)
  const writing = useRef(false)
  const [pending, setPending] = useState<Command | null>(() => {
    try { return JSON.parse(pendingJson(storageKey) ?? 'null') as Command | null }
    catch { return null }
  })
  useEffect(() => {
    let live = true
    void load().then(value => {
      if (live) { setData(value); setLoadError(false) }
    }).catch(() => {
      if (live) { setData(undefined); setLoadError(true) }
    })
    return () => { live = false }
  }, [attempt, load])

  const binding = data?.bindings.find(row => row.bindingId === bindingId)
  const right = data?.rights.find(row => row.rightId === rightId)
  const eligible = data?.trips.some(trip => trip.tripId === binding?.destinationTripId)
  const limit = binding && right && eligible ? Math.min(right.held, binding.quantity - binding.transferred) : 0
  const amount = (value: number) => format.format(value) + ' ' + unit

  async function send(command: Command) {
    if (writing.current || !navigator.onLine) return
    writing.current = true
    setBusy(true)
    rememberPending(storageKey, command)
    setPending(command)
    setNotice('')
    try {
      await apply(command)
      forgetPending(storageKey, command.commandId)
      setPending(null)
      setNotice(command.bindingId ? 'Đã chuyển lượng HELD.' : 'Đã xếp chuyến kế nhiệm.')
      onApplied()
    } catch (error) {
      const status = (error as { status?: number }).status
      if (status && status >= 400 && status < 500) {
        forgetPending(storageKey, command.commandId)
        setPending(null)
        setNotice('Yêu cầu bị từ chối. Kiểm tra chuyến, nguồn và số lượng hiện tại.')
      } else {
        setNotice('Chưa xác nhận được kết quả. Có thể gửi lại cùng yêu cầu.')
      }
    } finally {
      writing.current = false
      setBusy(false)
      setAttempt(value => value + 1)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>, transfer: boolean) {
    event.preventDefault()
    if (!data?.canWrite || pending || busy) return
    const quantity = String(new FormData(event.currentTarget).get('quantity') ?? '')
    const numericQuantity = Number(quantity)
    const destinationTripId = transfer ? binding?.destinationTripId : target
    if (!destinationTripId || !/^[0-9]+(?:\.[0-9]{1,3})?$/.test(quantity) || numericQuantity <= 0 ||
        numericQuantity > (transfer ? limit : data.unscheduled)) return
    void send({
      commandId: crypto.randomUUID(), destinationTripId, confirmSourceId: data.confirmSourceId, quantity,
      followUpId: data.followUpId, expectedRevision: data.revision,
      ...(transfer ? { bindingId, originalRightId: rightId } : {}),
    })
  }

  return <div>
    <h5>Chuyến kế nhiệm</h5>
    {loadError ? <><p role="alert">Không tải được nguồn kế nhiệm.</p>
      <Button onClick={() => setAttempt(value => value + 1)}>Tải lại</Button></> : !data && <p role="status">Đang tải…</p>}
    {notice && <p role="status">{notice}</p>}
    {data && <>
      <dl className="sales-facts">
        <dt>Còn phải giao</dt><dd>{amount(data.remaining)}</dd>
        <dt>Còn chờ giữ</dt><dd>{amount(data.unheld)}</dd>
        <dt>Chưa xếp chuyến</dt><dd>{amount(data.unscheduled)}</dd>
        <dt>HELD chưa chuyển</dt><dd>{amount(data.rights.reduce((sum, row) => sum + row.held, 0))}</dd>
      </dl>
      {data.bindings.length > 0 && <ul>
        {data.bindings.map((row, index) => <li key={row.bindingId ?? index}>
          {row.code || 'Chuyến ngoài phân công'} · Xếp {amount(row.quantity)} · Đã chuyển HELD {amount(row.transferred)}
        </li>)}
      </ul>}
      {data.canWrite && (pending ? <Button disabled={busy} onClick={() => void send(pending)}>Gửi lại yêu cầu kế nhiệm</Button>
        : <>
          {data.unscheduled > 0 && data.trips.length === 0 && <p>Chưa có chuyến kế nhiệm đủ điều kiện trong ngày nghiệp vụ.</p>}
          {data.unscheduled > 0 && data.trips.length > 0 && <form aria-label="Xếp chuyến kế nhiệm" onSubmit={event => submit(event, false)}>
            <fieldset className="sales-filter" disabled={busy}>
              <div className="field">
                <label htmlFor={id + '-trip'}>Chuyến đích</label>
                <select id={id + '-trip'} value={target} onChange={event => setTarget(event.target.value)} required>
                  <option value="">Chọn chuyến</option>
                  {data.trips.map(trip => <option key={trip.tripId} value={trip.tripId}>{trip.code}</option>)}
                </select>
              </div>
              <Field name="quantity" label={'Lượng xếp chuyến (' + unit + ')'} type="number"
                step="0.001" min="0.001" max={data.unscheduled} required />
              <Button type="submit">Xếp chuyến</Button>
            </fieldset>
          </form>}
          {data.bindings.some(row => row.transferred < row.quantity && data.trips.some(trip => trip.tripId === row.destinationTripId))
              && data.rights.some(row => row.held > 0) && <form aria-label="Chuyển HELD" onSubmit={event => submit(event, true)}>
            <fieldset className="sales-filter" disabled={busy}>
              <div className="field">
                <label htmlFor={id + '-binding'}>Phần đã xếp</label>
                <select id={id + '-binding'} value={bindingId} onChange={event => setBinding(event.target.value)} required>
                  <option value="">Chọn phần đã xếp</option>
                  {data.bindings.filter(row => row.transferred < row.quantity && data.trips.some(trip => trip.tripId === row.destinationTripId))
                    .map(row => <option key={row.bindingId} value={row.bindingId ?? ''}>{row.code} · {amount(row.quantity - row.transferred)}</option>)}
                </select>
              </div>
              <div className="field">
                <label htmlFor={id + '-right'}>Quyền giữ gốc</label>
                <select id={id + '-right'} value={rightId} onChange={event => setRight(event.target.value)} required>
                  <option value="">Chọn quyền giữ</option>
                  {data.rights.filter(row => row.held > 0).map(row => <option key={row.rightId} value={row.rightId}>
                    {row.rightId} · Chưa nạp {amount(row.neverLoaded)} · Đã trả {amount(row.returned)}
                  </option>)}
                </select>
              </div>
              <Field name="quantity" label={'Lượng chuyển HELD (' + unit + ')'} type="number"
                step="0.001" min="0.001" max={limit} required />
              <Button type="submit" disabled={limit <= 0}>Chuyển HELD</Button>
            </fieldset>
          </form>}
        </>)}
    </>}
  </div>
}
