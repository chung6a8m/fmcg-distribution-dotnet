import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))
const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'))

function isPatchedGlob(version) {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version)
  if (!match) return false
  const [, major, minor, patch] = match.map(Number)
  return major > 14 || (major === 14 && (minor > 0 || patch >= 2))
}

test('Workbox lockfile resolves the patched glob major (GHSA-3g3m-2fjr-6pw4)', () => {
  const pinnedGlob = pkg.overrides?.['workbox-build']?.glob
  assert.ok(pinnedGlob && isPatchedGlob(pinnedGlob), 'Workbox needs a patched glob override until upstream ships one')

  const installed = Object.entries(lock.packages).filter(([path]) => path.endsWith('node_modules/glob'))
  assert.ok(installed.length > 0, 'glob should be present as the Workbox build dependency')
  for (const [path, entry] of installed) {
    assert.ok(isPatchedGlob(entry.version), `Vulnerable glob resolution: ${path}@${entry.version}`)
    assert.equal(entry.version, pinnedGlob, `Unexpected glob resolution: ${path}`)
  }
})

test('Workbox and patched glob stay build-time-only dependencies', () => {
  assert.ok(pkg.devDependencies['vite-plugin-pwa'])
  assert.equal(pkg.dependencies['vite-plugin-pwa'], undefined)
  assert.equal(lock.packages['node_modules/workbox-build']?.dev, true)
  assert.equal(lock.packages['node_modules/glob']?.dev, true)
})
