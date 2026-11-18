import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

// Independent fail-closed acceptance for GHSA-fc5q-39xc-vjpf / CVE-2026-112366.
// The upstream advisory identifies picomatch through 4.0.7 as affected.
// A future version is NOT automatically considered secure by this test; full
// npm audit, a reviewed fix diff/provenance and regression evidence are separate gates.
const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'))
const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))

const affectedMax = [4, 0, 7]
function parseVersion(value) {
  const match = /^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/.exec(value || '')
  return match ? match.slice(1, 4).map(Number) : null
}
function compareParts(a, b) {
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i]
  return 0
}

test('Issue #59: no affected picomatch <=4.0.7 in lockfile', () => {
  assert.equal(lock.lockfileVersion, 3)
  const matches = Object.entries(lock.packages || {})
    .filter(([path]) => path === 'node_modules/picomatch' || path.endsWith('/node_modules/picomatch'))
  assert.ok(matches.length > 0, 'Missing dependency inventory: no picomatch found')
  const affected = []
  for (const [path, entry] of matches) {
    const version = parseVersion(entry.version)
    assert.ok(version, `Unreviewed picomatch version at ${path}: ${entry.version}`)
    assert.ok(entry.integrity && entry.resolved, `Missing package integrity/provenance at ${path}`)
    if (compareParts(version, affectedMax) <= 0) affected.push(`${path}@${entry.version}`)
  }
  assert.deepEqual(affected, [],
    'BLOCKED GHSA-fc5q-39xc-vjpf: upstream 4.0.7 and older are affected; no waivers: ' + affected.join(', '))
})

test('Issue #59: Node 26 and Workbox/PWA baseline stay unchanged', () => {
  assert.equal(pkg.engines?.node, '>=26.13.1 <27')
  assert.equal(pkg.devDependencies?.vite, '8.3.8')
  assert.equal(pkg.devDependencies?.['vite-plugin-pwa'], '2.0.0')
  assert.equal(pkg.overrides?.['workbox-build']?.glob, '14.0.6')
})
