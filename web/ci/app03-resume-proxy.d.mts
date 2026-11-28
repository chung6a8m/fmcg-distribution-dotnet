import type { Plugin, ProxyOptions } from 'vite'

export function isResumeProxyEnabled(): boolean
export function resumeProxyWitness(enabled: boolean, write?: (line: string) => void): {
  ingress: Plugin
  configure: NonNullable<ProxyOptions['configure']>
}
