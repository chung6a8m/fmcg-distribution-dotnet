import assert from 'node:assert/strict'
import test from 'node:test'
import { resumeIdentity, isResumeProxyEnabled, resumeProxyPort } from '../ci/dev-http-proxy.mjs'
const id = 'a'.repeat(32)
const req = (method,url,correlation=id) => ({method,url,headers:{'x-fmcg-d2-peer-witness':correlation}})
test('fixed labels, no identifiers or query values', () => {
  assert.deepEqual(resumeIdentity(req('GET','/api/synthetic/app02d2/sales/private-producer/private-command?tripId=secret')), {id,route:'D2_LOOKUP'})
  assert.deepEqual(resumeIdentity(req('POST','/api/synthetic/app02c2/reserve')), {id,route:'C2_RESERVE'})
  assert.deepEqual(resumeIdentity(req('GET','/api/synthetic/app02c1/session')), {id,route:'C1_SESSION'})
  assert.equal(resumeIdentity(req('GET','/api/synthetic/app02d2/sales/a/b/c')), null)
  assert.equal(resumeIdentity(req('GET','/api/synthetic/app02c2/reserve')), null)
  assert.equal(resumeIdentity(req('GET','/health/live')), null)
  assert.equal(resumeIdentity(req('GET','/api/synthetic/app02c1/session','secret')), null)
})
test('fixed Windows hosted opt-in and strict port', () => {
  const env = {FMCG_DEV_HTTP_PROXY:'1',GITHUB_ACTIONS:'true',RUNNER_OS:'Windows'}
  assert.equal(isResumeProxyEnabled(env,'win32'),true)
  assert.equal(isResumeProxyEnabled(env,'linux'),false)
  assert.equal(isResumeProxyEnabled({...env,FMCG_DEV_HTTP_PROXY:'0'},'win32'),false)
  assert.equal(resumeProxyPort(['5173']),5173)
  for (const args of [['5116'],['5173','--config'],['1'],['05173']]) assert.throws(()=>resumeProxyPort(args))
})
