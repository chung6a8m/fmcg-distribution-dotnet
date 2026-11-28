import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createServer, request } from 'node:http'
import { createServer as createTcpServer } from 'node:net'
import { once } from 'node:events'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const root = fileURLToPath(new URL('../', import.meta.url))
const launcher = fileURLToPath(new URL('../ci/run-app03-resume-vite.mjs', import.meta.url))
const prefix = 'APP03_RESUME_PROXY '
const session = '/api/synthetic/app02c1/session'
const catalog = '/api/synthetic/app02c2/catalog'
const header = 'x-fmcg-app03-catalog-witness'
const ids = ['1', '2', '3', '4'].map(x => x.repeat(32))

test('launcher rejects missing opt-in and arbitrary CLI config before loading Vite', { timeout: 10000 }, async () => {
  const child = spawn(process.execPath, [launcher, '5173'], {
    cwd: root, env: { ...process.env, FMCG_APP03_RESUME_PROXY: '0' }, stdio: 'ignore'
  })
  const [code] = await once(child, 'exit')
  assert.notEqual(code, 0)
  const invalid = spawn(process.execPath, [launcher, '5173', '--config', 'untrusted'], {
    cwd: root, env: { ...process.env, FMCG_APP03_RESUME_PROXY: '1' }, stdio: 'ignore'
  })
  const [invalidCode] = await once(invalid, 'exit')
  assert.notEqual(invalidCode, 0)
})

test('real Vite DEV proxy correlates response, client abort and upstream failure using the fixed target',
  { timeout: 40000 }, async t => {
    // This test is required in the two Windows hosted diagnostic jobs, not a
    // substitute for the APP-03 business scenarios. No simulated platform opt-in.
    assert.equal(process.platform, 'win32')
    assert.equal(process.env.GITHUB_ACTIONS, 'true')
    assert.equal(process.env.RUNNER_OS, 'Windows')
    const upstreamIds = []
    const upstream = createServer((req, res) => {
      upstreamIds.push(req.headers[header])
      if (req.headers[header] === ids[1]) {
        res.writeHead(200, { 'content-type': 'text/plain' })
        res.write('partial')
        // The client aborts after receiving this chunk; keep the response incomplete.
      } else if (req.headers[header] === ids[2]) {
        req.socket.destroy()
      } else {
        res.writeHead(200, { 'content-type': 'text/plain' })
        res.end('ok')
      }
    })
    t.after(async () => {
      upstream.closeAllConnections()
      if (upstream.listening) await new Promise(resolve => upstream.close(resolve))
    })
    await new Promise((resolve, reject) => {
      upstream.once('error', reject)
      upstream.listen(5116, 'localhost', resolve)
    })
    const probe = createTcpServer()
    await new Promise((resolve, reject) => {
      probe.once('error', reject)
      probe.listen(0, '127.0.0.1', resolve)
    })
    const port = probe.address().port
    await new Promise(resolve => probe.close(resolve))
    const child = spawn(process.execPath, [launcher, String(port)], {
      cwd: root,
      env: { ...process.env, FMCG_APP03_RESUME_PROXY: '1' },
      stdio: ['ignore', 'pipe', 'pipe']
    })
    const exited = once(child, 'exit')
    t.after(async () => {
      if (child.exitCode === null && child.signalCode === null) child.kill()
      await exited
    })
    child.stderr.resume()
    const rows = []
    let ready = false, buffer = ''
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', chunk => {
      buffer += chunk
      const lines = buffer.split('\n')
      buffer = lines.pop()
      for (const line of lines) {
        if (line.trim() === 'APP03_RESUME_VITE_READY') ready = true
        if (line.startsWith(prefix)) rows.push(JSON.parse(line.slice(prefix.length)))
      }
    })
    async function until(predicate, description, budget = 8000) {
      const deadline = performance.now() + budget
      while (!predicate() && performance.now() < deadline) {
        assert.equal(child.exitCode, null, 'Vite exited before ' + description)
        await delay(25)
      }
      assert.ok(predicate(), 'Missing ' + description)
    }
    await until(() => ready, 'diagnostic Vite readiness', 20000)
    function send(route, id, abort = false) {
      return new Promise((resolve, reject) => {
        const req = request({ host: '127.0.0.1', port, path: route, method: 'GET',
          headers: { [header]: id, authorization: 'Bearer private-token' } }, res => {
          if (abort) {
            res.once('data', () => { res.destroy(); resolve('aborted') })
            res.once('error', reject)
          } else {
            let body = ''
            res.setEncoding('utf8')
            res.on('data', chunk => { body += chunk })
            res.on('end', () => resolve({ status: res.statusCode, body }))
            res.on('error', reject)
          }
        })
        req.once('error', reject)
        req.setTimeout(5000, () => req.destroy(new Error('controlled proxy test deadline')))
        req.end()
      })
    }
    assert.deepEqual(await send(session + '?private=value', ids[0]), { status: 200, body: 'ok' })
    await until(() => rows.some(r => r.id === ids[0] && r.phase === 'client-finish'), 'successful proxy finish')
    assert.equal(await send(catalog, ids[1], true), 'aborted')
    await until(() => rows.some(r => r.id === ids[1] && r.phase === 'client-close'), 'client-close witness')
    await send(session, ids[2]).catch(() => undefined)
    await until(() => rows.some(r => r.id === ids[2] && r.phase === 'proxy-error'), 'upstream error witness')
    assert.deepEqual(await send('/api/unrelated', ids[3]), { status: 200, body: 'ok' })
    assert.deepEqual(upstreamIds, ids)
    assert.ok(rows.every(r => r.vitePid === child.pid && r.id !== ids[3]))
    for (const phase of ['ingress','upstream-request','upstream-headers','upstream-end','client-finish'])
      assert.ok(rows.some(r => r.id === ids[0] && r.phase === phase), phase)
    assert.ok(rows.some(r => r.id === ids[1] && r.phase === 'upstream-headers'))
    assert.ok(!JSON.stringify(rows).includes('private'))
  })
