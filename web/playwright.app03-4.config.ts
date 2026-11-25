import base from './playwright.app02c5.config'
import { browserEvidenceConfig } from './ci/app03-browser-evidence.mjs'

export default {
  ...base,
  ...browserEvidenceConfig('app03-4'),
  testDir: './e2e-app03-4',
  timeout: 120_000,
}
