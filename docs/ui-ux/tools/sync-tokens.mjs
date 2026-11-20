// Docs-only reference generation/verification. Does not import into web/ or change CI gates.
import assert from 'node:assert/strict'
import { readFile, writeFile } from 'node:fs/promises'

const mode = process.argv[2] ?? '--check'
assert.ok(['--check', '--write'].includes(mode), 'Usage: node docs/ui-ux/tools/sync-tokens.mjs [--check|--write]')
const tokenUrl = new URL('../tokens/tokens.json', import.meta.url)
const source = JSON.parse(await readFile(tokenUrl, 'utf8'))
const entries = Object.entries(source.tokens)
const semanticColors = entries.filter(([name]) => name.startsWith('color.') && !name.startsWith('color.primitive.'))
const dark = source.themes.dark.values
const alias = /^\{([^}]+)\}$/
const hex = /^#[0-9a-f]{6}$/i
const variables = new Set()
const types = new Set(['color', 'dimension', 'duration', 'number', 'fontFamily'])

function resolved(name, theme = 'light', path = []) {
  assert.ok(!path.includes(name), `Alias cycle: ${[...path, name].join(' -> ')}`)
  const token = source.tokens[name]
  assert.ok(token, `Unknown token ${name}`)
  const value = theme === 'dark' && Object.hasOwn(dark, name) ? dark[name] : token.value
  const target = typeof value === 'string' && value.match(alias)?.[1]
  if (!target) return value
  assert.equal(source.tokens[target]?.type, token.type, `Alias type mismatch: ${name}`)
  return resolved(target, theme, [...path, name])
}

for (const [name, token] of entries) {
  assert.ok(types.has(token.type), `Unknown type: ${name}`)
  assert.match(token.cssVariable, /^--fmcg-[a-z0-9-]+$/)
  assert.ok(!variables.has(token.cssVariable), `Duplicate CSS variable: ${name}`)
  variables.add(token.cssVariable)
  const value = resolved(name)
  if (token.type === 'color') assert.match(value, hex, name)
  if (token.type === 'dimension') assert.match(value, /^\d+(?:\.\d+)?px$/, name)
  if (token.type === 'duration') assert.match(value, /^\d+(?:\.\d+)?ms$/, name)
  if (token.type === 'number') assert.ok(Number.isFinite(value), name)
  if (token.type === 'fontFamily') assert.ok(typeof value === 'string' && value.length > 0, name)
}
assert.deepEqual(Object.keys(dark).sort(), semanticColors.map(([name]) => name).sort(), 'Dark must override every semantic color, and only semantic colors')
for (const [name, value] of Object.entries(dark)) assert.match(value, hex, `Dark must have an explicit color: ${name}`)

function cssValue(token) {
  const target = typeof token.value === 'string' && token.value.match(alias)?.[1]
  return target ? `var(${source.tokens[target].cssVariable})` : token.value
}
const header = `/* FMCG UI/UX v0.1 - OWNER_APPROVED / DOCS_ONLY reference.\n * Generated from tokens.json by tools/sync-tokens.mjs; do not edit alone.\n * Adoption follows docs/decisions/owner-uiux-2026-11-20.md and PR #79 review/CI/merge.\n * Not imported by the application. Runtime integration belongs to a separate UI-01 PR.\n */\n`
const css = `${header}:root {\n  color-scheme: light;\n${entries.map(([, token]) => `  ${token.cssVariable}: ${cssValue(token)};`).join('\n')}\n}\n\n/* Theme is resolved on html only; missing/invalid preference falls back to Light. */\n:root[data-theme="dark"] {\n  color-scheme: dark;\n${semanticColors.map(([name, token]) => `  ${token.cssVariable}: ${dark[name]};`).join('\n')}\n}\n`

const bindings = semanticColors.map(([, token]) => [`--color-fmcg-${token.cssVariable.slice('--fmcg-color-'.length)}`, token.cssVariable])
bindings.push(['--font-fmcg-body', source.tokens['font.family'].cssVariable])
for (const weight of ['regular', 'bold']) bindings.push([`--font-weight-fmcg-${weight}`, source.tokens[`font.weight.${weight}`].cssVariable])
for (const [name, token] of entries.filter(([name]) => name.startsWith('font.size.'))) {
  const suffix = token.cssVariable.slice('--fmcg-font-size-'.length)
  bindings.push([`--text-fmcg-${suffix}`, token.cssVariable])
  bindings.push([`--text-fmcg-${suffix}--line-height`, source.tokens[name.replace('font.size.', 'font.lineHeight.')].cssVariable])
}
for (const [, token] of entries.filter(([name]) => name.startsWith('space.'))) bindings.push([`--spacing-fmcg-${token.cssVariable.slice('--fmcg-space-'.length)}`, token.cssVariable])
for (const [, token] of entries.filter(([name]) => name.startsWith('radius.'))) bindings.push([`--radius-fmcg-${token.cssVariable.slice('--fmcg-radius-'.length)}`, token.cssVariable])
const breakpoints = ['medium', 'large', 'wide'].map(name => {
  // Media-query rem uses the browser's initial font size, not the application's 14px root.
  const pixels = Number.parseFloat(resolved(`layout.breakpoint.${name}`))
  return `  --breakpoint-fmcg-${name}: ${pixels / 16}rem;`
})
const adapter = `${header}/* Import Tailwind once from the consuming web entry, then this mapping. */\n@custom-variant dark (&:where(html[data-theme="dark"], html[data-theme="dark"] *));\n\n@theme inline {\n${bindings.map(([name, variable]) => `  ${name}: var(${variable});`).join('\n')}\n}\n\n/* Build-time literals; CSS custom properties cannot be media-query thresholds. */\n@theme {\n${breakpoints.join('\n')}\n}\n`

const pairs = [
  ['text.primary', 'surface.card', 4.5], ['text.secondary', 'surface.card', 4.5],
  ['text.primary', 'surface.page', 4.5],
  ...['background', 'hover', 'pressed'].map(state => ['action.primary.foreground', `action.primary.${state}`, 4.5]),
  ['action.destructive.foreground', 'action.destructive.background', 4.5],
  ...['surface.card', 'surface.page'].map(surface => ['border.control', surface, 3]),
  ...['surface.card', 'surface.page'].map(surface => ['focus.ring', surface, 3]),
  ...['success', 'info', 'warning', 'danger'].map(state => [`status.${state}.foreground`, `status.${state}.background`, 4.5]),
  ...['surface.page', 'surface.subtle', 'surface.rowAlternate', 'selection.background'].map(surface => ['text.secondary', surface, 4.5]),
  ...['surface.subtle', 'surface.rowAlternate', 'selection.background'].map(surface => ['text.primary', surface, 4.5]),
  ...['surface.subtle', 'surface.rowAlternate'].map(surface => ['border.control', surface, 3]),
  ...['surface.subtle', 'surface.rowAlternate', 'selection.background'].map(surface => ['focus.ring', surface, 3]),
  ...['surface.card', 'surface.page', 'surface.subtle'].map(surface => ['link.text', surface, 4.5]),
  ['selection.indicator', 'selection.background', 3],
  ['action.disabled.foreground', 'action.disabled.background', 4.5]
]
function luminance(color) {
  const rgb = color.slice(1).match(/../g).map(part => Number.parseInt(part, 16) / 255)
  const linear = rgb.map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4)
  return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722
}
for (const theme of ['light', 'dark']) {
  for (const [foreground, background, threshold] of pairs) {
    const a = luminance(resolved(`color.${foreground}`, theme))
    const b = luminance(resolved(`color.${background}`, theme))
    const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
    assert.ok(ratio >= threshold, `${theme}: ${foreground} / ${background}: ${ratio} < ${threshold}`)
  }
  console.log(`${theme}: ${pairs.length}/${pairs.length} declared contrast pairs PASS (unrounded comparisons)`)
}
for (const [filename, generated] of [['tokens.css', css], ['tailwind-v4.css', adapter]]) {
  const url = new URL(`../tokens/${filename}`, import.meta.url)
  if (mode === '--write') await writeFile(url, generated)
  else {
    // Git may check out the same generated LF artifact as CRLF on Windows.
    const checkedOut = (await readFile(url, 'utf8')).replaceAll('\r\n', '\n')
    assert.equal(checkedOut, generated, `${filename} drift; run with --write and review the diff`)
  }
}
console.log(`${entries.length} base tokens; ${semanticColors.length} complete Dark overrides; ${bindings.length} inline bindings; ${breakpoints.length} literal breakpoints; ${mode} PASS`)
