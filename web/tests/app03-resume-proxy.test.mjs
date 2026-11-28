import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import test from 'node:test'
import { isResumeProxyEnabled, resumeProxyPort, resumeIdentity, resumeProxyWitness } from '../ci/app03-resume-proxy.mjs'

const id = 'abcdef0123456789'.repeat(2)
const request = (route = '/api/synthetic/app02c1/session', method = 'GET', value = id) =>
  ({ method, url: route, headers: { 'x-fmcg-app03-catalog-witness': value, authorization: 'private-token' } })

test('only fixed GET routes and canonical IDs are eligible', () => {
  for (const route of ['/api/synthetic/app02c1/session', '/api/synthetic/app02c2/catalog'])
    assert.deepEqual(resumeIdentity(request(route + '?private=value')), { route, id })
  for (const req of [request('/api/private'), request(undefined, 'POST'), request(undefined, 'GET', id.toUpperCase()),
    request(undefined, 'GET', id + '\n'), request(undefined, 'GET', [id, id]), request(undefined, 'GET', 'private-id')])
    assert.equal(resumeIdentity(req), null)
})

test('proxy markers correlate ingress, upstream and downstream without arbitrary data', () => {
  const lines = [], proxy = new EventEmitter(), res = new EventEmitter(), upstream = new EventEmitter()
  const witness = resumeProxyWitness(true, line => lines.push(JSON.parse(line.slice('APP03_RESUME_PROXY '.length))))
  let middleware, calls = 0
  witness.ingress.configureServer({ middlewares: { use: fn => { middleware = fn } } })
  witness.configure(proxy)
  const req = request('/api/synthetic/app02c1/session?private=value')
  res.statusCode = 200
  upstream.statusCode = 200
  middleware(req, res, () => calls++)
  proxy.emit('proxyReq', {}, req)
  proxy.emit('proxyRes', upstream, req)
  upstream.emit('end')
  res.emit('finish')
  assert.equal(calls, 1)
  assert.deepEqual(lines.map(x => x.phase), ['ingress', 'upstream-request', 'upstream-headers', 'upstream-end', 'client-finish'])
  assert.ok(lines.every(x => x.id === id && x.vitePid === process.pid))
  assert.ok(!JSON.stringify(lines).includes('private'))
})

test('disabled proxy is unchanged; failing sink does not interfere with events', () => {
  const proxy = new EventEmitter()
  resumeProxyWitness(false, () => assert.fail('disabled')).configure(proxy)
  assert.equal(proxy.listenerCount('proxyReq'), 0)
  resumeProxyWitness(true, () => { throw new Error('sink failure') }).configure(proxy)
  assert.doesNotThrow(() => proxy.emit('proxyReq', {}, request()))
})

test('proxy error exposes only an allowlisted code and incomplete responses remain visible', () => {
  const lines = [], proxy = new EventEmitter()
  resumeProxyWitness(true, line => lines.push(JSON.parse(line.slice('APP03_RESUME_PROXY '.length)))).configure(proxy)
  proxy.emit('error', { code: 'ECONNREFUSED', message: 'private' }, request())
  proxy.emit('error', { code: 'private', message: 'private' }, request())
  assert.deepEqual(lines.map(x => x.errorCode), ['ECONNREFUSED', 'other'])
  assert.ok(!JSON.stringify(lines).includes('private'))
})

test('diagnostic launch requires all opt-ins, Windows, and exactly one canonical loopback port', () => {
  const env = { FMCG_APP03_RESUME_PROXY: '1', GITHUB_ACTIONS: 'true', RUNNER_OS: 'Windows' }
  assert.equal(isResumeProxyEnabled(env, 'win32'), true)
  assert.equal(isResumeProxyEnabled(env, 'linux'), false)
  for (const key of Object.keys(env)) assert.equal(isResumeProxyEnabled({ ...env, [key]: '' }, 'win32'), false)
  assert.equal(resumeProxyPort(['5173']), 5173)
  for (const args of [[], ['5116'], ['0'], ['65536'], ['5173\n'], ['05173'], ['https://example.com'], ['5173', '--config'], ['5173', '--host', '0.0.0.0']])
    assert.throws(() => resumeProxyPort(args))
})
