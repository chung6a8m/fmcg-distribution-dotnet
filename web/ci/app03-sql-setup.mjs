import { spawn } from 'node:child_process'
import { renameSync, rmSync, writeFileSync } from 'node:fs'
import { lstat, open, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'
import { evidenceDirectory } from './app03-browser-evidence.mjs'

const repository = fileURLToPath(new URL('../../', import.meta.url))

function launchObserver(directory, profile, runId) {
  // The browser harness holds synthetic credentials. The SQL observer only needs its Windows profile.
  const env = {}
  for (const name of ['SystemRoot', 'LOCALAPPDATA', 'USERPROFILE', 'TEMP', 'TMP']) {
    if (process.env[name]) env[name] = process.env[name]
  }
  return spawn(path.join(env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', path.join(repository, 'scripts', 'Watch-App03SqlReads.ps1'),
      '-ProfileName', profile, '-RunId', runId, '-ReporterPid', String(process.pid)], {
      cwd: repository, windowsHide: true, stdio: ['pipe', 'ignore', 'ignore'],
      env: { ...env, FMCG_APP03_READ_DIAGNOSTICS: '1', GITHUB_ACTIONS: 'true', RUNNER_OS: 'Windows',
        APP03_EVIDENCE_DIR: directory },
    })
}

// Read only the bounded prefix containing startup and the first sample. Never emit SQL records here.
async function waitForSample(file, profile, runId, child, signal) {
  const buffer = Buffer.alloc(64 * 1024)
  while (!signal.aborted) {
    let handle
    try {
      const stat = await lstat(file)
      if (!stat.isFile() || stat.isSymbolicLink()) return 'unexpectedOutput'
      handle = await open(file, 'r')
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0)
      const lines = buffer.toString('utf8', 0, bytesRead).split('\n').slice(0, -1)
      const events = lines.map(line => JSON.parse(line))
      const first = events[0]
      if (first && (first.event !== 'start' || first.profile !== profile || first.runId !== runId
          || first.pid !== child.pid)) return 'unexpectedOutput'
      if (events.some(row => row.event === 'unavailable' || row.event === 'stop')) return 'unavailable'
      const available = events.findIndex(row => row.event === 'available')
      if (available > 0) {
        const observation = events.slice(available + 1).find(row => row.event === 'sample' || row.event === 'sampleUnavailable')
        if (observation) return observation.event
      }
      if (bytesRead === buffer.length) return 'unexpectedOutput'
    } catch (error) {
      if (error.code !== 'ENOENT') return 'unreadableOutput'
    } finally { await handle?.close() }
    await delay(100, undefined, { signal }).catch(() => {})
  }
  return 'cancelled'
}

export async function startSqlObserver(directory, profile, runId, spawnChild = launchObserver, timeoutMs = 45_000) {
  const file = path.join(directory, 'sql-reads.ndjson')
  const receipt = path.join(directory, 'sql-observer-startup.json')
  const started = performance.now()
  const startupDeadline = started + Math.max(0, Math.min(45_000, timeoutMs))
  const timing = { profile, runId, startedUtc: new Date().toISOString(), status: 'starting' }
  let child, closed, ownReceipt = false
  function save() {
    const pending = receipt + '.tmp'
    writeFileSync(pending, JSON.stringify(timing) + '\n', { flag: 'wx' })
    try { renameSync(pending, receipt) } finally { rmSync(pending, { force: true }) }
  }
  async function stop(deadline) {
    if (!child || !closed) return
    try { child.stdin.end() } catch {}
    const graceMs = Math.floor(Math.min(3500, deadline - performance.now()))
    let killTimer
    if (child.exitCode === null && child.signalCode === null) {
      if (graceMs > 0) killTimer = setTimeout(() => child.kill('SIGKILL'), graceMs)
      else child.kill('SIGKILL')
    }
    try { await closed } finally { clearTimeout(killTimer) }
  }

  try {
    // Neither a previous receipt nor an orphan's observations may establish readiness.
    await writeFile(receipt, JSON.stringify(timing) + '\n', { flag: 'wx' })
    ownReceipt = true
    const existing = await lstat(file).catch(error => { if (error.code !== 'ENOENT') throw error })
    if (existing) {
      timing.status = 'existingOutput'
    } else {
      timing.spawnRequestedUtc = new Date().toISOString()
      save()
      child = spawnChild(directory, profile, runId)
      child.stdin.on('error', () => {})
      child.once('spawn', () => {
        timing.spawnUtc = new Date().toISOString()
        try { save() } catch { console.log('APP-03 SQL observer spawn receipt unavailable.') }
      })
      let startError = false
      child.once('error', () => { startError = true })
      closed = new Promise(resolve => child.once('close', code => {
        timing.exitUtc = new Date().toISOString()
        timing.exitCode = code
        resolve(startError ? 'spawnError' : 'exited')
      }))
      const cancel = new AbortController()
      let deadline
      try {
        const timeout = new Promise(resolve => {
          deadline = setTimeout(() => resolve('timeout'), Math.max(0, startupDeadline - performance.now()))
        })
        const outcome = await Promise.race([closed, timeout, waitForSample(file, profile, runId, child, cancel.signal)])
        if (outcome === 'sample' || outcome === 'sampleUnavailable') {
          timing.status = child.exitCode === null && child.signalCode === null ? 'ready' : 'exited'
          timing.firstObservation = outcome
        } else {
          timing.status = outcome
        }
      } finally { clearTimeout(deadline); cancel.abort() }
    }
    timing.decisionUtc = new Date().toISOString()
    timing.elapsedMs = Math.round(performance.now() - started)
    save()
    if (timing.status === 'ready') {
      return async () => {
        await stop(performance.now() + 3500)
        try { save() } catch { console.log('APP-03 SQL observer shutdown receipt unavailable.') }
      }
    }
    await stop(startupDeadline)
  } catch {
    console.log('APP-03 SQL observer startup unavailable.')
    if (ownReceipt) {
      timing.status = 'unavailable'
      timing.decisionUtc = new Date().toISOString()
      timing.elapsedMs = Math.round(performance.now() - started)
      try { save() } catch {}
    }
    if (child) await stop(startupDeadline)
  } finally {
    if (ownReceipt) {
      timing.setupEndedUtc = new Date().toISOString()
      timing.setupElapsedMs = Math.round(performance.now() - started)
      try { save() } catch { console.log('APP-03 SQL observer setup completion receipt unavailable.') }
    }
  }
}

export default async function globalSetup(config) {
  const env = process.env
  if (env.FMCG_APP03_READ_DIAGNOSTICS !== '1' || env.GITHUB_ACTIONS !== 'true'
      || env.RUNNER_OS !== 'Windows' || process.platform !== 'win32') return
  try {
    const profile = path.basename(config.configFile).match(/^playwright\.(app03-[45])\.config\.ts$/)?.[1]
    const directory = evidenceDirectory(repository, profile, env.APP02C1_RUN_ID, env.APP03_EVIDENCE_DIR)
    if (!directory) throw new Error()
    return await startSqlObserver(directory, profile, env.APP02C1_RUN_ID)
  } catch { console.log('APP-03 SQL observer setup unavailable.') }
}
