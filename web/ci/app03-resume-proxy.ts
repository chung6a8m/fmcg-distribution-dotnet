// Only the two observed read routes and a canonical correlation ID are published.
// Never log request URLs, credentials, payloads, socket addresses or exception messages.
import type { IncomingMessage } from 'node:http'
import type { Plugin, ProxyOptions } from 'vite'

const header = 'x-fmcg-app03-catalog-witness'
const routes = new Set(['/api/synthetic/app02c1/session', '/api/synthetic/app02c2/catalog'])

export function resumeIdentity(req: Pick<IncomingMessage, 'method' | 'url' | 'headers'>) {
  const route = req.url?.split('?')[0]
  const id = req.headers[header]
  return req.method === 'GET' && route && routes.has(route)
    && typeof id === 'string' && /^[0-9a-f]{32}$/.test(id) ? { route, id } : null
}

export function resumeProxyWitness(enabled: boolean, write: (line: string) => void = console.log) {
  const marker = (req: IncomingMessage, phase: string, status?: number) => {
    if (!enabled) return
    const identity = resumeIdentity(req)
    if (!identity) return
    try {
      write('APP03_RESUME_PROXY ' + JSON.stringify({
        schema: 'APP03_RESUME_PROXY_V1', ...identity, phase, vitePid: process.pid,
        utc: new Date().toISOString(), ...(status === undefined ? {} : { status })
      }))
    } catch { /* Diagnostic logging cannot change the proxy outcome. */ }
  }
  const ingress: Plugin = {
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
  const configure: NonNullable<ProxyOptions['configure']> = proxy => {
    if (!enabled) return
    proxy.on('proxyReq', (_outgoing, req) => marker(req, 'upstream-request'))
    proxy.on('proxyRes', (upstream, req) => {
      marker(req, 'upstream-headers', upstream.statusCode)
      upstream.once('end', () => marker(req, 'upstream-end'))
      upstream.once('aborted', () => marker(req, 'upstream-aborted'))
      upstream.once('error', () => marker(req, 'upstream-error'))
    })
    proxy.on('error', (_error, req) => {
      if (req) marker(req, 'proxy-error')
    })
  }
  return { ingress, configure }
}
