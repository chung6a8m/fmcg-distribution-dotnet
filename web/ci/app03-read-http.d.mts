import type { Plugin, ProxyOptions } from 'vite'

export function createApp03ReadHttpDiagnostics(enabled?: string, write?: (line: string) => void): {
  plugin: Plugin
  configure: NonNullable<ProxyOptions['configure']>
} | undefined
