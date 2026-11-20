import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'
import tokenSource from '../docs/ui-ux/tokens/tokens.json' with { type: 'json' }

const pageSurface = 'color.surface.page'
const tokens: Record<string, { type: string; value: string | number }> = tokenSource.tokens

function resolveLightColor(name: string, path: string[] = []): string {
  if (path.includes(name)) throw new Error(`Color token alias cycle: ${[...path, name].join(' -> ')}`)
  const token = tokens[name]
  if (token?.type !== 'color' || typeof token.value !== 'string') {
    throw new Error(`Expected a color token for ${name}`)
  }
  const alias = token.value.match(/^\{([^}]+)\}$/)?.[1]
  return alias ? resolveLightColor(alias, [...path, name]) : token.value
}

const lightPageSurface = resolveLightColor(pageSurface)
const darkPageSurface = tokenSource.themes.dark.values[pageSurface]
if (![lightPageSurface, darkPageSurface].every(color => /^#[0-9a-f]{6}$/i.test(color))) {
  throw new Error('Expected Light and Dark page surface tokens to resolve to six-digit hex colors')
}

export default defineConfig({
  plugins: [
    {
      name: 'fmcg-theme-metadata',
      transformIndexHtml: {
        order: 'pre',
        handler: html => html
          .replaceAll('%UI01_LIGHT_PAGE_SURFACE%', lightPageSurface)
          .replaceAll('%UI01_DARK_PAGE_SURFACE%', darkPageSurface)
      }
    },
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'prompt',
      manifest: {
        name: 'FMCG DMS — APP-00A (Synthetic)',
        short_name: 'FMCG DMS',
        description: 'Application foundation only. No offline business writes.',
        theme_color: lightPageSurface, background_color: lightPageSurface,
        display: 'standalone', start_url: '/',
        icons: [{ src: '/icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' }]
      },
      workbox: {
        navigateFallbackDenylist: [/^\/api\//],
        runtimeCaching: [],
        globPatterns: ['**/*.{js,css,html,svg,ico,png,woff2}']
      }
    })
  ],
  server: { proxy: { '/health': 'http://localhost:5116', '/api': 'http://localhost:5116' } }
})
