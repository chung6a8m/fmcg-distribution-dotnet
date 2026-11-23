import base from './playwright.app02c5.config'

export default {
  ...base,
  testDir: './e2e-dev-f03',
  outputDir: `test-results-app02c5/${process.env.APP02C1_RUN_ID}/fefo-browser`,
}
