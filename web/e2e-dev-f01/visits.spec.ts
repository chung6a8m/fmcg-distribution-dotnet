import { expect, test, type Page } from '@playwright/test'

const route = '/api/synthetic/app02c1'

async function openVisits(page: Page) {
  const token = process.env.APP02C1_BROWSER_TOKEN_A
  if (!token) throw new Error('Missing synthetic browser credential')
  await page.goto('/sales')
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await page.getByRole('button', { name: 'Viếng thăm', exact: true }).click()
  const customer = page.getByLabel('Khách hàng', { exact: true })
  if (await customer.isEnabled()) {
    await customer.selectOption({ label: 'C2-FIXTURE · Khách hàng C.2 DEV' })
  }
}

test('F01-01 and F01-03: visit survives offline guard, lost ACK and reload', async ({ page, context, request }, info) => {
  const headers = { Authorization: 'Bearer ' + process.env.APP02C1_BROWSER_TOKEN_A }
  const customers = await request.get(route + '/customers', { headers })
  expect(customers.ok()).toBeTruthy()
  const customer = (await customers.json()).find((entry: { code: string }) => entry.code === 'C2-FIXTURE')
  const outlet = await request.post(route + '/outlets/save', { headers, data: {
    commandId: crypto.randomUUID(), customerId: customer.id, name: 'Outlet Visit DEV',
  } })
  expect(outlet.ok()).toBeTruthy()

  const pageErrors: string[] = []
  let posts = 0
  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('request', req => { if (req.method() === 'POST' && req.url().endsWith(route + '/visits')) posts++ })
  await openVisits(page)
  await expect(page.getByRole('heading', { name: 'Viếng thăm' })).toBeVisible()
  await page.getByLabel('Điểm bán').selectOption({ label: 'Outlet Visit DEV' })
  await page.getByLabel('Kết quả viếng thăm').selectOption('NO_ORDER')
  await page.getByLabel('Lý do').fill('Hẹn lần sau')
  await page.getByLabel('Quan sát').fill('Có ghi chú')
  const complete = page.getByRole('button', { name: 'Hoàn tất viếng thăm' })
  await context.setOffline(true)
  await expect(complete).toBeDisabled()
  expect(posts).toBe(0)
  await context.setOffline(false)
  await expect(complete).toBeEnabled()
  expect(posts).toBe(0)

  let committedId: string | undefined
  await page.route('**/app02c1/visits', async intercept => {
    const response = await intercept.fetch()
    expect(response.status()).toBe(200)
    committedId = (await response.json()).id
    await intercept.abort('failed')
  }, { times: 1 })
  await complete.click()
  await expect(page.getByText('Chưa rõ kết quả viếng thăm.')).toBeVisible()
  await openVisits(page)
  await page.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(page.getByText('Đã hoàn tất viếng thăm.')).toBeVisible()
  await expect(page.getByText('Có ghi chú', { exact: true })).toBeVisible()
  expect(posts).toBe(1)

  await openVisits(page)
  await expect(page.getByText('Có ghi chú', { exact: true })).toBeVisible()
  const visits = await request.get(route + '/visits?customerId=' + customer.id, { headers })
  expect(await visits.json()).toMatchObject([{ id: committedId, customerId: customer.id,
    outletId: (await outlet.json()).id, outcome: 'NO_ORDER', reason: 'Hẹn lần sau', observation: 'Có ghi chú' }])
  expect(pageErrors).toEqual([])
  await page.screenshot({ path: info.outputPath('visit-desktop.png') })
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByText('Có ghi chú', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390)
  await page.screenshot({ path: info.outputPath('visit-mobile.png'), fullPage: true })
})
