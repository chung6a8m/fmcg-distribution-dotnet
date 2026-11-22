import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.APP02C1_WEB_URL
const runId = process.env.APP02C1_RUN_ID
if (!baseURL || !/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL) || !runId || !/^[a-f0-9]{32}$/.test(runId)
    || !process.env.APP02C6_CHECKPOINTS) {
  throw new Error('Start APP-02C.6 with scripts/Run-App02C6.ps1')
}

const artifacts = `test-results-app02c6/${runId}`
export default defineConfig({
  testDir: './e2e-app02c6',
  outputDir: `${artifacts}/browser`,
  reporter: [['list'], ['junit', { outputFile: `${artifacts}/junit.xml` }]],
  workers: 1,
  retries: 0,
  timeout: 180_000,
  use: { ...devices['Desktop Chrome'], baseURL, actionTimeout: 12_000, trace: 'off', screenshot: 'off', video: 'off' },
})
