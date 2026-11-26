import { expect, type APIRequestContext, type Browser } from '@playwright/test'
import { auth, open } from '../e2e-app03-7/return-refund'
import { app, get, type CloseInput, type Closure } from './fixture'

export const commandHistory = (request: APIRequestContext, commandId: string, role: string) =>
  get<unknown[]>(request, app + '/commands?commandId=' + commandId, role)

export async function verifyFreshCommandRecovery(browser: Browser, request: APIRequestContext, main: string,
  receipt: Closure, original: CloseInput) {
  const history = app + '/commands'
  const fresh = await browser.newContext({ serviceWorkers: 'block' })
  try {
    const page = await open(fresh, 'OPS', 'app05-work')
    const row = page.getByTestId('app05-command-' + original.commandId)
    await expect(row).toContainText('Đã áp dụng')
    await expect(row).toContainText(receipt.closureId)
    await expect(row.getByRole('link', { name: 'Mở chuyến gốc' })).toHaveAttribute('href', '/work/trip-close?trip=' + main)

    await page.getByLabel('Mã lệnh', { exact: true }).fill(original.commandId)
    await page.getByRole('button', { name: 'Tra lịch sử lệnh' }).press('Enter')
    await expect(page.getByRole('status').filter({ hasText: '1 kết quả' })).toBeVisible()
    await expect(row).toContainText(receipt.closureId)

    const lookup = history + '?commandId=' + original.commandId
    const found = await request.get(lookup, { headers: auth('OPS') })
    expect(found.status(), await found.text()).toBe(200)
    expect(await found.json()).toEqual([expect.objectContaining({
      kind: 'TRIP_CLOSE', commandId: original.commandId, tripId: main, resultId: receipt.closureId, status: 'APPLIED',
    })])
    for (const role of ['PEER_OPS', 'ASSIGNEE']) {
      const forbidden = await request.get(lookup, { headers: auth(role) })
      expect([200, 403]).toContain(forbidden.status())
      if (forbidden.ok()) expect(await forbidden.json()).toEqual([])
    }
    const replay = await request.post(app + '/' + main + '/close', { headers: auth('OPS'), data: original })
    expect(replay.status()).toBe(200)
    expect(await replay.json()).toEqual(receipt)
    const conflict = await request.post(app + '/' + main + '/close', {
      headers: auth('OPS'), data: { ...original, expectedRevision: 'A'.repeat(64) },
    })
    expect(conflict.status()).toBe(409)
    expect(await conflict.json()).toMatchObject({ code: 'COMMAND_PAYLOAD_CONFLICT' })
  } finally {
    await fresh.close()
  }
}
