import base from './playwright.app03-5.config'

const artifacts = `../TestResults/app03-6/${process.env.APP02C1_RUN_ID}`
export default {
  ...base,
  expect: { timeout: 12_000 },
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
}
