import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { setTimeout as delay } from 'node:timers/promises'
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import test from 'node:test'
import Reporter from '../ci/app03-read-reporter.mjs'
import globalSetup, { startSqlObserver } from '../ci/app03-sql-setup.mjs'
import { browserEvidenceConfig, evidenceDirectory, publishBrowserEvidence } from '../ci/app03-browser-evidence.mjs'

const id = '1234567890abcdef'.repeat(2)

async function fixture(t) {
  const root = await mkdtemp(path.join(tmpdir(), 'app03-read-reporter-'))
  t.after(() => rm(root, { recursive: true, force: true }))
  const directory = path.join(root, 'TestResults', 'app03-5', id)
  await mkdir(directory, { recursive: true })
  return { root, directory }
}

test('only the exact harness directory and validated run/profile are eligible', async t => {
  const { root, directory } = await fixture(t)
  assert.equal(evidenceDirectory(root, 'app03-5', id, directory), directory)
  for (const [profile, runId, supplied] of [
    ['app03-6', id, directory], ['app03-5', id.toUpperCase(), directory],
    ['app03-5', id + '\n', directory], ['app03-5', '../' + id, directory],
    ['app03-5', id, path.dirname(directory)], ['app03-5', id, './TestResults'],
  ]) assert.equal(evidenceDirectory(root, profile, runId, supplied), null)
  const other = path.join(root, 'TestResults', 'app03-4')
  await symlink(path.dirname(directory), other, 'junction')
  assert.equal(evidenceDirectory(root, 'app03-4', id, path.join(other, id)), null)
})

async function stagedFixture(t) {
  const { root, directory } = await fixture(t)
  const temp = await mkdtemp(path.join(tmpdir(), 'app03-private-fixture-'))
  t.after(() => rm(temp, { recursive: true, force: true }))
  const env = { FMCG_APP03_READ_DIAGNOSTICS: '1', RUNNER_TEMP: temp, APP02C1_RUN_ID: id, APP03_EVIDENCE_DIR: directory,
    APP03_SOURCE_TOKEN: 'synthetic.fixture_token-1', APP03_PEER_TOKEN: 'synthetic.other_token-2',
    APP03_RECEIVER_TOKEN: 'synthetic.receiver_token-3' }
  const config = browserEvidenceConfig('app03-5', env, root)
  const raw = path.dirname(config.reporter[1][1].outputFile)
  const browserCase = path.join(config.outputDir, 'field-fixture')
  await mkdir(browserCase, { recursive: true })
  return { root, directory, raw, browserCase, env }
}

test('interruption before publication leaves all raw Playwright outputs outside both upload roots', async t => {
  const { root, directory, raw, browserCase, env } = await stagedFixture(t)
  await writeFile(path.join(raw, 'junit.xml'), env.APP03_SOURCE_TOKEN)
  await writeFile(path.join(browserCase, 'error-context.md'), env.APP03_SOURCE_TOKEN)
  assert.ok(raw.startsWith(env.RUNNER_TEMP + path.sep))
  assert.ok(!raw.startsWith(root + path.sep))
  assert.ok(!raw.split(path.sep).includes('TestResults'))
  await assert.rejects(readFile(path.join(directory, 'junit.xml')), { code: 'ENOENT' })
  await assert.rejects(readFile(path.join(directory, 'browser', 'field-fixture', 'error-context.md')), { code: 'ENOENT' })
  assert.throws(() => browserEvidenceConfig('app03-5', { ...env, RUNNER_TEMP: root }, root))
  assert.throws(() => browserEvidenceConfig('app03-5', { ...env, APP02C1_RUN_ID: '../bad' }, root))
  assert.equal(browserEvidenceConfig('app03-5', { ...env, FMCG_APP03_READ_DIAGNOSTICS: '0' }, root).outputDir,
    '../TestResults/app03-5/' + id + '/browser')
})

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5lqioAAAAASUVORK5CYII=', 'base64')

test('only known sanitized text and named PNG screenshots enter canonical evidence paths', async t => {
  const { root, directory, raw, browserCase, env } = await stagedFixture(t)
  const xml = '<failure><![CDATA[expected heading; Bearer ' + env.APP03_SOURCE_TOKEN + ' and ' + env.APP03_PEER_TOKEN + ']]></failure>'
  const markdown = 'Failed: ' + env.APP03_SOURCE_TOKEN + '\n' + env.APP03_SOURCE_TOKEN + ' and a lookalike syntheticXfixture_token-1\n'
  await writeFile(path.join(raw, 'junit.xml'), xml)
  await writeFile(path.join(browserCase, 'error-context.md'), markdown)
  await writeFile(path.join(browserCase, 'field-320.png'), png)
  await writeFile(path.join(raw, 'unrecognized-report.txt'), env.APP03_SOURCE_TOKEN)
  await writeFile(path.join(browserCase, 'unknown.md'), env.APP03_SOURCE_TOKEN)
  await symlink(root, path.join(raw, 'browser', 'field-symlink'), 'junction')
  const result = await publishBrowserEvidence('app03-5', env, root)
  assert.deepEqual(result, { status: 'withheld', published: 3, withheld: 3 })
  assert.deepEqual(JSON.parse(await readFile(path.join(directory, 'browser-publication.json'), 'utf8')), result)
  const final = path.join(directory, 'browser', 'field-fixture')
  assert.equal(await readFile(path.join(directory, 'junit.xml'), 'utf8'),
    '<failure><![CDATA[expected heading; Bearer [REDACTED] and [REDACTED]]]></failure>')
  assert.equal(await readFile(path.join(final, 'error-context.md'), 'utf8'),
    'Failed: [REDACTED]\n[REDACTED] and a lookalike syntheticXfixture_token-1\n')
  assert.deepEqual(await readFile(path.join(final, 'field-320.png')), png)
  await assert.rejects(readFile(path.join(final, 'unknown.md')), { code: 'ENOENT' })
  await assert.rejects(readFile(path.join(directory, 'browser', 'field-symlink')), { code: 'ENOENT' })
})

test('a publication I/O error or unredactable text cannot expose either original', async t => {
  const { root, directory, raw, browserCase, env } = await stagedFixture(t)
  await writeFile(path.join(raw, 'junit.xml'), '<failure>Bearer unknown-unissued-secret</failure>')
  await writeFile(path.join(browserCase, 'error-context.md'), 'Bearer ' + env.APP03_SOURCE_TOKEN)
  await writeFile(path.join(browserCase, 'field-320.png'), png)
  // A real filesystem error prevents renaming the scrubbed file over this directory.
  const blocked = path.join(directory, 'browser', 'field-fixture', 'error-context.md')
  await mkdir(blocked, { recursive: true })
  const result = await publishBrowserEvidence('app03-5', env, root)
  assert.deepEqual(result, { status: 'withheld', published: 1, withheld: 2 })
  await assert.rejects(readFile(path.join(directory, 'junit.xml')), { code: 'ENOENT' })
  assert.deepEqual(await readFile(path.join(path.dirname(blocked), 'field-320.png')), png)
  assert.equal(await readFile(path.join(browserCase, 'error-context.md'), 'utf8'), 'Bearer ' + env.APP03_SOURCE_TOKEN)
})

const receipt = async directory => JSON.parse(await readFile(path.join(directory, 'sql-observer-startup.json'), 'utf8'))

function fakeObserver(directory, code) {
  const bootstrap = `
    const fs = require('node:fs');
    const file = process.argv[1];
    const write = data => fs.appendFileSync(file, JSON.stringify(data) + '\\n');
    write({event:'start', profile:'app03-5', runId:process.argv[2], pid:process.pid});
  `
  return spawn(process.execPath, ['-e', bootstrap + code, path.join(directory, 'sql-reads.ndjson'), id],
    { stdio: ['pipe', 'pipe', 'ignore'] })
}

test('opt-in global setup waits for this child to sample before yielding to the scenario, then reaps it', async t => {
  const { root, directory, env } = await stagedFixture(t)
  assert.equal(browserEvidenceConfig('app03-5', env, root).globalSetup, './ci/app03-sql-setup.mjs')
  assert.equal(browserEvidenceConfig('app03-5', { ...env, FMCG_APP03_READ_DIAGNOSTICS: '0' }, root).globalSetup, undefined)
  let child, resolved = false
  const available = Promise.withResolvers()
  const setup = startSqlObserver(directory, 'app03-5', id, () => {
    child = fakeObserver(directory, `
      write({event:'available'}); console.log('available');
      process.stdin.on('data', () => write({event:'sample', rows:[]}));
      process.stdin.on('end', () => process.exit(0));`)
    t.after(() => child.kill('SIGKILL'))
    once(child.stdout, 'data').then(available.resolve, available.reject)
    return child
  }).then(cleanup => { resolved = true; return cleanup })
  await available.promise
  await delay(150)
  assert.equal(resolved, false)
  assert.ok((await receipt(directory)).spawnUtc)
  child.stdin.write('sample')
  const cleanup = await setup
  const ready = await receipt(directory)
  assert.equal(ready.status, 'ready')
  assert.equal(ready.firstObservation, 'sample')
  assert.ok(ready.elapsedMs >= 100)
  assert.ok(Date.parse(ready.spawnUtc) <= Date.parse(ready.decisionUtc))
  assert.equal(await cleanup(), undefined)
  assert.equal(child.exitCode, 0)
  assert.ok((await receipt(directory)).exitUtc)
})

test('an unavailable first observation is distinguished from a successful observation', async t => {
  const { directory } = await fixture(t)
  const cleanup = await startSqlObserver(directory, 'app03-5', id, () =>
    fakeObserver(directory, "write({event:'available'}); write({event:'sampleUnavailable'}); process.stdin.resume()"))
  assert.equal((await receipt(directory)).firstObservation, 'sampleUnavailable')
  await cleanup()
})

test('an unavailable observer is stopped before proceeding and cannot override a test result', async t => {
  const { directory } = await fixture(t)
  let child
  const cleanup = await startSqlObserver(directory, 'app03-5', id, () => {
    child = fakeObserver(directory, "write({event:'unavailable'}); process.stdin.resume()")
    return child
  })
  assert.equal(cleanup, undefined)
  assert.equal((await receipt(directory)).status, 'unavailable')
  assert.equal(child.exitCode, 0)
})

for (const status of ['timeout', 'unavailable']) {
  test(`startup cleanup shares the deadline after ${status}, even when the child ignores EOF`, async t => {
    const { directory } = await fixture(t)
    let child
    const budgetMs = status === 'timeout' ? 350 : 1000
    const start = performance.now()
    const cleanup = await startSqlObserver(directory, 'app03-5', id, () => {
      const event = status === 'unavailable' ? "write({event:'unavailable'});" : ''
      child = fakeObserver(directory, event + "process.stdin.resume(); setInterval(() => {}, 1000)")
      return child
    }, budgetMs)
    const observedMs = performance.now() - start
    assert.equal(cleanup, undefined)
    const result = await receipt(directory)
    assert.equal(result.status, status)
    assert.ok(child.signalCode || child.exitCode !== null)
    assert.ok(Date.parse(result.setupEndedUtc) >= Date.parse(result.exitUtc))
    assert.ok(result.setupElapsedMs >= result.elapsedMs)
    assert.ok(result.setupElapsedMs >= budgetMs - 30)
    // Permit Windows process-reaping / runner scheduling jitter, but no second 3.5s grace.
    assert.ok(observedMs < budgetMs + 1500, `Setup took ${observedMs} ms for a ${budgetMs} ms budget`)
    assert.ok(Math.abs(result.setupElapsedMs - observedMs) < 1000)
  })
}

test('existing observations or another PID cannot manufacture readiness', async t => {
  const { directory } = await fixture(t)
  const file = path.join(directory, 'sql-reads.ndjson')
  await writeFile(file, '{"event":"available"}\n{"event":"sample"}\n')
  assert.equal(await startSqlObserver(directory, 'app03-5', id, () => assert.fail('Spawned against existing output')), undefined)
  assert.equal((await receipt(directory)).status, 'existingOutput')
  const another = await fixture(t)
  const cleanup = await startSqlObserver(another.directory, 'app03-5', id, () => fakeObserver(another.directory, `
    fs.writeFileSync(file, JSON.stringify({event:'start',profile:'app03-5',runId:process.argv[2],pid:0})+'\\n');
    write({event:'available'}); write({event:'sample'}); process.stdin.resume();`))
  assert.equal(cleanup, undefined)
  assert.equal((await receipt(another.directory)).status, 'unexpectedOutput')
})

test('early child exit records its code and does not wait for the startup deadline', async t => {
  const { directory } = await fixture(t)
  const cleanup = await startSqlObserver(directory, 'app03-5', id,
    () => fakeObserver(directory, 'process.exit(19)'))
  const startup = await receipt(directory)
  assert.equal(cleanup, undefined)
  assert.equal(startup.status, 'exited')
  assert.equal(startup.exitCode, 19)
  assert.ok(startup.exitUtc)
})

test('spawn failure is recorded without blocking the scenario', async t => {
  const { directory } = await fixture(t)
  const cleanup = await startSqlObserver(directory, 'app03-5', id, () =>
    spawn(path.join(tmpdir(), 'nonexistent-app03-observer-executable'), [], { stdio: ['pipe', 'ignore', 'ignore'] }))
  assert.equal(cleanup, undefined)
  assert.equal((await receipt(directory)).status, 'spawnError')
})

test('disabled diagnostics do not open a child or modify reports', async t => {
  const { directory } = await fixture(t)
  t.mock.property(process, 'env', { ...process.env, FMCG_APP03_READ_DIAGNOSTICS: '0' })
  const junit = path.join(directory, 'junit.xml')
  await writeFile(junit, 'unmodified')
  const reporter = new Reporter({ profile: 'app03-5' })
  reporter.onBegin()
  assert.equal(await globalSetup({}), undefined)
  await reporter.onExit()
  assert.equal(await readFile(junit, 'utf8'), 'unmodified')
})
