import assert from 'node:assert/strict'
import { test } from 'node:test'
import { isLiveApiResponse } from '../src/health.ts'
import './ui-02.test.mjs'
import './ui-03.test.mjs'

const live = { status: 'live', scope: 'APP-00A', businessWrites: false }
const jsonResponse = (payload, status = 200) =>
  new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' }
  })

test('accepts the actual APP-00A liveness response', async () => {
  assert.equal(await isLiveApiResponse(jsonResponse(live)), true)
})

test('rejects an HTTP 200 HTML SPA fallback when the API is absent', async () => {
  const response = new Response('<!doctype html><html></html>', {
    status: 200, headers: { 'content-type': 'text/html' }
  })
  assert.equal(await isLiveApiResponse(response), false)
})

test('rejects HTTP 200 without the expected API contract', async () => {
  assert.equal(await isLiveApiResponse(jsonResponse({ status: 'live' })), false)
  assert.equal(await isLiveApiResponse(jsonResponse({ ...live, scope: 'another-service' })), false)
  assert.equal(await isLiveApiResponse(jsonResponse({ ...live, businessWrites: true })), false)
})

test('rejects a failed HTTP response and malformed JSON', async () => {
  assert.equal(await isLiveApiResponse(jsonResponse(live, 503)), false)
  assert.equal(await isLiveApiResponse(new Response('not-json', {
    status: 200, headers: { 'content-type': 'application/json' }
  })), false)
  assert.equal(await isLiveApiResponse(new Response(JSON.stringify(live))), false)
})
