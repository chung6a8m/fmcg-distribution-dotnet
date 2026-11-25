const routes = new Map([
  ['/api/synthetic/app02c2/session', 'c2Session'],
  ['/api/synthetic/app02c3/trips', 'c3Trips'],
  ['/api/synthetic/app02c4/trips', 'c4Trips'],
  ['/api/synthetic/app02d1/catalog', 'd1Catalog'],
  ['/api/synthetic/app02d2/trips', 'd2Trips'],
])

export function createApp03ReadHttpDiagnostics(enabled = process.env.FMCG_APP03_READ_DIAGNOSTICS, write = console.log) {
  if (enabled !== '1') return

  const requests = new WeakMap()
  let sequence = 0

  function observe(state, event, status) {
    if (!state) return
    const row = {
      pid: process.pid,
      id: state.id,
      route: state.route,
      event,
      utc: new Date().toISOString(),
      elapsedMs: Math.round((performance.now() - state.started) * 1000) / 1000,
    }
    if (Number.isInteger(status)) row.status = status
    const line = `APP03-READ-HTTP ${JSON.stringify(row)}`
    try {
      write(line)
    } catch {
      // A diagnostic sink failure must leave the original proxy flow intact.
    }
  }

  function middleware(request, response, next) {
    const route = request.method === 'GET' ? routes.get(request.url?.split('?', 1)[0]) : undefined
    if (route) {
      const state = { id: ++sequence, route, started: performance.now() }
      requests.set(request, state)
      observe(state, 'ingress')
      response.once('finish', () => observe(state, 'clientFinish', response.statusCode))
      response.once('close', () => {
        if (!response.writableFinished) observe(state, 'clientClose')
      })
    }
    next()
  }

  return {
    plugin: {
      name: 'app03-read-http-diagnostics',
      configureServer(server) {
        server.middlewares.use(middleware)
      },
    },
    configure(proxy) {
      proxy.on('proxyReq', (_upstream, request) => observe(requests.get(request), 'forward'))
      proxy.on('proxyRes', (upstream, request) => {
        const state = requests.get(request)
        if (!state) return
        observe(state, 'upstreamResponse', upstream.statusCode)
        upstream.once('end', () => observe(state, 'upstreamEnd', upstream.statusCode))
      })
      proxy.on('error', (_error, request) => observe(requests.get(request), 'proxyError'))
    },
  }
}
