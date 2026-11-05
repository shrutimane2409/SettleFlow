# SettleFlow

> Production-oriented event-driven payment processing and fraud-screening platform built with .NET 10, PostgreSQL, Kafka-compatible Redpanda, Docker, Kubernetes, OpenTelemetry, Prometheus, Grafana and React.

---

## Overview

SettleFlow is a distributed payment-processing system designed around asynchronous event-driven workflows.

A payment enters through the ASP.NET Core API, is persisted to PostgreSQL, and is published through a transactional outbox to Redpanda/Kafka. Independent workers then process fraud screening, settlement and notification workflows. Each step is an independent service connected only by Kafka events, with exactly-once *business effects* on top of at-least-once delivery.

The project focuses on backend engineering concerns such as:

- reliable event publication
- idempotent payment creation
- asynchronous processing
- fraud decisioning
- payment state transitions
- retries and dead-letter handling
- double-entry ledger, payouts and reconciliation
- security (roles, merchant API keys, audit log)
- observability and SLO alerting
- containerized and Kubernetes deployment
- automated testing (unit, end-to-end, chaos, browser, load)

---

## Architecture

```text
                    ┌──────────── React dashboard (nginx, :3000) ────────────┐
                    │  /api/* proxied to the API                             │
                    ▼                                                        │
  POST /api/payments ──► API ── one transaction ──► Payments + IdempotencyKeys + Outbox + Timeline
                                                              │
                         Outbox publisher (runs in every service, FOR UPDATE SKIP LOCKED)
                                                              ▼
 ┌──────────────────────────────── Redpanda / Kafka ─────────────────────────────────┐
 │ payments.created → FraudWorker ──► payments.fraud.completed ─┬► SettlementWorker   │
 │                                   (+ payments.failed)        │   (acquirer)        │
 │                                                              │   ├► settlement.completed
 │                                                              │   └► payments.failed
 │                                                              └► ReviewNotification │
 │ settlement.completed / payments.failed ─► Notification ─► notifications.requested  │
 │                                                       ─► NotificationSink (stores) │
 │ any consumer that gives up ──────────────────────────────────► payments.dlq        │
 └────────────────────────────────────────────────────────────────────────────────────┘
```

### Payment lifecycle

```text
PendingFraud ─► FraudApproved ─► Settled ─► PartiallyRefunded ─► Refunded
     │               └────────► SettlementFailed          (acquirer declined)
     ├─► Authorized ─(capture)─► FraudApproved ─► Settled  (captureMode: manual)
     │        └──────(void / 7-day expiry)──► Voided
     ├─► FraudReview ─(approve)─► FraudApproved / Authorized
     │        └─────(reject)──► Declined
     └─► Declined                                         (risk score ≥ 50)
```

## Payments API

| Action | Endpoint | Notes |
|---|---|---|
| Create | `POST /api/payments` | `Idempotency-Key` required. Optional `"captureMode": "manual"` to authorize only |
| Capture | `POST /api/payments/{id}/capture` | `{ "amount": 150 }` (optional, default full); partial capture allowed |
| Void | `POST /api/payments/{id}/void` | Releases an authorization. Uncaptured ones auto-void after 7 days |
| Refund | `POST /api/payments/{id}/refunds` | `Idempotency-Key` required. `{ "amount": 30, "reason": "…" }`, partial or full. Concurrent refunds can never exceed the captured amount |
| Refunds | `GET /api/payments/{id}/refunds`, `GET /api/refunds` | |
| Webhook URL | `PUT /api/merchants/me/webhook` (merchant) or `PUT /api/merchants/{id}/webhook` (admin) | Returns the signing secret **once** |
| Deliveries | `GET /api/webhook-deliveries`, `POST /api/webhook-deliveries/{id}/resend` | |

## Fraud platform

- **Rules are data, not code.** The active rule set (rule on/off switches, scores, thresholds, low-risk countries, velocity window) is a **versioned** row in Postgres, edited from the dashboard (`PUT /api/fraud/rules`, admin). Workers pick changes up within ~5 s, with no redeploy. Every payment records the rule version that scored it, and every change is audited with a field-by-field diff.
- **Simulation before activation**: `POST /api/fraud/simulate` re-scores the last N days of payments with a candidate rule set. It shows decision totals before and after, the transitions (`APPROVED→REVIEW`, …), example payments, and how many known chargebacks the candidate would have caught.
- **Blocklist** of customers and countries (optional expiry); blocklisted payments are declined outright.
- **Chargebacks**: `POST /api/payments/{id}/chargebacks` (reason code; optionally blocklists the customer). The payment becomes `ChargedBack`, the ledger debits the merchant for the amount plus a dispute fee (`Chargebacks:Fee`, default 15.00), and a `payment.charged_back` webhook is sent.
- **Feedback loop**: `GET /api/fraud/performance` shows per rule how often it fires and how many approved payments were later charged back ("missed fraud"), plus approval and chargeback rates per rule version.
- **ML hook**: `IRiskModel` (default `HeuristicRiskModel`, a small logistic stand-in). Enable with `modelEnabled`; a probability above `modelThreshold` adds `modelScore`. Swap in a trained model by registering another `IRiskModel`.

## Analytics & export

- `GET /api/analytics?days=30`: daily payments by outcome and settled volume, top merchants (approval rate, average risk, refunds, chargebacks), countries, and fraud-reason breakdown. It's all computed in SQL (`GROUP BY`, `FILTER`, `jsonb_array_elements_text`), and the dashboard shows it on the Analytics page.
- `GET /api/payments/export.csv?status=&q=&from=&to=`: **streams** up to 100k rows with constant memory. Merchants only export their own payments. Text cells are quoted and protected against spreadsheet formula injection.

## Money: ledger, payouts, reconciliation

**Double-entry ledger.** Every movement of money is a ledger transaction whose entries sum to zero (debit +, credit −), posted **in the same DB transaction** as the payment state change:

| Event | Entries (example: 100.00 EUR, fee 1.5% + 0.25) |
|---|---|
| Settlement | Dr `acquirer:clearing:EUR` 100.00 · Cr `merchant:{id}:balance:EUR` 98.25 · Cr `platform:fees:EUR` 1.75 |
| Refund 30 | Dr `merchant:{id}:balance:EUR` 30.00 · Cr `acquirer:clearing:EUR` 30.00 (the fee is kept) |
| Payout | Dr `merchant:{id}:balance:EUR` net · Cr `acquirer:clearing:EUR` net |

- Postgres enforces it: a **deferred constraint trigger** rejects any transaction that doesn't sum to zero at COMMIT, and ledger rows are **append-only** (corrections are new reversing transactions). Postings are idempotent per (type, reference).
- Fees are configurable per merchant (`PUT /api/merchants/{id}/fees`), rounded half away from zero, and never more than the amount.
- `GET /api/ledger/accounts`, `/api/ledger/trial-balance`, `/api/ledger/transactions?reference=`; merchants use `GET /api/balance`.

**Payouts (settlement batches).** A scheduled job (daily; every 5 min in the local demo) groups each merchant's settled payments and completed refunds into a payout: net = gross − fees − refunds. If net ≤ 0, the items carry over. A payout marks its items, posts to the ledger, sends a `payout.paid` webhook, and has a CSV line-item report (`GET /api/payouts/{id}/report.csv`). Admins can run it on demand (`POST /api/payouts/run`).

**Reconciliation.** Hourly (or `POST /api/reconciliation/runs`), every settlement and refund is matched three ways: SettleFlow ⇄ the acquirer's own records ⇄ the ledger. It also runs a trial balance per currency. Discrepancy kinds: `AmountMismatch`, `MissingAtAcquirer`, `MissingInSettleFlow`, `MissingLedgerPosting`, `LedgerImbalance`. Each is resolved by an admin with a note, which is audited. The local demo makes the acquirer record ~3% of captures 0.01 off (`Settlement:AmountDiscrepancyRate`) so there is something to find. Gauge: `settleflow_reconciliation_discrepancies`.

> Payments settled before the acquirer kept records (before phase 2) show up as `MissingAtAcquirer` in reconciliation. That's accurate; resolve them with a note.

## Observability & operations

| Signal | Where | Notes |
|---|---|---|
| **Traces** | Jaeger :16686 | One trace per payment across all services (trace context in Kafka headers) |
| **Logs** | Grafana → Explore → Loki | JSON logs from every service. Each line has a `TraceId` that links to the trace |
| **Metrics** | Prometheus :9090, Grafana | Business metrics, consumer lag, time-to-settle, circuit breakers, outbox, webhooks, payouts, reconciliation |
| **Correlation id** | `X-Correlation-Id` response header, and `traceId` in error bodies | The same id appears in the logs and in Jaeger |
| **Alerts** | Alertmanager :9093 | Routed to `webhook-receiver` locally; add Slack in `monitoring/alertmanager.yml` |

**SLOs** (`monitoring/alerts.yml`, Grafana dashboard "SettleFlow SLOs"):

| SLO | Target | Alerting |
|---|---|---|
| API availability | 99.9% non-5xx | Multi-window burn rate: page at 14.4× (1h and 5m), warn at 6× (6h and 30m) |
| Settlement latency | 99% of payments settle within 10 s | Warn when the 1 h ratio drops below 99% |
| Create latency | p95 `POST /api/payments` < 300 ms | Warn after 10 min above target |

Operational alerts: service down, dead letters, outbox backlog, consumer lag, acquirer circuit open, reconciliation discrepancies, and webhook deliveries exhausting their retries.

**Real-time without polling.** Postgres triggers `pg_notify` on outbox inserts and payment changes:
- Outbox publishers wake **instantly**, with a 1 s poll kept only as a safety net. Tests prove it: with the fallback poll set to 5 s, a payment still settles in under 4 s.
- The dashboard subscribes to `GET /api/stream` (Server-Sent Events) and refreshes the moment a payment changes. EventSource can't send headers, so it uses a **60-second stream-only token** (`POST /api/stream/token`). Long-lived tokens are never put in URLs, and stream tokens are rejected everywhere else.

**Kubernetes** (`helm/settleflow`):
- Migrations run as a `pre-install`/`pre-upgrade` **Job** (`--migrate-only`). Pods start with `Database__MigrateOnStartup=false` and wait for it.
- **Graceful shutdown**: on SIGTERM, `/health/ready` turns red first (draining check), then in-flight work gets 30 s (`terminationGracePeriodSeconds: 40`).
- PodDisruptionBudgets, an HPA for the API (CPU), and **KEDA** autoscaling for workers based on **Kafka consumer lag** (`keda.enabled=true`).
- Rolling updates with `maxUnavailable: 0`.

## API contracts

- **Conditional requests**: `GET /api/payments/{id}` returns an `ETag` (the row version), and `If-None-Match` gives a `304`. Review, capture, void and refund honor `If-Match`: acting on a stale copy returns **412** instead of overwriting someone else's change.
- **OpenAPI is a tested contract**: `frontend/openapi.json` is a snapshot of `/swagger/v1/swagger.json`, and an integration test fails if they diverge. Response DTOs are fully typed (required/nullable). CI checks that the dashboard's TypeScript types have exactly the API's fields (`npm run check:contract`).
- **Event contracts**: the JSON Schema of every Kafka event is committed in `docs/events/`, and unit tests fail on any unreviewed change. A test also proves old messages still deserialize. Consumers dead-letter envelopes with a `Version` newer than they support (safe rolling deploys), so they can be replayed after the upgrade.
- After an intentional change: `SETTLEFLOW_UPDATE_SNAPSHOTS=1 make test`, review the diff, and bump the event `Version` if old consumers can't read the new shape.

## Webhooks

Merchant-facing events (`payment.authorized`, `payment.settled`, `payment.failed`, `payment.voided`, `refund.succeeded`, `refund.failed`, `payout.paid`) are POSTed to the merchant's URL with:

```
SettleFlow-Event: payment.settled
SettleFlow-Delivery: 5b0c…            (unique per delivery, use it to dedupe)
SettleFlow-Signature: t=1730700000,v1=9f2c…  (HMAC-SHA256 of "{t}.{raw body}" with your signing secret)
```

Verify the signature before trusting a webhook:

```csharp
var ok = SettleFlow.Processing.WebhookSignature.Verify(secret, signatureHeader, rawBody, TimeSpan.FromMinutes(5));
```
```js
const [t, v1] = header.split(',').map(p => p.split('=')[1]);
const expected = crypto.createHmac('sha256', secret).update(`${t}.${rawBody}`).digest('hex');
const ok = crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(v1)) && Math.abs(Date.now()/1000 - t) < 300;
```

- Non-2xx responses or timeouts (5 s) are retried at 10 s, 30 s, 2 m, 10 m, 30 m, 1 h, 3 h, then marked `Failed`. They can be resent from the dashboard or API.
- Signing secrets are encrypted at rest (ASP.NET Data Protection, with a key ring shared by all services in Postgres).
- **SSRF protection**: http(s) only, no credentials in the URL, and no private, loopback, link-local or cloud-metadata addresses. The IP is checked when connecting, which defeats DNS rebinding. Redirects are not followed.
- Locally, `merchant_demo` sends to the `webhook-receiver` container: `docker compose logs -f webhook-receiver`.

## Reliability features

| Problem | How SettleFlow handles it |
|---|---|
| DB write succeeds but Kafka publish fails (or vice versa) | **Transactional outbox**: events are written in the same transaction as the state change, and published afterwards. |
| Several publishers / API replicas | Outbox rows are claimed with `FOR UPDATE SKIP LOCKED`; a row is only sent after every earlier row for the same topic and key, so **per-payment ordering** holds even when a publish is retried. Failed publishes back off exponentially. |
| One slow step backs everything up | Consumers process **partitions in parallel** (one ordered lane per partition, 12 partitions), so throughput scales with partitions and replicas while per-payment ordering holds. Lanes drain before a rebalance hands their partition away. |
| Kafka delivers a message twice | **Inbox** per consumer `(Consumer, EventId)`, written in the handler's transaction. Handlers also check the payment's state, so they are idempotent. |
| Client retries a POST | `Idempotency-Key` header stored with a unique constraint and a hash of the request. Same key and body: the same payment is returned (`Idempotent-Replayed: true`). Same key with a different body: `409`. Race-safe under concurrency. |
| Database or broker briefly down | Transient errors are retried with backoff and **never skip** the message. |
| Acquirer slow or down | 2 s **timeout** plus a **circuit breaker per merchant** (Polly). After repeated failures, calls fail fast for 15 s instead of piling up, and other merchants are unaffected. Gauge: `settleflow_acquirer_circuit_open` |
| Retried acquirer call | The acquirer is **idempotent**: every capture or refund is recorded by `(operation, reference)` outside our transaction, and a repeat returns the recorded result. A crash between "bank charged" and "we committed" can't double-charge. |
| Tables growing forever | **Retention job** (hourly, single-instance via advisory lock): idempotency keys 24 h, inbox 14 d, notifications and webhook deliveries 30 d |
| A message that keeps failing (poison) | Retried `Kafka:MaxHandlerAttempts` times (default 5), then **dead-lettered**: stored in `DeadLetters`, published to `payments.dlq`, and **replayable** from the dashboard or API. |
| Two people review the same payment | Optimistic concurrency (Postgres `xmin`): the second reviewer gets `409`. |
| Request floods | Per-IP fixed-window rate limit on `POST /api/payments`. |

## Security

| Who | How they authenticate | What they can do |
|---|---|---|
| `admin` | `POST /api/auth/login` → bearer token | Everything, including merchants, users, the audit log and DLQ replay |
| `analyst` | bearer token | Read everything, create payments, approve or reject reviews |
| `viewer` | bearer token | Read only |
| merchant | `X-Api-Key` header | Create payments for **its own** merchant id and read **only its own** payments |

- Every endpoint is deny-by-default; only `/health/*`, `/metrics` and login are public.
- Passwords are hashed (ASP.NET Core Identity PBKDF2), and an account locks for 5 minutes after 5 failed logins. Logins are rate-limited per IP.
- API keys are random 256-bit secrets. Only their SHA-256 hash is stored, and a key is shown once, at creation or rotation. Rotating or disabling a key takes effect immediately.
- Reviewers are taken from the signed-in user, never from the request body.
- **Audit log**: every login (including failures), payment creation, review, DLQ replay, and merchant or user change is recorded with actor, IP and trace id. A Postgres trigger rejects `UPDATE`, `DELETE` and `TRUNCATE` on the table.
- Idempotency keys are scoped per merchant.
- Secrets (DB password, JWT signing key, admin password) come from environment variables or Kubernetes secrets, never from code. See `.env.example`.
- Security headers on the API and the dashboard (CSP, `X-Frame-Options`, `nosniff`, `Referrer-Policy`), plus HSTS outside development.
- CI runs **CodeQL** (C# and TypeScript), **Trivy** image scanning (fails on fixable HIGH/CRITICAL), and **Dependabot** updates.

**Local demo accounts** (seeded when `DEMO_DATA=true`): `admin` / `admin-change-me`, and `analyst` or `viewer` / `settleflow-demo`. The demo merchant API key is `sf_live_demo0000_local-demo-key-not-a-secret`. Change all of these via `.env` (`cp .env.example .env`) before running anywhere public.

## Features

- **Fraud engine** with configurable rules (`Fraud` config section): high amount, country risk, round amounts, and **velocity** (many payments from one customer in a short window).
- **Manual review queue**: REVIEW-scored payments wait for a human to approve or reject them.
- **Simulated acquirer**: realistic latency, random transient outages (retried automatically), declines for blocked merchants and unsupported currencies, and a merchant that is always down (`merchant_outage`) to demonstrate the DLQ.
- **Payment timeline**: every step is recorded and shown in the dashboard.
- **Observability**: one **distributed trace per payment** across all services (trace context travels in Kafka headers, viewable in Jaeger); Prometheus metrics from every service; a provisioned Grafana dashboard; JSON health checks (`/health/live`, `/health/ready`).
- **Finance**: double-entry ledger with DB-enforced balance, per-merchant fees, payout batches with CSV reports, and three-way reconciliation.
- **Ops dashboard**: live KPIs, throughput chart, one-click test scenarios, a "burst ×10" load button, search and filters, a timeline drawer, review actions, notifications feed and dead-letter replay.

## Run it

Prerequisite: Docker Desktop.

```bash
docker compose up --build
```

| What | URL |
|---|---|
| Dashboard | http://localhost:3000 |
| API + Swagger | http://localhost:8083/swagger |
| Health | http://localhost:8083/health |
| Kafka console (Redpanda) | http://localhost:8082 |
| Webhook receiver (demo merchant) | http://localhost:8084 (`docker compose logs -f webhook-receiver`) |
| Traces (Jaeger) | http://localhost:16686 |
| Grafana | http://localhost:3001 |
| Prometheus | http://localhost:9090 |
| Alertmanager | http://localhost:9093 |

Migrations and Kafka topics are applied automatically at startup. If you have a database volume from a version before migrations were introduced, reset it once with `docker compose down -v`.

### Try the scenarios

The dashboard's **Create payment** card has presets for each of these:

| Scenario | Input | Result |
|---|---|---|
| Low risk | 1249.50 EUR, IE | Settled |
| High amount | 6000 EUR, DE | Review queue → approve/reject |
| Risky | 7500 EUR, US | Declined (score 70) |
| Blocked merchant | merchant `blocked_shop` | SettlementFailed |
| Acquirer outage | merchant `merchant_outage` | Retries, circuit opens, dead-letter queue, replay |
| Slow acquirer | merchant `merchant_slow` | 2 s timeout, circuit breaker, dead-letter queue |
| Manual capture | `captureMode: manual` | Authorized → capture (or void) from the drawer → Settled |
| Refund | any settled payment | Refund from the drawer → PartiallyRefunded / Refunded, plus a `refund.succeeded` webhook |

### API examples

```bash
# As a merchant (API key): merchantId comes from the key
curl -X POST http://localhost:8083/api/payments \
  -H 'X-Api-Key: sf_live_demo0000_local-demo-key-not-a-secret' \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-001' \
  -d '{"amount":1250.50,"currency":"EUR","customerId":"cust_1001","country":"IE"}'

# As staff: log in, then send the token
TOKEN=$(curl -s -X POST http://localhost:8083/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"admin-change-me"}' | jq -r .token)
AUTH="Authorization: Bearer $TOKEN"

curl -H "$AUTH" 'http://localhost:8083/api/payments?status=FraudReview&page=1&pageSize=20'
curl -H "$AUTH" http://localhost:8083/api/payments/{id}/timeline
curl -H "$AUTH" -X POST http://localhost:8083/api/payments/{id}/review \
  -H 'Content-Type: application/json' -d '{"decision":"APPROVE","note":"known customer"}'
curl -H "$AUTH" http://localhost:8083/api/stats
curl -H "$AUTH" -X POST http://localhost:8083/api/merchants -H 'Content-Type: application/json' -d '{"id":"acme","name":"Acme"}'
curl -H "$AUTH" http://localhost:8083/api/audit
```

## Performance

50 payments/s sustained on a laptop: API p95 **3.6 ms**, time-to-settle p95 **1.75 s**, 0 errors, max consumer lag 1.
See [docs/performance.md](docs/performance.md), including the three pipeline bottlenecks the first load test found and how they were fixed.

## Developer commands

```bash
make up            # build + start everything
make test          # unit + end-to-end tests
make load-test     # k6: 50 payments/s for 60s (RATE=… DURATION=…)
make logs          # follow service logs
make migration name=AddThing
make reset         # wipe local data (asks first)
```

## Local development without Docker for .NET

```bash
docker compose up -d postgres redpanda jaeger      # infrastructure only
dotnet run --project src/SettleFlow.Api            # http://localhost:5080
dotnet run --project src/SettleFlow.FraudWorker    # :5101
dotnet run --project src/SettleFlow.SettlementWorker  # :5102
dotnet run --project src/SettleFlow.NotificationWorker # :5103
cd frontend && npm install && VITE_PROXY_TARGET=http://localhost:5080 npm run dev
```

`appsettings.Development.json` points at `localhost:5432` and `localhost:19092`.

## Tests

| Suite | Command | What it covers |
|---|---|---|
| Backend unit | `make test-unit` | Fraud engine, acquirer and circuit breaker, ledger and fees, webhooks and SSRF guard, validation, CSV safety, **event contract snapshots** |
| End-to-end | `make test-e2e` | API and all workers in-process against **real Postgres + Redpanda** (Testcontainers): every payment flow, security, money, live stream, ETags, OpenAPI contract snapshot |
| **Chaos** | (part of e2e) | Postgres frozen mid-flight, Kafka frozen mid-flight, API accepting payments while Kafka is down. Asserts **every payment settles and none is dead-lettered** |
| Frontend unit | `cd frontend && npm test` | Formatting and permissions (`node --test`, zero test dependencies) |
| Browser | `make test-ui` | Playwright in Chrome against the running stack: login, create a payment and watch it settle live, role-based UI, every page renders without errors |
| Load | `make load-test` | k6, see [docs/performance.md](docs/performance.md) |

`make lint` runs the formatting checks (`dotnet format`, Prettier), the type check, and the dashboard ⇄ API contract check. CI runs all of these except the browser and load tests, which need the full stack.

`make seed` fills a running stack with realistic demo data (mixed outcomes, manual captures, refunds) and respects the API's rate limits.

The integration suite runs the API and every worker in-process against real containers. It covers the settle, decline, review, acquirer decline and DLQ/replay flows, plus idempotency (including 10 concurrent requests with one key), validation, paging and health. CI runs both suites, checks that migrations match the model, builds the dashboard, and builds all Docker images, pushing them to GHCR on `main`.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Kafka:MaxHandlerAttempts` | 5 | Attempts before dead-lettering (non-transient errors) |
| `Kafka:TopicPartitions` | 12 | Partitions for auto-created topics |
| `Outbox:PollInterval` | 200 ms | Outbox poll interval when idle |
| `Outbox:Retention` | 7 days | Published outbox rows are purged after this |
| `Fraud:*` | see `FraudOptions` | Thresholds, scores, low-risk countries, velocity window |
| `Settlement:TransientFailureRate` | 0.1 | Simulated acquirer timeout probability |
| `RateLimiting:PaymentsPerWindow` / `WindowSeconds` | 60 / 10 | POST rate limit per IP |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Enables trace export (Jaeger in compose) |

## Project structure

```text
src/
  SettleFlow.Api                HTTP API (payments, review, stats, notifications, dead letters)
  SettleFlow.Contracts          Event contracts and topic names
  SettleFlow.Infrastructure     EF Core model + migrations, outbox, Kafka consumer base, telemetry, service defaults
  SettleFlow.Processing         Fraud scorer, acquirer gateway, all event handlers
  SettleFlow.*Worker            Thin hosts for each worker (health + metrics endpoints)
frontend/                       React + Vite dashboard (nginx in Docker)
tests/                          Unit and Testcontainers end-to-end tests
docs/architecture/              ADRs and event catalog
monitoring/                     Prometheus config, Grafana datasource + dashboard
k8s/, helm/                     Kubernetes manifests and Helm chart
```

## Not included (yet)

A real acquirer integration, schema-registry contracts (Avro/Protobuf), and multi-acquirer routing. The structure is designed so each can be added without changing the payment API contract.
