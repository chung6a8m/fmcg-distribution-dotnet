import { expect, test } from '@playwright/test'
import assert from 'node:assert/strict'
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { dist, serveBuild } from './support/production'
import { assertTheme, attachEvidence, embeddedFonts, renderedFont, shellFonts, themeLabel } from './support/evidence'

const titleV1 = 'FMCG DMS — APP-00A'
const titleV2 = 'FMCG DMS — APP-00A [Playwright V2]'

test('a real service worker waits for consent, then updates and reloads', async ({ browser }, testInfo) => {
  const work = await mkdtemp(join(tmpdir(), 'fmcg-pwa-e2e-'))
  const v1 = join(work, 'v1')
  const v2 = join(work, 'v2')
  await cp(dist, v1, { recursive: true })
  await cp(dist, v2, { recursive: true })

  // Deploy a synthetic second release without editing source, lockfiles or the checked-in baseline.
  const htmlPath = join(v2, 'index.html')
  const html = await readFile(htmlPath, 'utf8')
  assert.ok(html.includes('<title>' + titleV1 + '</title>'), 'V1 title marker is missing')
  await writeFile(htmlPath, html.replace(titleV1, titleV2))

  const swPath = join(v2, 'sw.js')
  const sw = await readFile(swPath, 'utf8')
  assert.match(sw, /index\.html/, 'index.html must be in the precache')
  let revisions = 0
  const updatedSw = sw.replace(/((?:["']?revision["']?)\s*:\s*["'])([^"']+)(["'])/g,
    (_whole, start: string, revision: string, end: string) => {
      revisions += 1
      return start + revision + '-e2e-v2' + end
    })
  assert.ok(revisions > 0 && updatedSw !== sw, 'V2 must change Workbox precache revisions')
  await writeFile(swPath, updatedSw)

  let activeRoot = v1
  const server = await serveBuild(() => activeRoot)
  let context: Awaited<ReturnType<typeof browser.newContext>> | undefined
  try {
    const { origin } = server
    context = await browser.newContext({ serviceWorkers: 'allow' })
    const page = await context.newPage()

    await page.goto(origin)
    await expect(page).toHaveTitle(titleV1)
    await page.getByLabel(themeLabel).selectOption('dark')
    await assertTheme(page, 'dark')
    await expect.poll(async () => page.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration()
      return registration?.active?.state === 'activated'
    })).toBeTruthy()

    // The first installation may not control the initial navigation.
    await page.reload()
    await expect.poll(async () => page.evaluate(() => Boolean(navigator.serviceWorker.controller))).toBeTruthy()
    await expect(page.getByRole('status')).toHaveText('API hoạt động')
    await page.evaluate(() => { (window as Window & { __pwaRun?: string }).__pwaRun = 'V1' })

    // Same origin, same open V1 tab. Browser checks and installs the updated SW.
    activeRoot = v2
    await page.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration()
      if (!registration) throw new Error('Expected a registered V1 service worker')
      await registration.update()
    })
    const prompt = page.getByRole('heading', { name: 'Có phiên bản mới' })
    await expect(prompt).toBeVisible({ timeout: 30_000 })
    await expect.poll(async () => page.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration()
      return registration?.waiting?.state === 'installed'
    })).toBeTruthy()

    // A waiting worker must NOT silently reload this existing tab.
    await page.waitForTimeout(800)
    await expect(page).toHaveTitle(titleV1)
    expect(await page.evaluate(() => (window as Window & { __pwaRun?: string }).__pwaRun)).toBe('V1')

    const button = page.getByRole('button', { name: 'Cập nhật ứng dụng' })
    for (let tabs = 0; tabs < 8; tabs++) {
      await page.keyboard.press('Tab')
      if (await button.evaluate(el => el === document.activeElement)) break
    }
    await expect(button).toBeFocused()
    const outline = await button.evaluate(element => {
      const style = getComputedStyle(element)
      return { type: style.outlineStyle, width: parseFloat(style.outlineWidth) }
    })
    expect(outline.type).not.toBe('none')
    expect(outline.width).toBeGreaterThan(0)
    await testInfo.attach('pwa-v2-waiting-consent', {
      body: await page.screenshot({ fullPage: true }),
      contentType: 'image/png'
    })

    await page.keyboard.press('Enter')
    await expect(page).toHaveTitle(titleV2, { timeout: 30_000 })
    await expect.poll(async () => page.evaluate(() => Boolean(navigator.serviceWorker.controller))).toBeTruthy()
    expect(await page.evaluate(() => (window as Window & { __pwaRun?: string }).__pwaRun)).toBeUndefined()
    await expect(page.getByLabel(themeLabel)).toHaveValue('dark')
    await expect(page.getByLabel(themeLabel)).toHaveCSS('color-scheme', 'dark')
    const theme = await assertTheme(page, 'dark')
    await attachEvidence(testInfo, 'pwa-update-theme', browser.version(), { theme, fonts: await shellFonts(page) })
    await testInfo.attach('pwa-v2-activated', {
      body: await page.screenshot({ fullPage: true }),
      contentType: 'image/png'
    })
  } finally {
    await context?.close()
    await server.close()
    await rm(work, { recursive: true, force: true })
  }
})

test('controlled cached shell retains Dark and fonts while offline API is disconnected', async ({ browser }, info) => {
  const server = await serveBuild()
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  try {
    const page = await context.newPage()
    const cdp = await context.newCDPSession(page)
    await cdp.send('Network.enable')
    await cdp.send('Network.setCacheDisabled', { cacheDisabled: true })
    const writes: string[] = []
    context.on('request', request => {
      if (!['GET', 'HEAD'].includes(request.method())) writes.push(request.method() + ' ' + request.url())
    })
    await page.goto(server.origin)
    await expect(page.getByRole('status')).toHaveText('API hoạt động')
    await page.getByLabel(themeLabel).selectOption('dark')
    await page.evaluate(async () => { await navigator.serviceWorker.ready })
    await page.reload()
    await expect.poll(() => page.evaluate(() => Boolean(navigator.serviceWorker.controller))).toBe(true)
    const cachePaths = () => page.evaluate(async () => {
      const entries = await Promise.all((await caches.keys()).map(async name => {
        const cache = await caches.open(name)
        return (await cache.keys()).map(key => new URL(key.url).pathname)
      }))
      return entries.flat().sort()
    })
    const before = await cachePaths()
    expect(before).toEqual(expect.arrayContaining(['/index.html', '/theme-runtime.js']))
    expect(before.filter(path => /^\/(health|api)(\/|$)/.test(path))).toEqual([])
    await context.setOffline(true)
    const servedByWorker = new Set<string>()
    page.on('response', response => {
      if (response.fromServiceWorker()) servedByWorker.add(response.url())
    })
    const response = await page.reload()
    expect(response?.fromServiceWorker()).toBe(true)
    await expect(page.getByRole('status')).toHaveText('Không kết nối API')
    await expect(page.getByLabel(themeLabel)).toHaveValue('dark')
    await expect(page.getByLabel(themeLabel)).toHaveCSS('color-scheme', 'dark')
    const theme = await assertTheme(page, 'dark')
    const fonts = await shellFonts(page)
    const assets = await embeddedFonts(page)
    const css = new URL(assets[0].stylesheet!)
    expect(before).toContain(css.pathname)
    expect([...servedByWorker]).toEqual(expect.arrayContaining([css.href, server.origin + '/theme-runtime.js']))
    await info.attach('offline-shell', { body: await page.screenshot(), contentType: 'image/png' })
    await page.getByRole('link', { name: 'Nền tảng giao diện' }).click()
    const products = [
      await renderedFont(page, '[data-font-sample="regular"]', '400'),
      await renderedFont(page, '[data-font-sample="bold"]', '700'),
      await renderedFont(page, '.sample-table tbody tr:first-child td:last-child', '400')
    ]
    expect(await cachePaths()).toEqual(before)
    expect(writes).toEqual([])
    await attachEvidence(info, 'offline-cached-shell', browser.version(), { theme, fonts, cachePaths: before, workerResponses: [...servedByWorker], assets, products, writes })
  } finally {
    await context.close()
    await server.close()
  }
})
