import { expect, test, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const app03 = '/api/synthetic/app03'
const c1 = '/api/synthetic/app02c1'
const c2 = '/api/synthetic/app02c2'
const c4 = '/api/synthetic/app02c4'
const f05 = '/api/synthetic/dev-f05'
const headers = (token: string) => ({ Authorization: 'Bearer ' + token })
const nav = (page: Page, name: string) => page.getByRole('navigation', { name: 'Công việc DEV' })
  .getByRole('link', { name, exact: true })

async function signIn(page: Page, token: string) {
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

test('one session moves through real A workspaces and isolates a lost return ACK by actor and tenant', async ({ page, request }, info) => {
  const { APP03_SOURCE_TOKEN: source, APP03_PEER_TOKEN: peer, APP03_EXPIRED_TOKEN: expired,
    APP03_FINANCE_TOKEN: finance, APP03_RECEIVER_TOKEN: warehouse, APP03_EVIDENCE_DIR: directory, APP02C1_RUN_ID: run } = process.env
  if (!source || !peer || !expired || !finance || !warehouse || !directory || !run) throw new Error('Missing APP-03.3 runner configuration')
  const get = async (path: string, token = source) => {
    const response = await request.get(path, { headers: headers(token) })
    expect(response.status()).toBe(200)
    return response.json()
  }
  expect((await request.get(app03 + '/session')).status()).toBe(401)
  expect((await request.get(app03 + '/session', { headers: headers(expired) })).status()).toBe(401)
  expect((await request.get(c2 + '/orders', { headers: headers(expired) })).status()).toBe(401)
  const catalog = await get(c2 + '/catalog')
  const customer = catalog.customers[0]
  const detail = await get(c1 + '/customers/' + customer.id)
  const registered = detail.registeredShipTos[0]
  const selection = await request.post(c1 + '/ship-to-selections', { headers: headers(source), data: {
    commandId: crypto.randomUUID(), customerId: customer.id, registeredId: registered.id, revision: registered.revision,
  } })
  expect(selection.status()).toBe(200)
  const trip = catalog.trips.find((row: { label: string }) => row.label === 'DEV-20261123-B').id

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'FMCG DMS · APP-03 DEV' })).toBeVisible()
  await expect(page).toHaveTitle('FMCG DMS — APP-03 DEV')
  await signIn(page, expired)
  await expect(page.getByRole('alert')).toContainText('hết hạn')
  await signIn(page, source)
  await expect(page.getByText('tenant-a · c1-dev-sales')).toBeVisible()
  await expect(page.locator('time')).toHaveAttribute('datetime', '2026-11-23')
  await expect(page.locator('main')).toHaveCSS('font-size', '14px')
  await expect(page.locator('main')).toHaveCSS('font-family', /Noto Sans/)
  await expect(page.getByRole('link', { name: /Mixed|Quota|Tạm ứng|Hoàn tiền/ })).toHaveCount(0)
  await nav(page, 'Đơn A và giữ hàng').focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('region', { name: 'Đơn A và giữ hàng' })).toBeFocused()
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer.id)
  await page.getByRole('combobox', { name: 'ShipTo đã chọn', exact: true }).selectOption((await selection.json()).id)
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(trip)
  await page.getByRole('combobox', { name: 'SKU dòng 1', exact: true }).selectOption(catalog.skus[0].id)
  await page.getByLabel('Số lượng dòng 1').fill('2')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(catalog.warehouses[0].id)
  await page.getByLabel('Số lượng giữ').fill('2')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByRole('region', { name: 'Đơn A', exact: true }).getByText('Đã giữ đủ')).toBeVisible()
  const [order] = await get(c2 + '/orders')

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, warehouse)
  await page.getByRole('link', { name: 'Nạp đơn lên xe' }).click()
  await expect(page.getByRole('combobox', { name: 'Chuyến', exact: true })).toHaveValue(trip)
  await page.getByLabel('Số lượng nạp dòng 1').fill('2')
  await page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText(/Đã nạp 2 EA/)).toBeVisible()
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, source)
  await nav(page, 'Giao hàng A').click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(trip)
  await page.reload()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(trip)
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.screenshot({ path: info.outputPath('workspace-desktop-dark.png'), fullPage: true })
  await page.getByLabel('Số lượng đã giao dòng 1').fill('2')
  await page.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  const [delivery] = await get(f05 + '/deliveries')
  const [ar] = await get(c4 + '/trips/' + trip + '/ar')
  expect(ar.currency).toBe('VND')
  expect(Number(ar.amount)).toBe(20000)

  await nav(page, 'Đơn A và giữ hàng').click()
  await page.getByText('Nguồn và địa chỉ đã Confirm', { exact: true }).click()
  await page.getByRole('button', { name: 'Công nợ theo đơn' }).click()
  await expect(page).toHaveURL(new RegExp('order=' + order.id))
  await expect(page.getByText('Phạm vi chuyến được phân công · VND')).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.locator('summary').filter({ hasText: 'Đã đối soát' }).click()
  await page.getByRole('link', { name: 'Trả hàng từ lần giao này' }).click()
  await expect(page.getByLabel('Nguồn giao và lô')).toHaveValue(`${delivery.deliveryId}:${delivery.lotId}`)

  let command: Record<string, unknown> = {}
  await page.route('**' + f05 + '/commands', async route => {
    command = route.request().postDataJSON()
    const committed = await route.fetch()
    expect(committed.status()).toBe(200)
    await route.abort()
  }, { times: 1 })
  await page.getByLabel('Số lượng trả').fill('1')
  await page.getByLabel('Lý do', { exact: true }).fill('Đối chiếu mất ACK')
  await page.getByRole('button', { name: 'Đề nghị trả hàng' }).click()
  await expect(page.getByText('Chưa rõ kết quả ghi hồ sơ trả hàng.')).toBeVisible()
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await expect(page.getByLabel('Mã truy cập DEV')).toBeFocused()
  await expect(page).not.toHaveURL(/delivery=/)
  await signIn(page, finance)
  await expect(page.getByRole('heading', { name: 'Duyệt trả hàng' })).toBeVisible()
  await expect(nav(page, 'Đơn A và giữ hàng')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Tra cứu kết quả' })).toHaveCount(0)
  expect((await request.get(f05 + '/commands/' + command.commandId, { headers: headers(finance) })).status()).toBe(404)

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, peer)
  await expect(page.getByText('tenant-b · c1-dev-sales')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Tra cứu kết quả' })).toHaveCount(0)
  expect(await get(f05 + '/cases', peer)).toEqual([])
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await signIn(page, source)
  await page.getByRole('button', { name: 'Tra cứu kết quả' }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  expect(await get(f05 + '/cases')).toMatchObject([{ deliveryId: delivery.deliveryId, state: 'REQUESTED' }])
  await page.setViewportSize({ width: 320, height: 812 })
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBe(320)
  await expect(page.locator('main')).toHaveCSS('padding-inline-start', '16px')
  await expect(page.getByRole('region', { name: 'Đề nghị trả hàng' })).toHaveCSS('padding-inline-start', '16px')
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.getByLabel('Chế độ hiển thị').selectOption('system')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.emulateMedia({ colorScheme: 'light' })
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  await page.getByLabel('Chế độ hiển thị').selectOption('dark')
  await page.screenshot({ path: info.outputPath('workspace-mobile-dark.png'), fullPage: true })

  await writeFile(join(directory, 'revoke.partial'), run)
  await rename(join(directory, 'revoke.partial'), join(directory, 'revoke.request'))
  await expect.poll(async () => {
    try { return await readFile(join(directory, 'revoke.ready'), 'utf8') } catch { return '' }
  }).toBe(run)
  await page.reload()
  await expect(nav(page, 'Giao hàng A')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Nhận vào cách ly' })).toBeVisible()
  expect((await request.get(c4 + '/trips/' + trip + '/ar', { headers: headers(source) })).status()).toBe(403)
  expect((await request.get(f05 + '/commands/' + command.commandId, { headers: headers(source) })).status()).toBe(403)
  expect((await request.post(f05 + '/commands', { headers: headers(source), data: command })).status()).toBe(403)
  expect((await request.post(f05 + '/commands', { headers: headers(expired), data: command })).status()).toBe(401)
})
