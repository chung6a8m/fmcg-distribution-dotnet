import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'
import babelParser from '@babel/parser'

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
  assert.equal(node?.type, 'ObjectExpression', 'Scope changed: expected literal object')
  const result = new Map()
  for (const property of node.properties) {
    assert.equal(property?.type, 'ObjectProperty', 'No spreads, methods or computed properties permitted')
    assert.ok(!property.computed && ['Identifier', 'StringLiteral'].includes(property.key?.type),
      'No dynamic config property name permitted')
    const name = property.key.name ?? property.key.value
    assert.ok(!result.has(name), 'No duplicate config property: ' + name)
    result.set(name, property.value)
  }
  return result
}
function exactKeys(map, names) {
  assert.deepEqual([...map.keys()].sort(), [...names].sort(), 'New config property needs a security review')
}
function singleNamedCall(expr, name) {
  assert.ok(expr?.type === 'CallExpression' && expr.callee?.type === 'Identifier'
    && expr.callee.name === name && expr.arguments.length === 1,
  'Unexpected call/input for ' + name)
  return expr.arguments[0]
}

function verifyViteSource(text) {
  const file = babelParser.parse(text, { sourceType: 'module', plugins: ['typescript'] })
  const imports = []
  const exports = []
  for (const statement of file.program.body) {
    if (statement.type === 'ImportDeclaration') {
      assert.equal(statement.source?.type, 'StringLiteral')
      imports.push(statement.source.value)
    } else {
      assert.equal(statement.type, 'ExportDefaultDeclaration',
        'Unexpected executable top-level configuration')
      exports.push(statement)
    }
  }
  assert.deepEqual(imports.sort(),
    ['vite', '@vitejs/plugin-react', '@tailwindcss/vite', 'vite-plugin-pwa'].sort(),
    'New import/plugin needs independent risk review')
  assert.equal(exports.length, 1)
  const root = properties(singleNamedCall(exports[0].declaration, 'defineConfig'), file)
  exactKeys(root, ['plugins', 'server'])
  const plugins = root.get('plugins')
  assert.equal(plugins?.type, 'ArrayExpression')
  assert.equal(plugins.elements.length, 3, 'Additional Vite plugins require security review')
  assert.ok(plugins.elements[0]?.type === 'CallExpression' &&
    plugins.elements[0].callee?.type === 'Identifier' && plugins.elements[0].callee.name === 'react' &&
    plugins.elements[0].arguments.length === 0)
  assert.ok(plugins.elements[1]?.type === 'CallExpression' &&
    plugins.elements[1].callee?.type === 'Identifier' && plugins.elements[1].callee.name === 'tailwindcss' &&
    plugins.elements[1].arguments.length === 0)
  const pwa = properties(singleNamedCall(plugins.elements[2], 'VitePWA'), file)
  exactKeys(pwa, ['registerType', 'manifest', 'workbox'])
  const register = pwa.get('registerType')
  assert.ok(register?.type === 'StringLiteral' && register.value === 'prompt')
  const workbox = properties(pwa.get('workbox'), file)
  exactKeys(workbox, ['navigateFallbackDenylist', 'runtimeCaching', 'globPatterns'])
  const globs = workbox.get('globPatterns')
  assert.ok(globs?.type === 'ArrayExpression' && globs.elements.length === 1)
  assert.ok(globs.elements[0]?.type === 'StringLiteral' && globs.elements[0].value === literalGlob,
    'Dynamic or changed precache glob is outside the conditional risk acceptance')
  const caching = workbox.get('runtimeCaching')
  assert.ok(caching?.type === 'ArrayExpression' && caching.elements.length === 0,
    'Runtime caching must stay disabled')
  const deny = workbox.get('navigateFallbackDenylist')
  assert.ok(deny?.type === 'ArrayExpression' && deny.elements.length === 1)
  assert.equal(deny.elements[0]?.type, 'RegExpLiteral')
  assert.equal(text.slice(deny.elements[0].start, deny.elements[0].end), '/^\\/api\\//')
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
  const parser = lock.packages['node_modules/@babel/parser']
  assert.equal(parser?.version, '7.29.14', 'Transitive parser changed: re-review AST test provenance')
  assert.equal(parser?.integrity,
    'sha512-VL220vXuyO2z3rNIQwY7ftj9UwpqOt+33sH+sBWkOekbsSR2g7EEMnOx24ZlCH0xOA3DM/33SifVFGbnJD8r/w==',
    'AST parser bytes changed: review required')
  assert.equal(parser?.dev, true, 'AST parser must be build-time-only')
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
