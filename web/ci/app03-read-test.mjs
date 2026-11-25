import { test as base } from '@playwright/test'
import { observeApp03ReadHttp } from './app03-read-http.mjs'

export const test = process.env.FMCG_APP03_READ_DIAGNOSTICS === '1'
  ? base.extend({
    app03ReadDiagnostics: [async ({ context }, use) => {
      const stop = observeApp03ReadHttp(context, process.env.APP02C1_WEB_URL)
      try {
        await use()
      } finally {
        stop()
      }
    }, { auto: true }],
  })
  : base
