import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.APP02C1_WEB_URL
if (!baseURL || !/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL)) {
  throw new Error('Start APP-02C.1 with scripts/Run-App02C1.ps1 -Verify')
}

export default defineConfig({
  testDir: './e2e-app02c1',
  outputDir: 'test-results-app02c1/browser',
  reporter: [['list'], ['junit', { outputFile: 'test-results-app02c1/junit.xml' }]],
  workers: 1,
  retries: 0,
  timeout: 90_000,
  use: { ...devices['Desktop Chrome'], baseURL, trace: 'off', screenshot: 'off', video: 'off' },
})
