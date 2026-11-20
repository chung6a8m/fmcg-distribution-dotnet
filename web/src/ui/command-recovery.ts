import { parseDemoQuantity } from './decimal.ts'

// UI-03 is deliberately browser-only. This state machine is NOT an API contract,
// permission oracle, idempotency implementation or a durable business command queue.
export type PermissionFixture = 'write' | 'read' | 'none'
export type ConnectionFixture = 'online' | 'offline'
export type Phase = 'draft' | 'checked' | 'pending' | 'unknown' | 'ack' | 'waiting' | 'posted' | 'rejected' | 'conflict'
export type LookupFixture = 'no-result' | 'ack' | 'waiting' | 'posted' | 'rejected' | 'conflict'

export type SentRequest = Readonly<{
  id: string
  quantity: string
  revision: string
  signature: string
}>

export type Workflow = {
  phase: Phase
  id: string
  sequence: number
  quantity: string
  revision: string
  permission: PermissionFixture
  connection: ConnectionFixture
  checkedSignature: string | null
  sent: SentRequest | null
  intakeAcknowledged: boolean
  history: string[]
}

export type WorkflowEvent =
  | { type: 'quantity'; value: string }
  | { type: 'revision'; value: string }
  | { type: 'permission'; value: PermissionFixture }
  | { type: 'connection'; value: ConnectionFixture }
  | { type: 'check' }
  | { type: 'send' }
  | { type: 'lost-ack' }
  | { type: 'lookup'; outcome: LookupFixture }
  | { type: 'next' }

const phases: Phase[] = ['draft', 'checked', 'pending', 'unknown', 'ack', 'waiting', 'posted', 'rejected', 'conflict']
const permissions: PermissionFixture[] = ['write', 'read', 'none']
const connections: ConnectionFixture[] = ['online', 'offline']

function requestId(sequence: number): string {
  return 'UI03-LOCAL-' + String(sequence).padStart(4, '0')
}

export function newWorkflow(sequence = 1): Workflow {
  return {
    phase: 'draft', id: requestId(sequence), sequence, quantity: '2', revision: 'rev-07',
    permission: 'write', connection: 'online', checkedSignature: null, sent: null,
    intakeAcknowledged: false, history: []
  }
}

export function isLocked(workflow: Workflow): boolean {
  return workflow.sent !== null
}

export function canCheck(workflow: Workflow): boolean {
  return !isLocked(workflow) && workflow.permission === 'write' &&
    workflow.connection === 'online' && parseDemoQuantity(workflow.quantity) !== null
}

// The signature demonstrates local payload/version consistency, NOT cryptographic integrity.
export function draftSignature(workflow: Workflow): string {
  return [workflow.id, workflow.quantity, workflow.revision].join('|')
}

export function canSend(workflow: Workflow): boolean {
  return workflow.phase === 'checked' && canCheck(workflow) &&
    workflow.checkedSignature === draftSignature(workflow)
}

export function canLookup(workflow: Workflow): boolean {
  return isLocked(workflow) && ['pending', 'unknown', 'ack', 'waiting'].includes(workflow.phase) &&
    workflow.permission !== 'none' && workflow.connection === 'online'
}

function addHistory(workflow: Workflow, entry: string): string[] {
  return [...workflow.history, entry].slice(-24)
}

export function workflowReducer(workflow: Workflow, event: WorkflowEvent): Workflow {
  switch (event.type) {
    case 'quantity':
      if (isLocked(workflow)) return workflow
      return { ...workflow, quantity: event.value.slice(0, 24), phase: 'draft', checkedSignature: null }
    case 'revision':
      if (isLocked(workflow) || !['rev-07', 'rev-08'].includes(event.value)) return workflow
      return { ...workflow, revision: event.value, phase: 'draft', checkedSignature: null }
    case 'permission':
      return {
        ...workflow, permission: event.value,
        phase: workflow.phase === 'checked' ? 'draft' : workflow.phase,
        checkedSignature: null
      }
    case 'connection':
      return {
        ...workflow, connection: event.value,
        phase: workflow.phase === 'checked' ? 'draft' : workflow.phase,
        checkedSignature: null
      }
    case 'check':
      if (!canCheck(workflow)) return workflow
      return { ...workflow, phase: 'checked', checkedSignature: draftSignature(workflow),
        history: addHistory(workflow, 'Đã kiểm tra điều kiện UI giả lập; commit vẫn cần kiểm lại ở server.') }
    case 'send':
      if (!canSend(workflow)) return workflow
      return {
        ...workflow, phase: 'pending',
        sent: { id: workflow.id, quantity: workflow.quantity, revision: workflow.revision,
          signature: draftSignature(workflow) },
        checkedSignature: null,
        history: addHistory(workflow, 'Đã bấm gửi mô phỏng; chưa có ACK hoặc business outcome.')
      }
    case 'lost-ack':
      if (workflow.phase !== 'pending') return workflow
      return { ...workflow, phase: 'unknown',
        history: addHistory(workflow, 'Mất phản hồi giả lập; UNKNOWN không đồng nghĩa bị từ chối.') }
    case 'lookup': {
      if (!canLookup(workflow)) return workflow
      const outcome = event.outcome
      if (outcome === 'no-result') return {
        ...workflow,
        phase: workflow.intakeAcknowledged ? workflow.phase : 'unknown',
        history: addHistory(workflow, 'Tra cùng ID: chưa có kết quả mới; không gửi lại.')
      }
      if (outcome === 'ack') return {
        ...workflow, phase: workflow.phase === 'waiting' ? 'waiting' : 'ack',
        intakeAcknowledged: true,
        history: addHistory(workflow, 'Tra cùng ID: ACK tiếp nhận bền (mô phỏng), chưa POSTED.')
      }
      if (outcome === 'waiting') return {
        ...workflow, phase: 'waiting', intakeAcknowledged: true,
        history: addHistory(workflow, 'Tra cùng ID: đã tiếp nhận, đang chờ phụ thuộc (mô phỏng).')
      }
      if (outcome === 'posted') return {
        ...workflow, phase: 'posted', intakeAcknowledged: true,
        history: addHistory(workflow, 'Tra cùng ID: POSTED (chỉ kết quả giả lập, chưa đối soát).')
      }
      if (outcome === 'rejected') return {
        ...workflow, phase: 'rejected',
        history: addHistory(workflow, 'Tra cùng ID: rejected trước hiệu ứng (mô phỏng).')
      }
      return { ...workflow, phase: 'conflict',
        history: addHistory(workflow, 'Tra cùng ID: nội dung khác bản gốc; khóa gửi, chuyển xử lý conflict.') }
    }
    case 'next':
      // Unknown, pending or identity conflict can NEVER be cleared to evade reconciliation.
      if (!['posted', 'rejected'].includes(workflow.phase)) return workflow
      return { ...newWorkflow(workflow.sequence + 1),
        permission: workflow.permission, connection: workflow.connection }
  }
}

// SessionStorage is a convenience for a synthetic lab only. Never trust it as
// evidence of server ACK/POSTED, current identity, policy or write permission.
export function tryRestoreWorkflow(raw: string | null): Workflow | null {
  if (!raw || raw.length > 10000) return null
  try {
    const item: unknown = JSON.parse(raw)
    if (!item || typeof item !== 'object') return null
    const value = item as Partial<Workflow> & { schema?: unknown }
    if (value.schema !== 1 || !phases.includes(value.phase as Phase) ||
      !permissions.includes(value.permission as PermissionFixture) ||
      !connections.includes(value.connection as ConnectionFixture) ||
      typeof value.sequence !== 'number' || !Number.isInteger(value.sequence) || value.sequence < 1 || value.sequence > 9999 ||
      value.id !== requestId(value.sequence!) ||
      typeof value.quantity !== 'string' || value.quantity.length > 24 ||
      !['rev-07', 'rev-08'].includes(value.revision ?? '') ||
      typeof value.intakeAcknowledged !== 'boolean' ||
      !Array.isArray(value.history) || value.history.length > 24 ||
      !value.history.every(h => typeof h === 'string' && h.length < 200)) return null
    // A persisted browser fixture must not invent an ACK on a draft or forget a known ACK.
    // UNKNOWN means no durable intake ACK is established in the state machine.
    // Do not restore a contradictory browser snapshot as business truth.
    if ((['draft', 'checked', 'pending', 'unknown'].includes(value.phase!) && value.intakeAcknowledged) ||
      (['ack', 'waiting', 'posted'].includes(value.phase!) && !value.intakeAcknowledged)) return null
    const sent = value.sent
    if (value.phase !== 'draft' && value.phase !== 'checked') {
      if (!sent || typeof sent !== 'object' || sent.id !== value.id ||
        sent.quantity !== value.quantity || sent.revision !== value.revision ||
        sent.signature !== [sent.id, sent.quantity, sent.revision].join('|')) return null
    } else if (sent !== null) return null
    if (value.phase === 'checked' &&
      value.checkedSignature !== [value.id, value.quantity, value.revision].join('|')) return null
    return {
      phase: value.phase!, id: value.id!, sequence: value.sequence!, quantity: value.quantity!,
      revision: value.revision!, permission: value.permission!, connection: value.connection!,
      checkedSignature: value.phase === 'checked' ? value.checkedSignature! : null,
      sent: value.phase === 'draft' || value.phase === 'checked' ? null : sent!,
      intakeAcknowledged: value.intakeAcknowledged!, history: value.history
    }
  } catch {
    return null
  }
}

