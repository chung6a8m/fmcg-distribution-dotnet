import { resolve } from 'node:path'
import base from './playwright.app02c5.config'

const stage = process.env.APP05_DAILY_STAGE
const evidence = process.env.APP05_EVIDENCE_DIR
if (!evidence || (stage !== 'day-one' && stage !== 'day-two')) {
  throw new Error('Start APP-05.7 with scripts/Run-App03Dev.ps1 -Action VerifyDailyClose')
}

export default {
  ...base,
  testDir: './e2e-app05-7',
  timeout: 180_000,
  outputDir: resolve(evidence, stage + '-browser'),
  reporter: [['list'], ['junit', { outputFile: resolve(evidence, stage + '.xml') }]],
  use: { ...base.use, serviceWorkers: 'block' as const },
}
