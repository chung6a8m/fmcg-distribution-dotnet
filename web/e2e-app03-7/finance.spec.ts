import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'
import { auth, open, submit, verifyReturnRefund } from './return-refund'

const sale = process.env.APP03_FINANCE_DELIVERY!
const accounting = '/api/synthetic/app02d4/accounting'
test.use({ serviceWorkers: 'block' })

test('owner-overview reads the shared sources with a distinct tenant grant', async ({ context, request }) => {
  const endpoint = '/api/synthetic/app03/owner-overview'
  const advances = '/api/synthetic/dev-f02/commands'
  const employee = await request.get('/api/synthetic/app03/session', { headers: auth('SOURCE') })
  const { actorId } = await employee.json()
  const advance = async (role: string, action: string, fields: object, evidenceKey = action) => {
    const result = await request.post(advances, { headers: auth(role), data: {
      commandId: crypto.randomUUID(), action, evidence: 'APP03-8-' + evidenceKey, ...fields,
    } })
    expect(result.ok(), await result.text()).toBe(true)
    return (await result.json()).id as string
  }
  const advanceId = await advance('FINANCE', 'ISSUE', { employeeId: actorId, amount: 500000 })
  const offer = await advance('SOURCE', 'RETURN_OFFER', { advanceId, amount: 200000 })
  await advance('CASHIER', 'RETURN_ACCEPT', { advanceId, referenceId: offer, amount: 200000, decisionReason: 'Đã kiểm đếm DEV' })

  const partial = await advance('SOURCE', 'EXPENSE_REQUEST', { advanceId, amount: 100000 }, 'EXPENSE-PARTIAL')
  const full = await advance('SOURCE', 'EXPENSE_REQUEST', { advanceId, amount: 20000 }, 'EXPENSE-FULL')
  await advance('FINANCE', 'EXPENSE_APPROVE', { advanceId, referenceId: partial, amount: 40000,
    decisionReason: 'Duyệt một phần DEV' }, 'APPROVE-PARTIAL')
  await advance('FINANCE', 'EXPENSE_APPROVE', { advanceId, referenceId: full, amount: 20000,
    decisionReason: 'Duyệt đủ DEV' }, 'APPROVE-FULL')
  const ownerResponse = await request.get(endpoint, { headers: auth('OWNER') })
  expect(ownerResponse.ok(), await ownerResponse.text()).toBe(true)
  const summary = (await ownerResponse.json()).metrics.find((m: { metric: string }) => m.metric === 'advance-expense-pending')
  expect(summary).toMatchObject({ unit: 'VND', known: 2, expected: 2, value: '60000.0000', sources: expect.arrayContaining([
    expect.objectContaining({ id: partial, relatedId: advanceId, value: '60000.0000', ownerRole: 'finance' }),
    expect.objectContaining({ id: full, relatedId: advanceId, value: '0.0000', state: 'APPROVED', ownerRole: null }),
  ]) })
  expect((await request.get(endpoint)).status()).toBe(401)
  expect((await request.get(endpoint, { headers: auth('SOURCE') })).status()).toBe(403)

  const owner = await open(context, 'OWNER', 'owner')
  await expect(owner.getByRole('heading', { name: 'Tổng quan Chủ NPP' })).toBeVisible()
  await expect(owner.getByRole('link', { name: 'Hàng đợi kế toán' })).toHaveCount(0)
  await owner.getByRole('button', { name: /Credit còn lại F08/ }).focus()
  await owner.keyboard.press('Enter')
  await expect(owner.getByRole('region', { name: 'Nguồn Credit còn lại F08' })).toBeFocused()
  await expect(owner.getByRole('table', { name: 'Nguồn Credit còn lại F08' })).toContainText('6.000')
  await expect(owner.getByRole('table', { name: 'Nguồn Credit còn lại F08' })).toContainText(sale)
  await owner.getByRole('button', { name: 'Đóng chi tiết' }).click()
  await expect(owner.getByRole('button', { name: /Credit còn lại F08/ })).toBeFocused()
  await owner.getByRole('button', { name: /Tạm ứng còn lại F02/ }).click()
  await expect(owner.getByRole('table', { name: 'Nguồn Tạm ứng còn lại F02' })).toContainText('240.000')
  await expect(owner.getByRole('table', { name: 'Nguồn Tạm ứng còn lại F02' })).toContainText(advanceId)
  await expect(owner.getByText('Chưa đủ dữ liệu', { exact: false }).first()).toBeVisible()
  await owner.getByRole('button', { name: /Khoản chi F02 chờ duyệt/ }).click()
  const expenseTable = owner.getByRole('table', { name: 'Nguồn Khoản chi F02 chờ duyệt' })
  await expect(expenseTable.getByRole('row').filter({ hasText: partial })).toContainText('60.000 VND')
  await expect(expenseTable.getByRole('row').filter({ hasText: partial })).toContainText('Duyệt khoản chi')
  await expect(expenseTable.getByRole('row').filter({ hasText: full })).toContainText('0 VND')
  await owner.setViewportSize({ width: 390, height: 844 })
  expect(await owner.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)

  const peer = await request.get(endpoint, { headers: auth('PEER_OWNER') })
  expect(peer.status()).toBe(200)
  const body = await peer.text()
  expect(body).not.toContain(sale)
  expect(body).not.toContain(advanceId)
  expect(body).not.toContain(partial)
  expect(body).not.toContain('advance-expense-pending')
  expect(JSON.parse(body)).toMatchObject({ tenantId: 'tenant-b' })
  expect((await request.post(endpoint, { headers: auth('OWNER'), data: {} })).status()).toBe(405)
  expect((await request.post(advances, { headers: auth('OWNER'), data: {
    commandId: crypto.randomUUID(), action: 'ISSUE', employeeId: actorId, amount: 1, evidence: 'BLOCK-OWNER',
  } })).status()).toBe(403)
})

test('refund from a shared delivery goes through QA, credit, verification and cashier payout', async ({ context, request }, info) => {
  await verifyReturnRefund(context, request, sale, 'APP03-7', true)

  const accountant = await open(context, 'ACCOUNTANT', 'finance')
  const sourceRow = accountant.locator(`li[data-delivery=${JSON.stringify(sale)}]`)
  await expect(sourceRow).toContainText('Chưa tiếp nhận · Chưa ghi sổ · Chưa đối soát')
  await sourceRow.getByRole('button').click()
  let transferRequest: { transportId: string } | undefined
  let originalReceipt: string | undefined
  await accountant.route('**' + accounting + '/' + sale + '/receive', async route => {
    transferRequest = route.request().postDataJSON()
    const response = await route.fetch()
    expect(response.ok()).toBeTruthy()
    originalReceipt = (await response.json()).id
    await route.abort()
  }, { times: 1 })
  await accountant.getByRole('button', { name: 'Tiếp nhận nguồn' }).click()
  await expect(accountant.getByText(/Chưa rõ kết quả ·/)).toBeVisible()
  await accountant.reload()
  await accountant.getByRole('button', { name: 'Tra cứu nguồn gốc' }).click()
  await expect(sourceRow).toContainText('Đã tiếp nhận · Chưa ghi sổ · Chưa đối soát')
  await expect(accountant.getByRole('status').filter({ hasText: 'Đã tiếp nhận ·' })).toBeVisible()
  expect(transferRequest?.transportId).toBeTruthy()
  const replayReceipt = await request.post(accounting + '/' + sale + '/receive', {
    headers: auth('ACCOUNTANT'), data: transferRequest,
  })
  expect((await replayReceipt.json()).id).toBe(originalReceipt)
  const unrelated = await request.get(accounting + '/' + sale + '/transfers/receive/' + crypto.randomUUID(), {
    headers: auth('ACCOUNTANT'),
  })
  expect(unrelated.status()).toBe(404)
  const peer = await request.get(accounting + '/queue', { headers: auth('PEER_ACCOUNTANT') })
  expect(peer.status(), await peer.text()).toBe(200)
  expect((await peer.json()).sources).toEqual([])
  expect((await request.get(accounting + '/queue', { headers: auth('SOURCE') })).status()).toBe(403)
  const otherRow = accountant.locator(`li[data-delivery]:not([data-delivery=${JSON.stringify(sale)}])`)
  await otherRow.getByRole('button').click()
  await accountant.getByRole('button', { name: 'Tiếp nhận nguồn' }).click()
  await expect(otherRow).toContainText('Đã tiếp nhận · Chưa ghi sổ · Chưa đối soát')
  await accountant.screenshot({ path: info.outputPath('receipt.png'), fullPage: true })
})

async function reconciliationWindow(page: Page, asOf: string) {
  await page.getByLabel('Từ (gồm)', { exact: true }).fill('2026-11-23T00:00:00+07:00')
  await page.getByLabel('Cutoff (loại trừ)', { exact: true }).fill('2026-11-24T00:00:00+07:00')
  await page.getByLabel('Đối soát tính đến').fill(asOf)
}

function deliveryRow(page: Page) {
  return page.locator(`li[data-delivery=${JSON.stringify(sale)}]`)
}

async function readQueue(page: Page, role: string) {
  const response = await page.request.get(accounting + '/queue', { headers: auth(role) })
  expect(response.status(), await response.text()).toBe(200)
  return response.json()
}

test('accounting preserves report provenance and marks reconciliation stale after posting', async ({ context }) => {
  const reconciler = await open(context, 'RECONCILER', 'finance')
  await reconciliationWindow(reconciler, '2026-11-24T08:59:00+07:00')
  let reportRequest: { requestId: string; from: string; cutoff: string; asOf: string } | undefined
  await reconciler.route('**' + accounting + '/reconcile', async route => {
    reportRequest = route.request().postDataJSON()
    expect((await route.fetch()).ok()).toBeTruthy()
    await route.abort()
  }, { times: 1 })
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  await expect(reconciler.getByText(/Chưa rõ kết quả đối soát ·/)).toBeVisible()
  await reconciler.reload()
  await reconciler.getByRole('button', { name: 'Tra cứu báo cáo gốc' }).click()
  expect(reportRequest?.requestId).toBeTruthy()
  await expect(reconciler.getByText(reportRequest!.requestId, { exact: true })).toBeVisible()
  const replay = await reconciler.request.post(accounting + '/reconcile', { headers: auth('RECONCILER'), data: reportRequest })
  expect(replay.status(), await replay.text()).toBe(200)
  expect((await replay.json()).report.id).toBe(reportRequest!.requestId)
  const conflict = await reconciler.request.post(accounting + '/reconcile', { headers: auth('RECONCILER'),
    data: { ...reportRequest, from: '2026-11-22T00:00:00+07:00' } })
  expect(conflict.status()).toBe(409)
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText('POSTING_MISSING')

  const accountant = await open(context, 'ACCOUNTANT', 'finance')
  const sourceRow = deliveryRow(accountant)
  await sourceRow.getByRole('button').click()
  await accountant.getByLabel('Kịch bản ghi sổ DEV').selectOption('wrong-amount')
  await accountant.getByRole('button', { name: 'Ghi sổ nguồn' }).click()
  await expect(sourceRow).toContainText('Đã tiếp nhận · Đã ghi sổ · PENDING')
  const otherRow = accountant.locator(`li[data-delivery]:not([data-delivery=${JSON.stringify(sale)}])`)
  await otherRow.getByRole('button').click()
  await accountant.getByLabel('Kịch bản ghi sổ DEV').selectOption('')
  await accountant.getByRole('button', { name: 'Ghi sổ nguồn' }).click()
  await expect(otherRow).toContainText('Đã tiếp nhận · Đã ghi sổ · PENDING')

  await reconciler.getByRole('button', { name: 'Tải lại hàng đợi' }).click()
  const row = deliveryRow(reconciler)
  await expect(row).toContainText('Đối soát trước khi nguồn thay đổi')
  await expect(row).toContainText('08:59:00')
  await expect(row).toContainText(reportRequest!.requestId)
  await row.getByRole('button', { name: 'Mở báo cáo nguồn' }).click()
  await expect(reconciler.getByText(reportRequest!.requestId, { exact: true })).toBeVisible()
  await expect(sourceRow.getByRole('button', { name: 'Mở báo cáo nguồn' })).toHaveCount(0)

  await reconciliationWindow(reconciler, '2026-11-24T09:00:00+07:00')
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  await expect(row).toContainText('RECONCILED_DIFF')
  await expect(row).toContainText('09:00:00')
  await expect(row).not.toContainText('Đối soát trước khi nguồn thay đổi')
  const options = reconciler.getByLabel('Báo cáo đối soát', { exact: true }).locator('option')
  await expect(options.filter({ hasText: '08:59:00' })).toHaveCount(1)
  await expect(options.filter({ hasText: '09:00:00' })).toHaveCount(1)
})

test('approval preserves one discrepancy across repeated reports and loses the partner acknowledgement', async ({ context }) => {
  const reconciler = await open(context, 'RECONCILER', 'finance')
  const casePicker = reconciler.getByLabel('Hồ sơ sai lệch', { exact: true })
  await casePicker.selectOption({ index: 1 })
  const caseId = await casePicker.inputValue()
  await reconciliationWindow(reconciler, '2026-11-25T09:00:00+07:00')
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText(caseId)
  await expect(casePicker.locator('option')).toHaveCount(2)
  await reconciler.getByLabel('Từ (gồm)', { exact: true }).fill('2026-11-22T00:00:00+07:00')
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText(caseId)
  await expect(casePicker.locator('option')).toHaveCount(2)

  const maker = await open(context, 'MAKER', 'finance')
  await maker.getByLabel('Hồ sơ sai lệch', { exact: true }).selectOption(caseId)
  await maker.route('**' + accounting + '/reports/*/corrections', async route => {
    expect((await route.fetch()).ok()).toBeTruthy()
    await route.abort()
  }, { times: 1 })
  await maker.getByRole('button', { name: 'Đề nghị điều chỉnh' }).click()
  await expect(maker.getByText(/Chưa rõ kết quả điều chỉnh/)).toBeVisible()
  await maker.reload()
  await maker.getByRole('button', { name: 'Tra cứu điều chỉnh gốc' }).click()
  await expect(maker.getByText(/^PROPOSED ·/)).toBeVisible()

  const checker = await open(context, 'CHECKER', 'finance')
  const proposal = checker.getByLabel('Đề nghị điều chỉnh', { exact: true })
  await proposal.selectOption({ index: 1 })
  const correctionId = await proposal.inputValue()
  const unresolved = await checker.request.post(accounting + '/corrections/' + correctionId + '/approve', {
    headers: auth('CHECKER'), data: { recoverOnly: true },
  })
  expect(unresolved.status()).toBe(404)
  await checker.route('**' + accounting + '/corrections/' + correctionId + '/approve', async route => {
    const response = await route.fetch({ postData: { fault: 'after-commit' } })
    expect(response.status()).toBe(503)
    await route.fulfill({ response })
  }, { times: 1 })
  await checker.getByRole('button', { name: 'Duyệt điều chỉnh' }).click()
  await expect(checker.getByText(/Chưa rõ kết quả điều chỉnh/)).toBeVisible()
  await checker.reload()
  await expect(checker.getByText(/Chưa rõ kết quả điều chỉnh/)).toBeVisible()
  expect((await readQueue(checker, 'CHECKER')).corrections[0]).toMatchObject({ id: correctionId, postingId: null })
  const pending = await checker.evaluate(() => Object.fromEntries(
    Object.entries(sessionStorage).filter(([key]) => key.startsWith('app03.accounting.v1.') && key.endsWith('.correction')),
  ))
  expect(Object.keys(pending)).toHaveLength(1)
  await writeFile(join(process.env.APP03_FINANCE_EVIDENCE_DIR!, 'pending-approval.json'), JSON.stringify(pending))
})

test('recovery links the committed correction after NextDay and finds the original case closure', async ({ context }, info) => {
  const checker = await open(context, 'CHECKER', 'finance')
  const saved = JSON.parse(process.env.APP03_FINANCE_PENDING!)
  await checker.evaluate(entries => {
    for (const [key, value] of Object.entries(entries)) sessionStorage.setItem(key, value as string)
  }, saved)
  await checker.reload()
  await expect(checker.getByText('Ngày nghiệp vụ')).toContainText('01/12/2026')
  await checker.getByRole('button', { name: 'Tra cứu điều chỉnh gốc' }).click()
  await expect(checker.getByText(/^CORRECTION_POSTED ·/)).toBeVisible()
  const queue = await readQueue(checker, 'CHECKER')
  expect(queue.corrections).toHaveLength(1)
  expect(queue.corrections[0].postingId).toBeTruthy()

  const reconciler = await open(context, 'RECONCILER', 'finance')
  await reconciliationWindow(reconciler, '2026-12-01T09:00:00+07:00')
  const reconciliationResponse = reconciler.waitForResponse(response => response.url().endsWith(accounting + '/reconcile'))
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  const fresh = await reconciliationResponse
  expect(fresh.ok(), await fresh.text()).toBeTruthy()
  const reportId = (await fresh.json()).report.id
  await expect(reconciler.getByLabel('Báo cáo đối soát', { exact: true })).toHaveValue(reportId)
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText('RECONCILED_OK')
  const casePicker = reconciler.getByLabel('Hồ sơ sai lệch', { exact: true })
  const caseId = queue.corrections[0].caseId
  const historical = await readQueue(reconciler, 'RECONCILER')
  const originalCases = historical.cases as { id: string; canonicalCaseId: string; reportId: string; closedReportId: string | null }[]
  expect(originalCases).toHaveLength(3)
  const alias = originalCases.find(row => row.id !== caseId && historical.reports.some(
    (candidate: { id: string; fromUtc: string }) => candidate.id === row.reportId && Date.parse(candidate.fromUtc) === Date.parse('2026-11-22T00:00:00+07:00'),
  ))!
  expect(alias).toMatchObject({ canonicalCaseId: caseId, closedReportId: null })
  const maker = await open(context, 'MAKER', 'finance')
  const makerCases = maker.getByLabel('Hồ sơ sai lệch', { exact: true }).locator('option')
  await expect(makerCases).toHaveCount(2)
  await expect(makerCases.nth(1)).toHaveAttribute('value', caseId)
  await casePicker.selectOption(caseId)
  await reconciler.route('**' + accounting + '/cases/' + caseId + '/close', async route => {
    expect((await route.fetch()).ok()).toBeTruthy()
    await route.abort()
  }, { times: 1 })
  await reconciler.getByRole('button', { name: 'Đóng hồ sơ sai lệch' }).click()
  await expect(reconciler.getByText(/Chưa rõ kết quả điều chỉnh/)).toBeVisible()
  await reconciler.reload()
  await expect(casePicker.locator('option')).toHaveCount(2)
  await reconciler.getByRole('button', { name: 'Tra cứu điều chỉnh gốc' }).click()
  await expect(reconciler.getByText(/^CLOSED ·/)).toContainText(reportId)
  await expect(reconciler.getByRole('region', { name: 'Hồ sơ đã đóng' })).toContainText(caseId)
  const afterCanonical = (await readQueue(reconciler, 'RECONCILER')).cases
  expect(afterCanonical.find((row: { id: string }) => row.id === caseId)).toMatchObject({ closedReportId: reportId })
  expect(afterCanonical.find((row: { id: string }) => row.id === alias.id)).toMatchObject({ closedReportId: null })
  const wrongWindow = await reconciler.request.post(accounting + '/cases/' + alias.id + '/close', {
    headers: auth('RECONCILER'), data: { reportId },
  })
  expect(wrongWindow.status()).toBe(409)
  expect(await wrongWindow.json()).toMatchObject({ status: 'RECONCILIATION_REQUIRED' })

  await reconciliationWindow(reconciler, '2026-12-01T09:00:00+07:00')
  await reconciler.getByLabel('Từ (gồm)', { exact: true }).fill('2026-11-22T00:00:00+07:00')
  const aliasResponse = reconciler.waitForResponse(response => response.url().endsWith(accounting + '/reconcile'))
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  const aliasFresh = await aliasResponse
  expect(aliasFresh.ok(), await aliasFresh.text()).toBeTruthy()
  const aliasReportId = (await aliasFresh.json()).report.id
  expect(aliasReportId).not.toBe(reportId)
  await expect(reconciler.getByLabel('Báo cáo đối soát', { exact: true })).toHaveValue(aliasReportId)
  await casePicker.selectOption(alias.id)
  await expect(reconciler.getByText('Bút toán điều chỉnh theo hồ sơ ' + caseId, { exact: true })).toBeVisible()
  await reconciler.getByRole('button', { name: 'Đóng hồ sơ sai lệch' }).click()
  await expect(reconciler.getByText(/^CLOSED ·/)).toContainText(aliasReportId)
  await expect(casePicker.locator('option')).toHaveCount(1)
  await expect(reconciler.getByRole('region', { name: 'Hồ sơ đã đóng' })).toContainText(alias.id)
  const finalQueue = await readQueue(reconciler, 'RECONCILER')
  expect(finalQueue.cases).toHaveLength(3)
  expect(finalQueue.cases.find((row: { id: string }) => row.id === alias.id)).toMatchObject({
    canonicalCaseId: caseId, reportId: alias.reportId, closedReportId: aliasReportId,
  })
  expect(finalQueue.corrections).toHaveLength(1)
  const canonical = originalCases.find(row => row.id === caseId)!
  const historicalAliasReport = await reconciler.request.get(accounting + '/reports/' + alias.reportId, {
    headers: auth('RECONCILER'),
  })
  expect((await historicalAliasReport.json()).lines.find((line: { deliveryId: string }) => line.deliveryId === sale).caseId).toBe(alias.id)
  const oldProposal = await reconciler.request.post(accounting + '/reports/' + canonical.reportId + '/corrections', {
    headers: auth('MAKER'), data: { deliveryId: sale },
  })
  expect(oldProposal.status(), await oldProposal.text()).toBe(200)
  expect(await oldProposal.json()).toMatchObject({ status: 'PROPOSED', id: queue.corrections[0].id })
  await reconciler.screenshot({ path: info.outputPath('reconciliation.png'), fullPage: true })
})

test('cost adjustment uses the same locked source with a separate QA checker', async ({ context }, info) => {
  const finance = await open(context, 'FINANCE', 'cost-adjustments')
  await finance.getByLabel('Lần giao nguồn').selectOption(sale)
  await submit(finance, 'Đề nghị điều chỉnh', 'APP03-7-F06-PROPOSE', ['Giá vốn sau điều chỉnh (VND)', '6200'])
  await expect(finance.getByText('Đã ghi đề nghị điều chỉnh.')).toBeVisible()
  const checker = await open(context, 'QA', 'cost-adjustments')
  await checker.getByLabel('Đề nghị chờ duyệt').selectOption({ index: 1 })
  await submit(checker, 'Duyệt điều chỉnh', 'APP03-7-F06-APPROVE')
  await expect(checker.getByText('Đã ghi điều chỉnh giá vốn.')).toBeVisible()
  await checker.screenshot({ path: info.outputPath('cost-adjustment.png'), fullPage: true })
})
