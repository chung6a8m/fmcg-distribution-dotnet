import base from './playwright.app02c5.config'

const artifacts = `../TestResults/app03-3/${process.env.APP02C1_RUN_ID}`
export default {
  ...base,
  testDir: './e2e-app03-3',
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
  timeout: 120_000,
}
