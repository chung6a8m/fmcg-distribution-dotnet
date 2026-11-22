import { readFile, rename, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { expect, test, type Page, type Response } from '@playwright/test'

type Slot = 'A' | 'B'
type Case = 'D2-02' | 'D2-03' | 'D2-04' | 'D2-05' | 'D2-06' | 'D2-17'
type SaleInput = {
  producerId: string; commandId: string; sourceNumber: string
  tripId: string; customerId: string; shipToSnapshotId: string; selectionId: string
  outletId: string | null; tripManifestLineId: string; skuId: string; baseQuantity: number
  receivedCashAmount: number; verificationEvidenceRef: string
  pendingBankEvidenceRef: string | null; customerCreditReservationId: string | null
}
type Scene = { slot: Slot; caseId: Case; request: SaleInput }
const checkpointDir = process.env.APP02D2_CHECKPOINTS!
const fixtureFile = process.env.APP02D2_BROWSER_FIXTURE!
const sales = '/api/synthetic/app02d2/sales'
const preview = '/api/synthetic/app02d2/preview'

async function getScene(slot: Slot, caseId: Case): Promise<Scene> {
  const fixture = JSON.parse((await readFile(fixtureFile, 'utf8')).replace(/^\uFEFF/, '')) as { scenes: Scene[] }
  const scene = fixture.scenes.find(item => item.slot === slot && item.caseId === caseId)
  if (!scene) throw new Error(`Missing browser scene ${slot}/${caseId}`)
  return scene
}

async function connect(page: Page, slot: Slot) {
  const token = process.env['APP02D2_SALES_TOKEN_' + slot]
  if (!token) throw new Error('Missing signed DEV sales credential')
  await page.goto('/sales/b-sale')
  await page.getByLabel('Mã truy cập DEV').fill(token)
  await page.getByRole('button', { name: 'Kết nối' }).click()
  await expect(page.getByLabel('Chuyến', { exact: true })).toBeVisible()
}

async function fillSale(page: Page, request: SaleInput) {
  await page.getByLabel('Chuyến', { exact: true }).selectOption(request.tripId)
  await page.getByLabel('Dòng hàng B').selectOption(request.tripManifestLineId)
  await page.getByLabel('Khách hàng', { exact: true }).selectOption(request.customerId)
  await page.getByLabel('ShipTo đã chốt').selectOption(request.selectionId)
  await page.getByLabel('Số lượng bán').fill(String(request.baseQuantity))
  if (request.pendingBankEvidenceRef) {
    await page.getByLabel('Nguồn thanh toán').selectOption('bank')
    await page.getByLabel('Chứng cứ ngân hàng chờ xác minh').fill(request.pendingBankEvidenceRef)
  } else {
    await page.getByLabel('Số tiền mặt thực nhận').fill(String(request.receivedCashAmount))
    await page.getByLabel('Chứng cứ tiền mặt đã xác minh').fill(request.verificationEvidenceRef)
    if (request.customerCreditReservationId)
      await page.getByLabel('Mã CustomerCredit đã giữ').fill(request.customerCreditReservationId)
  }
}

async function clickAndCapture(page: Page, label: string, path: string, method: 'GET' | 'POST') {
  const [response] = await Promise.all([
    page.waitForResponse(candidate => new URL(candidate.url()).pathname === path && candidate.request().method() === method),
    page.getByRole('button', { name: label, exact: true }).click(),
  ])
  return response
}

async function showPreview(page: Page, expectedAr: number) {
  const response = await clickAndCapture(page, 'Xem trước giao dịch', preview, 'POST')
  expect(response.status()).toBe(200)
  await expect(page.getByText('Phải trả 100', { exact: true })).toBeVisible()
  await expect(page.getByText(`Công nợ phát sinh ${expectedAr}`, { exact: true })).toBeVisible()
}

async function savedJson(slot: Slot, suffix: string, value: object) {
  const path = join(checkpointDir, `${slot}-${suffix}`)
  await writeFile(path + '.tmp', JSON.stringify(value), { flag: 'wx' })
  await rename(path + '.tmp', path + '.json')
}

async function authority(slot: Slot, phase: 'revoke' | 'restore') {
  await savedJson(slot, phase + '.request', { slot, phase })
  await expect.poll(async () => {
    try {
      await readFile(join(checkpointDir, `${slot}-${phase}.response.json`), 'utf8')
      return true
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      return false
    }
  }, { timeout: 12_000, message: `SQL writer ${phase} checkpoint ${slot}` }).toBe(true)
}

async function assertApplied(response: Response) {
  const body = await response.json() as { status: string; sourceId: string | null }
  expect(response.status()).toBe(200)
  expect(body.status).toBe('APPLIED')
  expect(body.sourceId).toMatch(/^[0-9a-f-]{36}$/i)
  const request = response.request().postDataJSON() as SaleInput
  return {
    ...commandIdentity(request), sourceId: body.sourceId!, status: body.status, httpStatus: response.status(),
  }
}

function commandIdentity(request: Pick<SaleInput, 'producerId' | 'commandId' | 'sourceNumber'>) {
  return { producerId: request.producerId, commandId: request.commandId, sourceNumber: request.sourceNumber }
}

async function assertDenied(response: Response) {
  expect([401, 403, 404]).toContain(response.status())
  const body = (await response.text()).trim()
  if (body.startsWith('{')) {
    const payload = JSON.parse(body) as { status?: string; sourceId?: string }
    expect(payload.status).not.toBe('APPLIED')
    expect(payload.sourceId).toBeFalsy()
  }
  return response.status()
}

const payableCases = [['D2-02', 40], ['D2-03', 0], ['D2-04', 100], ['D2-05', 20], ['D2-06', 0]] as const

for (const slot of ['A', 'B'] as const) {
  for (const [caseId, expectedAr] of payableCases) {
    test(`tenant ${slot} ${caseId} browser posting`, async ({ page }, info) => {
      const { request } = await getScene(slot, caseId)
      await connect(page, slot)
      await fillSale(page, request)
      await showPreview(page, expectedAr)
      const response = await clickAndCapture(page, 'Ghi bán và giao', sales, 'POST')
      const observed = await assertApplied(response)
      const sent = response.request().postDataJSON() as SaleInput
      expect(sent).toMatchObject({
        tripId: request.tripId, customerId: request.customerId, selectionId: request.selectionId,
        shipToSnapshotId: request.shipToSnapshotId, outletId: request.outletId ?? null,
        tripManifestLineId: request.tripManifestLineId, skuId: request.skuId, baseQuantity: Number(request.baseQuantity),
        receivedCashAmount: Number(request.receivedCashAmount),
        pendingBankEvidenceRef: request.pendingBankEvidenceRef ?? null,
        customerCreditReservationId: request.customerCreditReservationId ?? null,
      })
      await expect(page.getByRole('status').filter({ hasText: 'Đã ghi bán và giao.' })).toBeVisible()
      await expect(page.getByText(`Mã nguồn ${observed.sourceId}`, { exact: true })).toBeVisible()
      if (caseId === 'D2-04') {
        await expect(page.getByRole('status').filter({ hasText: 'Ngân hàng chờ xác minh.' })).toBeVisible()
        const path = sales + '/' + encodeURIComponent(observed.producerId) + '/' + encodeURIComponent(observed.commandId)
        const reopened = await clickAndCapture(page, 'Mở lại kết quả', path, 'GET')
        expect(reopened.status()).toBe(200)
        expect(await reopened.json()).toMatchObject({
          status: 'APPLIED', outcomeCode: 'D2_BANK_PENDING_POSTED', sourceId: observed.sourceId,
        })
        await expect(page.getByRole('status').filter({ hasText: 'Ngân hàng chờ xác minh.' })).toBeVisible()
      }
      await savedJson(slot, caseId, observed)
      await info.attach('command-result', { body: JSON.stringify(observed), contentType: 'application/json' })
    })
  }

  test(`tenant ${slot} D2-17 offline and revoked Sales writer`, async ({ page, context }, info) => {
    const { request } = await getScene(slot, 'D2-17')
    await connect(page, slot)
    await fillSale(page, request)
    const expectedAr = Math.max(0, 100 - Number(request.receivedCashAmount))
    await showPreview(page, expectedAr)
    let postCount = 0
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === sales) postCount++
    })
    await context.setOffline(true)
    try {
      await expect(page.getByRole('button', { name: 'Ghi bán và giao', exact: true })).toBeDisabled()
      await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.', { exact: true })).toBeVisible()
    } finally { await context.setOffline(false) }
    await expect(page.getByRole('button', { name: 'Ghi bán và giao', exact: true })).toBeEnabled()
    const offlinePostCount = postCount
    const queuedAfterOffline = await page.evaluate(() =>
      [sessionStorage, localStorage].some(store => Object.keys(store).some(key => key.startsWith('app02d2.pending.'))))
    expect(offlinePostCount).toBe(0)
    expect(queuedAfterOffline).toBe(false)

    const other = await context.newPage()
    await connect(other, slot)
    await fillSale(other, request)
    await showPreview(other, expectedAr)

    expect(postCount).toBe(0)
    const applied: (ReturnType<typeof commandIdentity> & { sourceId: string; status: string; httpStatus: number })[] = []
    await page.route('**/api/synthetic/app02d2/sales', async route => {
      const upstream = await route.fetch()
      const body = await upstream.json() as { status: string; sourceId: string }
      expect(upstream.status()).toBe(200)
      expect(body.status).toBe('APPLIED')
      expect(body.sourceId).toMatch(/^[0-9a-f-]{36}$/i)
      applied.push({ ...commandIdentity(route.request().postDataJSON() as SaleInput), ...body, httpStatus: upstream.status() })
      await route.abort('failed')
    }, { times: 1 })

    await page.getByRole('button', { name: 'Ghi bán và giao', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Chưa rõ kết quả' })).toBeVisible()
    expect(applied).toHaveLength(1)
    const [accepted] = applied
    await authority(slot, 'revoke')
    try {
      const path = sales + '/' + encodeURIComponent(accepted.producerId) + '/' + encodeURIComponent(accepted.commandId)
      const revokedLookupStatus = await assertDenied(await clickAndCapture(page, 'Tra cứu yêu cầu', path, 'GET'))
      const replay = await clickAndCapture(page, 'Gửi lại cùng yêu cầu', sales, 'POST')
      expect(commandIdentity(replay.request().postDataJSON() as SaleInput)).toEqual(commandIdentity(accepted))
      const revokedReplayStatus = await assertDenied(replay)
      const fresh = await clickAndCapture(other, 'Ghi bán và giao', sales, 'POST')
      expect((fresh.request().postDataJSON() as SaleInput).commandId).not.toBe(accepted.commandId)
      const revokedSubmitStatus = await assertDenied(fresh)
      await expect(page.getByText(`Mã nguồn ${accepted.sourceId}`, { exact: true })).toBeHidden()
      const result = { ...accepted, offlinePostCount, queuedAfterOffline, revokedLookupStatus, revokedReplayStatus, revokedSubmitStatus }
      await savedJson(slot, 'D2-17', result)
      await info.attach('command-result', { body: JSON.stringify(result), contentType: 'application/json' })
    } finally { await authority(slot, 'restore') }
  })
}
