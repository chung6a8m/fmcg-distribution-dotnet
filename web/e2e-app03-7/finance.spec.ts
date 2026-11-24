import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type BrowserContext, type Page } from '@playwright/test'

const sale = process.env.APP03_FINANCE_DELIVERY!
const accounting = '/api/synthetic/app02d4/accounting'
const refund = '/api/synthetic/dev-f08'
const auth = (role: string) => ({ Authorization: 'Bearer ' + process.env['APP03_' + role + '_TOKEN'] })
test.use({ serviceWorkers: 'block' })

async function open(context: BrowserContext, role: string, path: string) {
  const page = await context.newPage()
  await page.goto('/work/' + path)
  await page.getByLabel('Mã truy cập DEV').fill(process.env['APP03_' + role + '_TOKEN']!)
  await page.getByRole('button', { name: 'Kết nối' }).click()
  await expect(page.getByRole('navigation', { name: 'Công việc DEV' })).toBeVisible()
  return page
}

async function submit(page: Page, action: string, document: string, quantity?: [string, string]) {
  if (quantity) await page.getByLabel(quantity[0], { exact: true }).fill(quantity[1])
  await page.getByLabel('Lý do', { exact: true }).fill('Hồ sơ APP-03.7')
  if (document) await page.getByLabel(/Chứng từ.*DEV/).fill(document)
  await page.getByRole('button', { name: action, exact: true }).click()
}

test('refund from a shared delivery goes through QA, credit, verification and cashier payout', async ({ context, request }, info) => {
  const source = await open(context, 'SOURCE', 'returns?delivery=' + sale)
  await expect(source.getByLabel('Nguồn giao và lô')).toHaveValue(new RegExp('^' + sale + ':'))
  await submit(source, 'Đề nghị trả hàng', '', ['Số lượng trả', '1'])
  await expect(source.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  const receiver = await open(context, 'RECEIVER', 'returns')
  const caseSelection = (page: Page) => page.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true })
  await caseSelection(receiver).selectOption({ index: 1 })
  const caseId = await caseSelection(receiver).inputValue()
  await submit(receiver, 'Nhận vào cách ly', 'APP03-7-RETURN-RECEIVE', ['Số lượng thực nhận', '1'])
  await expect(receiver.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const finance = await open(context, 'FINANCE', 'returns?source=' + caseId)
  await expect(caseSelection(finance)).toHaveValue(caseId)
  await submit(finance, 'Duyệt trả hàng', 'APP03-7-RETURN-APPROVE')
  await expect(finance.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const qa = await open(context, 'QA', 'returns')
  await caseSelection(qa).selectOption(caseId)
  await submit(qa, 'Giải phóng hàng', 'APP03-7-RETURN-RELEASE')
  await expect(qa.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  await source.goto('/work/returns')
  const giftOption = source.getByLabel('Nguồn giao và lô').locator('option').filter({ hasText: 'R-05' }).first()
  await expect(giftOption).toBeAttached()
  await source.getByLabel('Nguồn giao và lô').selectOption(await giftOption.getAttribute('value') ?? '')
  await submit(source, 'Đề nghị trả hàng', '', ['Số lượng trả', '1'])
  await expect(source.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  await finance.getByRole('link', { name: 'Credit và hoàn tiền' }).click()
  await expect(finance.getByText('1 EA · Nguồn ngoài phạm vi F08 v1')).toBeVisible()
  await expect(finance.getByText('1 EA · Chờ đối soát đủ tiền')).toBeVisible()
  await qa.getByRole('link', { name: 'Credit và hoàn tiền' }).click()
  await qa.getByLabel('Lần giao nguồn').selectOption(sale)
  await submit(qa, 'Xác minh tiền vào', 'APP03-7-F08-PAYMENT', ['Số tiền thực nhận (VND)', '20000'])
  await expect(qa.getByText('Đã xác minh chứng từ tiền vào.')).toBeVisible()
  await finance.getByRole('button', { name: 'Tải lại danh sách' }).click()
  await finance.getByLabel('Lần giao nguồn').selectOption(sale)
  await submit(finance, 'Đối soát tiền vào', 'APP03-7-F08-PAYMENT', ['Số tiền đối soát (VND)', '20000'])
  await expect(finance.getByText('Đã đối soát tiền vào.')).toBeVisible()
  await finance.getByLabel('Thao tác hoàn tiền').selectOption('CREDIT')
  await finance.getByLabel('Hồ sơ trả đã giải phóng').selectOption(caseId)
  await submit(finance, 'Ghi credit', 'APP03-7-F08-CREDIT')
  await expect(finance.getByText('Đã ghi credit.', { exact: true })).toBeVisible()
  const credits = async () => {
    const response = await request.get(refund + '/credits', { headers: auth('FINANCE') })
    expect(response.status(), await response.text()).toBe(200)
    return response.json() as Promise<{ id: string; available: number }[]>
  }
  const [credit] = await credits()
  expect(credit.available).toBe(10000)
  await finance.getByLabel('Thao tác hoàn tiền').selectOption('PROPOSE')
  await finance.getByLabel('Nguồn credit').selectOption(credit.id)
  await submit(finance, 'Đề nghị chi hoàn tiền', 'APP03-7-F08-PROPOSE', ['Số tiền đề nghị (VND)', '4000'])
  await expect(finance.getByText('Đã ghi đề nghị chi hoàn tiền.')).toBeVisible()

  const checker = await open(context, 'REFUND_CHECKER', 'refunds')
  await checker.getByLabel('Thao tác hoàn tiền').selectOption('APPROVE')
  await checker.getByLabel('Đề nghị chi', { exact: true }).selectOption({ index: 1 })
  const refundId = await checker.getByLabel('Đề nghị chi', { exact: true }).inputValue()
  const selfApproval = await request.post(refund + '/commands', { headers: auth('FINANCE'), data: {
    commandId: crypto.randomUUID(), action: 'APPROVE', refundId, reason: 'Cùng người', evidence: 'APP03-7-SELF',
  } })
  expect(await selfApproval.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'F08_SEPARATION_REQUIRED' })
  await submit(checker, 'Duyệt chi hoàn tiền', 'APP03-7-F08-APPROVE')
  await expect(checker.getByText('Đã duyệt chi hoàn tiền.')).toBeVisible()
  expect((await credits())[0].available).toBe(10000)

  await qa.getByRole('button', { name: 'Tải lại danh sách' }).click()
  await qa.getByLabel('Thao tác hoàn tiền').selectOption('WITNESS_PAYOUT')
  await qa.getByLabel('Đề nghị đã duyệt').selectOption(refundId)
  await submit(qa, 'Xác minh thực chi', 'APP03-7-F08-PAYOUT', ['Số tiền thực chi (VND)', '4000'])
  await expect(qa.getByText('Đã xác minh chứng từ thực chi.', { exact: true })).toBeVisible()
  expect((await credits())[0].available).toBe(10000)

  const cashier = await open(context, 'CASHIER', 'refunds')
  await cashier.getByLabel('Đề nghị đã duyệt').selectOption(refundId)
  await submit(cashier, 'Ghi thực chi hoàn tiền', 'APP03-7-F08-PAYOUT', ['Số tiền thực chi (VND)', '4000'])
  await expect(cashier.getByText('Đã ghi thực chi hoàn tiền.')).toBeVisible()
  expect((await credits())[0].available).toBe(6000)

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
