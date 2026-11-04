import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { formatMoney, timeAgo } from './format.ts';

describe('formatMoney', () => {
  it('formats with the currency', () => {
    assert.equal(formatMoney(1249.5, 'EUR', 'en-IE'), '€1,249.50');
    assert.equal(formatMoney(0.1, 'USD', 'en-US'), '$0.10');
  });

  it('falls back gracefully for unknown currencies', () => {
    assert.equal(formatMoney(12, 'NOT_A_CURRENCY'), 'NOT_A_CURRENCY 12.00');
  });
});

describe('timeAgo', () => {
  const now = Date.parse('2026-11-04T12:00:00Z');
  const ago = (ms: number) => new Date(now - ms).toISOString();

  it('uses the largest sensible unit', () => {
    assert.equal(timeAgo(ago(5_000), now), '5s ago');
    assert.equal(timeAgo(ago(5 * 60_000), now), '5m ago');
    assert.equal(timeAgo(ago(3 * 3_600_000), now), '3h ago');
  });

  it('never shows negative times for clock skew', () => {
    assert.equal(timeAgo(new Date(now + 10_000).toISOString(), now), '0s ago');
  });
});
