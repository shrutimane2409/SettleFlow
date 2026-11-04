// Fills a fresh stack with realistic demo data: a mix of payment outcomes, manual captures,
// partial refunds and items in the review queue. Run with `make seed`.
import http from 'k6/http';
import { sleep } from 'k6';
import { randomIntBetween, randomItem, uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

const BASE = __ENV.BASE_URL || 'http://localhost:8083';
const COUNT = Number(__ENV.COUNT || 150);
const KEY = __ENV.DEMO_MERCHANT_API_KEY || 'sf_live_demo0000_local-demo-key-not-a-secret';

export const options = { vus: 1, iterations: 1 };

const json = (key, extra = {}) => ({ headers: { 'Content-Type': 'application/json', 'X-Api-Key': key, ...extra } });

export default function () {
  const created = [];
  for (let i = 0; i < COUNT; i++) {
    const roll = Math.random();
    const body = {
      currency: randomItem(['EUR', 'EUR', 'EUR', 'GBP', 'USD']),
      customerId: `cust_seed_${randomIntBetween(1, 60)}`,
      country: roll < 0.12 ? randomItem(['US', 'BR', 'NG']) : randomItem(['IE', 'DE', 'FR', 'NL', 'ES', 'GB']),
      amount: roll > 0.9 ? randomIntBetween(5001, 9000) : randomIntBetween(500, 400000) / 100,
      captureMode: roll > 0.8 && roll <= 0.9 ? 'manual' : 'automatic',
    };
    // Respect the API's per-merchant rate limit: on 429, back off and retry with the SAME idempotency key,
    // so a retry can never create a duplicate payment.
    const key = uuidv4();
    let r = http.post(`${BASE}/api/payments`, JSON.stringify(body), json(KEY, { 'Idempotency-Key': key }));
    for (let attempt = 0; r.status === 429 && attempt < 10; attempt++) {
      sleep(2);
      r = http.post(`${BASE}/api/payments`, JSON.stringify(body), json(KEY, { 'Idempotency-Key': key }));
    }
    if (r.status === 202) created.push({ id: r.json('id'), manual: body.captureMode === 'manual', amount: body.amount });
  }
  console.log(`created ${created.length} payments; waiting for the pipeline…`);
  sleep(8);

  let captured = 0;
  let refunded = 0;
  for (const p of created) {
    if (p.manual && Math.random() < 0.6) {
      const r = http.post(`${BASE}/api/payments/${p.id}/capture`, JSON.stringify({}), json(KEY));
      if (r.status === 202) captured++;
    } else if (!p.manual && Math.random() < 0.08) {
      const amount = Math.min(p.amount, Math.round(p.amount * randomIntBetween(20, 100)) / 100);
      const r = http.post(`${BASE}/api/payments/${p.id}/refunds`, JSON.stringify({ amount, reason: 'seed: customer return' }),
        json(KEY, { 'Idempotency-Key': uuidv4() }));
      if (r.status === 202) refunded++;
    }
  }
  console.log(`captured ${captured} manual payments, refunded ${refunded}. Open http://localhost:3000`);
}
