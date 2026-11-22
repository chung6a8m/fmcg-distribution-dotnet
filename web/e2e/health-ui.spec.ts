import { expect, test } from '@playwright/test'

// Ordinary UI smoke tests do not rely on cached PWA responses.
// The dedicated PWA suite below exercises a real service worker.
test.use({ serviceWorkers: 'block' })

const live = { status: 'live', scope: 'APP-00A', businessWrites: false }

test('renders the actual API liveness contract', async ({ page }) => {
  await page.route('**/health/live', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(live)
  }))
  await page.goto('/')
  await expect(page.getByRole('status')).toHaveText('API hoạt động')
  await expect(page.getByText('Cần kết nối để ghi nghiệp vụ.')).toBeVisible()
})

test('reports disconnected when an absent API returns the SPA HTML fallback', async ({ page }) => {
  await page.route('**/health/live', route => route.fulfill({
    status: 200, contentType: 'text/html', body: '<!doctype html><html></html>'
  }))
  await page.goto('/')
  await expect(page.getByRole('status')).toHaveText('Không kết nối API')
})

for (const size of [
  { name: 'desktop', width: 1280, height: 800 },
  { name: 'narrow', width: 375, height: 812 }
]) {
  test('100% UI layout has no horizontal overflow: ' + size.name, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: size.width, height: size.height })
    await page.route('**/health/live', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(live)
    }))
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'FMCG Distribution · APP-00A' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Trạng thái kết nối' })).toBeVisible()
    await expect(page.getByRole('status')).toHaveText('API hoạt động')

    const metrics = await page.evaluate(() => ({
      innerWidth: window.innerWidth,
      clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      minBodyWidth: getComputedStyle(document.body).minWidth
    }))
    expect(metrics.clientWidth).toBeGreaterThanOrEqual(320)
    expect(metrics.scrollWidth, JSON.stringify(metrics)).toBeLessThanOrEqual(metrics.clientWidth)

    await testInfo.attach('layout-100pct-' + size.name, {
      body: await page.screenshot({ fullPage: true }),
      contentType: 'image/png'
    })
    await testInfo.attach('viewport-100pct-' + size.name, {
      body: Buffer.from(JSON.stringify({ width: size.width, height: size.height, ...metrics }, null, 2)),
      contentType: 'application/json'
    })
  })
}
