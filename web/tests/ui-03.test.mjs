import test from 'node:test'
import assert from 'node:assert/strict'
import {
  canCheck, canLookup, canSend, draftSignature, isLocked, newWorkflow,
  restoreWorkflow, workflowReducer
} from '../src/ui/command-recovery.ts'

function reduce(state, ...events) {
  return events.reduce((current, event) => workflowReducer(current, event), state)
}
function submitted() {
  const checked = reduce(newWorkflow(), { type: 'check' })
  assert.equal(canSend(checked), true)
  return reduce(checked, { type: 'send' })
}

test('UI03-C01 preflight invalidated by quantity, source version, permission and connectivity', () => {
  let state = reduce(newWorkflow(), { type: 'check' })
  assert.equal(state.phase, 'checked')
  assert.equal(state.checkedSignature, draftSignature(state))
  state = reduce(state, { type: 'quantity', value: '2,5' })
  assert.equal(canSend(state), false)
  state = reduce(state, { type: 'check' }, { type: 'revision', value: 'rev-08' })
  assert.equal(canSend(state), false)
  state = reduce(state, { type: 'check' }, { type: 'permission', value: 'read' })
  assert.equal(canCheck(state), false)
  assert.equal(canSend(state), false)
  assert.equal(reduce(state, { type: 'send' }).sent, null)
  state = reduce(state, { type: 'permission', value: 'write' }, { type: 'check' },
    { type: 'connection', value: 'offline' })
  assert.equal(canSend(state), false)
  for (const invalid of ['', '0', '3,', '3.1234', '-1', '1e5']) {
    const rejected = reduce(newWorkflow(), { type: 'quantity', value: invalid }, { type: 'check' })
    assert.equal(rejected.phase, 'draft', invalid)
  }
})

test('UI03-C02 sending freezes identity and payload; pending/UNKNOWN reject duplicates', () => {
  const sent = submitted()
  assert.equal(sent.phase, 'pending')
  assert.equal(sent.sent.id, 'UI03-LOCAL-0001')
  assert.equal(sent.sent.quantity, '2')
  assert.equal(isLocked(sent), true)
  assert.equal(canSend(sent), false)
  assert.strictEqual(reduce(sent, { type: 'send' }), sent)
  assert.strictEqual(reduce(sent, { type: 'quantity', value: '999' }), sent)
  const unknown = reduce(sent, { type: 'lost-ack' }, { type: 'lookup', outcome: 'no-result' })
  assert.equal(unknown.phase, 'unknown')
  assert.equal(unknown.sent.id, sent.sent.id)
  assert.strictEqual(reduce(unknown, { type: 'next' }), unknown)
  assert.strictEqual(reduce(unknown, { type: 'send' }), unknown)
})

test('UI03-C03 durable ACK not POSTED, no-result does not downgrade ACK/waiting', () => {
  let state = reduce(submitted(), { type: 'lookup', outcome: 'no-result' })
  assert.equal(state.intakeAcknowledged, false)
  assert.equal(state.phase, 'unknown')
  state = reduce(state, { type: 'lookup', outcome: 'ack' })
  assert.equal(state.intakeAcknowledged, true)
  assert.equal(state.phase, 'ack')
  state = reduce(state, { type: 'lookup', outcome: 'no-result' })
  assert.equal(state.phase, 'ack')
  state = reduce(state, { type: 'lookup', outcome: 'waiting' })
  assert.equal(state.phase, 'waiting')
  state = reduce(state, { type: 'lookup', outcome: 'ack' }, { type: 'lookup', outcome: 'no-result' })
  assert.equal(state.phase, 'waiting')
  state = reduce(state, { type: 'lookup', outcome: 'posted' })
  assert.equal(state.phase, 'posted')
  assert.equal(canLookup(state), false)
  const next = reduce(state, { type: 'next' })
  assert.equal(next.id, 'UI03-LOCAL-0002')
})

test('UI03-P01 permission revocation and offline guard lookup; retain same UNKNOWN request ID', () => {
  const unknown = reduce(submitted(), { type: 'lost-ack' })
  const denied = reduce(unknown, { type: 'permission', value: 'none' })
  assert.equal(canLookup(denied), false)
  assert.strictEqual(reduce(denied, { type: 'lookup', outcome: 'posted' }), denied)
  assert.equal(denied.id, unknown.id)
  const readOnly = reduce(denied, { type: 'permission', value: 'read' })
  assert.equal(canLookup(readOnly), true)
  assert.equal(canSend(readOnly), false)
  const offline = reduce(readOnly, { type: 'connection', value: 'offline' })
  assert.equal(canLookup(offline), false)
  assert.strictEqual(reduce(offline, { type: 'lookup', outcome: 'posted' }), offline)
})

test('UI03-C04 identity conflict never rotates ID to evade reconciliation', () => {
  const conflict = reduce(submitted(), { type: 'lookup', outcome: 'conflict' })
  assert.equal(conflict.phase, 'conflict')
  assert.strictEqual(reduce(conflict, { type: 'next' }), conflict)
  assert.strictEqual(reduce(conflict, { type: 'send' }), conflict)
  const rejected = reduce(submitted(), { type: 'lookup', outcome: 'rejected' })
  assert.equal(rejected.phase, 'rejected')
  assert.equal(rejected.intakeAcknowledged, false)
  assert.equal(reduce(rejected, { type: 'next' }).id, 'UI03-LOCAL-0002')
})

test('UI03-C05 versioned same-tab restore validates frozen identity and fails closed', () => {
  const unknown = reduce(submitted(), { type: 'lost-ack' }, { type: 'permission', value: 'none' })
  assert.deepEqual(restoreWorkflow(JSON.stringify({ ...unknown, schema: 1 })), unknown)
  for (const raw of [
    '{invalid', JSON.stringify({ ...unknown, schema: 2 }),
    JSON.stringify({ ...unknown, schema: 1, id: 'FORGED' }),
    JSON.stringify({ ...unknown, schema: 1, sent: { ...unknown.sent, quantity: '3' } }),
    JSON.stringify({ ...unknown, schema: 1, history: ['A'.repeat(201)] })
  ]) assert.deepEqual(restoreWorkflow(raw), newWorkflow())
})
