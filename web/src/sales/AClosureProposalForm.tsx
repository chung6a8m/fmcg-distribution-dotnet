import { useEffect, useEffectEvent, useRef, useState, type FormEvent } from 'react'
import { Button, Field } from '../ui/controls'
import { successorUnits } from './ASuccessorForm'
import { forgetPending, pendingJson, rememberPending, requestSalesApi } from './customer-api'

type Command = {
  commandId: string; confirmSourceId: string; followUpId: string; quantity: string; reason: string; expectedRevision: string
}
type ApprovalIntent = { proposalId: string; quantity: string; expectedRevision: string }
type ApprovalCommand = ApprovalIntent & { commandId: string }
type Approval = { commandId: string; sourceId: string; checker: string; quantity: string; released: string; approvedAt: string }
type ApprovalReceipt = Pick<Approval, 'commandId' | 'sourceId' | 'quantity' | 'released'> & { proposalId: string; status: string }
type Proposal = { proposalId: string; commandId: string; quantity: string; reason: string; maker: string; recordedAt: string;
  canApprove: boolean; approval?: Approval }
type ProposalList = {
  scope: Pick<Command, 'confirmSourceId' | 'followUpId'> & { remaining: string; quantityStep: string; revision: string }
  canPropose: boolean; proposals: Proposal[]
}

const api = '/api/synthetic/app05/trip-close'
const title = 'Đề nghị đóng phần còn lại'
const finding = 'Đang tra cứu biên nhận đề nghị…'
const unknown = 'Chưa xác nhận được kết quả đề nghị. Có thể tra cứu hoặc gửi lại cùng yêu cầu.'
const saved = 'Đã ghi nhận đề nghị đóng. Đề nghị đang chờ duyệt.'
const findingApproval = 'Đang tra cứu kết quả duyệt…'
const unknownApproval = 'Chưa xác nhận được kết quả duyệt. Có thể tra cứu hoặc gửi lại cùng yêu cầu.'
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

function restoreApproval(storageKey: string): ApprovalCommand | null {
  try {
    const command = JSON.parse(pendingJson(storageKey) ?? 'null') as ApprovalCommand | null
    return command && uuid.test(command.commandId) && uuid.test(command.proposalId)
      && revision.test(command.expectedRevision) && (successorUnits(command.quantity) ?? 0n) > 0n ? command : null
  } catch { return null }
}

function validRelease(expected: string, receipt: Partial<Pick<Approval, 'quantity' | 'released' | 'sourceId'>>) {
  const quantity = successorUnits(expected)
  const released = successorUnits(receipt.released)
  return uuid.test(receipt.sourceId ?? '') && quantity !== null && quantity === successorUnits(receipt.quantity)
    && released !== null && released <= quantity
}

function rejection(status: number, code?: string) {
  if (status === 400 && code === 'A_CLOSURE_PROPOSAL_INVALID') return 'Kiểm tra lại lượng và lý do đề nghị.'
  if (status !== 409) return undefined
  if (code === 'A_CLOSURE_SCOPE_CHANGED') return 'Dữ liệu nguồn đã thay đổi. Kiểm tra lại trước khi gửi đề nghị mới.'
  if (code === 'A_CLOSURE_SOURCE_INVALID' || code === 'A_CLOSURE_REMAINDER_NOT_ELIGIBLE')
    return 'Đề nghị bị từ chối. Kiểm tra lại nguồn và lượng còn phải giao.'
}

function approvalRejection(status: number, code?: string) {
  if (status === 404) return 'Không tìm thấy đề nghị trong phạm vi nguồn hiện hành.'
  if (status !== 409) return undefined
  switch (code) {
    case 'A_CLOSURE_SELF_APPROVAL': return 'Người lập đề nghị cần người khác duyệt.'
    case 'A_CLOSURE_SCOPE_CHANGED': return 'Dữ liệu nguồn đã thay đổi. Kiểm tra lại trước khi duyệt.'
    case 'A_CLOSURE_REMAINDER_NOT_ELIGIBLE': return 'Phần còn lại hiện chưa đủ điều kiện duyệt đóng.'
    case 'A_CLOSURE_SUCCESSOR_ACTIVE': return 'Quyền trên chuyến kế nhiệm đang hoạt động. Kiểm tra lại trước khi duyệt.'
    case 'A_CLOSURE_RELEASE_UNPROVEN': return 'Chưa xác minh được nguồn của lượng giữ cần giải phóng.'
    default: return undefined
  }
}

export function AClosureProposalForm({ token, actorId, tripId, lineId, storageKey, unit, enabled, initiallyOpen = false, onApprovalRead }: {
  token: string; actorId: string; tripId: string; lineId: string; storageKey: string; unit: string; enabled: boolean
  initiallyOpen?: boolean; onApprovalRead: () => void
}) {
  const path = `/trips/${tripId}/a-remainders/${lineId}/closure-proposals`
  const approvalKey = storageKey + '.approval'
  const [pending, setPending] = useState(() => restore(storageKey))
  const [pendingApproval, setPendingApproval] = useState(() => restoreApproval(approvalKey))
  const [confirmation, setConfirmation] = useState<ApprovalIntent>()
  const [open, setOpen] = useState(initiallyOpen || pending !== null || pendingApproval !== null)
  const [data, setData] = useState<ProposalList>()
  const [attempt, setAttempt] = useState(0)
  const [loadError, setLoadError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const writing = useRef(false)
  const publishApproval = useEffectEvent(onApprovalRead)

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
      const receipt = original && result.scope.confirmSourceId === original.confirmSourceId
        && result.scope.followUpId === original.followUpId && result.proposals.some(proposal =>
          uuid.test(proposal.proposalId) && proposal.commandId === original.commandId && proposal.maker === actorId
          && sameQuantity(original.quantity, proposal.quantity) && proposal.reason === original.reason)
      if (receipt) {
        forgetPending(storageKey, original.commandId)
        setPending(null)
        setNotice(saved)
      } else if (original) {
        setNotice(message => message === finding ? unknown : message)
      }
      const approvalRequest = restoreApproval(approvalKey)
      if (approvalRequest) {
        const proposal = result.proposals.find(row => row.proposalId === approvalRequest.proposalId)
        const approval = proposal?.approval
        if (approval && uuid.test(approval.commandId) && validRelease(approvalRequest.quantity, approval)) {
          const sameCommand = approval.commandId === approvalRequest.commandId
          if (!sameCommand || approval.checker === actorId) {
            forgetPending(approvalKey, approvalRequest.commandId)
            setPendingApproval(null)
            setNotice(sameCommand ? 'Đã duyệt đề nghị đóng phần còn lại.' : 'Đề nghị đã được duyệt bằng yêu cầu khác.')
            publishApproval()
            return
          }
        }
        setNotice(message => message === findingApproval ? unknownApproval : message)
      }
    }).catch(() => {
      if (current) { setData(undefined); setLoadError('Không tải được đề nghị đóng.') }
    })
    return () => { current = false }
  }, [open, attempt, token, path, storageKey, approvalKey, actorId])

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
      if (rejected) {
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

  async function sendApproval(command: ApprovalCommand) {
    if (!enabled || !navigator.onLine || writing.current) return
    writing.current = true
    setBusy(true)
    setConfirmation(undefined)
    setNotice('')
    rememberPending(approvalKey, command)
    setPendingApproval(command)
    try {
      const { commandId, expectedRevision, proposalId } = command
      const response = await requestSalesApi(api, token, `${path}/${proposalId}/approve`, JSON.stringify({ commandId, expectedRevision }))
      const result = await response.json().catch(() => null) as Partial<ApprovalReceipt> & { code?: string } | null
      const rejected = approvalRejection(response.status, result?.code)
      if (rejected) {
        forgetPending(approvalKey, commandId)
        setPendingApproval(null)
        setNotice(rejected)
        reload()
      } else if ((response.status === 409 && result?.code === 'A_CLOSURE_ALREADY_APPROVED')
        || (response.ok && result?.status === 'APPROVED' && result.proposalId === proposalId
          && result.commandId === commandId && validRelease(command.quantity, result))) {
        setNotice(findingApproval)
        reload()
      } else {
        setNotice([401, 403].includes(response.status) ? 'Chưa đủ quyền xác nhận kết quả duyệt. Gửi lại cùng yêu cầu khi có quyền.' : unknownApproval)
      }
    } catch {
      setNotice(unknownApproval)
    } finally {
      writing.current = false
      setBusy(false)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!data?.canPropose || pending || pendingApproval || confirmation || busy) return
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
    {pendingApproval && <>
      <p>Yêu cầu duyệt đang chờ xác nhận · Mã đề nghị {pendingApproval.proposalId} · Mã yêu cầu {pendingApproval.commandId}</p>
      <div className="button-row">
        <Button disabled={busy} onClick={reload}>Tra cứu kết quả duyệt</Button>
        <Button disabled={!enabled || busy || !data} onClick={() => void sendApproval(pendingApproval)}>Gửi lại yêu cầu duyệt</Button>
      </div>
    </>}
    {loadError ? <div>
      <p role="alert">{loadError}</p>
      {!pending && !pendingApproval && <Button onClick={reload}>Tải lại đề nghị</Button>}
    </div> : !data && <p role="status">Đang tải đề nghị đóng…</p>}
    {data && <>
      <p>Còn phải giao {amount(data.scope.remaining)}</p>
      {data.proposals.length > 0 && <ul className="c1-rows" aria-label="Đề nghị đã ghi nhận">
        {data.proposals.map(proposal => <li key={proposal.proposalId} data-proposal-id={proposal.proposalId}>
          <div className="c3-source">
            <p><strong>{proposal.approval ? 'Đã duyệt' : 'Chờ duyệt'}</strong> · {amount(proposal.quantity)} · {proposal.maker} ·{' '}
              <time dateTime={proposal.recordedAt}>{timestamp.format(new Date(proposal.recordedAt))} ICT</time>
            </p>
            <p>Lý do {proposal.reason}</p>
            <p>Mã đề nghị {proposal.proposalId}</p>
            {proposal.approval ? <dl className="sales-facts">
              <dt>Người duyệt</dt><dd>{proposal.approval.checker}</dd>
              <dt>Thời điểm duyệt</dt><dd><time dateTime={proposal.approval.approvedAt}>
                {timestamp.format(new Date(proposal.approval.approvedAt))} ICT
              </time></dd>
              <dt>Đã giải phóng</dt><dd>{amount(proposal.approval.released)}</dd>
              <dt>Mã nguồn đóng phần còn lại</dt><dd><code>{proposal.approval.sourceId}</code></dd>
            </dl> : proposal.canApprove && (confirmation?.proposalId === proposal.proposalId ? <div>
              <p>Duyệt đóng {amount(confirmation.quantity)}.</p>
              <div className="button-row">
                <Button onClick={() => setConfirmation(undefined)}>Hủy</Button>
                <Button variant="primary" disabled={!enabled || busy || !!pending || !!pendingApproval || confirmation.expectedRevision !== data.scope.revision}
                  onClick={() => void sendApproval({ ...confirmation, commandId: crypto.randomUUID() })}>Xác nhận duyệt</Button>
              </div>
            </div> : <Button disabled={!enabled || busy || !!confirmation || !!pending || !!pendingApproval}
              onClick={() => setConfirmation({ proposalId: proposal.proposalId, quantity: proposal.quantity, expectedRevision: data.scope.revision })}>
              Duyệt đề nghị
            </Button>)}
          </div>
        </li>)}
      </ul>}
      {!pending && !pendingApproval && (data.canPropose ? <form aria-label={title} onSubmit={submit}>
        <fieldset className="sales-filter" disabled={!enabled || busy || !!confirmation}>
          <Field name="quantity" label={unit ? `Lượng đề nghị đóng (${unit})` : 'Lượng đề nghị đóng'} type="number"
            step="any" required />
          <Field name="reason" label="Lý do đề nghị đóng" maxLength={240} required />
          <Button variant="primary" type="submit">Gửi đề nghị</Button>
        </fieldset>
      </form> : <p>Hiện chưa thể gửi đề nghị cho nguồn này.</p>)}
    </>}
  </section>
}
