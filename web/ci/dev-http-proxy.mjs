// Fixed synthetic route labels and canonical correlation IDs only.
// Never log request URLs, credentials, payloads, socket addresses or exception messages.

const header = 'x-fmcg-d2-peer-witness'
const posts = new Map([
  ['/api/synthetic/app02d2/sales','D2_POST'],
  ['/api/synthetic/app02c1/ship-to-selections','C1_SHIP_TO'],
  ['/api/synthetic/app02c2/drafts','C2_DRAFT'],
  ['/api/synthetic/app02c2/confirm','C2_CONFIRM'],
  ['/api/synthetic/app02c2/reserve','C2_RESERVE'],
  ['/api/synthetic/app02d1/quota','D1_QUOTA'],
  ['/api/synthetic/app02d1/credit','D1_CREDIT'],
  ['/api/synthetic/app02d1/load','D1_LOAD'],
  ['/api/synthetic/app02c3/load','C3_LOAD']
])
export function resumeIdentity(req) {
  const path = req.url?.split('?')[0]
  const route = req.method === 'POST' ? posts.get(path)
    : req.method === 'GET' && path === '/api/synthetic/app02c1/session' ? 'C1_SESSION'
    : req.method === 'GET' && /^\/api\/synthetic\/app02d2\/sales\/[^/]+\/[^/]+$/.test(path ?? '') ? 'D2_LOOKUP' : null
  const id = req.headers[header]
  return route && typeof id === 'string' && id.length === 32 && /^[0-9a-f]{32}$/.test(id) ? { route, id } : null
}

const safeProxyError = error => ['ECONNREFUSED', 'ETIMEDOUT', 'ECONNRESET', 'EPIPE', 'ENOTFOUND', 'EAI_AGAIN', 'EHOSTUNREACH'].includes(error?.code) ? error.code : 'other'

export function isResumeProxyEnabled(env = process.env, platform = process.platform) {
  return platform === 'win32' && env.FMCG_DEV_HTTP_PROXY === '1'
    && env.GITHUB_ACTIONS === 'true' && env.RUNNER_OS === 'Windows'
}

export function resumeProxyPort(args) {
  if (args.length !== 1 || typeof args[0] !== 'string') throw new Error('Expected one loopback port')
  const port = Number(args[0])
  if (!Number.isInteger(port) || String(port) !== args[0] || port < 1024 || port > 65535 || port === 5116)
    throw new Error('Invalid loopback port')
  return port
}

export function resumeProxyWitness(enabled, write = console.log) {
  const marker = (req, phase, status, errorCode) => {
    if (!enabled) return
    const identity = resumeIdentity(req)
    if (!identity) return
    try {
      write('DEV_HTTP_PROXY ' + JSON.stringify({
        schema: 'DEV_HTTP_PROXY_V1', ...identity, phase, vitePid: process.pid,
        utc: new Date().toISOString(), ...(status === undefined ? {} : { status }), ...(errorCode === undefined ? {} : { errorCode })
      }))
    } catch { /* Diagnostic logging cannot change the proxy outcome. */ }
  }
  const ingress = {
    name: 'dev-http-proxy-witness',
    configureServer(server) {
      if (!enabled) return
      server.middlewares.use((req, res, next) => {
        if (resumeIdentity(req)) {
          marker(req, 'ingress')
          res.once('finish', () => marker(req, 'client-finish', res.statusCode))
          res.once('close', () => {
            if (!res.writableFinished) marker(req, 'client-close')
          })
        }
        next()
      })
    }
  }
  const configure = proxy => {
    if (!enabled) return
    proxy.on('proxyReq', (_outgoing, req) => marker(req, 'upstream-request'))
    proxy.on('proxyRes', (upstream, req) => {
      marker(req, 'upstream-headers', upstream.statusCode)
      upstream.once('end', () => marker(req, 'upstream-end'))
      upstream.once('aborted', () => marker(req, 'upstream-aborted'))
      upstream.once('error', error => marker(req, 'upstream-error', undefined, safeProxyError(error)))
    })
    proxy.on('error', (_error, req) => {
      if (req) marker(req, 'proxy-error', undefined, safeProxyError(_error))
    })
  }
  return { ingress, configure }
}
