import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'
import ts from 'typescript'

// OWN-ISSUE59-B-20261119: AST-verified, narrowly scoped build-only input boundary.
// This checks first-party source, NOT general picomatch ReDoS resistance.
const web = new URL('../', import.meta.url)
const source = readFileSync(new URL('vite.config.ts', web), 'utf8')
const pkg = JSON.parse(readFileSync(new URL('package.json', web), 'utf8'))
const lock = JSON.parse(readFileSync(new URL('package-lock.json', web), 'utf8'))
const app00a = readFileSync(new URL('../../.github/workflows/app00a.yml', import.meta.url), 'utf8')
const securityCI = readFileSync(
  new URL('../../.github/workflows/security-picomatch-issue59.yml', import.meta.url), 'utf8')

const literalGlob = '**/*.{js,css,html,svg,ico,png}'

function properties(node, file) {
  assert.ok(ts.isObjectLiteralExpression(node), 'Scope changed: expected literal object')
  const result = new Map()
  for (const property of node.properties) {
    assert.ok(ts.isPropertyAssignment(property), 'No spreads, methods or computed properties permitted')
    assert.ok(ts.isIdentifier(property.name) || ts.isStringLiteral(property.name),
      'No dynamic config property name permitted')
    const name = property.name.text
    assert.ok(!result.has(name), 'No duplicate config property: ' + name)
    result.set(name, property.initializer)
  }
  return result
}
function exactKeys(map, names) {
  assert.deepEqual([...map.keys()].sort(), [...names].sort(), 'New config property needs a security review')
}
function singleNamedCall(expr, name) {
  assert.ok(ts.isCallExpression(expr) && ts.isIdentifier(expr.expression)
    && expr.expression.text === name && expr.arguments.length === 1,
  'Unexpected call/input for ' + name)
  return expr.arguments[0]
}

function verifyViteSource(text) {
  const file = ts.createSourceFile('vite.config.ts', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS)
  assert.equal(file.parseDiagnostics.length, 0, 'Configuration parse errors are not accepted')
  const imports = []
  const exports = []
  for (const statement of file.statements) {
    if (ts.isImportDeclaration(statement)) {
      assert.ok(ts.isStringLiteral(statement.moduleSpecifier))
      imports.push(statement.moduleSpecifier.text)
    } else {
      assert.ok(ts.isExportAssignment(statement) && !statement.isExportEquals,
        'Unexpected executable top-level configuration')
      exports.push(statement)
    }
  }
  assert.deepEqual(imports.sort(),
    ['vite', '@vitejs/plugin-react', '@tailwindcss/vite', 'vite-plugin-pwa'].sort(),
    'New import/plugin needs independent risk review')
  assert.equal(exports.length, 1)
  const root = properties(singleNamedCall(exports[0].expression, 'defineConfig'), file)
  exactKeys(root, ['plugins', 'server'])
  const plugins = root.get('plugins')
  assert.ok(ts.isArrayLiteralExpression(plugins))
  assert.equal(plugins.elements.length, 3, 'Additional Vite plugins require security review')
  assert.ok(ts.isCallExpression(plugins.elements[0]) &&
    ts.isIdentifier(plugins.elements[0].expression) && plugins.elements[0].expression.text === 'react')
  assert.ok(ts.isCallExpression(plugins.elements[1]) &&
    ts.isIdentifier(plugins.elements[1].expression) && plugins.elements[1].expression.text === 'tailwindcss')
  const pwa = properties(singleNamedCall(plugins.elements[2], 'VitePWA'), file)
  exactKeys(pwa, ['registerType', 'manifest', 'workbox'])
  const register = pwa.get('registerType')
  assert.ok(ts.isStringLiteral(register) && register.text === 'prompt')
  const workbox = properties(pwa.get('workbox'), file)
  exactKeys(workbox, ['navigateFallbackDenylist', 'runtimeCaching', 'globPatterns'])
  const globs = workbox.get('globPatterns')
  assert.ok(ts.isArrayLiteralExpression(globs) && globs.elements.length === 1)
  assert.ok(ts.isStringLiteral(globs.elements[0]) && globs.elements[0].text === literalGlob,
    'Dynamic or changed precache glob is outside the conditional risk acceptance')
  const caching = workbox.get('runtimeCaching')
  assert.ok(ts.isArrayLiteralExpression(caching) && caching.elements.length === 0,
    'Runtime caching must stay disabled')
  const deny = workbox.get('navigateFallbackDenylist')
  assert.ok(ts.isArrayLiteralExpression(deny) && deny.elements.length === 1)
  assert.ok(ts.isRegularExpressionLiteral(deny.elements[0]))
  assert.equal(deny.elements[0].getText(file), '/^\\/api\\//')
}

test('C59-B02: Vite/PWA/Workbox uses only the inspected literal glob AST', () => {
  verifyViteSource(source)
})

test('C59-B02 negative tests: injected input, computed globs, extra plugins, spread, new caching DENIED', () => {
  const canonical = "globPatterns: ['" + literalGlob + "']"
  assert.ok(source.includes(canonical))
  const mutated = [
    source.replace(canonical, 'globPatterns: [process.env.UNTRUSTED_PATTERN]'),
    source.replace(canonical, 'globPatterns: [...process.env.EXTRA_PATTERNS]'),
    source.replace(canonical, "globPatterns: ['" + literalGlob + "', request.body.pattern]"),
    source.replace(canonical, "globPatterns: [getPatternFromTenant()]"),
    source.replace('runtimeCaching: []', 'runtimeCaching: [customerRule]'),
    source.replace('workbox: {', 'workbox: { ...extraConfig,'),
    source.replace('plugins: [', 'plugins: [ untrustedPlugin(),'),
    source.replace('VitePWA({', 'VitePWA(getUntrustedConfig() || {'),
    source.replace(canonical, '/*' + canonical + '*/ globPatterns: [process.env.PATTERN]'),
    source + '\nprocess.env.BAD_PATTERN\n'
  ]
  for (let i = 0; i < mutated.length; i++) {
    assert.notEqual(mutated[i], source, 'Mutation was ineffective: ' + i)
    assert.throws(() => verifyViteSource(mutated[i]), 'Unexpectedly accepted glob ingress mutation: ' + i)
  }
})

test('C59-B01/B02: production/browser source does not directly import build-only glob parsers', () => {
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
        assert.doesNotMatch(body, /\b(?:picomatch|tinyglobby|fdir|workbox-build)\b/,
          'Build-time glob dependency referenced by frontend runtime source: ' + path)
      }
    }
  }
  walk(srcDir)
  assert.ok(inspected > 0, 'Empty runtime source inventory')
  for (const name of ['picomatch', 'fdir', 'tinyglobby', 'workbox-build', 'vite', 'vite-plugin-pwa']) {
    assert.equal(pkg.dependencies?.[name], undefined, 'Build-only package moved into runtime deps: ' + name)
  }
  assert.equal(lock.packages['node_modules/picomatch']?.dev, true)
  assert.equal(lock.packages['node_modules/workbox-build']?.dev, true)
})

test('C59-B04: original APP-00A audits and separate full-audit security job remain mandatory', () => {
  for (const workflow of [app00a, securityCI]) {
    assert.match(workflow, /npm ci --no-audit --no-fund/)
    assert.match(workflow, /npm audit --omit=dev --audit-level=low/)
    assert.match(workflow, /npm audit --audit-level=moderate/)
    assert.doesNotMatch(workflow, /audit-level=(?:high|critical)|--omit=dev --audit-level=moderate/,
      'No weaker full-tree npm audit gates permitted')
  }
  assert.match(securityCI, /node --test web\/tests\/picomatch-advisory\.test\.mjs/)
  assert.match(securityCI, /node --test web\/tests\/issue59-glob-boundary\.test\.mjs/)
})
