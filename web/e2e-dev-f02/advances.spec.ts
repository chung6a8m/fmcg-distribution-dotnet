import { expect, test, type Page } from '@playwright/test'

const tokens = {
  finance: 'DEV_F02_FINANCE',
  cashier: 'DEV_F02_CASHIER',
  employee: 'APP02C1_BROWSER_TOKEN_A',
} as const

async function connect(page: Page, role: keyof typeof tokens) {
  const token = process.env[tokens[role]]
  if (!token) throw new Error('Missing DEV-F02 role credential')
  await page.goto('/sales/advances')
  await page.getByLabel('Mã truy cập DEV')
    .evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Sổ tạm ứng' })).toBeVisible()
}

async function fill(page: Page, amount: number, evidence: string) {
  await page.getByLabel('Số tiền (VND)').fill(String(amount))
  await page.getByLabel('Chứng từ DEV').fill(evidence)
  const reason = page.getByLabel('Lý do xác nhận')
  if (await reason.isVisible()) await reason.fill('Đã đối chiếu chứng từ DEV')
}

test('F02-04: Finance, employee and cashier close the AC-20 advance with two receipts', async ({ page, context }, info) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const balance = page.getByTestId('advance-outstanding')

  await connect(page, 'finance')
  await page.getByLabel('Nhân viên').selectOption('c1-dev-sales')
  await fill(page, 500000, 'DEV-F02-ISSUE')
  await page.getByRole('button', { name: 'Phát ứng', exact: true }).click()
  await expect(balance).toHaveText('500.000 VND')

  let releaseAdvances!: () => void
  const holdAdvances = new Promise<void>(resolve => { releaseAdvances = resolve })
  await page.route('**/dev-f02/advances', async route => {
    await holdAdvances
    await route.continue()
  })
  try {
    await connect(page, 'employee')
    await expect(page.getByLabel('Số tiền (VND)')).toBeDisabled({ timeout: 1000 })
    await expect(page.getByLabel('Chứng từ DEV')).toBeDisabled({ timeout: 1000 })
  } finally {
    releaseAdvances()
  }
  await fill(page, 300000, 'DEV-F02-EXPENSE')
  await page.getByRole('button', { name: 'Đề nghị quyết toán chi', exact: true }).click()
  await expect(page.getByText('Đã ghi sổ tạm ứng.')).toBeVisible()
  await expect(balance).toHaveText('500.000 VND')

  await connect(page, 'finance')
  await page.getByLabel('Thao tác').selectOption('EXPENSE_APPROVE')
  await page.getByLabel('Nguồn đề nghị').selectOption({ label: 'DEV-F02-EXPENSE' })
  await fill(page, 300000, 'DEV-F02-APPROVAL')
  await page.getByRole('button', { name: 'Duyệt chi', exact: true }).click()
  await expect(balance).toHaveText('200.000 VND')

  await connect(page, 'employee')
  await page.getByLabel('Thao tác').selectOption('RETURN_OFFER')
  await fill(page, 200000, 'DEV-F02-OFFER')
  const offer = page.getByRole('button', { name: 'Đề nghị hoàn ứng', exact: true })
  await context.setOffline(true)
  await expect(offer).toBeDisabled()
  await context.setOffline(false)
  await offer.click()
  await expect(page.getByText('Đã ghi sổ tạm ứng.')).toBeVisible()
  await expect(balance).toHaveText('200.000 VND')

  await connect(page, 'cashier')
  const source = page.getByLabel('Nguồn đề nghị')
  await source.selectOption({ label: 'DEV-F02-OFFER' })
  await fill(page, 100000, 'DEV-F02-COUNT-1')
  const accept = page.getByRole('button', { name: 'Xác nhận đã nhận hoàn ứng', exact: true })
  await accept.click()
  await expect(balance).toHaveText('100.000 VND')
  await source.selectOption({ label: 'DEV-F02-OFFER' })
  await fill(page, 100000, 'DEV-F02-COUNT-2')
  await page.route('**/dev-f02/commands', async route => {
    const committed = await route.fetch()
    expect(committed.status()).toBe(200)
    await route.abort('failed')
  }, { times: 1 })
  await accept.click()
  await expect(page.getByText('Chưa rõ kết quả ghi sổ tạm ứng.')).toBeVisible()
  await connect(page, 'cashier')
  await page.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(page.getByText('Đã ghi sổ tạm ứng.')).toBeVisible()
  await expect(balance).toHaveText('0 VND')
  expect(errors).toEqual([])
  await page.screenshot({ path: info.outputPath('employee-advance.png') })
})
