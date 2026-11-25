import base from './playwright.app02c5.config'

const artifacts = `../TestResults/app05-1/${process.env.APP02C1_RUN_ID}`
export default {
  ...base,
  testDir: './e2e-app05',
  timeout: 150_000,
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
  use: { ...base.use, serviceWorkers: 'block' as const },
}
