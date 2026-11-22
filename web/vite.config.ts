import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'

function apiTarget(mode: string) {
  const port = loadEnv(mode, '.', 'APP02C1_API_').APP02C1_API_PORT
  if (port && (!/^\d{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535)) {
    throw new Error('Invalid APP02C1_API_PORT')
  }
  return port ? `http://127.0.0.1:${port}` : 'http://localhost:5116'
}

export default defineConfig(({ mode }) => ({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'prompt',
      manifest: {
        name: 'FMCG DMS — APP-00A (Synthetic)',
        short_name: 'FMCG DMS',
        description: 'Application foundation only. No offline business writes.',
        theme_color: '#F6F8FB', background_color: '#F6F8FB',
        display: 'standalone', start_url: '/',
        icons: [{ src: '/icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' }]
      },
      workbox: {
        navigateFallbackDenylist: [/^\/api\//],
        runtimeCaching: [],
        globPatterns: ['**/*.{js,css,html,svg,ico,png}']
      }
    })
  ],
  server: { proxy: { '/health': apiTarget(mode), '/api': apiTarget(mode) } }
}))
