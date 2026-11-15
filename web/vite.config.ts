import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'prompt',
      manifest: {
        name: 'FMCG DMS — APP-00A (Synthetic)',
        short_name: 'FMCG DMS',
        description: 'Application foundation only. No offline business writes.',
        theme_color: '#0f766e', background_color: '#ffffff',
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
  server: { proxy: { '/health': 'http://localhost:5116', '/api': 'http://localhost:5116' } }
})
