import { expect, type APIRequestContext, type BrowserContext } from '@playwright/test'
import { auth, open } from '../e2e-app03-7/return-refund'
import { app, get, post, type Closure, type Work } from './fixture'

type Assignment = {
  followUpId: string; assignee: string; dueAt: string; commandId: string; actorId: string; reason: string
}
type Row = {
  tripId: string; closureId: string; sourceKind: string; sourceId: string; relatedId: string; status: string
  overdue: boolean; closingAssignment: Assignment; assignment: Assignment; assignmentRevision: string; assignmentHistory: Assignment[]
  reconciliationReportId: string | null; canReassign: boolean; completionSourceIds: string[]
}
const path = app + '/work'
const backup = 'app05-backup-assignee'

async function tripWork(request: APIRequestContext, tripId: string, query = '', role = 'OPS') {
  const board = await get<{ work: Row[] }>(request, path + query, role)
  return board.work.filter(item => item.tripId === tripId)
}

export async function verifyBoardReassignment(context: BrowserContext, request: APIRequestContext,
  receipt: Closure, accountingWork: Work) {
  const before = await tripWork(request, receipt.tripId, '?status=OPEN')
  expect(before).toHaveLength(2)
  const a = before.find(item => item.sourceKind === 'A_REMAINDER')!
  expect(a).toMatchObject({ closureId: receipt.closureId, overdue: false, canReassign: true })
  const previous = a.assignment
  const dueAt = '2026-11-24T09:15:00+07:00'
  const intent = { commandId: crypto.randomUUID(), expectedAssignmentRevision: a.assignmentRevision,
    assignee: backup, dueAt, reason: 'Đổi hạn để điều phối chuyến giao tiếp' }
  const reassign = app + '/' + receipt.tripId + '/work/' + previous.followUpId + '/reassign'
  for (const role of ['ASSIGNEE', 'BACKUP_ASSIGNEE', 'PEER_OPS']) {
    const denied = await request.post(reassign, { headers: auth(role), data: intent })
    expect(denied.status(), await denied.text()).toBe(403)
  }

  const board = await open(context, 'OPS', 'app05-work')
  const row = board.getByTestId('app05-work-' + previous.followUpId)
  await row.getByRole('button', { name: 'Đổi người hoặc hạn xử lý' }).press('Enter')
  const assignee = row.getByLabel('Người phụ trách mới')
  await expect(assignee).toBeFocused()
  await assignee.selectOption(backup)
  await row.getByLabel('Hạn xử lý (ICT)').fill('2026-11-24T09:15')
  await row.getByLabel('Lý do thay đổi').fill(intent.reason)
  const response = board.waitForResponse(result => result.url().endsWith(reassign) && result.request().method() === 'POST')
  await row.getByRole('button', { name: 'Lưu phân công' }).press('Enter')
  const saved = await response
  expect(saved.ok(), await saved.text()).toBe(true)
  const data = saved.request().postDataJSON() as typeof intent
  expect(data).toEqual({ ...intent, commandId: expect.any(String) })
  const applied = await saved.json()
  expect(applied).toEqual({ followUpId: previous.followUpId })
  await expect(row.getByRole('button', { name: 'Đổi người hoặc hạn xử lý' })).toBeVisible()
  await expect(row).toContainText(backup)
  await row.getByText('Lịch sử phân công', { exact: true }).press('Enter')
  const audit = row.locator('details').filter({ hasText: 'Lịch sử phân công' })
  await expect(audit.getByRole('listitem').last()).toContainText(intent.reason)
  await board.close()
  expect(await post(request, reassign, data, 'OPS')).toEqual(applied)
  for (const [payload, code] of [
    [{ ...data, reason: 'Lý do đã thay đổi' }, 'COMMAND_PAYLOAD_CONFLICT'],
    [{ ...data, commandId: crypto.randomUUID(), dueAt: '2026-11-24T10:00:00+07:00' }, 'WORK_ASSIGNMENT_CHANGED'],
  ] as const) {
    const response = await request.post(reassign, { headers: auth('OPS'), data: payload })
    expect(response.status(), await response.text()).toBe(409)
    expect(await response.json()).toEqual({ code })
  }

  const updated = await tripWork(request, receipt.tripId,
    '?assignee=' + encodeURIComponent(backup) + '&dueBefore=' + encodeURIComponent('2026-11-24T09:16:00+07:00'))
  expect(updated).toHaveLength(1)
  expect(updated[0]).toMatchObject({ sourceId: a.sourceId, closingAssignment: previous,
    assignment: { commandId: data.commandId, assignee: backup, reason: data.reason, actorId: previous.actorId } })
  expect(Date.parse(updated[0].assignment.dueAt)).toBe(Date.parse(dueAt))
  expect(updated[0].assignmentHistory.at(-2)).toEqual(previous)
  expect(updated[0].assignmentHistory.at(-1)).toEqual(updated[0].assignment)
  expect(await tripWork(request, receipt.tripId, '?dueBefore=' + encodeURIComponent(dueAt))).toEqual([])
  expect(await tripWork(request, receipt.tripId, '', 'PEER_OPS')).toEqual([])
  expect(await tripWork(request, receipt.tripId, '', 'ASSIGNEE')).toEqual([expect.objectContaining({ sourceId: accountingWork.sourceId })])
  expect(await tripWork(request, receipt.tripId, '', 'BACKUP_ASSIGNEE')).toEqual([expect.objectContaining({ sourceId: a.sourceId })])
  expect(await tripWork(request, receipt.tripId, '?status=COMPLETED')).toEqual([])
  expect(await get(request, app + '/' + receipt.tripId + '/closure')).toEqual({ closure: receipt, currentOpenWorkCount: 2 })
  expect(before.find(item => item.sourceId === accountingWork.sourceId)).toMatchObject({ relatedId: accountingWork.relatedId })
}

export async function verifyBoardReconciliation(request: APIRequestContext, receipt: Closure, source: Work, reportId: string) {
  const completed = await tripWork(request, receipt.tripId, '?status=COMPLETED')
  expect(completed).toHaveLength(1)
  expect(completed[0]).toMatchObject({ sourceId: source.sourceId, relatedId: source.relatedId, status: 'COMPLETED',
    reconciliationReportId: reportId, canReassign: false, overdue: false })
  expect(await tripWork(request, receipt.tripId, '?status=OPEN', 'ASSIGNEE')).toEqual([])
  expect(await tripWork(request, receipt.tripId, '?status=OPEN', 'BACKUP_ASSIGNEE')).toHaveLength(1)
}

export async function verifyOverdueWork(request: APIRequestContext, tripId: string) {
  const open = await tripWork(request, tripId, '?status=OPEN', 'BACKUP_ASSIGNEE')
  expect(open).toEqual([expect.objectContaining({
    sourceKind: 'A_REMAINDER', overdue: true, successorTrips: [],
    aLine: expect.objectContaining({ remaining: 15, held: 15, unit: 'EA' }),
  })])
}

export async function verifySuccessorBoard(request: APIRequestContext, origin: string, line: string,
  successor: string, deliveries: string[]) {
  const complete = await tripWork(request, origin, '?status=COMPLETED', 'BACKUP_ASSIGNEE')
  expect(complete).toEqual([expect.objectContaining({
    sourceKind: 'A_REMAINDER', sourceId: line, overdue: false, canReassign: false,
    aLine: expect.objectContaining({ remaining: 0, held: 0, delivered: 40 }),
    successorTrips: [expect.objectContaining({ tripId: successor })],
    completionSourceIds: expect.arrayContaining(deliveries),
  })])
  expect(complete[0].completionSourceIds).toHaveLength(deliveries.length)
}
