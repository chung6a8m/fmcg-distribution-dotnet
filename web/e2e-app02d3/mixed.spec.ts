import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type Browser, type BrowserContext, type Page, type Response } from '@playwright/test'

type Slot = 'A' | 'B'
type Role = 'sales' | 'cashier' | 'warehouse'
type Scene = {
  slot: Slot; trip: string; customer: string; shipTo: string; selection: string; outlet?: string | null
  sku: string; main: string; truck: string; order: string; cashWitness: string; cashierEvidence: string
}
type Checkpoint = { ok: boolean; aRight?: string; bRight?: string; aLine?: string; b10?: string; b20?: string }
type PostPayload = { producerId?: string; commandId: string; sourceNumber?: string; tripId?: string; orderId?: string }
type Outcome = { status?: string; outcomeCode?: string; code?: string; sourceId?: string; id?: string; reasonCode?: string }
type Observation = {
  endpoint: string; httpStatus: number; status: string; code?: string; sourceId?: string
  producerId?: string; commandId: string; sourceNumber?: string; tripId: string
}
type Revoked = { role: Role; offlinePostCount: number; queuedAfterOffline: boolean; revokedSubmitStatus: number }

const mixed = '/api/synthetic/app02d3'
const fieldButton: Record<Role, string> = { sales: 'Gửi đề nghị nộp tiền', cashier: 'Xác nhận đã nhận tiền', warehouse: 'Trả hàng tồn xe' }
const paths: Record<Role, string> = { sales: 'offer', cashier: 'accept', warehouse: 'unload' }

async function sceneFor(slot: Slot) {
  const fixtures = JSON.parse(await readFile(process.env.APP02D3_BROWSER_FIXTURE!, 'utf8')) as { scenes: Scene[] }
  const scene = fixtures.scenes.find(value => value.slot === slot)
  if (!scene) throw new Error('Missing synthetic D.3 tenant fixture ' + slot)
  return scene
}

async function save(slot: Slot, name: string, payload: object) {
  const file = join(process.env.APP02D3_CHECKPOINTS!, slot + '-' + name)
  await writeFile(file + '.tmp', JSON.stringify(payload, null, 2), { flag: 'wx' })
  await rename(file + '.tmp', file + '.json')
}

async function checkpoint(slot: Slot, phase: string) {
  await save(slot, phase + '.request', { slot, phase })
  const file = join(process.env.APP02D3_CHECKPOINTS!, slot + '-' + phase + '.response.json')
  let result: Checkpoint | undefined
  await expect.poll(async () => {
    try {
      result = JSON.parse(await readFile(file, 'utf8')) as Checkpoint
      return true
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      return false
    }
  }, { timeout: 20_000, message: 'SQL checkpoint ' + slot + ' ' + phase }).toBe(true)
  expect(result?.ok).toBe(true)
  return result!
}

async function connect(page: Page, slot: Slot, role: Role | 'finance', route: string) {
  const token = process.env['APP02D3_' + role.toUpperCase() + '_TOKEN_' + slot]
  if (!token) throw new Error('Missing signed DEV role credential for ' + role)
  await page.goto(route)
  if (route === '/sales/mixed-trip') await expect(page.getByRole('heading', { name: 'Đối soát chuyến Mixed' })).toBeVisible()
  await page.getByLabel('Mã truy cập DEV').fill(token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

async function pickTrip(page: Page, id: string) {
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(id)
}

async function post(page: Page, endpoint: string, button: string, pane = page.getByRole('main')) {
  const pending = page.waitForResponse(response =>
    new URL(response.url()).pathname.endsWith(endpoint) && response.request().method() === 'POST')
  await pane.getByRole('button', { name: button, exact: true }).click()
  return pending
}

async function applied(response: Response, trip: string, order?: string): Promise<Observation> {
  const data = await response.json() as Outcome
  expect(response.status(), JSON.stringify(data)).toBe(200)
  expect(data.status ?? data.code).toBe('APPLIED')
  const sourceId = data.sourceId ?? data.id
  expect(sourceId).toMatch(/^[a-f0-9-]{36}$/i)
  const sent = response.request().postDataJSON() as PostPayload
  if (order) expect(sent.orderId).toBe(order)
  else expect(sent.tripId).toBe(trip)
  return {
    endpoint: new URL(response.url()).pathname, httpStatus: response.status(), status: 'APPLIED',
    code: data.outcomeCode ?? data.code, sourceId, commandId: sent.commandId, producerId: sent.producerId,
    sourceNumber: sent.sourceNumber, tripId: trip,
  }
}

async function mixedPage(browser: Browser, contexts: BrowserContext[], slot: Slot, role: Role, trip: string) {
  const context = await browser.newContext()
  contexts.push(context)
  const page = await context.newPage()
  await connect(page, slot, role, '/sales/mixed-trip')
  await pickTrip(page, trip)
  await expect(page.getByRole('region', { name: 'Đối soát chuyến' })).toBeVisible()
  return { context, page }
}

async function deniedAfterRevocation(page: Page, context: BrowserContext, slot: Slot, role: Role): Promise<Revoked> {
  let attempts = 0
  const endpoint = mixed + '/' + paths[role]
  const listener = (request: { method(): string; url(): string }) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === endpoint) attempts++
  }
  page.on('request', listener)
  try {
    const submit = page.getByRole('button', { name: fieldButton[role], exact: true })
    await expect(submit).toBeEnabled()
    await context.setOffline(true)
    try {
      await expect(submit).toBeDisabled()
      await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.', { exact: true })).toBeVisible()
      expect(attempts).toBe(0)
    } finally { await context.setOffline(false) }
    const queuedAfterOffline = await page.evaluate(() =>
      [sessionStorage, localStorage].some(store => Object.keys(store).some(k => k.startsWith('app02d3.pending.'))))
    expect(queuedAfterOffline).toBe(false)
    const offlinePostCount = attempts

    await checkpoint(slot, 'revoke-' + role)
    let revokedSubmitStatus: number
    try {
      await expect(submit).toBeEnabled()
      const denied = await post(page, endpoint, fieldButton[role])
      revokedSubmitStatus = denied.status()
      expect(revokedSubmitStatus).toBe(403)
      await expect(page.getByRole('status').filter({ hasText: 'Quyền đã thay đổi' })).toBeVisible()
      const body = await denied.text()
      if (body.trim().startsWith('{')) {
        const result = JSON.parse(body) as Outcome
        expect(result.status).not.toBe('APPLIED')
        expect(result.sourceId).toBeFalsy()
      }
    } finally { await checkpoint(slot, 'restore-' + role) }
    return { role, offlinePostCount, queuedAfterOffline, revokedSubmitStatus }
  } finally { page.off('request', listener) }
}

for (const slot of ['A', 'B'] as const) {
  test('tenant ' + slot + ' D3 browser journey through T0–T6 and current authority', async ({ page, browser }, info) => {
    await page.goto('/sales/mixed-trip')
    await expect(page.getByRole('heading', { name: 'Đối soát chuyến Mixed' })).toBeVisible({ timeout: 3_000 })
    const s = await sceneFor(slot)
    const observations: Observation[] = []
    const revocations: Revoked[] = []
    const contexts: BrowserContext[] = []
    const phases: string[] = []
    const verify = async (phase: string) => {
      const result = await checkpoint(slot, phase)
      phases.push(phase)
      return result
    }
    try {
      await connect(page, slot, 'warehouse', '/sales/orders')
      const order = page.getByRole('article').filter({ has: page.getByRole('heading', { name: /D3-CANON$/ }) })
      await order.getByRole('combobox', { name: 'Kho', exact: true }).selectOption(s.main)
      await order.getByLabel('Số lượng giữ').fill('40')
      observations.push(await applied(await post(page, '/app02c2/reserve', 'Reserve hàng', order), s.trip, s.order))
      await expect(order.getByText('Đã giữ đủ', { exact: true })).toBeVisible()

      await connect(page, slot, 'warehouse', '/sales/b-stock')
      await pickTrip(page, s.trip)
      await page.getByLabel('Kho cấp quota').selectOption(s.main)
      await page.getByLabel('SKU', { exact: true }).selectOption(s.sku)
      await page.getByLabel('Số lượng quota').fill('30')
      observations.push(await applied(await post(page, '/app02d1/quota', 'Cấp quota B'), s.trip))
      await expect(page.getByText('Đã cấp quota B.', { exact: true })).toBeVisible()

      const finance = await browser.newContext()
      contexts.push(finance)
      const issuer = await finance.newPage()
      await connect(issuer, slot, 'finance', '/sales/b-stock')
      await pickTrip(issuer, s.trip)
      await issuer.getByLabel('Khách hàng', { exact: true }).selectOption(s.customer)
      await issuer.getByLabel('Địa chỉ giao đã chốt').selectOption(s.shipTo)
      await issuer.getByLabel('Số tiền cấp').fill('100')
      observations.push(await applied(await post(issuer, '/app02d1/credit', 'Cấp hạn mức B'), s.trip))
      const reserved = await verify('t0')
      if (!reserved.bRight) throw new Error('Missing actual B right from SQL checkpoint')
      await page.getByLabel('Quota nạp').selectOption(reserved.bRight)
      for (const qty of ['10', '20']) {
        await page.getByLabel('Số lượng nạp B').fill(qty)
        const loaded = await post(page, '/app02d1/load', 'Nạp hàng B')
        observations.push(await applied(loaded, s.trip))
        await expect(page.getByText('Đã nạp hàng B.', { exact: true })).toBeVisible()
        if (qty === '10') {
          await expect(page.getByRole('button', { name: 'Nạp hàng B', exact: true })).toBeEnabled()
          await page.getByLabel('Quota nạp').selectOption(reserved.bRight)
        }
      }

      await connect(page, slot, 'warehouse', '/sales/load')
      await pickTrip(page, s.trip)
      await page.getByLabel('Số lượng nạp dòng 1').fill('40')
      observations.push(await applied(await post(page, '/app02c3/load', 'Nạp hàng'), s.trip))
      await expect(page.getByText('Đã nạp hàng lên xe.', { exact: true })).toBeVisible()
      const loads = await verify('t1')
      await verify('t2')
      if (!loads.b20) throw new Error('Missing actual B20 line from SQL checkpoint')

      await connect(page, slot, 'sales', '/sales/b-sale')
      await pickTrip(page, s.trip)
      await page.getByLabel('Dòng hàng B').selectOption(loads.b20)
      await page.getByLabel('Khách hàng', { exact: true }).selectOption(s.customer)
      await page.getByLabel('ShipTo đã chốt').selectOption(s.selection)
      await page.getByLabel('Số lượng bán').fill('20')
      await page.getByLabel('Số tiền mặt thực nhận').fill('200')
      await page.getByLabel('Chứng cứ tiền mặt đã xác minh').fill(s.cashWitness)
      await page.getByRole('button', { name: 'Xem trước giao dịch', exact: true }).click()
      await expect(page.getByText('Phải trả 300', { exact: true })).toBeVisible()
      await expect(page.getByText('Công nợ phát sinh 100', { exact: true })).toBeVisible()
      observations.push(await applied(await post(page, '/app02d2/sales', 'Ghi bán và giao'), s.trip))
      await expect(page.getByText('Đã ghi bán và giao.', { exact: true })).toBeVisible()
      await verify('t3')

      await connect(page, slot, 'sales', '/sales/delivery')
      await page.getByRole('combobox', { name: 'Chuyến giao', exact: true }).selectOption(s.trip)
      await page.getByLabel('Số lượng đã giao dòng 1').fill('25')
      observations.push(await applied(await post(page, '/app02c4/deliver', 'Xác nhận đã giao'), s.trip))
      await expect(page.getByText('Đã ghi giao hàng và công nợ.', { exact: true })).toBeVisible()
      await verify('t4')

      const sales = await mixedPage(browser, contexts, slot, 'sales', s.trip)
      await sales.page.getByLabel('Số tiền đề nghị nộp').fill('200')
      revocations.push(await deniedAfterRevocation(sales.page, sales.context, slot, 'sales'))
      await sales.page.getByLabel('Số tiền đề nghị nộp').fill('200')
      const offer = await applied(await post(sales.page, mixed + '/offer', fieldButton.sales), s.trip)
      observations.push(offer)
      await verify('offer')

      const cashier = await mixedPage(browser, contexts, slot, 'cashier', s.trip)
      await cashier.page.getByLabel('Chứng cứ kiểm đếm').fill(s.cashierEvidence)
      revocations.push(await deniedAfterRevocation(cashier.page, cashier.context, slot, 'cashier'))
      await cashier.page.getByLabel('Chứng cứ kiểm đếm').fill(s.cashierEvidence)
      const acceptance = await post(cashier.page, mixed + '/accept', fieldButton.cashier)
      expect(acceptance.request().postDataJSON()).toMatchObject({ offerId: offer.sourceId, countEvidenceRef: s.cashierEvidence })
      observations.push(await applied(acceptance, s.trip))
      await verify('t5')

      const warehouse = await mixedPage(browser, contexts, slot, 'warehouse', s.trip)
      revocations.push(await deniedAfterRevocation(warehouse.page, warehouse.context, slot, 'warehouse'))
      observations.push(await applied(await post(warehouse.page, mixed + '/unload', fieldButton.warehouse), s.trip))
      await expect(warehouse.page.getByText('Đang đối soát', { exact: true })).toBeVisible()
      await verify('t6')
      await save(slot, 'journey', { slot, trip: s.trip, phases, observations })
      await save(slot, 'D3-17', { slot, trip: s.trip, checks: revocations })
      await info.attach('D.3 browser commands', { body: JSON.stringify({ phases, observations, revocations }), contentType: 'application/json' })
    } finally { await Promise.all(contexts.map(context => context.close())) }
  })
}
