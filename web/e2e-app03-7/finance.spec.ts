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
  await sourceRow.getByRole('button').click()
  await accountant.getByLabel('Kịch bản ghi sổ DEV').selectOption('wrong-amount')
  await accountant.getByRole('button', { name: 'Ghi sổ nguồn' }).click()
  await expect(sourceRow).toContainText('Đã tiếp nhận · Đã ghi sổ · Chưa đối soát')

  const otherRow = accountant.locator(`li[data-delivery]:not([data-delivery=${JSON.stringify(sale)}])`)
  await otherRow.getByRole('button').click()
  await accountant.getByLabel('Kịch bản ghi sổ DEV').selectOption('')
  await accountant.getByRole('button', { name: 'Tiếp nhận nguồn' }).click()
  await expect(otherRow).toContainText('Đã tiếp nhận · Chưa ghi sổ · Chưa đối soát')
  await accountant.getByRole('button', { name: 'Ghi sổ nguồn' }).click()
  await expect(otherRow).toContainText('Đã tiếp nhận · Đã ghi sổ · Chưa đối soát')
  await accountant.screenshot({ path: info.outputPath('receipt.png'), fullPage: true })
})

test('accounting posts the original source, corrects the discrepancy and reconciles in the open period', async ({ context }, info) => {
  const reconciler = await open(context, 'RECONCILER', 'finance')
  await reconciler.getByLabel('Từ (gồm)', { exact: true }).fill('2026-11-23T00:00:00+07:00')
  await reconciler.getByLabel('Cutoff (loại trừ)', { exact: true }).fill('2026-11-24T00:00:00+07:00')
  await reconciler.getByLabel('Đối soát tính đến').fill('2026-11-24T08:59:00+07:00')
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
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText('AMOUNT_MISMATCH')
  const maker = await open(context, 'MAKER', 'finance')
  await expect(maker.getByRole('heading', { name: 'Hàng đợi kế toán' })).toBeVisible()
  await maker.getByLabel('Hồ sơ sai lệch', { exact: true }).selectOption({ index: 1 })
  const caseId = await maker.getByLabel('Hồ sơ sai lệch', { exact: true }).inputValue()
  await maker.getByRole('button', { name: 'Đề nghị điều chỉnh' }).click()
  await expect(maker.getByText(/^PROPOSED ·/)).toBeVisible()

  const checker = await open(context, 'CHECKER', 'finance')
  await checker.getByLabel('Đề nghị điều chỉnh', { exact: true }).selectOption({ index: 1 })
  await checker.getByRole('button', { name: 'Duyệt điều chỉnh' }).click()
  await expect(checker.getByText(/^CORRECTION_POSTED ·/)).toBeVisible()

  await reconciler.getByLabel('Từ (gồm)', { exact: true }).fill('2026-11-23T00:00:00+07:00')
  await reconciler.getByLabel('Cutoff (loại trừ)', { exact: true }).fill('2026-11-24T00:00:00+07:00')
  await reconciler.getByLabel('Đối soát tính đến').fill('2026-11-24T09:00:00+07:00')
  await reconciler.getByRole('button', { name: 'Chạy đối soát' }).click()
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).not.toContainText('AMOUNT_MISMATCH')
  await expect(reconciler.getByRole('region', { name: 'Dòng đối soát' })).toContainText('RECONCILED_OK')
  await reconciler.getByLabel('Hồ sơ sai lệch', { exact: true }).selectOption(caseId)
  await reconciler.getByRole('button', { name: 'Đóng hồ sơ sai lệch' }).click()
  await expect(reconciler.getByText(/^CLOSED ·/)).toBeVisible()
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
