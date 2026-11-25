import { spawn } from 'node:child_process'
import { writeFileSync } from 'node:fs'
import { evidenceDirectory, publishBrowserEvidence } from './app03-browser-evidence.mjs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repository = fileURLToPath(new URL('../../', import.meta.url))

// Observe exit immediately, including spawn errors and an exit before the test finishes.
export function monitorObserver(child) {
  child.stdin.on('error', () => {})
  const closed = new Promise(resolve => {
    child.once('error', () => console.log('APP-03 SQL observer unavailable: process start.'))
    child.once('close', code => {
      if (code !== 0) console.log('APP-03 SQL observer unavailable: incomplete capture.')
      resolve()
    })
  })
  return async () => {
    child.stdin.end()
    const timer = setTimeout(() => child.kill('SIGKILL'), 3500)
    try { await closed } finally { clearTimeout(timer) }
  }
}

export default class App03ReadReporter {
  constructor({ profile }) {
    this.profile = profile
  }

  onBegin() {
    const env = process.env
    if (env.FMCG_APP03_READ_DIAGNOSTICS !== '1' || env.GITHUB_ACTIONS !== 'true'
        || env.RUNNER_OS !== 'Windows' || process.platform !== 'win32') return
    try {
      const directory = evidenceDirectory(repository, this.profile, env.APP02C1_RUN_ID, env.APP03_EVIDENCE_DIR)
      if (!directory) throw new Error()
      try {
        writeFileSync(path.join(directory, 'browser-publication.json'), '{"status":"staged"}\n', { flag: 'wx' })
      } catch { console.log('APP-03 browser publication receipt unavailable.') }
      const powershell = path.join(env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
      // The browser child has synthetic credentials. The observer only needs the Windows
      // profile for integrated LocalDB auth and these validated diagnostic settings.
      const childEnv = {}
      for (const key of ['SystemRoot', 'LOCALAPPDATA', 'USERPROFILE', 'TEMP', 'TMP']) {
        if (env[key]) childEnv[key] = env[key]
      }
      const child = spawn(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive',
        '-File', path.join(repository, 'scripts', 'Watch-App03SqlReads.ps1'),
        '-ProfileName', this.profile, '-RunId', env.APP02C1_RUN_ID, '-ReporterPid', String(process.pid)], {
        cwd: repository, windowsHide: true, stdio: ['pipe', 'ignore', 'ignore'],
        env: { ...childEnv, FMCG_APP03_READ_DIAGNOSTICS: '1', GITHUB_ACTIONS: 'true',
          RUNNER_OS: 'Windows', APP03_EVIDENCE_DIR: directory },
      })
      this.stopObserver = monitorObserver(child)
    } catch {
      console.log('APP-03 SQL observer unavailable: startup validation.')
    }
  }

  async onEnd() {
    try {
      await this.stopObserver?.()
    } catch {
      console.log('APP-03 SQL observer unavailable: shutdown.')
    }
  }

  async onExit() {
    // Playwright invokes onExit after all reporters have finished writing their files.
    if (process.env.FMCG_APP03_READ_DIAGNOSTICS !== '1') return
    try { await publishBrowserEvidence(this.profile) }
    catch { console.log('APP-03 browser evidence publication unavailable.') }
  }
}
