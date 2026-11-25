import assert from 'node:assert/strict'
import { EventEmitter, once } from 'node:events'
import { createServer } from 'node:http'
import test from 'node:test'
import { observeApp03ReadHttp } from '../ci/app03-read-http.mjs'

const auth = 'Bearer fixture-authorization'
const query = 'fixture-query'
const body = 'fixture-response-body'
const d2Trips = '/api/synthetic/app02d2/trips'
const browserEvents = ['request', 'response', 'requestfinished', 'requestfailed']
const prefix = 'APP03-READ-HTTP '

class Receipt {
  lines = []

  write = line => this.lines.push(line)

  get rows() {
    return this.lines.map(line => {
      assert.ok(line.startsWith(prefix))
      assert.ok(![auth, query, body, '/api/synthetic/'].some(value => line.includes(value)))
      const row = JSON.parse(line.slice(prefix.length))
      const fields = ['elapsedMs', 'event', 'id', 'pid', 'route', 'utc']
      if (Object.hasOwn(row, 'status')) {
        assert.ok(Number.isInteger(row.status))
        fields.push('status')
      }
      assert.deepEqual(Object.keys(row).sort(), fields.sort())
      return row
    })
  }
}

async function listen(t, handleApi) {
  const server = createServer((request, response) => {
    if (request.url === '/') {
      response.writeHead(200, { 'content-type': 'text/html' }).end('<title>Loopback fixture</title>')
      return
    }
    handleApi(request, response)
  })
  const listening = once(server, 'listening')
  server.listen(0, '127.0.0.1')
  await listening
  t.after(() => new Promise(resolve => {
    server.close(resolve)
    server.closeAllConnections()
  }))
  return `http://127.0.0.1:${server.address().port}`
}

async function fixture(t, handleApi) {
  const { chromium } = await import('@playwright/test')
  const base = await listen(t, handleApi)
  const browser = await chromium.launch()
  t.after(() => browser.close())
  return { base, context: await browser.newContext() }
}

test('browser observation preserves all allowlisted GET requests and responses across pages', { timeout: 20000 }, async t => {
  const incoming = []
  const { base, context } = await fixture(t, (request, response) => {
    incoming.push({ method: request.method, url: request.url, authorization: request.headers.authorization })
    response.writeHead(207, { 'x-fixture-origin': 'loopback' }).end(body)
  })
  const receipt = new Receipt()
  t.after(observeApp03ReadHttp(context, base, receipt.write))
  const pages = await Promise.all([context.newPage(), context.newPage()])
  await Promise.all(pages.map(page => page.goto(base)))
  const reads = [
    ['/api/synthetic/app02c2/session', 'c2Session'],
    ['/api/synthetic/app02c3/trips', 'c3Trips'],
    ['/api/synthetic/app02c4/trips', 'c4Trips'],
    ['/api/synthetic/app02d1/catalog', 'd1Catalog'],
    [d2Trips, 'd2Trips'],
  ]
  for (const [index, [path]] of reads.entries()) {
    const url = `${path}?value=${query}`
    const finished = context.waitForEvent('requestfinished', request => request.url() === base + url)
    const result = await pages[index % pages.length].evaluate(async ({ url, auth }) => {
      const response = await fetch(url, { headers: { authorization: auth } })
      return { status: response.status, origin: response.headers.get('x-fixture-origin'), body: await response.text() }
    }, { url, auth })
    await finished
    assert.deepEqual(result, { status: 207, origin: 'loopback', body })
    assert.deepEqual(incoming.find(entry => entry.url === url), { method: 'GET', url, authorization: auth })
  }
  for (const [path, method] of [[d2Trips, 'POST'], [d2Trips + '/unknown', 'GET']]) {
    assert.equal(await pages[0].evaluate(async ({ path, method }) => (await fetch(path, { method })).status, { path, method }), 207)
  }
  await pages[1].goto(await listen(t, (_request, response) => response.writeHead(208).end(body)))
  assert.equal(await pages[1].evaluate(async path => (await fetch(path)).status, d2Trips), 208)
  assert.equal(receipt.rows.length, reads.length * 3)
  const ids = new Set()
  for (const [, route] of reads) {
    const events = receipt.rows.filter(row => row.route === route)
    assert.deepEqual(events.map(row => row.event), ['browserRequest', 'browserResponse', 'browserFinished'])
    assert.deepEqual(events.map(row => row.status), [undefined, 207, 207])
    assert.ok(events.every((row, index) => row.id === events[0].id && row.pid === process.pid &&
      Number.isFinite(Date.parse(row.utc)) && row.elapsedMs >= (events[index - 1]?.elapsedMs ?? 0)))
    ids.add(events[0].id)
  }
  assert.equal(ids.size, reads.length)
})

test('browser cancellation and pending cleanup exclude credentials and detach all listeners', { timeout: 20000 }, async t => {
  const pending = new EventEmitter()
  const { base, context } = await fixture(t, (request, response) => {
    if (request.url.startsWith(d2Trips + '?')) pending.emit('arrived', request.headers.authorization)
    else response.writeHead(404).end()
  })
  const page = await context.newPage()
  await page.goto(base)
  const priorListeners = browserEvents.map(event => context.listenerCount(event))
  const receipt = new Receipt()
  const stop = observeApp03ReadHttp(context, base, receipt.write)
  t.after(stop)
  async function startFetch() {
    const arrived = once(pending, 'arrived')
    await page.evaluate(({ url, auth }) => {
      window.controller = new AbortController()
      window.outcome = fetch(url, { headers: { authorization: auth }, signal: window.controller.signal })
        .then(() => 'unexpected-response', error => error.name)
    }, { url: `${d2Trips}?value=${query}`, auth })
    assert.deepEqual(await arrived, [auth])
  }
  async function abortFetch() {
    const failed = context.waitForEvent('requestfailed', request => request.url().startsWith(base + d2Trips))
    assert.equal(await page.evaluate(() => {
      window.controller.abort()
      return window.outcome
    }), 'AbortError')
    await failed
  }
  await startFetch()
  await abortFetch()
  await startFetch()
  stop()
  stop()
  const stopped = receipt.rows.length
  await abortFetch()

  assert.deepEqual(receipt.rows.map(row => row.event), ['browserRequest', 'browserFailed', 'browserRequest', 'browserPending'])
  assert.equal(receipt.rows[0].id, receipt.rows[1].id)
  assert.equal(receipt.rows[2].id, receipt.rows[3].id)
  assert.notEqual(receipt.rows[0].id, receipt.rows[2].id)
  assert.equal(receipt.rows.length, stopped)
  assert.deepEqual(browserEvents.map(event => context.listenerCount(event)), priorListeners)
})

test('the disabled observer fixture exports the original Playwright test', async () => {
  const { test: original } = await import('@playwright/test')
  const previous = process.env.FMCG_APP03_READ_DIAGNOSTICS
  try {
    delete process.env.FMCG_APP03_READ_DIAGNOSTICS
    assert.equal((await import('../ci/app03-read-test.mjs?disabled')).test, original)
  } finally {
    if (previous === undefined) delete process.env.FMCG_APP03_READ_DIAGNOSTICS
    else process.env.FMCG_APP03_READ_DIAGNOSTICS = previous
  }
})

test('writer failure cannot interrupt request events or release of observers', () => {
  const context = new EventEmitter()
  const base = 'http://127.0.0.1:6000'
  const request = { method: () => 'GET', url: () => base + d2Trips }
  const observed = []
  const stop = observeApp03ReadHttp(context, base, () => { throw new Error('diagnostic writer failed') })
  context.on('request', () => observed.push('request received'))
  context.on('requestfailed', () => observed.push('failure received'))
  assert.doesNotThrow(() => {
    context.emit('request', request)
    context.emit('requestfailed', request)
    context.emit('request', request)
    stop()
  })
  assert.deepEqual(observed, ['request received', 'failure received', 'request received'])
  assert.deepEqual(browserEvents.map(event => context.listenerCount(event)), [1, 0, 0, 1])
})
