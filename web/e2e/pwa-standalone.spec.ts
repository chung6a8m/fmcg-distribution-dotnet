import { chromium, expect, type BrowserContext, type CDPSession, type Page } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { test } from './support/production'
import { assertTheme, attachEvidence, shellFonts, themeLabel, type Resolved } from './support/evidence'

async function pageWindow(context: BrowserContext, control: CDPSession, page: Page) {
  const session = await context.newCDPSession(page)
  try {
    const { targetInfo } = await session.send('Target.getTargetInfo')
    const { windowId } = await control.send('Browser.getWindowForTarget', { targetId: targetInfo.targetId })
    return { targetId: targetInfo.targetId, type: targetInfo.type, windowId }
  } finally {
    await session.detach()
  }
}

async function launch(context: BrowserContext, control: CDPSession, manifestId: string, scope: URL, stages: unknown[]) {
  const existing = new Set(context.pages())
  const existingWindows = await Promise.all([...existing].map(page => pageWindow(context, control, page)))
  const { targetId } = await control.send('PWA.launch', { manifestId })
  const { targetInfo } = await control.send('Target.getTargetInfo', { targetId })
  stages.push({ launchedTarget: targetInfo, existingWindows })
  // PWA.launch returns the parent tab. Playwright Page identifies its child page target.
  const { windowId } = await control.send('Browser.getWindowForTarget', { targetId })
  expect(existingWindows.map(old => old.windowId)).not.toContain(windowId)
  let matches: Page[] = []
  await expect.poll(async () => {
    matches = []
    for (const candidate of context.pages()) {
      const url = new URL(candidate.url())
      if (existing.has(candidate) || url.origin !== scope.origin || !url.pathname.startsWith(scope.pathname)) continue
      const identity = await pageWindow(context, control, candidate)
      if (identity.windowId === windowId) matches.push(candidate)
    }
    return matches.length
  }, { message: 'one new page in the installed app window and scope' }).toBe(1)
  const page = matches[0]
  stages.push({ tabTargetId: targetId, windowId, page: await pageWindow(context, control, page) })
  return page
}

async function checkWindow(page: Page, resolved: Resolved) {
  const selector = page.getByLabel(themeLabel)
  await expect(selector).toBeVisible()
  await expect(selector).toHaveValue(resolved)
  await expect(selector).toHaveCSS('color-scheme', resolved)
  const state = await page.evaluate(() => ({
    standalone: matchMedia('(display-mode: standalone)').matches,
    browser: matchMedia('(display-mode: browser)').matches,
    saved: localStorage.getItem('fmcg.ui.theme.v1')
  }))
  expect(state).toEqual({ standalone: true, browser: false, saved: resolved })
  return { state, theme: await assertTheme(page, resolved), fonts: await shellFonts(page) }
}

test('installed desktop PWA keeps theme on standalone reload and relaunch', async ({ origin }, info) => {
  test.skip(!process.env.CI, 'NOT_RUN_LOCAL_ENVIRONMENT: disposable CI profile required; local Chromium socket launch blocked')
  test.setTimeout(90_000)
  expect(process.platform, 'dedicated disposable Windows CI runner').toBe('win32')
  const profile = await mkdtemp(join(tmpdir(), 'fmcg-installed-pwa-'))
  let context: BrowserContext | undefined
  let control: CDPSession | undefined
  let version = 'launch pending'
  let manifestId = ''
  let installAttempted = false
  let installed = false
  let failure: unknown
  const cleanupErrors: string[] = []
  const stages: unknown[] = []
  try {
    // Native Playwright launch supplies its own remote-debugging pipe.
    context = await chromium.launchPersistentContext(profile, {
      channel: 'chromium', headless: true, colorScheme: 'light', serviceWorkers: 'allow'
    })
    const browser = context.browser()
    if (!browser) throw new Error('Persistent profile has no browser connection')
    version = browser.version()
    control = await browser.newBrowserCDPSession()
    const setup = context.pages()[0] ?? await context.newPage()
    await setup.goto(origin)
    const manifestUrl = await setup.locator('link[rel=manifest]').evaluate((el: HTMLLinkElement) => el.href)
    const response = await context.request.get(manifestUrl)
    expect(response.ok()).toBe(true)
    const manifest = await response.json()
    const appUrl = new URL(manifest.start_url, manifestUrl)
    manifestId = new URL(manifest.id ?? appUrl.href, appUrl.origin).href
    const scope = new URL(manifest.scope ?? './', appUrl)
    expect(manifest.display).toBe('standalone')
    stages.push({ manifestId, appUrl: appUrl.href, scope: scope.href, manifestDisplay: manifest.display })
    await setup.getByLabel(themeLabel).selectOption('dark')
    await assertTheme(setup, 'dark')
    expect(await setup.evaluate(() => localStorage.getItem('fmcg.ui.theme.v1'))).toBe('dark')
    await setup.evaluate(async () => { await navigator.serviceWorker.ready })
    installAttempted = true
    await control.send('PWA.install', { manifestId, installUrlOrBundleUrl: appUrl.href })
    installed = true
    stages.push('installed')
    await control.send('PWA.changeAppUserSettings', { manifestId, displayMode: 'standalone' })
    stages.push('standalone selected')
    const app = await launch(context, control, manifestId, scope, stages)
    stages.push({ initial: await checkWindow(app, 'dark') })
    await info.attach('installed-dark', { body: await app.screenshot(), contentType: 'image/png' })
    await app.getByLabel(themeLabel).selectOption('light')
    await app.reload()
    stages.push({ reload: await checkWindow(app, 'light') })
    await app.close()
    const relaunched = await launch(context, control, manifestId, scope, stages)
    stages.push({ relaunch: await checkWindow(relaunched, 'light') })
    await info.attach('relaunched-light', { body: await relaunched.screenshot(), contentType: 'image/png' })
  } catch (error) {
    failure = error
  } finally {
    if (control && installAttempted) {
      try {
        await control.send('PWA.uninstall', { manifestId })
        stages.push('uninstalled')
      } catch (error) {
        cleanupErrors.push('uninstall: ' + String(error))
      }
    }
    try { await control?.detach() } catch (error) { cleanupErrors.push('detach: ' + String(error)) }
    try { await context?.close() } catch (error) { cleanupErrors.push('close: ' + String(error)) }
    try { await rm(profile, { recursive: true, force: true, maxRetries: 3 }) } catch (error) { cleanupErrors.push('profile: ' + String(error)) }
    const blockedCapability = /PWA\.\w+.*(?:wasn't found|method not found|not supported)/i.test(String(failure))
    await attachEvidence(info, 'installed-runtime', version, { channel: 'chromium', executable: chromium.executablePath(),
      headless: true, transport: 'native pipe', profile: 'disposable persistent', manifestId,
      installAttempted, installed, stages, error: failure ? String(failure) : null, cleanupErrors,
      outcome: blockedCapability ? 'BLOCKED_CAPABILITY / NOT_RUN' : failure || cleanupErrors.length ? 'FAILED' : 'PASS' })
    if (failure && !cleanupErrors.length) throw failure
    if (failure || cleanupErrors.length) throw new AggregateError([...(failure ? [failure] : []), ...cleanupErrors], 'Installed runtime or cleanup failed')
  }
})
