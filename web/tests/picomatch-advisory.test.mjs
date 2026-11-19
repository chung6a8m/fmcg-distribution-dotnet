import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

// OWN-ISSUE59-B-20261119: an expiring, source-only withdrawn-advisory
// classification, NOT a patched picomatch. Never suppress npm audit.
const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'))
const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))
const policy = JSON.parse(readFileSync(
  new URL('../../docs/security/issue59-conditional-risk-policy.json', import.meta.url), 'utf8'))
const decision = readFileSync(
  new URL('../../docs/decisions/owner-issue59-conditional-risk-reclassification-2026-11-19.md', import.meta.url),
  'utf8')

const DECISION = 'OWN-ISSUE59-B-20261119'
const EXPIRY_UTC = '2026-12-19T00:00:00Z'
const OFFICIAL = {
  path: 'node_modules/picomatch',
  version: '4.0.7',
  resolved: 'https://registry.npmjs.org/picomatch/-/picomatch-4.0.7.tgz',
  integrity: 'sha512-qcJu88Q2IWqJsDD529JKMdwGm/dvInW4HvQnRwiH9JtihJvzGOscDtHE3x1pBKeUOTysQ8kVmLnJ2kJu7yhcGA==',
  devOnly: true
}

function verifyConditionalPolicy(record, nowMs = Date.now()) {
  assert.equal(record.schema, 'ISSUE59_WITHDRAWN_ADVISORY_RISK_V1')
  assert.equal(record.decisionId, DECISION)
  assert.equal(record.ownerDecision, 'CONDITIONAL_SCOPED_RECLASSIFICATION')
  assert.equal(record.advisoryId, 'GHSA-fc5q-39xc-vjpf')
  assert.equal(record.cveId, 'CVE-2026-112366')
  assert.equal(record.advisoryState, 'WITHDRAWN_2026-11-18')
  assert.equal(record.softwareState, 'UNPATCHED_NO_PATCH_CLAIM')
  assert.equal(record.scope, 'APP00A_SYNTHETIC_CI_BUILD_TOOLS_ONLY')
  assert.equal(record.expiresAtUtc, EXPIRY_UTC, 'No automatic expiry extension')
  assert.ok(Number.isFinite(nowMs))
  assert.ok(nowMs < Date.parse(EXPIRY_UTC),
    'Issue #59 risk acceptance EXPIRED: explicit independent review and new Owner decision required')
  assert.deepEqual(record.permittedPackage, OFFICIAL)
  assert.deepEqual(record.requiredApprovals, {
    owner: 'CONDITIONAL_DIRECTION_APPROVED_2026-11-19',
    independentSecurityReview: 'REQUIRED_BEFORE_MERGE',
    finalHeadAcceptance: 'REQUIRED_BEFORE_MERGE'
  })
  assert.deepEqual(record.prohibitions, {
    dynamicOrUntrustedGlobPatterns: true,
    dependencyAliasOrUnreviewedFork: true,
    auditIgnoreOrSeverityReduction: true,
    frontendArtifactPublication: true,
    physicalPreprodDeployment: true,
    productionOrPilotAdmission: true
  })
}

function verifyPinnedDevelopmentDependency(data) {
  assert.equal(data.lockfileVersion, 3)
  assert.ok(data.packages && typeof data.packages === 'object')
  const paths = Object.entries(data.packages)
    .filter(([path]) => path === 'node_modules/picomatch' || path.endsWith('/node_modules/picomatch'))
  assert.equal(paths.length, 1, 'Unreviewed extra/removed picomatch resolution')
  const [path, entry] = paths[0]
  assert.equal(path, OFFICIAL.path)
  assert.equal(entry.version, OFFICIAL.version, 'New picomatch version needs a new security decision')
  assert.equal(entry.resolved, OFFICIAL.resolved, 'Not the approved official npm tarball')
  assert.equal(entry.integrity, OFFICIAL.integrity, 'Package bytes/integrity changed')
  assert.equal(entry.dev, true, 'Build-only dependency was promoted to production')

  for (const [location, item] of Object.entries(data.packages)) {
    assert.doesNotMatch(location, /node_modules\/(?:@rootio|@ryotsu)\/picomatch(?:\/|$)/,
      'Unreviewed fork or alias')
    if (location.endsWith('/node_modules/workbox-build') || location === 'node_modules/workbox-build') {
      assert.equal(item.dev, true, 'Workbox must remain build-time-only')
    }
  }
  const root = data.packages['']
  assert.ok(root)
  assert.equal(root.dependencies?.picomatch, undefined)
  assert.equal(root.devDependencies?.picomatch, undefined)
  assert.equal(root.engines?.node, '>=26.13.1 <27')
  assert.equal(root.devDependencies?.vite, '8.3.8')
  assert.equal(root.devDependencies?.['vite-plugin-pwa'], '2.0.0')
}

test('C59-B01/B03: exact Owner decision, unchanged unpatched classification and review expiry', () => {
  verifyConditionalPolicy(policy)
  assert.match(decision, /OWN-ISSUE59-B-20261119/)
  assert.match(decision, /Independent Security reviewer/)
  assert.match(decision, /NO_GO/)
})

test('C59-B01: only the official picomatch 4.0.7 dev-only exact npm tarball is admitted', () => {
  verifyPinnedDevelopmentDependency(lock)
  assert.equal(pkg.dependencies?.picomatch, undefined)
  assert.equal(pkg.devDependencies?.picomatch, undefined)
  assert.equal(pkg.engines?.node, '>=26.13.1 <27')
  assert.equal(pkg.devDependencies?.vite, '8.3.8')
  assert.equal(pkg.devDependencies?.['vite-plugin-pwa'], '2.0.0')
  assert.deepEqual(pkg.overrides, { 'workbox-build': { glob: '14.0.6' } },
    'Unexpected nested/global dependency override needs a new review')
  assert.deepEqual(Object.keys(pkg.dependencies || {}).sort(), ['react', 'react-dom'])
  assert.equal(pkg.dependencies.react, '19.3.1')
  assert.equal(pkg.dependencies['react-dom'], '19.3.1')
  assert.equal(pkg.scripts?.build, 'tsc -b && vite build',
    'Build script must not accept uncontrolled glob inputs')
  const [major, minor, patch] = process.versions.node.split('.').map(Number)
  assert.equal(major, 26, 'Risk verification requires Node.js 26 LTS')
  assert.ok(minor > 13 || (minor === 13 && patch >= 1),
    'Risk verification requires Node 26.13.1+')
})

test('C59-B01 negative cases: version, second resolution, production flag, publisher or integrity drift DENIED', () => {
  const mutate = fn => {
    const candidate = structuredClone(lock)
    fn(candidate)
    assert.throws(() => verifyPinnedDevelopmentDependency(candidate))
  }
  mutate(l => { l.packages['node_modules/picomatch'].version = '4.0.6' })
  mutate(l => { l.packages['node_modules/picomatch'].version = '4.0.8' })
  mutate(l => { l.packages['node_modules/picomatch'].dev = false })
  mutate(l => { l.packages['node_modules/picomatch'].resolved = 'https://example.invalid/picomatch.tgz' })
  mutate(l => { l.packages['node_modules/picomatch'].integrity = 'sha512-UNREVIEWED' })
  mutate(l => { l.packages['node_modules/extra/node_modules/picomatch'] = structuredClone(l.packages['node_modules/picomatch']) })
  mutate(l => { l.packages['node_modules/@ryotsu/picomatch'] = { version: '4.0.7-a', dev: true } })
  mutate(l => { l.packages['node_modules/workbox-build'].dev = false })
  mutate(l => { l.packages[''].dependencies.picomatch = '4.0.7' })
})

test('C59-B03 negative cases: expired, extended, widened or missing risk decision DENIED', () => {
  assert.throws(() => verifyConditionalPolicy(policy, Date.parse(EXPIRY_UTC)))
  const mutate = fn => {
    const candidate = structuredClone(policy)
    fn(candidate)
    assert.throws(() => verifyConditionalPolicy(candidate, Date.parse('2026-11-19T00:00:00Z')))
  }
  mutate(p => { p.expiresAtUtc = '2027-12-19T00:00:00Z' })
  mutate(p => { p.scope = 'PRODUCTION' })
  mutate(p => { p.decisionId = 'SELF_APPROVED' })
  mutate(p => { p.advisoryState = 'FIXED' })
  mutate(p => { p.softwareState = 'PATCH_VERIFIED' })
  mutate(p => { p.prohibitions.frontendArtifactPublication = false })
  mutate(p => { p.requiredApprovals.independentSecurityReview = 'WAIVED' })
  mutate(p => { delete p.permittedPackage })
})
