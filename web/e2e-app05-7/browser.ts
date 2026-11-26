import { access, readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { app, c1, c2, c3, c4, d3, accounting, type CloseInput, type Closure, type Work } from '../e2e-app05-2/fixture'

type Choice = { id: string; label: string }
export type Catalog = { customers: Choice[]; trips: Choice[]; skus: Choice[]; warehouses: Choice[] }
export type OrderSource = { orderId: string; orderLineId: string; confirmSourceId: string }
type Outcome = { sourceId: string; status?: string }
type OrderRead = { id: string; lines: { lineId: string }[] }

export const evidence = process.env.APP05_EVIDENCE_DIR!
export const assignee = 'app05-assignee'
export const dueAt = '2026-11-24T12:00'

export function choice(rows: Choice[], label: string, exact = false) {
  const row = rows.find(item => exact ? item.label === label : item.label.startsWith(label + ' ·'))
  expect(row, 'Fixture ' + label).toBeDefined()
  return row!.id
}

export async function checkpoint<T>(name: string, payload: T) {
  await writeFile(join(evidence, name + '.json'), JSON.stringify(payload, null, 2))
  await writeFile(join(evidence, name + '.ready'), '')
  await expect.poll(() => access(join(evidence, name + '.checked')).then(() => true, () => false), {
    message: 'Đối chiếu SQL độc lập cho ' + name, timeout: 30_000,
  }).toBe(true)
}

export async function readCheckpoint<T>(name: string): Promise<T> {
  return JSON.parse(await readFile(join(evidence, name + '.json'), 'utf8'))
}

export async function submit<T>(page: Page, path: string, name: string, scope: Page | Locator = page): Promise<T> {
  const [response] = await Promise.all([
    page.waitForResponse(res => res.url().endsWith(path) && res.request().method() === 'POST', { timeout: 20_000 }),
    scope.getByRole('button', { name, exact: true }).click(),
  ])
  expect(response.ok(), path + ' ' + await response.text()).toBe(true)
  return response.json()
}

export async function draft(page: Page, trip: string, customer: string, sku: string, quantity: number, readOrders: () => Promise<OrderRead[]>): Promise<OrderSource> {
  await page.goto('/work/customers?customer=' + customer)
  const selection = await submit<{ id: string }>(page, c1 + '/ship-to-selections', 'Chọn địa chỉ')
  expect(selection.id).toBeTruthy()
  await expect(page.getByText('Đã ghi vào cơ sở dữ liệu.')).toBeVisible()
  await page.goto('/work/orders')
  const form = page.getByRole('form', { name: 'Tạo đơn A nháp' })
  await form.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption(customer)
  await form.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption(selection.id)
  await form.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(trip)
  await form.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption(sku)
  await form.getByLabel('Số lượng dòng 1').fill(String(quantity))
  const saved = await submit<Outcome>(page, c2 + '/drafts', 'Lưu đơn nháp', form)
  await page.goto('/work/orders?trip=' + trip + '&order=' + saved.sourceId)
  const confirmed = await submit<Outcome>(page, c2 + '/confirm', 'Confirm đơn')
  const orderLineId = (await readOrders()).find(order => order.id === saved.sourceId)?.lines[0].lineId
  expect(orderLineId).toBeTruthy()
  return { orderId: saved.sourceId, orderLineId: orderLineId!, confirmSourceId: confirmed.sourceId }
}

export async function reserve(page: Page, trip: string, order: string, warehouse: string, quantity: number) {
  await page.goto('/work/orders?trip=' + trip + '&order=' + order)
  const card = page.locator('[data-order-id="' + order + '"]')
  await card.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(warehouse)
  await card.getByLabel('Số lượng giữ').fill(String(quantity))
  return (await submit<Outcome>(page, c2 + '/reserve', 'Reserve hàng', card)).sourceId
}

export async function load(page: Page, trip: string, order: string, quantity: number) {
  await page.goto('/work/load?trip=' + trip + '&order=' + order)
  await page.getByLabel('Số lượng nạp dòng 1').fill(String(quantity))
  const result = await submit<Outcome>(page, c3 + '/load', 'Nạp hàng')
  await expect(page.getByText('Đã nạp hàng lên xe.')).toBeVisible()
  return result.sourceId
}

export async function deliver(page: Page, trip: string, quantity: number) {
  await page.goto('/work/delivery?trip=' + trip)
  await page.getByLabel(/Số lượng đã giao dòng/).fill(String(quantity))
  return (await submit<Outcome>(page, c4 + '/deliver', 'Xác nhận đã giao')).sourceId
}

export async function handoff(page: Page, trip: string, returnGoods: boolean) {
  await page.goto('/work/settlement?trip=' + trip)
  return (await submit<Outcome>(page, d3 + '/unload', returnGoods ? 'Trả hàng tồn xe' : 'Bàn giao để đối soát')).sourceId
}

export async function receiveAndPost(page: Page, source: Work) {
  expect(source.relatedId).toBeTruthy()
  const path = accounting + '/' + source.relatedId
  await page.goto('/work/finance?delivery=' + source.relatedId + '&source=' + source.sourceId)
  await expect(page.locator('li[data-delivery="' + source.relatedId + '"]').getByRole('button')).toHaveAttribute('aria-pressed', 'true')
  for (const [suffix, label] of [['receive', 'Tiếp nhận nguồn'], ['post', 'Ghi sổ nguồn']]) {
    const response = await submit<{ sourceLineId: string }>(page, path + '/' + suffix, label)
    expect(response.sourceLineId).toBe(source.sourceId)
  }
}

export async function assign(page: Page, trip: string, source: Work, nextAction: string) {
  const row = page.locator('[data-source-id="' + source.sourceId + '"]')
  const start = row.getByRole('button', { name: 'Giao việc', exact: true })
  await expect(start).toBeEnabled()
  await start.press('Enter')
  await row.getByLabel('Người phụ trách').selectOption(assignee)
  await row.getByLabel('Hạn xử lý (ICT)').fill(dueAt)
  await row.getByLabel('Hành động tiếp theo').fill(nextAction)
  await row.getByLabel('Lý do').fill('Bàn giao ca DEV APP-05.7')
  const result = await submit<{ followUpId: string }>(page, app + '/' + trip + '/follow-ups', 'Lưu giao việc', row)
  await expect(row).toContainText(assignee)
  return result.followUpId
}

export async function closeTrip(page: Page, trip: string): Promise<Closure> {
  const start = page.getByRole('button', { name: 'Đóng chuyến', exact: true })
  await expect(start).toBeEnabled()
  await start.press('Enter')
  const dialog = page.getByRole('dialog', { name: 'Xác nhận đóng chuyến' })
  return submit<Closure>(page, app + '/' + trip + '/close', 'Xác nhận đóng chuyến', dialog)
}

export async function closeWithLostAck(page: Page, trip: string) {
  const path = app + '/' + trip + '/close'
  const failed = page.waitForEvent('requestfailed', req => req.url().endsWith(path) && req.method() === 'POST')
  let closure: Closure | undefined
  await page.route('**' + path, async route => {
    const committed = await route.fetch({ maxRetries: 0 })
    expect(committed.ok(), await committed.text()).toBe(true)
    closure = await committed.json() as Closure
    await route.abort('failed')
  }, { times: 1 })
  const start = page.getByRole('button', { name: 'Đóng chuyến', exact: true })
  await expect(start).toBeEnabled()
  await start.press('Enter')
  await page.getByRole('dialog', { name: 'Xác nhận đóng chuyến' }).getByRole('button', { name: 'Xác nhận đóng chuyến' }).press('Enter')
  const closeCommand = (await failed).postDataJSON() as CloseInput
  expect(closure?.commandId).toBe(closeCommand.commandId)
  await expect(page.getByRole('status').filter({ hasText: 'Chưa xác nhận được kết quả đóng chuyến' })).toBeVisible()
  return { closure: closure!, closeCommand }
}

export async function assignSuccessor(page: Page, origin: string, line: string, target: string, quantity: number) {
  const form = page.locator('[data-source-id="' + line + '"]').getByRole('form', { name: 'Xếp chuyến kế nhiệm' })
  await form.getByLabel('Chuyến đích').selectOption(target)
  await form.getByLabel('Lượng xếp chuyến (EA)').fill(String(quantity))
  const path = app + '/' + origin + '/a-remainders/' + line
  const result = await submit<{ bindingId: string }>(page, path + '/assign-successor', 'Xếp chuyến', form)
  expect(result.bindingId).toBeTruthy()
  return result.bindingId
}

export async function transferHeld(page: Page, origin: string, line: string, binding: string, right: string, quantity: number) {
  const form = page.locator('[data-source-id="' + line + '"]').getByRole('form', { name: 'Chuyển HELD' })
  await form.getByLabel('Phần đã xếp').selectOption(binding)
  await form.getByLabel('Quyền giữ gốc').selectOption(right)
  await form.getByLabel('Lượng chuyển HELD (EA)').fill(String(quantity))
  return submit<{ successorRightId: string; quantity: number; returned: number }>(
    page, app + '/' + origin + '/a-remainders/' + line + '/transfer-held', 'Chuyển HELD', form,
  )
}

export async function reserveSuccessor(page: Page, origin: string, order: OrderSource, binding: string, warehouse: string, quantity: number) {
  await page.goto('/work/orders?trip=' + origin + '&order=' + order.orderId)
  const card = page.locator('[data-order-id="' + order.orderId + '"]')
  await card.getByRole('button', { name: 'Giữ hàng trên chuyến kế nhiệm', exact: true }).click()
  const form = card.getByRole('form', { name: 'Giữ bổ sung' })
  await form.getByLabel('Chuyến kế nhiệm').selectOption(binding)
  await form.getByLabel('Kho giữ').selectOption(warehouse)
  await form.getByLabel('Lượng giữ bổ sung').fill(String(quantity))
  return submit<{ sourceId: string; rightId: string }>(page,
    app + '/' + origin + '/a-remainders/' + order.orderLineId + '/reserve-unheld', 'Giữ bổ sung', form,
  )
}
