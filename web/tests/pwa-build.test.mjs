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
  assert.match(manifest.name, /APP-00A/)
})
