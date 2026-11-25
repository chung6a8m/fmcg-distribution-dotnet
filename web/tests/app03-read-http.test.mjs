import assert from 'node:assert/strict'
import { EventEmitter, once } from 'node:events'
import { createServer, get, request } from 'node:http'
import test from 'node:test'
import { createApp03ReadHttpDiagnostics } from '../ci/app03-read-http.mjs'

const d2Trips = '/api/synthetic/app02d2/trips'
const authorization = 'Bearer fixture-authorization'
const privateQuery = 'fixture-query'
const privateBody = 'fixture-response-body'
const privateError = 'fixture-upstream-error'

async function listen(t, handler) {
  const server = createServer(handler)
  const ready = once(server, 'listening')
  server.listen(0, '127.0.0.1')
  await ready
  t.after(() => new Promise(resolve => {
    server.close(resolve)
    server.closeAllConnections()
  }))
  return server.address().port
}

async function fixture(t, originHandler, { enabled = '1', onEvent = () => {} } = {}) {
  const events = []
  const rawLines = []
  const diagnostics = createApp03ReadHttpDiagnostics(enabled, line => {
    rawLines.push(line)
    const prefix = 'APP03-READ-HTTP '
    assert.ok(line.startsWith(prefix))
    const event = JSON.parse(line.slice(prefix.length))
    events.push(event)
    onEvent(event)
  })
  const origin = await listen(t, originHandler)
  const proxy = new EventEmitter()
  diagnostics?.configure(proxy)
  proxy.on('error', (_error, _request, response) => {
    if (!response.destroyed) response.writeHead(502).end('origin unavailable')
  })
  let middleware = (_incoming, _outgoing, next) => next()
  diagnostics?.plugin.configureServer({ middlewares: { use(handler) { middleware = handler } } })

  const port = await listen(t, (incoming, outgoing) => {
    const forward = () => {
      const upstream = request({
        host: '127.0.0.1', port: origin, path: incoming.url,
        method: incoming.method, headers: incoming.headers,
      }, response => {
        proxy.emit('proxyRes', response, incoming, outgoing)
        outgoing.writeHead(response.statusCode, response.headers)
        response.pipe(outgoing)
      })
      upstream.once('socket', () => proxy.emit('proxyReq', upstream, incoming, outgoing))
      upstream.once('error', error => proxy.emit('error', new Error(privateError, { cause: error }), incoming, outgoing))
      outgoing.once('close', () => { if (!outgoing.writableFinished) upstream.destroy() })
      incoming.pipe(upstream)
    }
    middleware(incoming, outgoing, forward)
  })
  return { base: `http://127.0.0.1:${port}`, events, rawLines }
}

function assertRedacted(lines) {
  for (const privateValue of [authorization, privateQuery, privateBody, privateError, '/api/synthetic/']) {
    assert.ok(!lines.join('\n').includes(privateValue))
  }
}

test('allowlisted reads preserve the original request and response and log a single correlated HTTP timeline', async t => {
  const received = []
  const { base, events, rawLines } = await fixture(t, (incoming, outgoing) => {
    received.push({ method: incoming.method, path: incoming.url, auth: incoming.headers.authorization })
    outgoing.writeHead(207, { 'x-fixture-origin': 'upstream' }).end(privateBody)
  })
  const reads = [
    ['/api/synthetic/app02c2/session', 'c2Session'],
    ['/api/synthetic/app02c3/trips', 'c3Trips'],
    ['/api/synthetic/app02c4/trips', 'c4Trips'],
    [d2Trips, 'd2Trips'],
    ['/api/synthetic/app02d1/catalog', 'd1Catalog'],
  ]
  for (const [pathname] of reads) {
    const url = `${pathname}?q=${privateQuery}`
    const response = await fetch(base + url, { headers: { authorization } })
    assert.equal(response.status, 207)
    assert.equal(response.headers.get('x-fixture-origin'), 'upstream')
    assert.equal(await response.text(), privateBody)
    assert.deepEqual(received.at(-1), { method: 'GET', path: url, auth: authorization })
  }
  for (const [index, [, route]] of reads.entries()) {
    const timeline = events.filter(event => event.route === route)
    assert.deepEqual(timeline.map(event => event.event), ['ingress', 'forward', 'upstreamResponse', 'upstreamEnd', 'clientFinish'])
    assert.ok(timeline.every(event => event.pid === process.pid && event.id === index + 1 && Number.isFinite(Date.parse(event.utc))))
    assert.deepEqual(timeline.map(event => event.status), [undefined, undefined, 207, 207, 207])
    assert.ok(timeline.every((event, n) => event.elapsedMs >= (timeline[n - 1]?.elapsedMs ?? 0)))
  }
  assert.equal(events.length, reads.length * 5)
  assertRedacted(rawLines)
})

test('flag disabled, non-GET, and similar paths produce no observations', async t => {
  const origin = (_incoming, outgoing) => outgoing.writeHead(204).end()
  const disabled = await fixture(t, origin, { enabled: '0' })
  const enabled = await fixture(t, origin)
  assert.equal((await fetch(disabled.base + d2Trips)).status, 204)
  assert.equal((await fetch(enabled.base + d2Trips, { method: 'POST' })).status, 204)
  assert.equal((await fetch(enabled.base + d2Trips + '/unexpected')).status, 204)
  assert.deepEqual(disabled.events, [])
  assert.deepEqual(enabled.events, [])
})

test('a downstream disconnect is recorded during an in-flight read without logging credentials', { timeout: 5000 }, async t => {
  const received = Promise.withResolvers()
  const closed = Promise.withResolvers()
  const { base, events, rawLines } = await fixture(t, request => received.resolve(request.headers.authorization), {
    onEvent: event => { if (event.event === 'clientClose') closed.resolve() },
  })
  const client = get(`${base}${d2Trips}?q=${privateQuery}`, { headers: { authorization } })
  client.on('error', () => {})
  assert.equal(await received.promise, authorization)
  client.destroy()
  await closed.promise
  assert.deepEqual(events.slice(0, 3).map(event => event.event), ['ingress', 'forward', 'clientClose'])
  assert.ok(!events.some(event => event.event === 'clientFinish' || event.event === 'upstreamResponse'))
  assertRedacted(rawLines)
})

test('a proxy error and a diagnostic writer failure preserve the ordinary bad-gateway response', async t => {
  const { base, events, rawLines } = await fixture(t, incoming => incoming.socket.destroy(), {
    onEvent: () => { throw new Error('unavailable diagnostic sink') },
  })
  const response = await fetch(`${base}${d2Trips}?q=${privateQuery}`, { headers: { authorization } })
  assert.equal(response.status, 502)
  assert.equal(await response.text(), 'origin unavailable')
  assert.deepEqual(events.map(event => event.event), ['ingress', 'forward', 'proxyError', 'clientFinish'])
  assert.equal(events.at(-1).status, 502)
  assertRedacted(rawLines)
})
