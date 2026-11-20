import { createServer } from 'node:http'
import { readFile } from 'node:fs/promises'
import { extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { AddressInfo } from 'node:net'
import { test as base } from '@playwright/test'

export const dist = fileURLToPath(new URL('../../dist/', import.meta.url))
export const demoPath = '/ui/foundations'

const policy = [
  "default-src 'none'", "script-src 'self'", "style-src 'self'", "font-src 'self' data:",
  "connect-src 'self'", "img-src 'self'", "worker-src 'self'", "manifest-src 'self'",
  "base-uri 'none'", "frame-ancestors 'none'"
].join('; ')
const mime: Record<string, string> = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.woff2': 'font/woff2',
  '.webmanifest': 'application/manifest+json', '.json': 'application/json'
}

export async function serveBuild(activeRoot = () => dist) {
  const server = createServer(async (request, response) => {
    response.setHeader('Cache-Control', 'no-store')
    response.setHeader('Content-Security-Policy', policy)
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      response.writeHead(405, { Allow: 'GET, HEAD' }).end()
      return
    }
    let path: string
    try {
      path = decodeURIComponent((request.url ?? '/').split('?')[0])
    } catch {
      response.writeHead(400).end()
      return
    }
    if (!path.startsWith('/') || /[\\:]/.test(path) || path.includes('\0') || path.split('/').includes('..')) {
      response.writeHead(403).end()
      return
    }
    if (path === '/health/live') {
      response.writeHead(200, { 'Content-Type': 'application/json' })
      response.end(JSON.stringify({ status: 'live', scope: 'APP-00A', businessWrites: false }))
      return
    }
    if (path === '/favicon.ico') {
      response.writeHead(204).end()
      return
    }
    const root = resolve(activeRoot())
    const file = resolve(root, '.' + (['/', demoPath].includes(path) ? '/index.html' : path))
    if (!file.startsWith(root + sep)) {
      response.writeHead(403).end()
      return
    }
    try {
      const bytes = await readFile(file)
      response.writeHead(200, { 'Content-Type': mime[extname(file)] ?? 'application/octet-stream' })
      response.end(bytes)
    } catch {
      response.writeHead(404).end()
    }
  })
  await new Promise<void>((done, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', done)
  })
  return {
    origin: 'http://127.0.0.1:' + (server.address() as AddressInfo).port,
    close: () => new Promise<void>((done, reject) => {
      server.close(error => error ? reject(error) : done())
      server.closeAllConnections()
    })
  }
}

export const test = base.extend<{ origin: string }>({
  origin: async ({}, use) => {
    const server = await serveBuild()
    try {
      await use(server.origin)
    } finally {
      await server.close()
    }
  }
})
