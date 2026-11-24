import base from './playwright.app02c5.config'

const stage = process.env.APP03_FINANCE_STAGE
const artifacts = `../TestResults/app03-7/${process.env.APP02C1_RUN_ID}`
export default {
  ...base,
  testDir: './e2e-app03-7',
  expect: { timeout: 12_000 },
  timeout: 120_000,
  outputDir: `${artifacts}/browser/${stage}`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/${stage}.xml` }]],
}
