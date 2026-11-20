// Docs-only compiler contract. No application imports, browser test, or CI gate.
import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { dirname, resolve } from 'node:path'

// Optional project-root argument supports independent, out-of-tree review.
const here = process.argv[2]
  ? pathToFileURL(`${resolve(process.argv[2], 'docs/ui-ux/tools')}/`)
  : new URL('.', import.meta.url)
const require = createRequire(new URL('../../../web/package.json', here))
const web = require('./package.json')
const tailwind = require('tailwindcss/package.json')
const compiler = require(resolve(dirname(require.resolve('@tailwindcss/node')), '../package.json'))
assert.equal(tailwind.version, web.devDependencies.tailwindcss)
assert.equal(compiler.version, tailwind.version)
assert.equal(web.devDependencies['@tailwindcss/vite'], tailwind.version)
const { compile } = require('@tailwindcss/node')
const source = `@layer theme, base, components, utilities;
@import "tailwindcss" source(none);
@import "../../docs/ui-ux/tokens/tokens.css" layer(theme);
@import "../../docs/ui-ux/tokens/tailwind-v4.css";`
const cases = [
  ['bg-fmcg-surface-card', 'background-color: var(--fmcg-color-surface-card)'],
  ['text-fmcg-text-primary', 'color: var(--fmcg-color-text-primary)'],
  ['border-fmcg-border-control', 'border-color: var(--fmcg-color-border-control)'],
  ['text-fmcg-status-danger-foreground', 'color: var(--fmcg-color-status-danger-foreground)'],
  ['bg-fmcg-action-primary-hover', 'background-color: var(--fmcg-color-action-primary-hover)'],
  ['font-fmcg-body', 'font-family: var(--fmcg-font-family)'],
  ['font-fmcg-bold', 'font-weight: var(--fmcg-font-weight-bold)'],
  ['text-fmcg-body', 'font-size: var(--fmcg-font-size-body)'],
  ['text-fmcg-table', 'line-height: var(--tw-leading, var(--fmcg-font-line-height-table))'],
  ['px-fmcg-12', 'padding-inline: var(--fmcg-space-12)'],
  ['gap-fmcg-8', 'gap: var(--fmcg-space-8)'],
  ['rounded-fmcg-small', 'border-radius: var(--fmcg-radius-small)'],
  ['min-h-(--fmcg-density-compact-control-min)', 'min-height: var(--fmcg-density-compact-control-min)'],
  ['h-(--fmcg-density-compact-row-min)', 'height: var(--fmcg-density-compact-row-min)'],
  ['h-(--fmcg-density-comfortable-row-min)', 'height: var(--fmcg-density-comfortable-row-min)'],
  ['min-h-(--fmcg-density-comfortable-control-min)', 'min-height: var(--fmcg-density-comfortable-control-min)'],
  ['px-fmcg-8', 'padding-inline: var(--fmcg-space-8)'],
  ['py-fmcg-0', 'padding-block: var(--fmcg-space-0)'],
  ['border-separate', 'border-collapse: separate'],
  ['border-spacing-0', 'border-spacing: var(--tw-border-spacing-x) var(--tw-border-spacing-y)'],
  ['outline-fmcg-focus-ring', 'outline-color: var(--fmcg-color-focus-ring)'],
  ['focus-visible:outline-2', 'outline-width: 2px'],
  ['focus-visible:outline-offset-2', 'outline-offset: 2px'],
  ['dark:underline', 'html[data-theme="dark"]'],
  ['fmcg-medium:block', 'display: block'],
  ['fmcg-large:block', 'display: block'],
  ['fmcg-wide:block', 'display: block'],
]
const result = await compile(source, {
  base: fileURLToPath(new URL('../../../web/src/', here)),
  onDependency() {},
})
const css = result.build(cases.map(([candidate]) => candidate))
function rule(candidate) {
  const escaped = candidate.replace(/([^\w-])/g, '\\$1')
  const start = css.indexOf(`.${escaped}`)
  assert.notEqual(start, -1, `Missing utility: ${candidate}`)
  assert.match(css[start + escaped.length + 1], /[\s:{]/, `Utility prefix collision: ${candidate}`)
  const open = css.indexOf('{', start)
  let depth = 1, end = open + 1
  for (; end < css.length && depth; end++) {
    if (css[end] === '{') depth++
    if (css[end] === '}') depth--
  }
  assert.equal(depth, 0, `Unbalanced utility: ${candidate}`)
  return css.slice(start, end)
}
for (const [candidate, expected] of cases) {
  assert.ok(rule(candidate).includes(expected), `${candidate}: expected ${expected}`)
}
assert.ok(rule('text-fmcg-body').includes('var(--fmcg-font-line-height-body)'))
assert.ok(rule('focus-visible:outline-2').includes(':focus-visible'))
const dark = rule('dark:underline')
assert.ok(dark.includes('html[data-theme="dark"] *'))
assert.ok(!dark.includes('prefers-color-scheme'), 'Dark variant must use the root choice')
const media = [...css.matchAll(/@media\s+([^{}]+)\{/g)].map(match => match[1].trim())
assert.ok(media.every(condition => !condition.includes('var(')), 'Unresolved media variable')
for (const [name, literal] of [['medium', '48rem'], ['large', '64rem'], ['wide', '80rem']]) {
  assert.ok(media.includes(`(width >= ${literal})`), `Missing literal breakpoint: ${literal}`)
  const start = css.indexOf(`@media (width >= ${literal})`)
  assert.match(css.slice(start), new RegExp(`^@media[^{}]+\\{\\s*\\.fmcg-${name}\\\\:block \\{`))
}
assert.ok(!/@(?:theme|custom-variant|import)\b/.test(css), 'Uncompiled Tailwind directive')
console.log(JSON.stringify({ node: process.version, tailwind: tailwind.version,
  compiler: compiler.version, utilities: cases.length, media,
  result: 'PASS: reference compiler contract only; no runtime/browser claim' }, null, 2))
