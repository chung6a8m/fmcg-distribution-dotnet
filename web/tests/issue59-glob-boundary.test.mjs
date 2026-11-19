import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

// Source-scope guard for Issue #59. This is NOT a picomatch patch or exploitability proof.
// Any new dynamic glob source demands independent security review and a new threat assessment.
const web = new URL('../', import.meta.url)
const source = readFileSync(new URL('vite.config.ts', web), 'utf8')
const pkg = JSON.parse(readFileSync(new URL('package.json', web), 'utf8'))
const lock = JSON.parse(readFileSync(new URL('package-lock.json', web), 'utf8'))
const ci = readFileSync(new URL('../../.github/workflows/app00a.yml', import.meta.url), 'utf8')

test('Issue #59: Vite/Workbox glob source stays a single static trusted literal', () => {
  assert.equal((source.match(/\bglobPatterns\s*:/g) || []).length, 1,
    'Review additional Workbox glob pattern inputs')
  assert.ok(source.includes("globPatterns: ['**/*.{js,css,html,svg,ico,png}']"),
    'A different or dynamic Workbox precache glob requires threat review')
  assert.ok(source.includes('navigateFallbackDenylist: [/^\\/api\\//]'),
    'API navigation exclusion must remain a fixed expression')
  assert.match(source, /runtimeCaching:\s*\[\s*\]/,
    'Dynamic Workbox runtime caching needs an independent input boundary review')
  assert.equal((source.match(/\bVitePWA\s*\(/g) || []).length, 1)
  assert.doesNotMatch(source, /\b(?:process\.env|import\.meta\.env|request\.body|req\.body)\b/,
    'Environment or request-derived patterns cannot be silently introduced')
})

test('Issue #59: picomatch and Workbox build tooling stay outside first-party runtime dependencies', () => {
  for (const moduleName of ['picomatch', 'fdir', 'tinyglobby', 'workbox-build', 'vite', 'vite-plugin-pwa']) {
    assert.equal(pkg.dependencies?.[moduleName], undefined,
      moduleName + ' must not become a first-party runtime dependency without review')
  }
  assert.ok(pkg.devDependencies?.vite && pkg.devDependencies?.['vite-plugin-pwa'])
  for (const [path, item] of Object.entries(lock.packages || {})) {
    if (/(?:^|\/)node_modules\/(?:picomatch|workbox-build)$/.test(path)) {
      assert.equal(item.dev, true, path + ' is no longer build/development-only')
    }
  }
})

test('Issue #59: first-party browser code does not import build-only glob parsers', () => {
  const srcDir = fileURLToPath(new URL('src/', web))
  let inspected = 0
  const walk = directory => {
    for (const item of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, item.name)
      if (item.isDirectory()) {
        walk(path)
      } else if (/\.[cm]?[jt]sx?$/.test(item.name)) {
        inspected++
        const body = readFileSync(path, 'utf8')
        assert.doesNotMatch(body,
          /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)['"](?:picomatch|tinyglobby|fdir|workbox-build)(?:\/|['"])/,
          'Unexpected runtime glob-parsing import: ' + path)
      }
    }
  }
  walk(srcDir)
  assert.ok(inspected > 0, 'Missing first-party runtime source inventory')
})

test('Issue #59: mandatory full-tree audit has not been replaced by a dev-only check', () => {
  assert.match(ci, /npm audit --omit=dev --audit-level=low/)
  assert.match(ci, /npm audit --audit-level=moderate/)
  assert.match(ci, /npm ci --no-audit --no-fund/)
})
