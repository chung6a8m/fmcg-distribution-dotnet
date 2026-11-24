import base from './playwright.app03-5.config'

const artifacts = `../TestResults/app03-9/${process.env.APP02C1_RUN_ID}`
const stage = process.env.APP03_ACCEPTANCE_STAGE
export default {
  ...base,
  testDir: './e2e-app03-9',
  outputDir: `${artifacts}/${stage}-browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/${stage}.xml` }]],
  expect: { timeout: 12_000 },
  use: { ...base.use, serviceWorkers: 'block' as const },
}
