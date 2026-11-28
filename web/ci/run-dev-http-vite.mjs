// Synthetic CI DEV entry point only. Keep the canonical config and C59-B02
// untouched; no config path, host, proxy destination, plugin, glob, or mode input.
import { fileURLToPath } from 'node:url'
import { isResumeProxyEnabled, resumeProxyPort, resumeProxyWitness } from './dev-http-proxy.mjs'

if (!isResumeProxyEnabled()) throw new Error('DEV HTTP diagnostic Vite launcher is disabled')
const port = resumeProxyPort(process.argv.slice(2))
const root = fileURLToPath(new URL('../', import.meta.url))
const configFile = fileURLToPath(new URL('../vite.config.ts', import.meta.url))
const witness = resumeProxyWitness(true)
const { createServer } = await import('vite')
// Vite loads the unchanged canonical PWA config. The only additions are a
// serve-only observation plugin and configure hook on the same literal /api target.
const server = await createServer({
  root,
  configFile,
  mode: 'development',
  plugins: [{ ...witness.ingress, apply: 'serve' }],
  server: {
    host: '127.0.0.1',
    port,
    strictPort: true,
    // Match Vite's string shorthand (changeOrigin:true) exactly.
    proxy: { '/api': { target: 'http://localhost:5116', changeOrigin: true, configure: witness.configure } }
  }
})
try {
  const proxy = server.config.server.proxy
  if (server.config.command !== 'serve' || server.config.server.host !== '127.0.0.1'
      || server.config.server.port !== port || server.config.server.strictPort !== true
      || Object.keys(proxy ?? {}).sort().join(',') !== '/api,/health'
      || proxy['/health'] !== 'http://localhost:5116'
      || proxy['/api']?.target !== 'http://localhost:5116'
      || proxy['/api']?.changeOrigin !== true
      || proxy['/api']?.configure !== witness.configure)
    throw new Error('DEV HTTP diagnostic Vite configuration is outside the fixed boundary')
  await server.listen()
  console.log('DEV_HTTP_VITE_READY')
} catch (error) {
  await server.close()
  throw error
}
