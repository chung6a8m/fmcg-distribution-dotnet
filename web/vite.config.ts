import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'
import { resumeProxyWitness } from './ci/app03-resume-proxy'

const witnessEnabled = process.env.FMCG_APP03_RESUME_PROXY === '1'
  && process.env.GITHUB_ACTIONS === 'true' && process.env.RUNNER_OS === 'Windows'
const witness = resumeProxyWitness(witnessEnabled)

export default defineConfig({
  plugins: [
    witness.ingress,
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
  server: { proxy: { '/health': 'http://localhost:5116', '/api': witnessEnabled ? { target: 'http://localhost:5116', configure: witness.configure } : 'http://localhost:5116' } }
})
