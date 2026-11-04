// k6 load test: sustained payment creation through the real API, then measures how fast the
// asynchronous pipeline (fraud → settlement → notifications) drains.
//
//   make load-test                      (or)
//   docker run --rm --network settleflow_default -v "$PWD/loadtest:/scripts" grafana/k6:1.3.0 \
//     run -e BASE_URL=http://api:8080 -e RATE=50 -e DURATION=60s /scripts/payments.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { randomIntBetween, uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

const BASE = __ENV.BASE_URL || 'http://localhost:8083';
const RATE = Number(__ENV.RATE || 50);
const MERCHANTS = Number(__ENV.MERCHANTS || 20);
const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || 'admin-change-me';

const accepted = new Counter('payments_accepted');
const drainSeconds = new Trend('pipeline_drain_seconds');

export const options = {
  scenarios: {
    create_payments: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: __ENV.DURATION || '60s',
      preAllocatedVUs: Math.max(20, RATE),
      maxVUs: RATE * 4,
    },
  },
  thresholds: {
    'http_req_failed{scenario:create_payments}': ['rate<0.01'],
    'http_req_duration{scenario:create_payments}': ['p(95)<300', 'p(99)<800'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  teardownTimeout: '200s',
};

function adminToken() {
  const r = http.post(`${BASE}/api/auth/login`, JSON.stringify({ username: 'admin', password: ADMIN_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  if (r.status !== 200) throw new Error(`login failed: ${r.status}`);
  return r.json('token');
}

export function setup() {
  const token = adminToken();
  const run = Date.now().toString(36);
  const keys = [];
  for (let i = 0; i < MERCHANTS; i++) {
    const r = http.post(`${BASE}/api/merchants`, JSON.stringify({ id: `load_${run}_${i}`, name: `Load test ${i}` }),
      { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` } });
    if (r.status !== 201) throw new Error(`create merchant failed: ${r.status} ${r.body}`);
    keys.push(r.json('apiKey'));
  }
  return { keys, run, started: Date.now() };
}

const COUNTRIES = ['IE', 'DE', 'FR', 'NL', 'ES', 'GB', 'IE', 'DE', 'US']; // mostly low risk

export default function (data) {
  const key = data.keys[randomIntBetween(0, data.keys.length - 1)];
  const amount = randomIntBetween(100, 499999) / 100; // 1.00 – 4,999.99
  const r = http.post(`${BASE}/api/payments`, JSON.stringify({
    amount,
    currency: 'EUR',
    customerId: `cust_${uuidv4().slice(0, 12)}`,
    country: COUNTRIES[randomIntBetween(0, COUNTRIES.length - 1)],
  }), {
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': key, 'Idempotency-Key': uuidv4() },
    tags: { name: 'POST /api/payments' },
  });
  if (check(r, { 'accepted (202)': x => x.status === 202 })) accepted.add(1);
}

// After the load stops, wait until every payment of THIS run has left the pipeline (none still
// scoring or settling) and report how long that took.
export function teardown(data) {
  const token = adminToken();
  const auth = { headers: { Authorization: `Bearer ${token}` } };
  const inFlight = () => http.get(`${BASE}/api/payments?q=load_${data.run}_&status=PendingFraud,FraudApproved&pageSize=1`, auth).json('total');
  const t0 = Date.now();
  for (let i = 0; i < 180; i++) {
    const left = inFlight();
    if (left === 0) {
      const secs = (Date.now() - t0) / 1000;
      drainSeconds.add(secs);
      const settled = http.get(`${BASE}/api/payments?q=load_${data.run}_&status=Settled&pageSize=1`, auth).json('total');
      console.log(`Pipeline drained ${secs.toFixed(1)}s after load stopped; ${settled} of this run's payments settled.`);
      return;
    }
    if (i % 10 === 0) console.log(`${left} payments still in flight…`);
    sleep(1);
  }
  console.log(`Pipeline did not drain within 180s (${inFlight()} still in flight)`);
}
