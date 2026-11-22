import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'

type Slot = 'A' | 'B'
type Snapshot = {
  main: number; mainValue: number; truck: number; truckValue: number; allocated: number
  aWarehouse: number; aLoaded: number; bWarehouse: number; bLoaded: number
  credit: number; consumed: number; financial: number
}
type Check = { phase: string; expected: Partial<Snapshot>; actual: Partial<Snapshot>; diff: string[] }

async function connect(page: Page, route: string, key: string) {
  const token = process.env[key]
  if (!token) throw new Error('Missing signed DEV role credential')
  await page.goto(route)
  await page.getByLabel('Mã truy cập DEV').fill(token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
}

async function checkpoint(slot: Slot, phase: string, expected: Partial<Snapshot>, evidence: Check[]) {
  const directory = process.env.APP02D1_CHECKPOINTS
  if (!directory) throw new Error('Missing SQL checkpoint directory')
  const file = join(directory, `${slot}-${phase}`)
  await writeFile(file + '.tmp', JSON.stringify({ slot, phase }), { flag: 'wx' })
  await rename(file + '.tmp', file + '.request.json')
  let actual: Snapshot | undefined
  await expect.poll(async () => {
    try {
      actual = JSON.parse(await readFile(file + '.response.json', 'utf8')) as Snapshot
      return true
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      return false
    }
  }, { timeout: 15_000, message: `SQL checkpoint ${slot} ${phase}` }).toBe(true)
  const diff = (Object.keys(expected) as (keyof Snapshot)[]).filter(key => actual?.[key] !== expected[key])
  evidence.push({ phase, expected, actual: actual!, diff })
  expect(diff, `SQL mismatch ${slot} ${phase}`).toEqual([])
}

async function submit(page: Page, endpoint: string, button: string, status: number,
  observed: { endpoint: string; expected: number; actual: number }[]) {
  const pending = page.waitForResponse(response => response.url().endsWith(endpoint) && response.request().method() === 'POST')
  await page.getByRole('button', { name: button, exact: true }).click()
  const response = await pending
  observed.push({ endpoint, expected: status, actual: response.status() })
  expect(response.status()).toBe(status)
  return response
}

async function reserveA(page: Page, slot: Slot, warehouseToken: string, tripId: string | undefined,
  http: { endpoint: string; expected: number; actual: number }[]) {
  const salesToken = 'APP02D1_SALES_TOKEN_' + slot
  const customerLabel = 'C2-FIXTURE · Khách hàng C.2 DEV'
  const directAddress = 'D1 Duong DEV'
  await connect(page, '/sales/customers', salesToken)
  await page.getByRole('button', { name: /C2-FIXTURE/ }).click()
  await page.getByRole('button', { name: 'Nhập địa chỉ một lần' }).click()
  await page.getByLabel('Quốc gia').selectOption('VN')
  await page.getByLabel('Vùng DEV').selectOption('HCM')
  await page.getByLabel('Khu vực').fill('Khu DEV')
  await page.getByLabel('Địa chỉ giao', { exact: true }).fill(directAddress)
  await page.getByLabel('Người nhận').fill('Nguoi Nhan')
  await submit(page, '/app02c1/ship-to-selections', 'Lưu lựa chọn', 200, http)
  await expect(page.getByText(directAddress + ', Khu DEV, HCM, VN')).toBeVisible()

  await connect(page, '/sales/orders', salesToken)
  await page.getByRole('combobox', { name: 'Khách hàng', exact: true }).selectOption({ label: customerLabel })
  await page.getByRole('combobox', { name: 'ShipTo đã chọn' }).selectOption({ label: directAddress + ' · Khu DEV · Một lần' })
  await page.getByRole('combobox', { name: 'Chuyến' }).selectOption(tripId ?? { label: 'C2-TRIP' })
  await page.getByRole('combobox', { name: 'SKU dòng 1' }).selectOption({ label: 'C2-SKU · Hàng tổng hợp' })
  await page.getByLabel('Số lượng dòng 1').fill('40')
  await submit(page, '/app02c2/drafts', 'Lưu đơn nháp', 200, http)
  await expect(page.getByText('Đơn nháp', { exact: true })).toBeVisible()
  await submit(page, '/app02c2/confirm', 'Confirm đơn', 200, http)
  await expect(page.getByText('Đã Confirm · Chưa giữ hàng')).toBeVisible()

  await connect(page, '/sales/orders', warehouseToken)
  await page.getByRole('combobox', { name: 'Kho', exact: true }).selectOption({ label: 'C2-MAIN' })
  await page.getByLabel('Số lượng giữ').fill('40')
  await submit(page, '/app02c2/reserve', 'Reserve hàng', 200, http)
  await expect(page.getByText('Đã giữ đủ', { exact: true })).toBeVisible()
}

for (const slot of ['A', 'B'] as const) {
  test(`tenant ${slot} D1-02 D1-04 D1-07 D1-12 D1-14 browser and read-only SQL`, async ({ page, browser, context }, info) => {
    const evidence: Check[] = []
    const http: { endpoint: string; expected: number; actual: number }[] = []
    const warehouseToken = 'APP02D1_WAREHOUSE_TOKEN_' + slot
    const financeToken = 'APP02D1_FINANCE_TOKEN_' + slot
    const tripId = process.env['APP02D1_TRIP_' + slot]
    const route = '/sales/b-stock'
    const financialZero = { consumed: 0, financial: 0 }
    const reservedA = { main: 100, mainValue: 1000, truck: 0, truckValue: 0, allocated: 40, aWarehouse: 40,
      aLoaded: 0, bWarehouse: 0, bLoaded: 0, credit: 0, ...financialZero }
    const quota = { ...reservedA, allocated: 70, bWarehouse: 30 }
    const credited = { ...quota, credit: 100 }
    const b10 = { ...credited, main: 90, mainValue: 900, truck: 10, truckValue: 100, allocated: 60,
      bWarehouse: 20, bLoaded: 10 }
    const b20 = { ...b10, main: 70, mainValue: 700, truck: 30, truckValue: 300, allocated: 40,
      bWarehouse: 0, bLoaded: 30 }
    try {
      await checkpoint(slot, 'stock-opening', { ...reservedA, allocated: 0, aWarehouse: 0 }, evidence)
      await test.step('D1-02: Sales confirms the A order and Warehouse reserves 40', async () => {
        await reserveA(page, slot, warehouseToken, tripId, http)
        await checkpoint(slot, 'opening', reservedA, evidence)
      })
      await connect(page, route, warehouseToken)
      const trip = page.getByRole('combobox', { name: 'Chuyến' })
      await expect(trip).toBeVisible()
      if (tripId) await trip.selectOption(tripId)
      await page.getByLabel('Kho cấp quota').selectOption({ label: 'C2-MAIN' })
      await page.getByLabel('SKU', { exact: true }).selectOption({ label: 'C2-SKU · Hàng tổng hợp' })
      await test.step('D1-02: grant 30 and reject 31', async () => {
        await page.getByLabel('Số lượng quota').fill('30')
        await submit(page, '/app02d1/quota', 'Cấp quota B', 200, http)
        await expect(page.getByRole('status').filter({ hasText: 'Đã cấp quota B.' })).toBeVisible()
        await checkpoint(slot, 'quota', quota, evidence)
        await page.getByLabel('Số lượng quota').fill('31')
        await submit(page, '/app02d1/quota', 'Cấp quota B', 422, http)
        await expect(page.getByRole('status').filter({ hasText: 'không đủ tồn khả dụng' })).toBeVisible()
        await checkpoint(slot, 'quota-rejected', quota, evidence)
      })
      await test.step('D1-04: distinct Finance session issues credit on approved proof', async () => {
        const finance = await browser.newContext()
        try {
          const issuer = await finance.newPage()
          await connect(issuer, route, financeToken)
          if (tripId) await issuer.getByRole('combobox', { name: 'Chuyến' }).selectOption(tripId)
          await expect(issuer.getByLabel('Số tiền cấp')).toBeVisible()
          const customerId = process.env['APP02D1_CUSTOMER_' + slot]
          const shipToId = process.env['APP02D1_SHIPTO_' + slot]
          if (customerId) await issuer.getByLabel('Khách hàng', { exact: true }).selectOption(customerId)
          if (shipToId) await issuer.getByLabel('Địa chỉ giao đã chốt').selectOption(shipToId)
          await issuer.getByLabel('Số tiền cấp').fill('100')
          await submit(issuer, '/app02d1/credit', 'Cấp hạn mức B', 200, http)
          await expect(issuer.getByText(/Phiên bản duyệt/)).toBeVisible()
          await expect(issuer.getByRole('status').filter({ hasText: 'Đã cấp hạn mức B.' })).toBeVisible()
        } finally { await finance.close() }
        await checkpoint(slot, 'credit', credited, evidence)
      })
      await test.step('D1-07: first B batch moves 10 EA preserving A40', async () => {
        await page.getByLabel('Số lượng nạp B').fill('10')
        await submit(page, '/app02d1/load', 'Nạp hàng B', 200, http)
        await expect(page.getByText(/Còn giữ tại kho 20 EA/)).toBeVisible()
        await checkpoint(slot, 'b10', b10, evidence)
      })
      await test.step('D1-14: offline blocks writes and lost ACK recovers original command', async () => {
        let attempts = 0
        const observe = (request: { method(): string; url(): string }) => {
          if (request.method() === 'POST' && request.url().endsWith('/app02d1/load')) attempts++
        }
        page.on('request', observe)
        try {
          await page.getByLabel('Số lượng nạp B').fill('20')
          await context.setOffline(true)
          await expect(page.getByRole('button', { name: 'Nạp hàng B' })).toBeDisabled()
          await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.')).toBeVisible()
          expect(attempts).toBe(0)
          await checkpoint(slot, 'offline', b10, evidence)
          await context.setOffline(false)
          let commandId = ''
          await page.route('**/app02d1/load', async intercepted => {
            const captured = intercepted.request().postDataJSON() as { commandId: string }
            commandId = captured.commandId
            let applied: Awaited<ReturnType<typeof intercepted.fetch>>
            try { applied = await intercepted.fetch() }
            catch { throw new Error('Synthetic load ACK probe failed') }
            http.push({ endpoint: '/app02d1/load', expected: 200, actual: applied.status() })
            expect(applied.status()).toBe(200)
            await intercepted.abort('failed')
          }, { times: 1 })
          await page.getByRole('button', { name: 'Nạp hàng B' }).click()
          await expect(page.getByRole('status').filter({ hasText: 'Chưa rõ kết quả' })).toBeVisible()
          await context.setOffline(true)
          await expect(page.getByRole('button', { name: 'Tra cứu yêu cầu' })).toBeDisabled()
          await context.setOffline(false)
          const result = page.waitForResponse(response =>
            response.url().endsWith('/commands/' + commandId) && response.request().method() === 'GET')
          await page.getByRole('button', { name: 'Tra cứu yêu cầu' }).click()
          const lookedUp = await result
          http.push({ endpoint: '/trip/commands/{original}', expected: 200, actual: lookedUp.status() })
          expect(lookedUp.status()).toBe(200)
          expect(attempts).toBe(1)
          await expect(page.getByText(/Đã nạp 30 EA/)).toBeVisible()
        } finally {
          await context.setOffline(false)
          page.off('request', observe)
        }
      })
      await checkpoint(slot, 'b20', b20, evidence)
      await test.step('D1-07: current A path loads its own right', async () => {
        await connect(page, '/sales/load', warehouseToken)
        if (tripId) await page.getByRole('combobox', { name: 'Chuyến' }).selectOption(tripId)
        await page.getByLabel('Số lượng nạp dòng 1').fill('40')
        await submit(page, '/app02c3/load', 'Nạp hàng', 200, http)
        await expect(page.getByText(/Đã nạp 40 EA/)).toBeVisible()
        await checkpoint(slot, 'a40', { ...b20, main: 30, mainValue: 300, truck: 70, truckValue: 700,
          allocated: 0, aWarehouse: 0, aLoaded: 40 }, evidence)
      })
      await test.step('D1-12: Warehouse grant revoked after the quota form renders', async () => {
        const posted = { ...b20, main: 30, mainValue: 300, truck: 70, truckValue: 700,
          allocated: 0, aWarehouse: 0, aLoaded: 40 }
        await connect(page, route, warehouseToken)
        if (tripId) await page.getByRole('combobox', { name: 'Chuyến' }).selectOption(tripId)
        await page.getByLabel('Số lượng quota').fill('1')
        await checkpoint(slot, 'revoke-warehouse', posted, evidence)
        try {
          await submit(page, '/app02d1/quota', 'Cấp quota B', 403, http)
          await expect(page.getByRole('status').filter({ hasText: 'quyền đã thay đổi' })).toBeVisible()
        } finally {
          await checkpoint(slot, 'restore-warehouse', posted, evidence)
        }
      })
    } finally {
      await writeFile(info.outputPath('sql-assertions.json'), JSON.stringify(evidence, null, 2))
      await writeFile(info.outputPath('http-observations.json'), JSON.stringify(http, null, 2))
    }
  })
}
