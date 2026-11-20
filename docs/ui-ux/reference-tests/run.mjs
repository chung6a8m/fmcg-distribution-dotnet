import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {mkdir, readFile, readdir, writeFile} from 'node:fs/promises'
import {createServer} from 'node:http'
import {createRequire} from 'node:module'
import os from 'node:os'
import {dirname, resolve} from 'node:path'
import {fileURLToPath} from 'node:url'
import {fixture} from './fixture.mjs'
import {contains, measurePaint, measureTable, verifyTable} from './measure.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const root = resolve(here, '../../..')
const git = (...args) => execFileSync('git', args, {cwd: root, encoding: 'utf8'}).trim()
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const now = new Date()
const timestamp = now.toISOString().replaceAll(':', '-')
const output = resolve(process.argv[2] ?? resolve(root, 'test-results', `ui00-reference-${timestamp}`))
await mkdir(dirname(output), {recursive: true})
await mkdir(output) // Refuse to mix new results with stale evidence.
const cases = [
  {id: 'REF-01', theme: 'light', density: 'compact'},
  {id: 'REF-02', theme: 'dark', density: 'compact'},
  {id: 'REF-03', theme: 'light', density: 'comfortable'},
  {id: 'REF-04', theme: 'dark', density: 'comfortable'},
].map(test => ({...test, viewport: [1440, 900], status: 'NOT_RUN'}))
const narrow = {id: 'REF-05', theme: 'light', density: 'compact', viewport: [1024, 768], status: 'NOT_RUN'}
const baseline = {...cases[0], id: 'BASE-01', clearance: false}
const svgCase = {id: 'SVG-01', renderer: 'Chromium standalone SVG document', viewport: [1600, 1280], status: 'NOT_RUN'}
const manifest = {
  tested_source_sha: git('rev-parse', 'HEAD'),
  dirtyInputs: git('status', '--porcelain', '--', 'docs/ui-ux/reference-tests', 'docs/ui-ux/tokens',
    'docs/ui-ux/components.md', 'docs/ui-ux/assets/design-system-sheet.svg', 'web/package.json', 'web/package-lock.json'),
  startedAt: now.toISOString(),
  startedAtBangkok: new Intl.DateTimeFormat('sv-SE', {dateStyle: 'short', timeStyle: 'medium',
    timeZone: 'Asia/Bangkok'}).format(now) + ' +07:00',
  environment: {os: `${os.type()} ${os.release()} (${os.version()}) ${os.arch()}`, node: process.version},
  cases: [...cases, narrow, baseline, svgCase],
}
const save = (name, data) => writeFile(resolve(output, name), JSON.stringify(data, null, 2) + '\n')

const require = createRequire(resolve(root, 'web/package.json'))
async function assets() {
  const {compile} = require('@tailwindcss/node')
  const config = require('./package.json')
  const tailwind = require('tailwindcss/package.json').version
  const compiler = require(resolve(dirname(require.resolve('@tailwindcss/node')), '../package.json')).version
  const playwright = require('@playwright/test/package.json').version
  assert.equal(tailwind, config.devDependencies.tailwindcss)
  assert.equal(compiler, tailwind)
  assert.equal(playwright, config.devDependencies['@playwright/test'])
  Object.assign(manifest.environment, {tailwind, compiler, playwright})
  const source = `@layer theme, base, components, utilities;
@import "tailwindcss" source(none);
@import "../../docs/ui-ux/tokens/tokens.css" layer(theme);
@import "../../docs/ui-ux/tokens/tailwind-v4.css";`
  const compiled = await compile(source, {base: resolve(root, 'web/src'), onDependency() {}})
  const markup = cases.map(({theme, density}) => fixture(theme, density)).join('')
  const candidates = new Set([...markup.matchAll(/\bclass="([^"]+)"/g)].flatMap(([, list]) => list.split(/\s+/)))
  const css = compiled.build([...candidates]) + '\n' + await readFile(resolve(here, 'fixture.css'), 'utf8')
  const font = await readFile(resolve(here, 'fonts/NotoSans.ttf'))
  const svg = await readFile(resolve(here, '../assets/design-system-sheet.svg'))
  const embedded = [...svg.toString().matchAll(/data:font\/[^;,]+;base64,([A-Za-z0-9+/=]+)/g)]
    .map(([, base64]) => hash(Buffer.from(base64, 'base64')))
  assert.equal(embedded.length, 2)
  manifest.resources = {
    cssSha256: hash(css),
    font: {name: 'Noto Sans 2.015', license: 'SIL OFL 1.1', sha256: hash(font)},
    svg: {sha256: hash(svg), embeddedFontSha256: embedded},
  }
  return {css, font, svg}
}

async function serve({css, font, svg}) {
  const routes = new Map([
    ['/reference.css', ['text/css; charset=utf-8', css]],
    ['/NotoSans.ttf', ['font/ttf', font]],
    ['/design-system-sheet.svg', ['image/svg+xml', svg]],
    ...[...cases, narrow, baseline].map(({id, theme, density, clearance}) =>
      [`/${id}`, ['text/html; charset=utf-8', fixture(theme, density, clearance)]]),
  ])
  const server = createServer((request, response) => {
    if (request.url === '/favicon.ico') return response.writeHead(204).end()
    const asset = routes.get(request.url)
    if (!asset) return response.writeHead(404).end()
    response.writeHead(200, {'Content-Type': asset[0], 'Cache-Control': 'no-store'}).end(asset[1])
  })
  await new Promise((accept, reject) => server.once('error', reject).listen(0, '127.0.0.1', accept))
  const origin = `http://127.0.0.1:${server.address().port}`
  return {server, origin}
}

async function platformFonts(context, page, selectors) {
  const cdp = await context.newCDPSession(page)
  try {
    await cdp.send('DOM.enable')
    await cdp.send('CSS.enable')
    const {root} = await cdp.send('DOM.getDocument')
    const fonts = {}
    for (const selector of selectors) {
      const {nodeId} = await cdp.send('DOM.querySelector', {nodeId: root.nodeId, selector})
      fonts[selector] = (await cdp.send('CSS.getPlatformFontsForNode', {nodeId})).fonts
    }
    return fonts
  } finally {
    await cdp.detach()
  }
}

function fullSize([width, height]) {
  return {width, height}
}

async function checkTable(context, page, origin, test) {
  await page.setViewportSize(fullSize(test.viewport))
  await page.emulateMedia({colorScheme: test.theme === 'light' ? 'dark' : 'light'})
  assert.equal((await page.goto(`${origin}/${test.id}`)).status(), 200)
  assert.equal(await page.title(), 'Bảng hàng hóa tham chiếu')
  await page.evaluate(() => document.fonts.ready)
  test.fonts = await platformFonts(context, page, ['h1', '[data-kind=read] th', '[data-kind=long] .product span',
    '#quantity-error', '[data-kind=edit] .status', '[data-kind=read] .numeric'])
  for (const [selector, fonts] of Object.entries(test.fonts)) {
    assert.ok(fonts.length > 0 && fonts.every(font => font.isCustomFont && font.familyName === 'Noto Sans'), selector)
  }
  const shot = (state) => page.screenshot({path: resolve(output, `${test.id}-${state}.png`)})
  const before = await shot('read')
  const focusSteps = [
    ['Tab', 'Xem dòng'], ['Tab', '#before-editor'], ['Tab', '#editor'],
    ['Tab', '#after-editor'], ['Shift+Tab', '#editor'],
  ]
  test.focusSteps = []
  for (const [key, expected] of focusSteps) {
    await page.keyboard.press(key)
    const focused = await page.evaluate(() => document.activeElement.id
      ? '#' + document.activeElement.id
      : document.activeElement.textContent)
    assert.equal(focused, expected)
    test.focusSteps.push({key, focused})
  }
  const after = await shot('focus')
  test.geometry = await measureTable(page)
  test.paint = await measurePaint(page, before, after, test.geometry.editor)
  if (test.clearance === false) {
    // This diagnostic has an intentional red geometry assertion. Only classify
    // the expected overflow as FAIL. Infrastructure errors use the normal path.
    const {editor, rows} = test.geometry
    assert.equal(rows.edit.height, 36)
    assert.equal(editor.bounds.height, 32)
    assert.equal(editor.ring.bottom - editor.ring.top, 40)
    assert.equal(contains(rows.edit, editor.ring), false, 'Baseline unexpectedly contains the ring')
    test.expectedFinding = '40px outline region escapes the 36px native row'
    return 'FAIL'
  }
  verifyTable(test.geometry, test)
  for (const [side, pixels] of Object.entries(test.paint.after)) {
    for (const [index, pixel] of pixels.entries()) {
      assert.deepEqual(pixel, test.paint.expected, `Outline pixel ${side} ${index}`)
      assert.notDeepEqual(test.paint.before[side][index], pixel, `Unfocused pixel ${side} ${index}`)
    }
  }
  return 'PASS'
}

async function checkSvg(context, page, origin, test) {
  await page.setViewportSize(fullSize(test.viewport))
  assert.equal((await page.goto(`${origin}/design-system-sheet.svg`)).status(), 200)
  await page.evaluate(() => document.fonts.ready)
  test.geometry = await page.evaluate(() => {
    const root = document.documentElement
    const bounds = root.viewBox.baseVal
    const texts = [...root.querySelectorAll('text')]
    const outOfCanvas = texts.filter(text => {
      const {x, y, width, height} = text.getBoundingClientRect()
      return x < 0 || y < 0 || x + width > bounds.width || y + height > bounds.height
    }).map(text => text.textContent)
    return {size: [root.width.baseVal.value, root.height.baseVal.value], count: texts.length, outOfCanvas}
  })
  assert.deepEqual(test.geometry.size, test.viewport)
  assert.ok(test.geometry.count > 0)
  assert.deepEqual(test.geometry.outOfCanvas, [])
  test.fonts = await platformFonts(context, page, ['svg'])
  assert.ok(test.fonts.svg.length > 0 && test.fonts.svg.every(font => font.isCustomFont && font.familyName === 'Noto Sans'))
  await page.screenshot({path: resolve(output, 'SVG-01-light.png')})
  return 'PASS'
}

async function runCase(context, origin, test, check) {
  let page
  let tracing = false
  const errors = []
  try {
    await context.tracing.start({snapshots: true, screenshots: true})
    tracing = true
    page = await context.newPage()
    page.on('pageerror', error => errors.push(error.message))
    page.on('console', message => {
      if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text())
    })
    page.on('requestfailed', request => errors.push(`${request.url()}: ${request.failure().errorText}`))
    page.on('response', response => {
      if (response.status() >= 400) errors.push(`HTTP ${response.status()} ${response.url()}`)
    })
    const status = await check(context, page, origin, test)
    assert.deepEqual(errors, [])
    test.status = status
  } catch (error) {
    const navigationBlocked = error.message.startsWith('page.goto:') &&
      /net::ERR_(CONNECTION_REFUSED|ADDRESS_UNREACHABLE|BLOCKED_BY_ADMINISTRATOR|BLOCKED_BY_CLIENT|NETWORK_ACCESS_DENIED|TIMED_OUT)\b|Timeout \d+ms exceeded/.test(error.message)
    test.status = navigationBlocked ? 'BLOCKED' : 'FAIL'
    test.error = error.stack
    if (page && !page.isClosed()) {
      await page.screenshot({path: resolve(output, `${test.id}-failure.png`), timeout: 3000}).catch(() => {})
    }
  } finally {
    test.browserErrors = errors
    if (tracing) {
      const keep = test.status !== 'PASS' || test.id === 'REF-01'
      await context.tracing.stop(keep ? {path: resolve(output, `${test.id}-trace.zip`)} : {})
    }
    await page?.close()
    console.log(`${test.id} ${test.status}${test.error ? '\n' + test.error : ''}`)
  }
}

let context, server
try {
  const {chromium} = require('playwright')
  const {origin, server: listener} = await serve(await assets())
  server = listener
  manifest.origin = origin
  // Empty userDataDir makes Playwright allocate a new temporary profile. This is
  // full Chromium in headless mode, so Settings is available for browser zoom.
  context = await chromium.launchPersistentContext('', {channel: 'chromium', chromiumSandbox: true, deviceScaleFactor: 1})
  context.setDefaultTimeout(10000)
  manifest.environment.chromium = context.browser().version()
  const settings = context.pages()[0]
  await settings.goto('chrome://settings/appearance')
  const zoomFactor = await settings.evaluate(() => new Promise(resolve => chrome.settingsPrivate.getDefaultZoom(resolve)))
  assert.equal(zoomFactor, 1)
  manifest.environment.browserZoom = {percent: zoomFactor * 100,
    method: 'Chromium Settings getDefaultZoom; fresh profile with no site zoom overrides or zoom actions'}
  manifest.environment.deviceScaleFactor = 1
  await settings.close()
  for (const test of cases) await runCase(context, origin, test, checkTable)
  if (cases.every(test => test.status === 'PASS')) await runCase(context, origin, narrow, checkTable)
  await runCase(context, origin, baseline, checkTable)
  await runCase(context, origin, svgCase, checkSvg)
} catch (error) {
  manifest.blocker = error.stack
  for (const test of manifest.cases) if (test.status === 'NOT_RUN') test.status = 'BLOCKED'
  console.error(error)
  process.exitCode = 1
} finally {
  try {
    try {
      await context?.close()
    } finally {
      if (server) await new Promise(resolve => server.close(resolve))
    }
  } finally {
    manifest.finishedAt = new Date().toISOString()
    manifest.files = Object.fromEntries(await Promise.all((await readdir(output)).sort().map(async name =>
      [name, hash(await readFile(resolve(output, name)))])))
    await save('manifest.json', manifest)
    console.log(`Evidence: ${output}`)
  }
}
if ([...cases, narrow, svgCase].some(test => test.status !== 'PASS') || !baseline.expectedFinding || baseline.error) {
  process.exitCode = 1
}
