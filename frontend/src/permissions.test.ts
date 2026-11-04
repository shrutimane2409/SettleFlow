import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { Role, Session } from './auth.ts';
import { can } from './permissions.ts';

const as = (role: Role): Session => ({ token: 't', expiresAt: '2999-01-01', user: { id: '1', username: role, role } });

describe('permissions (mirror the API policies)', () => {
  it('admin can do everything', () => {
    assert.ok(can.admin(as('admin')) && can.review(as('admin')) && can.createPayments(as('admin')));
  });

  it('analyst reviews and creates payments but is not admin', () => {
    assert.ok(can.review(as('analyst')) && can.createPayments(as('analyst')));
    assert.equal(can.admin(as('analyst')), false);
  });

  it('viewer is read-only', () => {
    assert.equal(can.review(as('viewer')) || can.createPayments(as('viewer')) || can.admin(as('viewer')), false);
  });

  it('signed-out users can do nothing', () => {
    assert.equal(can.review(null) || can.createPayments(null) || can.admin(null), false);
  });
});
