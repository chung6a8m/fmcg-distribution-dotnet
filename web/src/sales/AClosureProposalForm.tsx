import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { successorUnits } from './ASuccessorForm'
import { forgetPending, pendingJson, rememberPending, requestSalesApi } from './customer-api'

type Command = {
  commandId: string; confirmSourceId: string; followUpId: string; quantity: string; reason: string; expectedRevision: string
}
type Proposal = Command & { proposalId: string; maker: string; recordedAt: string }
type ProposalList = {
  scope: Pick<Command, 'confirmSourceId' | 'followUpId'> & { remaining: string; quantityStep: string; revision: string }
  canPropose: boolean; proposals: Proposal[]
}

const api = '/api/synthetic/app05/trip-close'
const title = 'Đề nghị đóng phần còn lại'
const finding = 'Đang tra cứu biên nhận đề nghị…'
const unknown = 'Chưa xác nhận được kết quả đề nghị. Có thể tra cứu hoặc gửi lại cùng yêu cầu.'
const saved = 'Đã ghi nhận đề nghị đóng. Đề nghị đang chờ duyệt.'
const conflict = 'Mã yêu cầu đã gắn với nội dung khác. Giữ mã yêu cầu để đối chiếu biên nhận.'
const uuid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i
const revision = /^[a-f0-9]{64}$/i
const reasonValid = (reason: string) => reason.trim().length > 0 && reason.trim().length <= 240 && !/[\u0000-\u001f\u007f-\u009f]/.test(reason)
const number = new Intl.NumberFormat('vi-VN')
const timestamp = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short', timeZone: 'Asia/Bangkok' })

function sameQuantity(expected: string, actual: unknown) {
  const units = successorUnits(expected)
  return units !== null && units === successorUnits(actual)
}

function fixedQuantity(units: bigint) {
  return `${units / 1000n}.${(units % 1000n).toString().padStart(3, '0')}`
}

function formatAmount(value: string, unit: string) {
  const units = successorUnits(value)
  if (units === null) return 'Chưa xác định'
  const fraction = fixedQuantity(units).split('.')[1].replace(/0+$/, '')
  const exact = number.format(units / 1000n) + (fraction ? ',' + fraction : '')
  return unit ? `${exact} ${unit}` : exact
}

function restore(storageKey: string): Command | null {
  try {
    const command = JSON.parse(pendingJson(storageKey) ?? 'null') as Command | null
    return command && [command.commandId, command.confirmSourceId, command.followUpId].every(value => uuid.test(value))
      && revision.test(command.expectedRevision) && (successorUnits(command.quantity) ?? 0n) > 0n
      && reasonValid(command.reason) ? command : null
  } catch { return null }
}

function rejection(status: number, code?: string) {
  if (status === 400 && code === 'A_CLOSURE_PROPOSAL_INVALID') return 'Kiểm tra lại lượng và lý do đề nghị.'
  if (status !== 409) return undefined
  if (code === 'A_CLOSURE_SCOPE_CHANGED') return 'Dữ liệu nguồn đã thay đổi. Kiểm tra lại trước khi gửi đề nghị mới.'
  if (code === 'A_CLOSURE_SOURCE_INVALID' || code === 'A_CLOSURE_REMAINDER_NOT_ELIGIBLE')
    return 'Đề nghị bị từ chối. Kiểm tra lại nguồn và lượng còn phải giao.'
}

export function AClosureProposalForm({ token, actorId, tripId, lineId, storageKey, unit, enabled }: {
  token: string; actorId: string; tripId: string; lineId: string; storageKey: string; unit: string; enabled: boolean
}) {
  const path = `/trips/${tripId}/a-remainders/${lineId}/closure-proposals`
  const [pending, setPending] = useState(() => restore(storageKey))
  const [open, setOpen] = useState(pending !== null)
  const [data, setData] = useState<ProposalList>()
  const [attempt, setAttempt] = useState(0)
  const [loadError, setLoadError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const writing = useRef(false)

  useEffect(() => {
    if (!open || !navigator.onLine) return
    let current = true
    void requestSalesApi(api, token, path).then(async response => {
      if (!response.ok) {
        if (current) {
          setData(undefined)
          setLoadError([401, 403].includes(response.status)
            ? 'Chưa đủ quyền đọc đề nghị đóng.' : 'Không tải được đề nghị đóng.')
        }
        return
      }
      const result = await response.json() as ProposalList
      if (!current) return
      setData(result)
      setLoadError('')
      const original = restore(storageKey)
      const receipt = original && result.proposals.find(proposal =>
        uuid.test(proposal.proposalId) && proposal.commandId === original.commandId && proposal.maker === actorId)
      if (original && receipt && receipt.confirmSourceId === original.confirmSourceId
        && receipt.followUpId === original.followUpId && sameQuantity(original.quantity, receipt.quantity)
        && receipt.reason === original.reason.trim()
        && receipt.expectedRevision === original.expectedRevision.toUpperCase()) {
        forgetPending(storageKey, original.commandId)
        setPending(null)
        setNotice(saved)
      } else if (receipt) {
        setNotice(conflict)
      } else if (original) {
        setNotice(message => message === finding ? unknown : message)
      }
    }).catch(() => {
      if (current) { setData(undefined); setLoadError('Không tải được đề nghị đóng.') }
    })
    return () => { current = false }
  }, [open, attempt, token, path, storageKey, actorId])

  function reload() {
    setData(undefined)
    setLoadError('')
    setAttempt(value => value + 1)
  }

  async function send(command: Command) {
    if (!enabled || !navigator.onLine || writing.current) return
    writing.current = true
    setBusy(true)
    setNotice('')
    rememberPending(storageKey, command)
    setPending(command)
    try {
      const response = await requestSalesApi(api, token, path, JSON.stringify(command))
      const result = await response.json().catch(() => null) as { proposalId?: string; quantity?: string; status?: string; code?: string } | null
      const rejected = rejection(response.status, result?.code)
      if (response.status === 409 && result?.code === 'COMMAND_PAYLOAD_CONFLICT') {
        setNotice(conflict)
      } else if (rejected) {
        forgetPending(storageKey, command.commandId)
        setPending(null)
        setNotice(rejected)
        reload()
      } else if (response.ok && result?.status === 'PENDING' && uuid.test(result.proposalId ?? '')
        && sameQuantity(command.quantity, result.quantity)) {
        setNotice(finding)
        reload()
      } else {
        setNotice([401, 403].includes(response.status) ? 'Chưa đủ quyền xác nhận kết quả. Gửi lại cùng yêu cầu khi có quyền.' : unknown)
      }
    } catch {
      setNotice(unknown)
    } finally {
      writing.current = false
      setBusy(false)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!data?.canPropose || pending || busy) return
    const values = new FormData(event.currentTarget)
    const quantity = String(values.get('quantity') ?? '').trim()
    const reason = String(values.get('reason') ?? '').trim()
    const units = successorUnits(quantity)
    const step = successorUnits(data.scope.quantityStep)
    const remaining = successorUnits(data.scope.remaining)
    if (units === null || units <= 0n || step === null || step <= 0n || remaining === null || units % step !== 0n || units > remaining) {
      setNotice('Nhập lượng đề nghị trong phần còn phải giao và theo bước số lượng của dòng.')
      return
    }
    if (!reasonValid(reason)) {
      setNotice('Nhập lý do đề nghị đóng, tối đa 240 ký tự.')
      return
    }
    void send({ commandId: crypto.randomUUID(), confirmSourceId: data.scope.confirmSourceId,
      followUpId: data.scope.followUpId, quantity: fixedQuantity(units), reason, expectedRevision: data.scope.revision })
  }

  if (!open) return <Button onClick={() => setOpen(true)}>{title}</Button>
  const amount = (value: string) => formatAmount(value, unit)
  return <section aria-label={`${title} của dòng A`}>
    {notice && <p role="status">{notice}</p>}
    {pending && <>
      <p>Đề nghị đang chờ xác nhận · Lượng {amount(pending.quantity)} · Mã yêu cầu {pending.commandId}</p>
      <p>Lý do {pending.reason}</p>
      <div className="button-row">
        <Button disabled={busy} onClick={reload}>Tra cứu đề nghị</Button>
        <Button disabled={!enabled || busy || !data} onClick={() => void send(pending)}>Gửi lại cùng yêu cầu</Button>
      </div>
    </>}
    {loadError ? <div>
      <p role="alert">{loadError}</p>
      {!pending && <Button onClick={reload}>Tải lại đề nghị</Button>}
    </div> : !data && <p role="status">Đang tải đề nghị đóng…</p>}
    {data && <>
      <p>Còn phải giao {amount(data.scope.remaining)}</p>
      {data.proposals.length > 0 && <ul className="c1-rows" aria-label="Đề nghị đã ghi nhận">
        {data.proposals.map(proposal => <li key={proposal.proposalId}>
          <div className="c3-source">
            <p><strong>Chờ duyệt</strong> · {amount(proposal.quantity)} · {proposal.maker} ·{' '}
              <time dateTime={proposal.recordedAt}>{timestamp.format(new Date(proposal.recordedAt))} ICT</time>
            </p>
            <p>Lý do {proposal.reason}</p>
            <p>Mã đề nghị {proposal.proposalId}</p>
          </div>
        </li>)}
      </ul>}
      {!pending && (data.canPropose ? <form aria-label={title} onSubmit={submit}>
        <fieldset className="sales-filter" disabled={!enabled || busy}>
          <Field name="quantity" label={unit ? `Lượng đề nghị đóng (${unit})` : 'Lượng đề nghị đóng'} type="number"
            step="any" required />
          <Field name="reason" label="Lý do đề nghị đóng" maxLength={240} required />
          <Button variant="primary" type="submit">Gửi đề nghị</Button>
        </fieldset>
      </form> : <p>Hiện chưa thể gửi đề nghị cho nguồn này.</p>)}
    </>}
  </section>
}
