import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.APP02C1_WEB_URL
const runId = process.env.APP02C1_RUN_ID
if (!baseURL || !/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL) || !runId || !/^[a-f0-9]{32}$/.test(runId)) {
  throw new Error('Start DEV-F02 with scripts/Run-App02C1.ps1 -Increment DevF02 -Verify')
}
const artifacts = `test-results-dev-f02/${runId}`
export default defineConfig({
  testDir: './e2e-dev-f02',
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
  workers: 1,
  retries: 0,
  timeout: 90_000,
  use: { ...devices['Desktop Chrome'], baseURL, actionTimeout: 12_000, trace: 'off', screenshot: 'off', video: 'off' },
})
