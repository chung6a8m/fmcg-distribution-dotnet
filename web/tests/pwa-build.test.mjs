import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { test } from 'node:test'

const dist = new URL('../dist/', import.meta.url)

test('generated PWA has a user-confirmed update path', () => {
  const worker = readFileSync(new URL('sw.js', dist), 'utf8')
  assert.match(worker, /SKIP_WAITING/, 'The service worker must support the explicit update action')
})

test('generated service worker precaches built HTML, JS, and CSS assets', () => {
  const worker = readFileSync(new URL('sw.js', dist), 'utf8')
  const precacheCall = worker.match(/\bprecacheAndRoute\(\s*\[([\s\S]*?)\]\s*(?:,|\))/)
  assert.ok(precacheCall, 'Expected Workbox precacheAndRoute with a generated manifest')

  const urls = [...precacheCall[1].matchAll(/["']?url["']?\s*:\s*(["'])(.*?)\1/g)]
    .map(([, , url]) => url.replace(/^\.?\//, ''))
  const precached = new Set(urls)
  assert.ok(precached.has('index.html'), 'Generated index.html must be precached')
  assert.ok(precached.has('theme-runtime.js'), 'The external parser-blocking bootstrap must be precached')
  assert.deepEqual(readFileSync(new URL('theme-runtime.js', dist)),
    readFileSync(new URL('../public/theme-runtime.js', import.meta.url)))
  assert.deepEqual(urls.filter(path => /^(health|api)(\/|$)/.test(path)), [])

  const assets = readdirSync(new URL('assets/', dist), { withFileTypes: true })
    .filter(entry => entry.isFile() && /\.(?:js|css)$/.test(entry.name))
    .map(entry => entry.name)
  assert.ok(assets.some(name => name.endsWith('.js')), 'Expected a built JavaScript asset')
  assert.ok(assets.some(name => name.endsWith('.css')), 'Expected a built CSS asset')
  for (const name of assets) {
    assert.ok(precached.has(`assets/${name}`), `Built asset missing from precache: ${name}`)
  }
})

test('generated manifest retains the online-first APP-00A app identity', () => {
  const manifest = JSON.parse(readFileSync(new URL('manifest.webmanifest', dist), 'utf8'))
  assert.equal(manifest.start_url, '/')
  assert.equal(manifest.display, 'standalone')
  assert.equal(manifest.name, 'FMCG DMS — APP-00A (Synthetic)')
  assert.equal(manifest.short_name, 'FMCG DMS')
  assert.equal(manifest.theme_color, '#F6F8FB')
  assert.equal(manifest.background_color, '#F6F8FB')
})

test('built head contains one canonical metadata pair and classic script ahead of React and CSS', () => {
  const html = readFileSync(new URL('index.html', dist), 'utf8')
  const head = html.match(/<head>([\s\S]*?)<\/head>/i)?.[1]
  assert.ok(head)
  assert.match(head, /<title>FMCG DMS — APP-00A<\/title>/)
  const metas = [...head.matchAll(/<meta\b[^>]+>/g)].map(match => match[0])
  assert.deepEqual(metas.filter(meta => meta.includes('name="theme-color"')),
    ['<meta name="theme-color" content="#F6F8FB" data-light="#F6F8FB" data-dark="#101827"/>'])
  assert.deepEqual(metas.filter(meta => meta.includes('name="color-scheme"')),
    ['<meta name="color-scheme" content="light"/>'])
  const bootstrap = '<script src="/theme-runtime.js"></script>'
  assert.equal(head.split(bootstrap).length, 2)
  const bootstrapIndex = head.indexOf(bootstrap)
  assert.ok(head.search(/<script type="module"[^>]+src="\/assets\//) > bootstrapIndex)
  assert.ok(head.search(/<link rel="stylesheet"[^>]+href="\/assets\//) > bootstrapIndex)
})

test('linked precached stylesheet carries both source-identical static Noto WOFF2 weights', () => {
  const html = readFileSync(new URL('index.html', dist), 'utf8')
  const styles = [...html.matchAll(/<link rel="stylesheet"[^>]+href="\/(assets\/[^"]+)"/g)]
  assert.equal(styles.length, 1)
  const css = readFileSync(new URL(styles[0][1], dist), 'utf8')
  const faces = [...css.matchAll(/@font-face\s*\{([^{}]+)\}/g)].map(match => match[1])
  assert.equal(faces.length, 2)
  for (const weight of [400, 700]) {
    const matches = faces.filter(face => face.includes('font-weight:' + weight + ';'))
    assert.equal(matches.length, 1)
    assert.match(matches[0], /font-family:["']?Noto Sans["']?;/)
    assert.match(matches[0], /font-display:swap;/)
    const src = matches[0].match(/src:url\(["']?data:font\/woff2;base64,([a-z0-9+/=]+)["']?\)\s*format\(["']woff2["']\)/i)
    assert.ok(src, 'Expected an embedded WOFF2 payload for weight ' + weight)
    const source = new URL('../src/assets/fonts/noto-sans-' + weight + '.woff2', import.meta.url)
    assert.deepEqual(Buffer.from(src[1], 'base64'), readFileSync(source))
  }
})
