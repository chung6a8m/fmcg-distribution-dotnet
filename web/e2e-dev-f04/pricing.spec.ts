import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFile, rename, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'

const salesApi = '/api/synthetic/app02c2'
const deliveryApi = '/api/synthetic/app02c4'
type Mode = 'EXCLUSIVE' | 'INCLUSIVE'
type GoldenRow = [string, number, number, number, number, number]
type Catalog = {
  customers: { id: string, label: string }[]
  trips: { id: string, label: string }[]
  skus: { id: string, label: string }[]
  warehouses: { id: string, label: string }[]
}
type Order = { id: string, lines: { sku: string, pricing: Record<string, string | number> }[] }

const tokenHeader = (token: string) => ({ Authorization: 'Bearer ' + token })
const section = (page: Page, name: string) => page.getByRole('navigation', { name: 'Sales A' })
  .getByRole('button', { name, exact: true }).click()

async function confirm(page: Page, request: APIRequestContext, token: string) {
  const headers = tokenHeader(token)
  const response = await request.get(salesApi + '/catalog', { headers })
  expect(response.status()).toBe(200)
  const catalog: Catalog = await response.json()
  const customer = catalog.customers.find(option => option.label.startsWith('C2-FIXTURE'))
  const products = ['P', 'Q'].map(sku => catalog.skus.find(option => option.label.startsWith(sku + ' ·')))
  if (!customer || products.some(product => !product)) throw new Error('Missing DEV-F04 C2 fixture')

  const address = await request.post('/api/synthetic/app02c1/ship-to-selections', { headers,
    data: { commandId: crypto.randomUUID(), customerId: customer.id,
      address: { country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'Pricing DEV', recipient: 'Nhận DEV' },
    } })
  expect(address.status()).toBe(200)

  await page.goto('/sales')
  await page.getByLabel('Mã truy cập DEV').evaluate((input: HTMLInputElement, value) => { input.value = value }, token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await section(page, 'Đơn A và giữ hàng')
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer.id)
  await page.getByRole('combobox', { name: 'ShipTo đã chọn', exact: true }).selectOption((await address.json()).id)
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(catalog.trips[0].id)
  await page.getByRole('combobox', { name: 'SKU dòng 1', exact: true }).selectOption(products[0]!.id)
  await page.getByLabel('Số lượng dòng 1').fill('3')
  await page.getByRole('button', { name: 'Thêm dòng', exact: true }).click()
  await page.getByRole('combobox', { name: 'SKU dòng 2', exact: true }).selectOption(products[1]!.id)
  await page.getByLabel('Số lượng dòng 2').fill('1')
  await page.getByRole('button', { name: 'Lưu đơn nháp' }).click()
  await page.getByRole('button', { name: 'Confirm đơn' }).click()
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()
  return catalog
}

async function verifyPricing(page: Page, request: APIRequestContext, token: string, mode: Mode,
  expected: GoldenRow[], currency: string) {
  const res = await request.get(salesApi + '/orders', { headers: tokenHeader(token) })
  expect(res.status()).toBe(200)
  const orders: Order[] = await res.json()
  expect(orders).toHaveLength(1)
  expect(orders[0].lines).toHaveLength(2)
  for (const [sku, base, discount, net, tax, gross] of expected) {
    const price = orders[0].lines.find(line => line.sku === sku)?.pricing
    expect(price).toMatchObject({ mode, currency })
    expect([price?.base, price?.discount, price?.net, price?.tax, price?.gross, price?.version].map(Number))
      .toEqual([base, discount, net, tax, gross, 1])
    const panel = page.getByRole('region', { name: 'Giá đã chốt ' + sku, exact: true })
    await expect(panel).toContainText(mode === 'INCLUSIVE' ? 'Giá gồm VAT' : 'Giá chưa VAT')
    await expect(panel.locator('dd')).toHaveText([base, discount, net, tax, gross]
      .map(amount => amount.toLocaleString('vi-VN') + ' VND'))
  }
  const totals = [3, 4, 5].map(component => expected.reduce((sum, row) => sum + Number(row[component]), 0))
  await expect(page.getByRole('region', { name: 'Tổng tiền đơn đã chốt' }).locator('dd'))
    .toHaveText(totals.map(amount => amount.toLocaleString('vi-VN') + ' VND'))
  return orders[0]
}

test('F04-01–04: confirm both AC-19 modes, revise master and post one frozen partial', async ({ page, request }, info) => {
  const tokenA = process.env.APP02C1_BROWSER_TOKEN_A
  const tokenB = process.env.APP02C1_BROWSER_TOKEN_B
  const warehouse = process.env.APP02C3_WAREHOUSE_TOKEN_A
  const checkpoints = process.env.DEV_F04_CHECKPOINTS
  const runId = process.env.APP02C1_RUN_ID
  if (!tokenA || !tokenB || !warehouse || !checkpoints || !runId) throw new Error('Missing protected pricing fixture')

  const golden = JSON.parse(await readFile(resolve('..', 'business', 'reference-data', 'expected-results',
    'golden-oracles.json'), 'utf8'))['AC-19']
  const pageB = await page.context().newPage()
  try {
    const catalogA = await confirm(page, request, tokenA)
    await confirm(pageB, request, tokenB)
    const a = await verifyPricing(page, request, tokenA, 'EXCLUSIVE', golden.EXCLUSIVE, golden.currency)
    await verifyPricing(pageB, request, tokenB, 'INCLUSIVE', golden.INCLUSIVE, golden.currency)
    await page.screenshot({ path: info.outputPath('exclusive-confirmed.png') })
    await pageB.screenshot({ path: info.outputPath('inclusive-confirmed.png') })

    const unauthorized = await request.post(salesApi + '/confirm', { headers: tokenHeader(warehouse),
      data: { commandId: crypto.randomUUID(), orderId: a.id } })
    expect(unauthorized.status()).toBe(403)
    const foreign = await request.post(salesApi + '/confirm', { headers: tokenHeader(tokenB),
      data: { commandId: crypto.randomUUID(), orderId: a.id } })
    expect(foreign.status()).toBe(422)
    expect((await foreign.json()).reasonCode).toBe('C2_DRAFT_UNAVAILABLE')

    const staging = join(checkpoints, 'master-change.partial')
    await writeFile(staging, runId)
    await rename(staging, join(checkpoints, 'master-change.request'))
    await expect.poll(async () => {
      try { return await readFile(join(checkpoints, 'master-change.ready'), 'utf8') } catch { return '' }
    }, { timeout: 15_000 }).toBe(runId)
    await page.getByRole('button', { name: 'Tải lại đơn' }).click()
    await pageB.getByRole('button', { name: 'Tải lại đơn' }).click()
    await verifyPricing(page, request, tokenA, 'EXCLUSIVE', golden.EXCLUSIVE, golden.currency)
    await verifyPricing(pageB, request, tokenB, 'INCLUSIVE', golden.INCLUSIVE, golden.currency)

    const headers = tokenHeader(tokenA)
    const baselineSku = catalogA.skus.find(sku => sku.label.startsWith('C2-SKU ·'))
    const customer = catalogA.customers.find(entry => entry.label.startsWith('C2-FIXTURE'))
    if (!baselineSku || !customer) throw new Error('Missing excluded baseline quote fixture')
    const selection = await request.post('/api/synthetic/app02c1/ship-to-selections', { headers,
      data: { commandId: crypto.randomUUID(), customerId: customer.id, address: {
        country: 'VN', region: 'HCM', locality: 'Khu DEV', line: 'Mixed quote guard', recipient: 'Nhận DEV',
      } } })
    expect(selection.status()).toBe(200)
    const mixedDraft = await request.post(salesApi + '/drafts', { headers, data: {
      commandId: crypto.randomUUID(), customerId: customer.id, tripId: catalogA.trips[0].id,
      selectionId: (await selection.json()).id, lines: [{ skuId: baselineSku.id, quantity: 700 }],
    } })
    expect(mixedDraft.status()).toBe(200)
    const mixedConfirm = await request.post(salesApi + '/confirm', { headers, data: {
      commandId: crypto.randomUUID(), orderId: (await mixedDraft.json()).sourceId,
    } })
    expect(mixedConfirm.status()).toBe(422)
    expect((await mixedConfirm.json()).reasonCode).toBe('F04_PRICE_INVALID')

    const hold = page.getByRole('form', { name: 'Giữ hàng P', exact: true })
    await hold.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(catalogA.warehouses[0].id)
    await hold.getByLabel('Số lượng giữ').fill('1')
    await hold.getByRole('button', { name: 'Reserve hàng', exact: true }).click()
    await expect(page.getByText('Đã giữ một phần')).toBeVisible()
    await section(page, 'Nạp hàng')
    await page.getByLabel('Số lượng nạp dòng 1').fill('1')
    await page.getByRole('region', { name: 'Lập đợt nạp' }).getByRole('button', { name: 'Nạp hàng', exact: true }).click()
    await expect(page.getByText(/Đã nạp 1 EA/)).toBeVisible()
    await section(page, 'Giao hàng')
    await page.getByLabel('Số lượng đã giao dòng 1').fill('1')
    const [post] = await Promise.all([
      page.waitForRequest(req => req.method() === 'POST' && req.url().endsWith(deliveryApi + '/deliver')),
      page.getByRole('button', { name: 'Xác nhận đã giao' }).click(),
    ])
    await expect(page.getByText('Đã ghi giao hàng và công nợ.')).toBeVisible()
    const pledge = page.getByRole('region', { name: 'Công nợ theo lần giao' })
    await expect(pledge).toContainText('69.600 VND')
    const ar = await request.get(deliveryApi + '/trips/' + catalogA.trips[0].id + '/ar', { headers })
    expect(ar.status()).toBe(200)
    const debts = await ar.json()
    expect(debts).toHaveLength(1)
    expect(debts[0]).toMatchObject({ currency: 'VND' })
    expect([debts[0].amount, debts[0].outstanding, debts[0].quantity, debts[0].cost].map(Number))
      .toEqual([69600, 69600, 1, 10])
    const retry = await request.post(deliveryApi + '/deliver', {
      headers: { ...tokenHeader(tokenA), 'content-type': 'application/json' },
      data: post.postData()!,
    })
    expect(retry.status()).toBe(200)
    await page.screenshot({ path: info.outputPath('exclusive-partial.png') })
  } finally {
    await pageB.close()
  }
})
