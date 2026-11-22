import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'

type Slot = 'A' | 'B'
type Role = 'invoice' | 'accountant' | 'reconciler' | 'maker' | 'checker'
type Scene = { slot: Slot; a10: string; a15: string; b20: string }
type Outcome = { status: string; id?: string; intentId?: string; deliveryId?: string; sourceLineId?: string; factId?: string; postingId?: string }
type Snapshot = {
  report: { id: string; status: string; expectedCount: number; receivedCount: number; postedCount: number; okCount: number; diffCount: number }
  lines: { deliveryId: string; caseId?: string; diffGross: number; diffAr: number }[]
}

const api = '/api/synthetic/app02d4'
const route = '/sales/partner-rehearsal'
const checkpoints = process.env.APP02D4_CHECKPOINTS!
const from = '2026-11-23T03:10:00Z'
const cutoff = '2026-11-23T04:00:00Z'
const invoiceSend = 'Gửi hóa đơn mô phỏng'

async function sceneFor(slot: Slot) {
  const fixture = JSON.parse(await readFile(process.env.APP02D4_BROWSER_FIXTURE!, 'utf8')) as { scenes: Scene[] }
  const scene = fixture.scenes.find(item => item.slot === slot)
  expect(scene, 'D.4 browser fixture ' + slot).toBeDefined()
  return scene!
}

async function save(file: string, data: object) {
  const target = join(checkpoints, file + '.json')
  await writeFile(target + '.tmp', JSON.stringify(data, null, 2), { flag: 'wx' })
  await rename(target + '.tmp', target)
}

async function checkpoint(slot: Slot, phase: string) {
  const key = slot + '-' + phase
  await save(key + '.request', { slot, phase })
  await expect.poll(async () => {
    try {
      return (JSON.parse(await readFile(join(checkpoints, key + '.response.json'), 'utf8')) as { ok: boolean }).ok
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      return false
    }
  }, { timeout: 20_000, message: 'SQL checkpoint ' + key }).toBe(true)
}

async function connect(page: Page, slot: Slot, role: Role) {
  const token = process.env['APP02D4_' + role.toUpperCase() + '_TOKEN_' + slot]
  if (!token) throw new Error('Missing signed DEV role credential for ' + role)
  const disconnect = page.getByRole('button', { name: 'Đổi phiên DEV' })
  if (await disconnect.isVisible()) await disconnect.click()
  else await page.goto(route)
  await page.getByLabel('Mã truy cập DEV').fill(token)
  await page.getByRole('button', { name: 'Kết nối', exact: true }).click()
  await expect(page.getByRole('combobox', { name: 'Giao hàng' })).toBeVisible()
}

async function pick(page: Page, deliveryId: string) {
  await page.getByRole('combobox', { name: 'Giao hàng' }).selectOption(deliveryId)
}

async function responseFor(page: Page, endpoint: string, button: string) {
  const request = page.waitForResponse(response =>
    new URL(response.url()).pathname === api + endpoint && response.request().method() === 'POST')
  await page.getByRole('button', { name: button, exact: true }).click()
  return request
}

async function post<T extends object>(page: Page, endpoint: string, button: string) {
  const response = await responseFor(page, endpoint, button)
  const body = await response.json() as T
  expect(response.status(), JSON.stringify(body)).toBe(200)
  return body
}

async function prepare(page: Page, deliveryId: string) {
  await pick(page, deliveryId)
  const intent = await post<Outcome>(page, '/invoice/prepare', 'Tạo yêu cầu hóa đơn')
  expect(intent).toMatchObject({ status: 'PREPARED', deliveryId })
  expect(intent.intentId).toMatch(/^[a-f0-9-]{36}$/i)
  return intent
}

async function snapshot(page: Page, asOf: string) {
  await page.getByLabel('Từ (gồm)').fill(from)
  await page.getByLabel('Cutoff (loại trừ)').fill(cutoff)
  await page.getByLabel('Đối soát tính đến').fill(asOf)
  return post<Snapshot>(page, '/accounting/reconcile', 'Chạy đối soát')
}

for (const slot of ['A', 'B'] as const) {
  test('D.4 browser invoice, accounting and online guard · ' + slot, async ({ page, context }, testInfo) => {
    const scene = await sceneFor(slot)
    await checkpoint(slot, 'clock-start')
    await connect(page, slot, 'invoice')
    const first = await prepare(page, scene.a10)
    const second = await prepare(page, scene.a15)
    const fromB = await prepare(page, scene.b20)
    expect(new Set([first.intentId, second.intentId, fromB.intentId]).size).toBe(3)

    await pick(page, scene.b20)
    const recordedB = await post<Outcome>(page, '/invoice/' + fromB.intentId + '/send', invoiceSend)
    expect(recordedB).toMatchObject({ status: 'MOCK_RECORDED', intentId: fromB.intentId })
    expect(recordedB.factId).toBeTruthy()

    await pick(page, scene.a10)
    await page.getByLabel('Kịch bản gửi DEV').selectOption('CRASH_AFTER_COMMIT')
    const unknown = await post<Outcome>(page, '/invoice/' + first.intentId + '/send', invoiceSend)
    expect(unknown).toMatchObject({ status: 'UNKNOWN', intentId: first.intentId })
    await checkpoint(slot, 'restart-invoice')
    const recovered = await post<Outcome>(page, '/invoice/' + first.intentId + '/lookup', 'Tra cứu hóa đơn gốc')
    expect(recovered).toMatchObject({ status: 'MOCK_RECORDED', intentId: first.intentId })
    expect(recovered.factId).toBeTruthy()

    await pick(page, scene.a15)
    await page.getByLabel('Kịch bản gửi DEV').selectOption('')
    const send = page.getByRole('button', { name: invoiceSend, exact: true })
    const attempted: string[] = []
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith(api)) attempted.push(request.url())
    })
    await expect(send).toBeEnabled()
    await context.setOffline(true)
    await expect(send).toBeDisabled()
    await send.evaluate((button: HTMLButtonElement) => button.click())
    await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.', { exact: true })).toBeVisible()
    await context.setOffline(false)
    await expect(send).toBeEnabled()
    await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.', { exact: true })).toBeHidden()
    const queuedAfterOffline = await page.evaluate(() =>
      [sessionStorage, localStorage].some(store => Object.keys(store).some(key => key.startsWith('app02d4.'))))
    expect(queuedAfterOffline).toBe(false)
    const offlinePostCount = attempted.length
    expect(offlinePostCount).toBe(0)

    let revokedHttp = 0
    try {
      await checkpoint(slot, 'revoke-invoice')
      const unauthorized = await responseFor(page, '/invoice/' + second.intentId + '/send', invoiceSend)
      revokedHttp = unauthorized.status()
      expect(revokedHttp).toBe(403)
      await expect(page.getByText('Bạn không còn quyền thực hiện thao tác trong phiên DEV này.')).toBeVisible()
      expect(attempted).toHaveLength(1)
    } finally {
      await checkpoint(slot, 'restore-invoice')
    }

    await connect(page, slot, 'accountant')
    const receipts: Outcome[] = []
    for (const delivery of [scene.a10, scene.a15, scene.b20]) {
      await pick(page, delivery)
      const receipt = await post<Outcome>(page, '/accounting/' + delivery + '/receive', 'Tiếp nhận nguồn')
      expect(receipt.status).toBe('RECEIVED')
      receipts.push(receipt)
    }
    const postings: Outcome[] = []
    for (const delivery of [scene.a10, scene.a15, scene.b20]) {
      await pick(page, delivery)
      if (delivery === scene.b20) await page.getByLabel('Kịch bản ghi sổ DEV').selectOption('wrong-amount')
      const posting = await post<Outcome>(page, '/accounting/' + delivery + '/post', 'Ghi sổ nguồn')
      expect(posting.status).toBe('POSTED')
      postings.push(posting)
    }

    await connect(page, slot, 'reconciler')
    await checkpoint(slot, 'clock-first-reconcile')
    const original = await snapshot(page, '2026-11-23T04:00:00Z')
    expect(original.report).toMatchObject({ status: 'RECONCILED_DIFF', expectedCount: 3, receivedCount: 3, postedCount: 3, okCount: 2, diffCount: 1 })
    const wrong = original.lines.find(line => line.deliveryId === scene.b20)
    expect(wrong).toMatchObject({ diffGross: 10, diffAr: 10 })
    expect(wrong?.caseId).toBeTruthy()
    await expect(page.getByRole('region', { name: 'Kết quả đối soát' }).getByText('RECONCILED_DIFF', { exact: true }).first()).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath('doi-soat-lech.png') })

    await checkpoint(slot, 'clock-correction')
    await connect(page, slot, 'maker')
    await pick(page, scene.b20)
    await page.getByLabel('Mã báo cáo').fill(original.report.id)
    const proposal = await post<Outcome>(page, '/accounting/reports/' + original.report.id + '/corrections', 'Đề nghị điều chỉnh')
    expect(proposal.status).toBe('PROPOSED')
    expect(proposal.id).toBeTruthy()

    await connect(page, slot, 'checker')
    await page.getByLabel('Mã đề nghị điều chỉnh').fill(proposal.id!)
    const correction = await post<Outcome>(page, '/accounting/corrections/' + proposal.id + '/approve', 'Duyệt điều chỉnh')
    expect(correction.status).toBe('CORRECTION_POSTED')

    await checkpoint(slot, 'clock-reconcile')
    await connect(page, slot, 'reconciler')
    const corrected = await snapshot(page, '2026-11-23T04:10:00Z')
    expect(corrected.report).toMatchObject({ status: 'RECONCILED_OK', expectedCount: 3, okCount: 3, diffCount: 0 })
    await expect(page.getByRole('region', { name: 'Kết quả đối soát' }).getByText('RECONCILED_OK', { exact: true }).first()).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath('doi-soat-da-khop.png') })

    await page.getByLabel('Mã hồ sơ sai lệch').fill(wrong!.caseId!)
    const closed = await post<Outcome>(page, '/accounting/cases/' + wrong!.caseId + '/close', 'Đóng hồ sơ sai lệch')
    expect(closed.status).toBe('CLOSED')
    await save(slot + '-browser', {
      slot, invoice: { first, second, fromB, recordedB, unknown, recovered }, receipts, postings,
      original, proposal, correction, corrected, closed, access: { revokedHttp, offlinePostCount, queuedAfterOffline },
    })
  })
}
