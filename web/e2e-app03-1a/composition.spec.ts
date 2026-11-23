import { expect, test, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const customers = '/api/synthetic/app02c1'
const orders = '/api/synthetic/app02c2'
const returns = '/api/synthetic/dev-f05'
const headers = (token: string) => ({ Authorization: 'Bearer ' + token })
const section = (page: Page, name: string) => page.getByRole('navigation', { name: 'Sales A' })
  .getByRole('button', { name, exact: true })

async function checkpoint(phase: string) {
  const dir = process.env.APP03_CHECKPOINTS
  const run = process.env.APP02C1_RUN_ID
  if (!dir || !run) throw new Error('Missing APP-03.1a checkpoint configuration')
  const staging = join(dir, phase + '.partial')
  await writeFile(staging, run)
  await rename(staging, join(dir, phase + '.request'))
  await expect.poll(async () => {
    try { return await readFile(join(dir, phase + '.ready'), 'utf8') } catch { return '' }
  }, { timeout: 45_000 }).toBe(run)
}

async function login(page: Page, token: string) {
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

test('a DEV-F05 order survives upgrade and yields a returnable delivery alongside a no-order visit', async ({ page, request }, info) => {
  const a = process.env.APP02C1_BROWSER_TOKEN_A
  const b = process.env.APP02C1_BROWSER_TOKEN_B
  const sales = process.env.DEV_F05_SALES
  if (!a || !b || !sales) throw new Error('Missing synthetic credentials')
  const get = async (path: string, token = sales) => {
    const response = await request.get(path, { headers: headers(token) })
    expect(response.status()).toBe(200)
    return response.json()
  }
  const catalog = await get(orders + '/catalog', a)
  const customer = catalog.customers.find((row: { label: string }) => row.label.startsWith('C2-FIXTURE'))
  const sku = catalog.skus.find((row: { label: string }) => row.label.startsWith('R-05 ·'))
  const selection = await request.post(customers + '/ship-to-selections', { headers: headers(a), data: {
    commandId: crypto.randomUUID(), customerId: customer.id,
    address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'APP-03.1a', recipient: 'Nhận DEV' },
  } })
  expect(selection.status()).toBe(200)
  await page.goto('/sales')
  await login(page, a)
  await section(page, 'Đơn A và giữ hàng').click()
  await expect(section(page, 'Viếng thăm')).toHaveCount(0)
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer.id)
  await page.getByRole('combobox', { name: 'ShipTo đã chọn', exact: true }).selectOption((await selection.json()).id)
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(catalog.trips[0].id)
  await page.getByRole('combobox', { name: 'SKU dòng 1', exact: true }).selectOption(sku.id)
  await page.getByLabel('Số lượng dòng 1').fill('2')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(catalog.warehouses[0].id)
  await page.getByLabel('Số lượng giữ').fill('2')
  await page.getByRole('button', { name: 'Reserve hàng' }).click()
  await expect(page.getByText('Đã giữ đủ')).toBeVisible()
  const predecessor = await get(orders + '/orders')
  expect(predecessor).toHaveLength(1)
  await checkpoint('upgrade')
  expect(await get(orders + '/orders')).toEqual(predecessor)
  await page.reload()
  await login(page, a)
  await expect(page.getByRole('link', { name: 'Trả hàng · DEV' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Đối soát Mixed · DEV' })).toHaveCount(0)

  await section(page, 'Nạp hàng').click()
  await page.getByLabel('Số lượng nạp dòng 1').fill('2')
  await page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true }).click()
  await expect(page.getByText(/Đã nạp 2 EA/)).toBeVisible()
  await section(page, 'Giao hàng').click()
  await page.getByLabel('Số lượng đã giao dòng 1').fill('2')
  await page.getByRole('button', { name: 'Xác nhận đã giao' }).click()
  await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
  const before = await get(returns + '/deliveries')
  expect(before).toHaveLength(1)
  const [delivery] = before
  expect(delivery).toMatchObject({ sku: 'R-05', lotCode: 'RET-ORIGIN', available: 2 })
  await checkpoint('delivered')

  await section(page, 'Viếng thăm').click()
  await page.getByLabel('Khách hàng', { exact: true }).selectOption(customer.id)
  await page.getByLabel('Kết quả viếng thăm').selectOption('NO_ORDER')
  await page.getByLabel('Lý do').fill('Hẹn lần sau')
  const visitPost = page.waitForRequest(req => req.method() === 'POST' && req.url().endsWith(customers + '/visits'))
  await page.getByRole('button', { name: 'Hoàn tất viếng thăm' }).click()
  await expect(page.getByText('Đã hoàn tất viếng thăm.')).toBeVisible()
  const visitInput = (await visitPost).postDataJSON()
  await checkpoint('visited')
  expect(await get(returns + '/deliveries')).toEqual(before)
  expect(await get(customers + '/visits?customerId=' + customer.id, b)).toEqual([])
  const peerVisit = await request.post(customers + '/visits', { headers: headers(b),
    data: { ...visitInput, commandId: crypto.randomUUID() } })
  expect(peerVisit.status()).toBe(404)
  expect(await get(returns + '/deliveries', b)).toEqual([])
  const peerReturn = await request.post(returns + '/commands', { headers: headers(b), data: {
    commandId: crypto.randomUUID(), action: 'REQUEST', deliveryId: delivery.deliveryId, lotId: delivery.lotId,
    quantity: 1, reason: 'Cross-tenant return',
  } })
  expect(peerReturn.status()).toBe(422)
  expect((await peerReturn.json()).reasonCode).toBe('F05_SOURCE_NOT_FOUND')

  await page.getByRole('link', { name: 'Trả hàng · DEV' }).click()
  await login(page, sales)
  await page.getByLabel('Nguồn giao và lô').selectOption(`${delivery.deliveryId}:${delivery.lotId}`)
  await page.getByLabel('Số lượng trả').fill('1')
  await page.getByLabel('Lý do', { exact: true }).fill('Trả một đơn vị')
  const returnPost = page.waitForRequest(req => req.method() === 'POST' && req.url().endsWith(returns + '/commands'))
  await page.getByRole('button', { name: 'Đề nghị trả hàng' }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  expect(await get(returns + '/cases')).toMatchObject([
    { deliveryId: delivery.deliveryId, lotId: delivery.lotId, state: 'REQUESTED' },
  ])
  await page.screenshot({ path: info.outputPath('sourced-return.png') })

  await checkpoint('revoke')
  for (const path of [customers + '/visits?customerId=' + customer.id, returns + '/deliveries']) {
    expect((await request.get(path, { headers: headers(sales) })).status()).toBe(403)
  }
  expect((await request.post(customers + '/visits', { headers: headers(sales), data: visitInput })).status()).toBe(403)
  expect((await request.post(returns + '/commands',
    { headers: headers(sales), data: (await returnPost).postDataJSON() })).status()).toBe(403)
})
