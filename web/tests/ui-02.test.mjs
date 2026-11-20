import test from 'node:test'
import assert from 'node:assert/strict'
import { parseDemoQuantity } from '../src/ui/decimal.ts'

test('UI-02 parser accepts positive locale-independent edit buffers without rounding', () => {
  for (const [raw, expected] of [
    ['1', 1], ['12,5', 12.5], ['12.375', 12.375], ['0,001', 0.001], [' 0001.25 ', 1.25]
  ]) assert.equal(parseDemoQuantity(raw), expected, raw)
})

test('UI-02 parser rejects incomplete, grouped, negative and nonfinite inputs without coercion', () => {
  for (const raw of [
    '', ' ', '0', '0,000', '12,', '12.', '1,2345', '1.000,00', '1,000.00',
    'NaN', 'Infinity', '-3', '+1', '1e3', '1 000'
  ]) assert.equal(parseDemoQuantity(raw), null, raw)
})
