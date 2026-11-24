import { access, unlink, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { createConnection } from 'node:net'
import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test'
import { auth, open, signIn, verifyReturnRefund } from '../e2e-app03-7/return-refund'

const c2 = '/api/synthetic/app02c2'
const c4 = '/api/synthetic/app02c4'
const d2 = '/api/synthetic/app02d2'
const d3 = '/api/synthetic/app02d3'
const app = '/api/synthetic/app03'
const folder = process.env.APP03_EVIDENCE_DIR!
const checkpoint = join(folder, 'day-one-sources.json')
type Option = { id: string; label: string }
type Catalog = { customers: Option[]; trips: Option[]; skus: Option[]; warehouses: Option[] }
type Reply = { id?: string; sourceId: string; status?: string; replayed?: boolean }
type CashCommand = { commandId: string; sourceNumber: string; producerId: string; tripId: string; offerId: string }

async function read<T>(request: APIRequestContext, path: string, role = 'SOURCE'): Promise<T> {
  const response = await request.get(path, { headers: auth(role) })
  expect(response.ok(), path + ' ' + await response.text()).toBe(true)
  return response.json()
}

async function clickCommand(page: Page, path: string, button: string): Promise<Reply> {
  const response = page.waitForResponse(item => item.url().endsWith(path) && item.request().method() === 'POST', { timeout: 15_000 })
  await page.getByRole('button', { name: button, exact: true }).click()
  const result = await response
  expect(result.ok(), path + ' ' + await result.text()).toBe(true)
  return result.json()
}

async function sqlCheckpoint(name: string, timeout = 15_000) {
  const ready = join(folder, name + '.ready')
  const checked = join(folder, name + '.checked')
  await writeFile(ready, '')
  await expect.poll(async () => access(checked).then(() => true, () => false), {
    timeout, message: 'Wait for the SQL snapshot ' + name,
  }).toBe(true)
  await unlink(ready)
  await unlink(checked)
}

async function refreshCredentials() {
  const path = '\\\\.\\pipe\\' + process.env.APP03_ACCEPTANCE_PIPE
  const socket = createConnection(path)
  try {
    const chunks: Buffer[] = []
    for await (const chunk of socket) chunks.push(chunk)
    const tokens: Record<string, string> = JSON.parse(Buffer.concat(chunks).toString('utf8'))
    for (const [role, token] of Object.entries(tokens)) process.env['APP03_' + role + '_TOKEN'] = token
  } finally {
    socket.destroy()
  }
}

async function draft(page: Page, catalog: Catalog, trip: string, quantity: number) {
  const customer = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE'))!.id
  await page.goto('/work/customers?customer=' + customer)
  const selection = await clickCommand(page, '/api/synthetic/app02c1/ship-to-selections', 'Chọn địa chỉ')
  expect(selection.id).toBeTruthy()
  await expect(page.getByText('Đã ghi vào cơ sở dữ liệu.')).toBeVisible()
  await page.goto('/work/orders')
  const form = page.getByRole('form', { name: 'Tạo đơn A nháp' })
  await form.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer)
  await form.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption(selection.id!)
  await form.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(trip)
  await form.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption(catalog.skus.find(row => row.label.startsWith('Z-REFUND-EA'))!.id)
  await form.getByLabel('Số lượng dòng 1').fill(String(quantity))
  const order = await clickCommand(page, c2 + '/drafts', 'Lưu đơn nháp')
  expect(order.sourceId).toBeTruthy()
  await page.goto(`/work/orders?trip=${trip}&order=${order.sourceId}`)
  expect((await clickCommand(page, c2 + '/confirm', 'Confirm đơn')).sourceId).toBeTruthy()
  return order.sourceId
}

async function load(page: Page, trip: string, order: string, quantity: number) {
  await page.goto(`/work/load?trip=${trip}&order=${order}`)
  await page.getByLabel('Số lượng nạp dòng 1').fill(String(quantity))
  expect((await clickCommand(page, '/api/synthetic/app02c3/load', 'Nạp hàng')).sourceId).toBeTruthy()
  await expect(page.getByText('Đã nạp hàng lên xe.')).toBeVisible()
}

test('day-one hands A and B across roles and recovers cash in the same tab after restart', async ({ context, request }, info) => {
  test.setTimeout(180_000)
  const catalog = await read<Catalog>(request, c2 + '/catalog')
  const trip = catalog.trips.find(row => row.label === 'DEV-20261123-A')!.id
  const pendingTrip = catalog.trips.find(row => row.label === 'DEV-20261123-B')!.id
  const customer = catalog.customers.find(row => row.label.startsWith('C2-FIXTURE'))!.id
  const sales = await open(context, 'SOURCE', 'orders')
  const order = await draft(sales, catalog, trip, 2)
  const pendingOrder = await draft(sales, catalog, pendingTrip, 5)

  const warehouse = await open(context, 'RECEIVER', 'orders?trip=' + trip + '&order=' + order)
  async function reserve(quantity: number, status: number): Promise<APIResponse> {
    await warehouse.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(
      catalog.warehouses.find(row => row.label === 'C2-MAIN')!.id,
    )
    await warehouse.getByLabel('Số lượng giữ').fill(String(quantity))
    const pending = warehouse.waitForResponse(response => response.url().endsWith(c2 + '/reserve'))
    await warehouse.getByRole('button', { name: 'Reserve hàng' }).click()
    const response = await pending
    expect(response.status(), await response.text()).toBe(status)
    return response
  }
  expect((await (await reserve(2, 200)).json()).sourceId).toBeTruthy()
  await load(warehouse, trip, order, 2)
  await warehouse.goto(`/work/orders?trip=${pendingTrip}&order=${pendingOrder}`)
  expect(await (await reserve(5, 422)).json()).toMatchObject({ status: 'REJECTED', reasonCode: 'F03_LOT_STOCK_INSUFFICIENT' })
  expect((await (await reserve(2, 200)).json()).sourceId).toBeTruthy()

  await sales.goto('/work/delivery?trip=' + trip)
  await sales.getByLabel(/Số lượng đã giao dòng/).fill('2')
  const deliverySource = (await clickCommand(sales, c4 + '/deliver', 'Xác nhận đã giao')).sourceId
  const returnable = await read<{ deliveryId: string }[]>(request, '/api/synthetic/dev-f05/deliveries')
  expect(returnable).toHaveLength(1)
  const delivery = returnable[0].deliveryId
  expect(delivery).toBeTruthy()
  await sales.goto('/work/visits?customer=' + customer)
  await expect(sales.getByRole('combobox', { name: 'Khách hàng', exact: true })).toHaveValue(customer)
  await sales.getByLabel('Lý do').fill('Điểm bán chưa cần đặt thêm')
  const visit = (await clickCommand(sales, '/api/synthetic/app02c1/visits', 'Hoàn tất viếng thăm')).id
  await expect(sales.getByText(/Không có đơn.*Điểm bán chưa cần/)).toBeVisible()

  await warehouse.goto('/work/b-stock?trip=' + trip)
  await warehouse.getByLabel('Số lượng quota').fill('2')
  await warehouse.getByRole('button', { name: 'Cấp quota B', exact: true }).click()
  await expect(warehouse.getByText('Đã cấp quota B.')).toBeVisible()
  await warehouse.getByLabel('Số lượng nạp B').fill('2')
  await warehouse.getByRole('button', { name: 'Nạp hàng B', exact: true }).click()
  await expect(warehouse.getByText('Đã nạp hàng B.')).toBeVisible()
  await sales.goto('/work/b-sale?trip=' + trip)
  await sales.getByLabel('Dòng hàng B').selectOption({ index: 1 })
  await sales.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer)
  await sales.getByLabel('ShipTo đã chốt').selectOption({ index: 1 })
  await sales.getByLabel('Số lượng bán').fill('2')
  const bCatalog = await read<{ verifiedCash: { amount: number; evidenceRef: string }[] }>(request, d2 + '/trips/' + trip)
  await sales.getByLabel('Tiền mặt đã xác minh').selectOption(bCatalog.verifiedCash.find(item => item.amount === 20000)!.evidenceRef)
  await sales.getByRole('button', { name: 'Xem trước giao dịch' }).click()
  await expect(sales.getByText(/Phải trả 20.000 VND/)).toBeVisible()
  const saleB = (await clickCommand(sales, d2 + '/sales', 'Ghi bán và giao')).sourceId

  const balances = async () => ({
    ar: await read(request, c4 + '/trips/' + trip + '/ar'),
    cash: await read(request, d3 + '/trips/' + trip),
  })
  const before = await balances()
  expect(before.cash).toMatchObject({ custody: 20000, fund: 0 })
  await sqlCheckpoint('before-advance')
  const finance = await open(context, 'FINANCE', 'advances')
  await finance.getByLabel('Nhân viên').selectOption('c1-dev-sales')
  await finance.getByLabel('Số tiền (VND)').fill('500000')
  await finance.getByLabel('Chứng từ DEV').fill('APP03-9-ADVANCE')
  await finance.getByRole('button', { name: 'Phát ứng' }).click()
  await expect(finance.getByTestId('advance-outstanding')).toHaveText('500.000 VND')
  expect(await balances()).toEqual(before)
  await sqlCheckpoint('after-advance')

  await sales.goto('/work/settlement?trip=' + trip)
  await sales.getByLabel('Số tiền đề nghị nộp').fill('20000')
  const offer = (await clickCommand(sales, d3 + '/offer', 'Gửi đề nghị nộp tiền')).sourceId
  const cashier = await open(context, 'CASHIER', 'settlement?trip=' + trip)
  await expect(cashier.getByText('Mã đề nghị ' + offer)).toBeVisible()
  let submittedCommand: CashCommand | undefined
  let committedCash: Reply | undefined
  await cashier.route('**' + d3 + '/accept', async route => {
    submittedCommand = route.request().postDataJSON()
    const result = await route.fetch({ maxRetries: 0 })
    committedCash = await result.json()
    expect(committedCash).toMatchObject({ status: 'APPLIED', sourceId: expect.any(String) })
    await route.abort()
  }, { times: 1 })
  await cashier.getByLabel('Số tiền kiểm đếm (VND)').fill('20000')
  await cashier.getByRole('button', { name: 'Xác nhận đã nhận tiền' }).click()
  await expect(cashier.getByText(/Chưa rõ kết quả/)).toBeVisible()
  if (!submittedCommand || !committedCash) throw new Error('The lost cash ACK was not exercised')
  const command = submittedCommand
  const cash = committedCash
  expect(command.offerId).toBe(offer)
  expect(visit).toBeTruthy()
  expect(saleB).toBeTruthy()
  const pending = cashier.getByRole('region', { name: 'Khôi phục yêu cầu' })
  await expect(pending).toContainText(command.commandId)
  await writeFile(checkpoint, JSON.stringify({
    trip, pendingTrip, order, pendingOrder, delivery, deliverySource, saleB, cash, command, offer, visit,
  }, null, 2))
  await cashier.screenshot({ path: info.outputPath('cash-unknown.png') })

  const oldCashier = auth('CASHIER')
  await sqlCheckpoint('restart', 60_000)
  await refreshCredentials()
  await cashier.reload()
  await expect(cashier.getByLabel('Mã truy cập DEV')).toBeVisible()
  await signIn(cashier, 'CASHIER')
  await expect(pending).toContainText(command.commandId)

  const accepted = d3 + `/trips/${trip}/commands/${command.producerId}/${command.commandId}`
  expect((await request.get(accepted, { headers: oldCashier })).status()).toBe(401)
  expect((await request.get(accepted, { headers: auth('SOURCE') })).status()).toBe(404)
  const peerTrips = await read<{ tripId: string }[]>(request, d3 + '/trips', 'PEER_CASHIER')
  expect(peerTrips).toHaveLength(2)
  expect(peerTrips.map(row => row.tripId)).not.toContain(trip)
  expect(await read(request, d3 + '/trips/' + peerTrips[0].tripId, 'PEER_CASHIER')).toMatchObject({ custody: 0, fund: 0 })
  expect((await request.get(accepted, { headers: auth('PEER_CASHIER') })).status()).toBe(403)
  expect((await request.get(d3 + '/trips/' + trip, { headers: auth('PEER_CASHIER') })).status()).toBe(403)
  const peerWriteCommand = crypto.randomUUID()
  expect((await request.post(d3 + '/accept', { headers: auth('PEER_CASHIER'),
    data: { ...command, commandId: peerWriteCommand },
  })).status()).toBe(403)
  expect((await request.post(d3 + '/accept', { headers: auth('PEER_CASHIER'), data: command })).status()).toBe(403)

  await cashier.getByRole('button', { name: 'Tra cứu yêu cầu' }).click()
  await expect(cashier.getByText('Đã ghi nhận thủ quỹ nhận tiền.')).toBeVisible()
  expect(await read(request, accepted, 'CASHIER')).toMatchObject({ sourceId: cash.sourceId })
  const replay = await request.post(d3 + '/accept', { headers: auth('CASHIER'), data: command })
  expect(replay.ok(), await replay.text()).toBe(true)
  expect(await replay.json()).toMatchObject({ sourceId: cash.sourceId, replayed: true })
  expect(await read(request, d3 + '/trips/' + trip)).toMatchObject({ custody: 0, fund: 20000, pendingOfferId: null })
  await cashier.screenshot({ path: info.outputPath('cash-recovered.png') })
  await writeFile(join(folder, 'replay-probes.json'), JSON.stringify({ peerWriteCommand }))
  await sqlCheckpoint('after-cash-replay')
  const resumedWarehouse = await open(context, 'RECEIVER', `load?trip=${pendingTrip}&order=${pendingOrder}`)
  await load(resumedWarehouse, pendingTrip, pendingOrder, 2)
  const refund = await verifyReturnRefund(context, request, delivery, 'APP03-9')
  const owner = await open(context, 'OWNER', 'owner')
  await expect(owner.getByRole('heading', { name: 'Tổng quan Chủ NPP' })).toBeVisible()
  await owner.getByRole('button', { name: /Credit còn lại F08/ }).click()
  const detail = owner.getByRole('table', { name: 'Nguồn Credit còn lại F08' })
  await expect(detail).toContainText(delivery)
  await expect(detail).toContainText('6.000')
  await owner.screenshot({ path: info.outputPath('owner-credit.png'), fullPage: true })
  const report = await read(request, app + '/owner-overview', 'OWNER')
  const peer = await read(request, app + '/owner-overview', 'PEER_OWNER')
  await writeFile(join(folder, 'owner.json'), JSON.stringify({ report, peer, refund }, null, 2))
})

test('day-two confirms new pricing on the prepared trip', async ({ context, request }) => {
  const catalog = await read<Catalog>(request, c2 + '/catalog')
  const trip = catalog.trips.find(row => row.label === 'DEV-20261124-A')!.id
  const sales = await open(context, 'SOURCE', 'orders')
  const order = await draft(sales, catalog, trip, 1)
  await expect(sales.getByText(/12.000/).first()).toBeVisible()
  await writeFile(join(folder, 'day-two-sources.json'), JSON.stringify({ trip, order }))
})
