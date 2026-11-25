import assert from 'node:assert/strict'
import { once } from 'node:events'
import { createServer as httpServer } from 'node:http'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { createServer as viteServer } from 'vite'

const web = fileURLToPath(new URL('../', import.meta.url))

test('actual Vite proxy sends the same Host, target and body with diagnostics on and off', async t => {
  const seen = []
  const origin = httpServer(async (req, res) => {
    const chunks = []
    for await (const chunk of req) chunks.push(chunk)
    seen.push({ method: req.method, url: req.url, host: req.headers.host, body: Buffer.concat(chunks).toString() })
    res.writeHead(207, { 'x-origin-result': 'original' }).end('original response')
  })
  origin.listen(5116, 'localhost')
  await once(origin, 'listening')
  t.after(() => new Promise(resolve => { origin.close(resolve); origin.closeAllConnections() }))
  const prev = process.env.FMCG_APP03_READ_DIAGNOSTICS
  t.after(() => {
    if (prev === undefined) delete process.env.FMCG_APP03_READ_DIAGNOSTICS
    else process.env.FMCG_APP03_READ_DIAGNOSTICS = prev
  })
  for (const flag of ['0', '1']) {
    process.env.FMCG_APP03_READ_DIAGNOSTICS = flag
    const server = await viteServer({ root: web, configFile: fileURLToPath(new URL('../vite.config.ts', import.meta.url)),
      logLevel: 'silent', server: { host: '127.0.0.1', port: 0, hmr: false, watch: null } })
    try {
      assert.equal(server.config.plugins.some(p => p.name === 'app03-read-http-diagnostics'), flag === '1')
      await server.listen()
      const base = 'http://127.0.0.1:' + server.httpServer.address().port
      for (const [url, init] of [
        ['/api/synthetic/app02d2/trips?q=1', {}], ['/api/other?q=2', { method: 'POST', body: 'fixture body' }],
      ]) {
        const res = await fetch(base + url, init)
        assert.equal(res.status, 207)
        assert.equal(res.headers.get('x-origin-result'), 'original')
        assert.equal(await res.text(), 'original response')
      }
    } finally { await server.close() }
  }
  const expected = [
    { method: 'GET', url: '/api/synthetic/app02d2/trips?q=1', host: 'localhost:5116', body: '' },
    { method: 'POST', url: '/api/other?q=2', host: 'localhost:5116', body: 'fixture body' },
  ]
  assert.deepEqual(seen, [...expected, ...expected])
})
