import test from 'node:test';
import assert from 'node:assert/strict';
import { Bounded } from '../src/bounded.js';

test('it keeps the last items only', () => {
  const b = new Bounded(3);
  for (let i = 0; i < 1000; i++) b.push(i);
  assert.equal(b.size, 3);
  assert.deepEqual(b.items(), [997, 998, 999]);
});

test('its items are a copy', () => {
  const b = new Bounded(2).push(1);
  b.items().push(9);
  assert.deepEqual(b.items(), [1]);
});

test('a limit must be a positive count', () => {
  for (const limit of [0, -1, 1.5, undefined]) assert.throws(() => new Bounded(limit), RangeError);
});
