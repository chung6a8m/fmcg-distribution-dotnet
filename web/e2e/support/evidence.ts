import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { readFile } from 'node:fs/promises'
import { platform, release } from 'node:os'
import { expect, type Page, type TestInfo } from '@playwright/test'

export async function attachEvidence(info: TestInfo, name: string, browserVersion: string, data: unknown) {
  const file = process.env.GITHUB_EVENT_PATH
  const event = file ? JSON.parse(await readFile(file, 'utf8')) : null
  const metadata = {
    pullRequestSourceHead: event?.pull_request?.head?.sha ?? null,
    workflowSha: process.env.GITHUB_SHA ?? null,
    checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
    runId: process.env.GITHUB_RUN_ID ?? null, runAttempt: process.env.GITHUB_RUN_ATTEMPT ?? null,
    retry: info.retry, utc: new Date().toISOString(), browserVersion, os: platform() + ' ' + release()
  }
  await info.attach(name, { body: JSON.stringify({ metadata, data }, null, 2), contentType: 'application/json' })
}

export const themeLabel = 'Chế độ hiển thị'
export type Resolved = 'light' | 'dark'
const surfaces = {
  light: { rgb: 'rgb(246, 248, 251)', hex: '#F6F8FB' },
  dark: { rgb: 'rgb(16, 24, 39)', hex: '#101827' }
}

export async function assertTheme(page: Page, theme: Resolved) {
  await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
  const actual = await page.evaluate(() => {
    const root = document.documentElement
    return {
      theme: root.dataset.theme, background: getComputedStyle(root).backgroundColor,
      scheme: getComputedStyle(root).colorScheme,
      metaScheme: [...document.querySelectorAll<HTMLMetaElement>('meta[name=color-scheme]')].map(m => m.content),
      metaColor: [...document.querySelectorAll<HTMLMetaElement>('meta[name=theme-color]')].map(m => m.content.toUpperCase())
    }
  })
  expect(actual).toEqual({ theme, background: surfaces[theme].rgb, scheme: theme,
    metaScheme: [theme], metaColor: [surfaces[theme].hex] })
  return actual
}

// CDP reports the fonts used for rendered glyphs. CSS family lists alone also contain fallback faces.
export async function renderedFont(page: Page, selector: string, weight: '400' | '700', custom = true) {
  const node = page.locator(selector)
  await expect(node).toBeVisible()
  await node.scrollIntoViewIfNeeded()
  const sample = await node.evaluate(async el => {
    const text = el.textContent ?? ''
    const style = getComputedStyle(el)
    await document.fonts.ready
    return { text, weight: style.fontWeight, cssFamily: style.fontFamily }
  })
  expect(sample.weight).toBe(weight)
  const cdp = await page.context().newCDPSession(page)
  try {
    await cdp.send('DOM.enable')
    await cdp.send('CSS.enable')
    const { root } = await cdp.send('DOM.getDocument')
    const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector })
    const { fonts } = await cdp.send('CSS.getPlatformFontsForNode', { nodeId })
    const used = fonts.filter(font => font.glyphCount > 0)
    expect(used.length, selector).toBeGreaterThan(0)
    for (const font of used) {
      expect(font.isCustomFont, selector + ' ' + font.familyName).toBe(custom)
      if (custom) {
        expect(font.familyName).toMatch(/Noto Sans/i)
        expect(font.postScriptName).toMatch(weight === '400' ? /Regular/i : /Bold/i)
      }
    }
    return { selector, ...sample, fonts: used }
  } finally {
    await cdp.detach()
  }
}

export async function shellFonts(page: Page) {
  return [
    await renderedFont(page, '.health-page .page-heading p', '400'),
    await renderedFont(page, '.health-page h1', '700')
  ]
}

export async function embeddedFonts(page: Page, expectedStatus: FontFaceLoadStatus = 'loaded') {
  const faces = await page.evaluate(async () => {
    await document.fonts.ready
    const list = []
    for (const sheet of document.styleSheets) {
      for (const rule of sheet.cssRules) {
        if (!(rule instanceof CSSFontFaceRule)) continue
        const src = rule.style.getPropertyValue('src')
        const payload = /url\(["']?data:font\/woff2;base64,([A-Za-z0-9+/=]+)/.exec(src)?.[1]
        if (!payload) throw new Error('Expected a WOFF2 data URL in the external stylesheet')
        const bytes = Uint8Array.from(atob(payload), character => character.charCodeAt(0))
        const hash = await crypto.subtle.digest('SHA-256', bytes)
        const weight = rule.style.getPropertyValue('font-weight')
        const face = [...document.fonts].find(f => f.weight === weight && /Noto Sans/.test(f.family))
        list.push({ stylesheet: sheet.href, weight, mediaType: 'font/woff2', status: face?.status, bytes: bytes.length,
          sha256: Array.from(new Uint8Array(hash), n => n.toString(16).padStart(2, '0')).join('') })
      }
    }
    return list
  })
  expect(faces.map(face => face.weight).sort()).toEqual(['400', '700'])
  for (const face of faces) {
    const source = await readFile(new URL('../../src/assets/fonts/noto-sans-' + face.weight + '.woff2', import.meta.url))
    expect(face.sha256).toBe(createHash('sha256').update(source).digest('hex'))
    expect(face.status).toBe(expectedStatus)
  }
  return faces
}
