// Only the two observed read routes and a canonical correlation ID are published.
// Never log request URLs, credentials, payloads, socket addresses or exception messages.

const header = 'x-fmcg-app03-catalog-witness'
const routes = new Set(['/api/synthetic/app02c1/session', '/api/synthetic/app02c2/catalog'])

export function resumeIdentity(req) {
  const route = req.url?.split('?')[0]
  const id = req.headers[header]
  return req.method === 'GET' && route && routes.has(route)
    && typeof id === 'string' && id.length === 32 && /^[0-9a-f]{32}$/.test(id) ? { route, id } : null
}

const safeProxyError = error => ['ECONNREFUSED', 'ETIMEDOUT', 'ECONNRESET', 'EPIPE', 'ENOTFOUND', 'EAI_AGAIN', 'EHOSTUNREACH'].includes(error?.code) ? error.code : 'other'

export function isResumeProxyEnabled() {
  return process.env.FMCG_APP03_RESUME_PROXY === '1'
    && process.env.GITHUB_ACTIONS === 'true' && process.env.RUNNER_OS === 'Windows'
}

export function resumeProxyWitness(enabled, write = console.log) {
  const marker = (req, phase, status, errorCode) => {
    if (!enabled) return
    const identity = resumeIdentity(req)
    if (!identity) return
    try {
      write('APP03_RESUME_PROXY ' + JSON.stringify({
        schema: 'APP03_RESUME_PROXY_V1', ...identity, phase, vitePid: process.pid,
        utc: new Date().toISOString(), ...(status === undefined ? {} : { status }), ...(errorCode === undefined ? {} : { errorCode })
      }))
    } catch { /* Diagnostic logging cannot change the proxy outcome. */ }
  }
  const ingress = {
    name: 'app03-resume-proxy-witness',
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
