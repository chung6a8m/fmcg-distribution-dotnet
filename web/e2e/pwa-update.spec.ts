import { expect, test } from '@playwright/test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import type { AddressInfo } from 'node:net'
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve, sep, extname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const dist = fileURLToPath(new URL('../dist/', import.meta.url))
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
  const server = createServer(async (request, response) => {
    if (request.url === '/health/live') {
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
      response.end(JSON.stringify({ status: 'live', scope: 'APP-00A', businessWrites: false }))
      return
    }

    let pathname: string
    try {
      pathname = decodeURIComponent(new URL(request.url || '/', 'http://127.0.0.1').pathname)
    } catch {
      response.writeHead(400).end()
      return
    }
    const root = resolve(activeRoot)
    const filename = resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname))
    if (!filename.startsWith(root + sep)) {
      response.writeHead(403).end()
      return
    }

    const mime: Record<string, string> = {
      '.html': 'text/html; charset=utf-8',
      '.js': 'text/javascript; charset=utf-8',
      '.css': 'text/css; charset=utf-8',
      '.svg': 'image/svg+xml',
      '.webmanifest': 'application/manifest+json',
      '.json': 'application/json'
    }
    try {
      const body = await readFile(filename)
      response.writeHead(200, {
        'Content-Type': mime[extname(filename)] || 'application/octet-stream',
        'Cache-Control': 'no-store'
      })
      response.end(body)
    } catch {
      response.writeHead(404).end()
    }
  })

  let context: Awaited<ReturnType<typeof browser.newContext>> | undefined
  try {
    await new Promise<void>((done, reject) => {
      server.once('error', reject)
      server.listen(0, '127.0.0.1', () => done())
    })
    const origin = 'http://127.0.0.1:' + (server.address() as AddressInfo).port
    context = await browser.newContext({ serviceWorkers: 'allow' })
    const page = await context.newPage()

    await page.goto(origin)
    await expect(page).toHaveTitle(titleV1)
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
    await page.keyboard.press('Tab')
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

    await button.click()
    await expect(page).toHaveTitle(titleV2, { timeout: 30_000 })
    await expect.poll(async () => page.evaluate(() => Boolean(navigator.serviceWorker.controller))).toBeTruthy()
    expect(await page.evaluate(() => (window as Window & { __pwaRun?: string }).__pwaRun)).toBeUndefined()
    await testInfo.attach('pwa-v2-activated', {
      body: await page.screenshot({ fullPage: true }),
      contentType: 'image/png'
    })
  } finally {
    await context?.close()
    if (server.listening) {
      await new Promise<void>((done, reject) => server.close(error => error ? reject(error) : done()))
    }
    await rm(work, { recursive: true, force: true })
  }
})
