import base from './playwright.app02c5.config'

const artifacts = `test-results-app02c5/${process.env.APP02C1_RUN_ID}/app03-1a`
export default {
  ...base,
  testDir: './e2e-app03-1a',
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
  timeout: 150_000,
}
