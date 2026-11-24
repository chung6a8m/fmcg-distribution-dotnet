import { expect, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test'

const refund = '/api/synthetic/dev-f08'
export const auth = (role: string) => ({ Authorization: 'Bearer ' + process.env['APP03_' + role + '_TOKEN'] })

export async function open(context: BrowserContext, role: string, path: string) {
  const page = await context.newPage()
  await page.goto('/work/' + path)
  await signIn(page, role)
  return page
}

export async function signIn(page: Page, role: string) {
  await page.getByLabel('Mã truy cập DEV').fill(process.env['APP03_' + role + '_TOKEN']!)
  await page.getByRole('button', { name: 'Kết nối' }).click()
  await expect(page.getByRole('navigation', { name: 'Công việc DEV' })).toBeVisible()
}

export async function submit(page: Page, action: string, document: string, quantity?: [string, string]) {
  if (quantity) await page.getByLabel(quantity[0], { exact: true }).fill(quantity[1])
  await page.getByLabel('Lý do', { exact: true }).fill('Hồ sơ synthetic DEV APP-03')
  if (document) await page.getByLabel(/Chứng từ.*DEV/).fill(document)
  await page.getByRole('button', { name: action, exact: true }).click()
}

export async function verifyReturnRefund(context: BrowserContext, request: APIRequestContext, sale: string,
  prefix: string, checkGiftExclusion = false) {
  const source = await open(context, 'SOURCE', 'returns?delivery=' + sale)
  await expect(source.getByLabel('Nguồn giao và lô')).toHaveValue(new RegExp('^' + sale + ':'))
  await submit(source, 'Đề nghị trả hàng', '', ['Số lượng trả', '1'])
  await expect(source.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  const receiver = await open(context, 'RECEIVER', 'returns')
  const caseSelection = (page: Page) => page.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true })
  await caseSelection(receiver).selectOption({ index: 1 })
  const caseId = await caseSelection(receiver).inputValue()
  await submit(receiver, 'Nhận vào cách ly', `${prefix}-RETURN-RECEIVE`, ['Số lượng thực nhận', '1'])
  await expect(receiver.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const finance = await open(context, 'FINANCE', 'returns?source=' + caseId)
  await expect(caseSelection(finance)).toHaveValue(caseId)
  await submit(finance, 'Duyệt trả hàng', `${prefix}-RETURN-APPROVE`)
  await expect(finance.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  const qa = await open(context, 'QA', 'returns')
  await caseSelection(qa).selectOption(caseId)
  await submit(qa, 'Giải phóng hàng', `${prefix}-RETURN-RELEASE`)
  await expect(qa.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  if (checkGiftExclusion) {
    await source.goto('/work/returns')
    const giftOption = source.getByLabel('Nguồn giao và lô').locator('option').filter({ hasText: 'R-05' }).first()
    await expect(giftOption).toBeAttached()
    await source.getByLabel('Nguồn giao và lô').selectOption(await giftOption.getAttribute('value') ?? '')
    await submit(source, 'Đề nghị trả hàng', '', ['Số lượng trả', '1'])
    await expect(source.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  }

  await finance.getByRole('link', { name: 'Credit và hoàn tiền' }).click()
  if (checkGiftExclusion) await expect(finance.getByText('1 EA · Nguồn ngoài phạm vi F08 v1')).toBeVisible()
  await expect(finance.getByText('1 EA · Chờ đối soát đủ tiền')).toBeVisible()
  await qa.getByRole('link', { name: 'Credit và hoàn tiền' }).click()
  await qa.getByLabel('Lần giao nguồn').selectOption(sale)
  await submit(qa, 'Xác minh tiền vào', `${prefix}-F08-PAYMENT`, ['Số tiền thực nhận (VND)', '20000'])
  await expect(qa.getByText('Đã xác minh chứng từ tiền vào.')).toBeVisible()
  await finance.getByRole('button', { name: 'Tải lại danh sách' }).click()
  await finance.getByLabel('Lần giao nguồn').selectOption(sale)
  await submit(finance, 'Đối soát tiền vào', `${prefix}-F08-PAYMENT`, ['Số tiền đối soát (VND)', '20000'])
  await expect(finance.getByText('Đã đối soát tiền vào.')).toBeVisible()
  await finance.getByLabel('Thao tác hoàn tiền').selectOption('CREDIT')
  await finance.getByLabel('Hồ sơ trả đã giải phóng').selectOption(caseId)
  await submit(finance, 'Ghi credit', `${prefix}-F08-CREDIT`)
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
  await submit(finance, 'Đề nghị chi hoàn tiền', `${prefix}-F08-PROPOSE`, ['Số tiền đề nghị (VND)', '4000'])
  await expect(finance.getByText('Đã ghi đề nghị chi hoàn tiền.')).toBeVisible()

  const checker = await open(context, 'REFUND_CHECKER', 'refunds')
  await checker.getByLabel('Thao tác hoàn tiền').selectOption('APPROVE')
  await checker.getByLabel('Đề nghị chi', { exact: true }).selectOption({ index: 1 })
  const refundId = await checker.getByLabel('Đề nghị chi', { exact: true }).inputValue()
  const selfApproval = await request.post(refund + '/commands', { headers: auth('FINANCE'), data: {
    commandId: crypto.randomUUID(), action: 'APPROVE', refundId, reason: 'Cùng người', evidence: `${prefix}-SELF`,
  } })
  expect(await selfApproval.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'F08_SEPARATION_REQUIRED' })
  await submit(checker, 'Duyệt chi hoàn tiền', `${prefix}-F08-APPROVE`)
  await expect(checker.getByText('Đã duyệt chi hoàn tiền.')).toBeVisible()
  expect((await credits())[0].available).toBe(10000)

  await qa.getByRole('button', { name: 'Tải lại danh sách' }).click()
  await qa.getByLabel('Thao tác hoàn tiền').selectOption('WITNESS_PAYOUT')
  await qa.getByLabel('Đề nghị đã duyệt').selectOption(refundId)
  await submit(qa, 'Xác minh thực chi', `${prefix}-F08-PAYOUT`, ['Số tiền thực chi (VND)', '4000'])
  await expect(qa.getByText('Đã xác minh chứng từ thực chi.', { exact: true })).toBeVisible()
  expect((await credits())[0].available).toBe(10000)

  const cashier = await open(context, 'CASHIER', 'refunds')
  await cashier.getByLabel('Đề nghị đã duyệt').selectOption(refundId)
  await submit(cashier, 'Ghi thực chi hoàn tiền', `${prefix}-F08-PAYOUT`, ['Số tiền thực chi (VND)', '4000'])
  await expect(cashier.getByText('Đã ghi thực chi hoàn tiền.')).toBeVisible()
  expect((await credits())[0].available).toBe(6000)

  return { caseId, creditId: credit.id, refundId }
}
