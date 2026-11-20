import { useSyncExternalStore } from 'react'

export type ThemePreference = 'light' | 'dark' | 'system'
export type ThemeSnapshot = Readonly<{
  preference: ThemePreference
  resolved: 'light' | 'dark'
  persistent: boolean
}>

interface ThemeController {
  getSnapshot(): ThemeSnapshot
  setPreference(preference: ThemePreference): void
  subscribe(listener: () => void): () => void
}

declare global {
  interface Window {
    FmcgTheme?: ThemeController
  }
}

function getThemeController(): ThemeController {
  const controller = window.FmcgTheme
  if (!controller) throw new Error('Theme bootstrap did not load. Expected /theme-runtime.js before the app entry.')
  return controller
}

export function useTheme(): ThemeSnapshot {
  const controller = getThemeController()
  return useSyncExternalStore(controller.subscribe, controller.getSnapshot)
}

export function setThemePreference(preference: ThemePreference): void {
  getThemeController().setPreference(preference)
}
