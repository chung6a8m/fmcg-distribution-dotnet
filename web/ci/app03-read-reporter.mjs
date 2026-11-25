import { writeFileSync } from 'node:fs'
import { evidenceDirectory, publishBrowserEvidence } from './app03-browser-evidence.mjs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repository = fileURLToPath(new URL('../../', import.meta.url))

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
      writeFileSync(path.join(directory, 'browser-publication.json'), '{"status":"staged"}\n', { flag: 'wx' })
    } catch { console.log('APP-03 browser publication receipt unavailable.') }
  }

  async onExit() {
    // Playwright invokes onExit after all reporters have finished writing their files.
    if (process.env.FMCG_APP03_READ_DIAGNOSTICS !== '1') return
    try { await publishBrowserEvidence(this.profile) }
    catch { console.log('APP-03 browser evidence publication unavailable.') }
  }
}
