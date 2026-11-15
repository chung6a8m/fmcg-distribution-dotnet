import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

const dist = new URL('../dist/', import.meta.url)

test('generated PWA has a user-confirmed update path', () => {
  const worker = readFileSync(new URL('sw.js', dist), 'utf8')
  assert.match(worker, /SKIP_WAITING/, 'The service worker must support the explicit update action')
})

test('generated manifest retains the online-first APP-00A app identity', () => {
  const manifest = JSON.parse(readFileSync(new URL('manifest.webmanifest', dist), 'utf8'))
  assert.equal(manifest.start_url, '/')
  assert.equal(manifest.display, 'standalone')
  assert.match(manifest.name, /APP-00A/)
})
