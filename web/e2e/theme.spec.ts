import { expect, type Locator } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { demoPath, dist, test as productionTest } from './support/production'
import { assertTheme, attachEvidence, embeddedFonts, renderedFont, themeLabel } from './support/evidence'

const test = productionTest.extend<{ diagnostics: void }>({
  diagnostics: [async ({ page, browser }, use, info) => {
    const errors: string[] = []
    const expected: string[] = []
    const failedFonts = info.tags.includes('@blocked-font')
    const inlineProbe = info.tags.includes('@forbidden-inline')
    page.on('pageerror', error => errors.push(error.message))
    page.on('requestfailed', request => {
      const message = request.failure()?.errorText ?? ''
      const blocked = failedFonts && request.url().startsWith('data:font/woff2;') && /CSP|blocked/i.test(message)
      const list = blocked ? expected : errors
      list.push((blocked ? 'embedded WOFF2' : request.url()) + ' ' + message)
    })
    page.on('console', event => {
      if (event.type() !== 'error') return
      const blockedFont = failedFonts && (
        /font-src 'none'/.test(event.text()) && /data:font\/woff2/.test(event.text()) ||
        event.location().url.startsWith('data:font/woff2;') && /ERR_BLOCKED_BY_CSP/.test(event.text())
      )
      const refusedInline = inlineProbe && /inline script/i.test(event.text()) && /Content Security Policy/i.test(event.text())
      const list = blockedFont || refusedInline ? expected : errors
      list.push(event.text().replace(/data:font\/woff2;base64,[a-z0-9+/=]+/gi, 'embedded WOFF2'))
    })
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', event => {
        const root = document.documentElement
        root.dataset.cspViolations = JSON.stringify([...JSON.parse(root.dataset.cspViolations ?? '[]'),
          { directive: event.effectiveDirective, blocked: event.blockedURI, disposition: event.disposition }])
      })
    })
    await use()
    const violations = await page.evaluate(() => JSON.parse(document.documentElement.dataset.cspViolations ?? '[]'))
    await attachEvidence(info, 'production-diagnostics', browser.version(), { errors, expected, violations })
    expect(errors).toEqual([])
    if (failedFonts) {
      expect(violations.length).toBeGreaterThanOrEqual(2)
      for (const violation of violations) expect(violation).toEqual({ directive: 'font-src', blocked: 'data', disposition: 'enforce' })
    } else {
      expect(violations).toEqual(inlineProbe ? [{ directive: 'script-src-elem', blocked: 'inline', disposition: 'enforce' }] : [])
    }
  }, { auto: true }]
})

// The React entry is the only delayed resource. Leave CSS, the classic script and HTML untouched.
for (const [saved, os, preference, resolved] of [
  ['light', 'dark', 'light', 'light'], ['dark', 'light', 'dark', 'dark'],
  ['system', 'dark', 'system', 'dark'], ['missing', 'dark', 'light', 'light'],
  ['invalid', 'dark', 'light', 'light'], ['read-throws', 'dark', 'light', 'light']
] as const) {
  test('before React: ' + saved + ' on ' + os + ' OS', async ({ page, origin, browser }, info) => {
    await page.emulateMedia({ colorScheme: os })
    await page.addInitScript(value => {
      if (value === 'read-throws') {
        Object.defineProperty(Storage.prototype, 'getItem', { value() { throw new Error('blocked read') } })
      } else if (value !== 'missing') localStorage.setItem('fmcg.ui.theme.v1', value)
    }, saved)
    const html = await readFile(join(dist, 'index.html'), 'utf8')
    const entry = html.match(/<script\b[^>]*type="module"[^>]*src="([^"]+)"/)?.[1]
    if (!entry) throw new Error('Expected a built module entry')
    const held = Promise.withResolvers<void>()
    const release = Promise.withResolvers<void>()
    await page.route(new URL(entry, origin).href, async route => {
      held.resolve()
      await release.promise
      await route.continue()
    })
    await page.goto(origin + demoPath, { waitUntil: 'commit' })
    try {
      await held.promise
      await page.waitForFunction(() => document.getElementById('root') && document.querySelector<HTMLLinkElement>('link[rel=stylesheet]')?.sheet)
      await page.evaluate(() => new Promise<void>(done => requestAnimationFrame(() => requestAnimationFrame(() => done()))))
      await expect(page.locator('#root')).toBeEmpty()
      const preReact = await assertTheme(page, resolved)
      await info.attach('pre-react-frame', { body: await page.screenshot(), contentType: 'image/png' })
      await attachEvidence(info, 'first-paint', browser.version(), { saved, os, entry, preReact })
    } finally {
      release.resolve()
    }
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Nền tảng giao diện')
    const select = page.getByLabel(themeLabel)
    await expect(select).toHaveValue(preference)
    await expect(select).toHaveCSS('color-scheme', resolved)
    if (saved === 'read-throws') {
      await select.selectOption('dark')
      await assertTheme(page, 'dark')
    }
  })
}

test('CSP rejects an inline script', { tag: '@forbidden-inline' }, async ({ page, origin }) => {
  await page.goto(origin + demoPath)
  await expect(page.getByLabel(themeLabel)).toBeVisible()
  await page.evaluate(() => {
    const probe = document.createElement('script')
    probe.textContent = 'document.documentElement.dataset.inlineExecuted = "yes"'
    document.head.append(probe)
  })
  await expect(page.locator('html')).toHaveAttribute('data-csp-violations', /script-src-elem/)
  expect(await page.locator('html').getAttribute('data-inline-executed')).toBeNull()
})

test('System follows OS during editing; explicit choices persist across reload', async ({ page, origin }) => {
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto(origin + demoPath)
  const select = page.getByLabel(themeLabel)
  const editor = page.getByLabel('Tên điểm bán', { exact: true })
  const value = 'Điểm bán – Nguyễn Huệ'
  await editor.fill(value)
  await editor.evaluate((el: HTMLInputElement) => el.setSelectionRange(3, 7))
  const original = await editor.elementHandle()
  await page.getByLabel('Mật độ').selectOption('comfortable')
  await select.selectOption('system')
  const selection = () => editor.evaluate((el: HTMLInputElement) => [el.selectionStart, el.selectionEnd])
  expect(await selection()).toEqual([3, 7])
  await editor.focus()
  await page.emulateMedia({ colorScheme: 'dark' })
  await assertTheme(page, 'dark')
  await expect(editor).toBeFocused()
  expect(await selection()).toEqual([3, 7])
  await expect(editor).toHaveValue(value)
  expect(await editor.evaluate((el, before) => el === before, original)).toBe(true)
  await page.emulateMedia({ colorScheme: 'light' })
  await assertTheme(page, 'light')
  await select.selectOption('dark')
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.emulateMedia({ colorScheme: 'light' })
  await assertTheme(page, 'dark')
  await page.reload()
  await expect(select).toHaveValue('dark')
  await assertTheme(page, 'dark')
})

test('failed write keeps applied choice for existing and new selectors', async ({ page, origin }) => {
  await page.addInitScript(() => {
    localStorage.setItem('fmcg.ui.theme.v1', 'light')
    Object.defineProperty(Storage.prototype, 'setItem', { value() { throw new Error('blocked write') } })
  })
  await page.goto(origin + demoPath)
  const select = page.getByRole('main').getByLabel(themeLabel, { exact: true })
  await select.selectOption('dark')
  await expect(select).toHaveAccessibleDescription(/Chưa lưu/)
  await page.getByLabel('Mật độ').selectOption('comfortable')
  const opener = page.getByRole('button', { name: 'Mở hộp thoại' })
  const dialog = page.getByRole('dialog', { name: 'Hộp thoại mẫu', includeHidden: true })
  await opener.click()
  await expect(dialog.getByLabel(themeLabel)).toHaveValue('dark')
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(opener).toBeFocused()
  await expect(select).toHaveValue('dark')
  await assertTheme(page, 'dark')
  expect(await page.evaluate(() => localStorage.getItem('fmcg.ui.theme.v1'))).toBe('light')
})

test('Vietnamese marks, numeric and currency glyphs render with packaged faces', async ({ page, origin, browser }, info) => {
  const responses: { url: string, status: number }[] = []
  page.on('response', response => {
    if (response.request().resourceType() === 'stylesheet') responses.push({ url: response.url(), status: response.status() })
  })
  await page.goto(origin + demoPath)
  const records = []
  for (const [sample, weight] of [['regular', '400'], ['bold', '700']] as const) {
    const selector = '[data-font-sample="' + sample + '"]'
    records.push(await renderedFont(page, selector, weight))
    // Exercise the canonically equivalent spelling on the same styled, visible product sample.
    await page.locator(selector).evaluate(el => { el.textContent = el.textContent!.normalize('NFD') })
    const decomposed = await renderedFont(page, selector, weight)
    expect(decomposed.text).toMatch(/\p{Mark}/u)
    records.push(decomposed)
  }
  const currency = await renderedFont(page, '.sample-table tbody tr:first-child td:last-child', '400')
  expect(currency.text).toContain('1.248.000 ₫')
  const assets = await embeddedFonts(page)
  expect(responses).toEqual([{ url: assets[0].stylesheet, status: 200 }])
  await attachEvidence(info, 'font-attribution', browser.version(), { records, currency, responses, assets })
})

test('blocked fonts use readable system fallback at 320px', { tag: '@blocked-font' }, async ({ page, origin, browser }, info) => {
  await page.setViewportSize({ width: 320, height: 800 })
  await page.route(origin + demoPath, async route => {
    const response = await route.fetch()
    const headers = response.headers()
    expect(headers['content-security-policy']).toContain("font-src 'self' data:")
    headers['content-security-policy'] = headers['content-security-policy'].replace("font-src 'self' data:", "font-src 'none'")
    await route.fulfill({ response, headers })
  })
  await page.goto(origin + demoPath)
  const records = []
  for (const [sample, weight] of [['regular', '400'], ['bold', '700']] as const) {
    const actual = await renderedFont(page, '[data-font-sample="' + sample + '"]', weight, false)
    expect(actual.cssFamily).toMatch(/Noto Sans.*Segoe UI.*system-ui.*sans-serif/i)
    records.push(actual)
  }
  const geometry = () => page.evaluate(() => ({
    width: document.documentElement.scrollWidth, viewport: document.documentElement.clientWidth,
    table: document.querySelector('table')!.getBoundingClientRect().width
  }))
  const before = await geometry()
  await page.getByLabel(themeLabel).selectOption('dark')
  expect(await geometry()).toEqual(before)
  expect(before.width).toBe(before.viewport)
  expect(before.viewport).toBe(320)
  const assets = await embeddedFonts(page, 'error')
  await info.attach('fallback-320', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' })
  await attachEvidence(info, 'system-fallback', browser.version(), { records, before, assets })
})

function luminance(cssColor: string) {
  const rgb = /^rgb\((\d+), (\d+), (\d+)\)$/.exec(cssColor)
  if (!rgb) throw new Error('Expected opaque computed sRGB color, got ' + cssColor)
  const [r, g, b] = rgb.slice(1).map(value => {
    const channel = Number(value) / 255
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

async function colors(node: Locator) {
  return node.evaluate(el => {
    function surface(from: Element | null) {
      for (let current = from; current; current = current.parentElement) {
        const color = getComputedStyle(current).backgroundColor
        if (color !== 'rgba(0, 0, 0, 0)' && color !== 'transparent') return color
      }
      throw new Error('No painted background for ' + el.tagName)
    }
    const style = getComputedStyle(el)
    return { text: style.color, surface: surface(el), outside: surface(el.parentElement),
      border: style.borderTopColor, borderWidth: parseFloat(style.borderTopWidth), borderStyle: style.borderTopStyle,
      outline: style.outlineColor, outlineWidth: style.outlineWidth,
      outlineStyle: style.outlineStyle }
  })
}

for (const theme of ['light', 'dark'] as const) {
  test('rendered contrast across states in ' + theme, async ({ page, origin, browser }, info) => {
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await page.goto(origin + demoPath)
    await page.getByLabel(themeLabel).selectOption(theme)
    await assertTheme(page, theme)
    const pairs: { name: string, foreground: string, background: string, ratio: number, minimum: number | null }[] = []
    function check(name: string, foreground: string, background: string, minimum: number | null) {
      const a = luminance(foreground)
      const b = luminance(background)
      const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
      pairs.push({ name, foreground, background, ratio, minimum })
      if (minimum !== null) expect.soft(ratio, name).toBeGreaterThanOrEqual(minimum)
    }
    async function measure(name: string, node: Locator, boundary?: 'border' | 'surface') {
      const c = await colors(node)
      check(name + ' text', c.text, c.surface, 4.5)
      if (boundary === 'border') {
        expect(c.borderWidth, name + ' border width').toBeGreaterThan(0)
        expect(['none', 'hidden'], name + ' border style ' + c.borderStyle).not.toContain(c.borderStyle)
      }
      if (boundary) check(name + ' control boundary', c[boundary], c.outside, 3)
      return c
    }
    try {
      const primary = page.locator('.button-primary')
      const normal = await measure('primary', primary, 'surface')
      await primary.hover()
      const hovered = await measure('primary hover', primary, 'surface')
      await page.mouse.down()
      let pressed
      try {
        pressed = await measure('primary pressed', primary, 'surface')
        await page.mouse.move(0, 0)
      } finally {
        await page.mouse.up()
      }
      expect(new Set([normal.surface, hovered.surface, pressed.surface]).size).toBe(3)
      await measure('secondary', page.locator('.button-secondary'), 'border')
      for (const selector of ['h1', '.page-heading p', '.field-help', '.field-error', '.page-link']) {
        await measure(selector, page.locator(selector))
      }
      for (const selector of ['input[readonly]', '#demo-density', 'input[aria-invalid=true]']) {
        await measure(selector, page.locator(selector), 'border')
      }
      const states = page.getByRole('region', { name: 'Trạng thái mẫu' })
      await expect(states.locator('.badge')).toHaveCount(4)
      await expect(states.locator('.notice')).toHaveCount(3)
      for (const [tone, label] of [
        ['success', 'Thành công'], ['info', 'Thông tin'], ['warning', 'Cảnh báo'], ['danger', 'Lỗi']
      ]) {
        const badge = states.locator(`.badge[data-tone="${tone}"]`)
        await expect(badge).toBeVisible()
        await expect(badge).toHaveText(label)
        await measure(label, badge)
      }
      for (const [tone, title, body] of [
        ['info', 'Đang tải', 'Đang đọc danh mục minh họa…'],
        ['warning', 'Trống', 'Danh sách mẫu chưa có mặt hàng.'],
        ['danger', 'Có lỗi', 'Không thể hiển thị dữ liệu trong ví dụ này.']
      ]) {
        const notice = states.locator(`.notice[data-tone="${tone}"]`)
        const heading = notice.locator('strong')
        const message = notice.locator('p')
        await expect(heading).toBeVisible()
        await expect(heading).toHaveText(title)
        await expect(message).toBeVisible()
        await expect(message).toHaveText(body)
        await measure(title, heading)
        await measure(body, message)
      }
      const disabled = await colors(page.locator('input:disabled'))
      check('disabled input (inactive control exception)', disabled.text, disabled.surface, null)
      const editor = page.getByLabel('Tên điểm bán', { exact: true })
      await page.getByLabel('Mật độ').press('Tab')
      await expect(editor).toBeFocused()
      const focus = await colors(editor)
      expect(focus.outlineWidth).toBe('2px')
      expect(focus.outlineStyle).toBe('solid')
      check('field focus ring', focus.outline, focus.outside, 3)
      await page.getByRole('button', { name: 'Mở hộp thoại' }).click()
      await page.getByLabel('Ghi chú trong hộp thoại').fill('Mẫu')
      await measure('destructive', page.locator('.button-destructive'), 'surface')
      await info.attach('dialog-' + theme, { body: await page.screenshot(), contentType: 'image/png' })
    } finally {
      await attachEvidence(info, 'contrast-' + theme, browser.version(), pairs)
    }
  })
}

test('forced colors retain focus and native states; reduced motion removes transition', async ({ page, origin }) => {
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' })
  await page.goto(origin + demoPath)
  await expect(page.locator('.button-primary')).toHaveCSS('transition-duration', '0s')
  await expect(page.getByLabel('Mã nội bộ (không khả dụng)')).toBeDisabled()
  await page.keyboard.press('Tab')
  const link = page.getByRole('link', { name: 'Trạng thái kết nối' })
  await expect(link).toBeFocused()
  await expect(link).toHaveCSS('outline-style', 'solid')
  await expect(link).toHaveCSS('outline-width', '2px')
  await expect(page.getByLabel('Số lượng mẫu')).toHaveAccessibleDescription(/số nguyên dương/i)
})
