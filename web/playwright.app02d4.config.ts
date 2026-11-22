import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.APP02C1_WEB_URL
const runId = process.env.APP02C1_RUN_ID
if (!baseURL || !/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL) || !runId || !/^[a-f0-9]{32}$/.test(runId)
  || !process.env.APP02D4_BROWSER_FIXTURE || !process.env.APP02D4_CHECKPOINTS) {
  throw new Error('Start APP-02D.4 with scripts/Run-App02D4.ps1')
}

const artifacts = 'test-results-app02d4/' + runId
export default defineConfig({
  testDir: './e2e-app02d4',
  outputDir: artifacts + '/browser',
  reporter: [['list'], ['junit', { outputFile: artifacts + '/junit.xml' }]],
  workers: 1,
  retries: 0,
  timeout: 150_000,
  use: { ...devices['Desktop Chrome'], baseURL, actionTimeout: 12_000, trace: 'off', screenshot: 'off', video: 'off' },
})
