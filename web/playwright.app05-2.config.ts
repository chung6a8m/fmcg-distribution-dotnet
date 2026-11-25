import base from './playwright.app02c5.config'

const increment = process.env.APP05_SUCCESSOR_EVIDENCE ? 'app05-3' : 'app05-2'
const artifacts = `../TestResults/${increment}/${process.env.APP02C1_RUN_ID}`
const stage = process.env.APP05_CLOSURE_STAGE
export default {
  ...base,
  testDir: './e2e-app05-2',
  timeout: 150_000,
  outputDir: `${artifacts}/${stage}-browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/${stage}.xml` }]],
  use: { ...base.use, serviceWorkers: 'block' as const },
}
