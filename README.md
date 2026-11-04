# SettleFlow

An event-driven payment processing and fraud-screening platform built with ASP.NET Core (.NET 10), PostgreSQL, Kafka-compatible Redpanda, OpenTelemetry and React.

A payment goes through risk scoring, optional manual review, settlement with a (simulated) acquirer, and customer notification. Each step is an independent service connected only by Kafka events, with exactly-once *business effects* on top of at-least-once delivery.

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
PendingFraud ─► FraudApproved ─► Settled
     │               └────────► SettlementFailed   (acquirer declined)
     ├─► FraudReview ─(approve)─► FraudApproved ─► …
     │        └─────(reject)──► Declined
     └─► Declined                                  (risk score ≥ 50)
```

## Reliability features

| Problem | How SettleFlow handles it |
|---|---|
| DB write succeeds but Kafka publish fails (or vice versa) | **Transactional outbox**: events are written in the same transaction as the state change, and published afterwards. |
| Several publishers / API replicas | Outbox rows are claimed with `FOR UPDATE SKIP LOCKED`; a row is only sent after every earlier row for the same topic and key, so **per-payment ordering** holds even when a publish is retried. Failed publishes back off exponentially. |
| Kafka delivers a message twice | **Inbox** per consumer `(Consumer, EventId)`, written in the handler's transaction. Handlers also check the payment's state, so they are idempotent. |
| Client retries a POST | `Idempotency-Key` header stored with a unique constraint and a hash of the request. Same key and body: the same payment is returned (`Idempotent-Replayed: true`). Same key with a different body: `409`. Race-safe under concurrency. |
| Database or broker briefly down | Transient errors are retried with backoff and **never skip** the message. |
| A message that keeps failing (poison) | Retried `Kafka:MaxHandlerAttempts` times (default 5), then **dead-lettered**: stored in `DeadLetters`, published to `payments.dlq`, and **replayable** from the dashboard or API. |
| Two people review the same payment | Optimistic concurrency (Postgres `xmin`): the second reviewer gets `409`. |
| Request floods | Per-IP fixed-window rate limit on `POST /api/payments`. |

## Features

- **Fraud engine** with configurable rules (`Fraud` config section): high amount, country risk, round amounts, and **velocity** (many payments from one customer in a short window).
- **Manual review queue**: REVIEW-scored payments wait for a human to approve or reject them.
- **Simulated acquirer**: realistic latency, random transient outages (retried automatically), declines for blocked merchants and unsupported currencies, and a merchant that is always down (`merchant_outage`) to demonstrate the DLQ.
- **Payment timeline**: every step is recorded and shown in the dashboard.
- **Observability**: one **distributed trace per payment** across all services (trace context travels in Kafka headers, viewable in Jaeger); Prometheus metrics from every service; a provisioned Grafana dashboard; JSON health checks (`/health/live`, `/health/ready`).
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
| Traces (Jaeger) | http://localhost:16686 |
| Grafana | http://localhost:3001 |
| Prometheus | http://localhost:9090 |

Migrations and Kafka topics are applied automatically at startup. If you have a database volume from a version before migrations were introduced, reset it once with `docker compose down -v`.

### Try the scenarios

The dashboard's **Create payment** card has presets for each of these:

| Scenario | Input | Result |
|---|---|---|
| Low risk | 1249.50 EUR, IE | Settled |
| High amount | 6000 EUR, DE | Review queue → approve/reject |
| Risky | 7500 EUR, US | Declined (score 70) |
| Blocked merchant | merchant `blocked_shop` | SettlementFailed |
| Acquirer outage | merchant `merchant_outage` | 5 retries → dead-letter queue → replay |

### API examples

```bash
curl -X POST http://localhost:8083/api/payments \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-001' \
  -d '{"amount":1250.50,"currency":"EUR","customerId":"cust_1001","merchantId":"merchant_demo","country":"IE"}'

curl 'http://localhost:8083/api/payments?status=FraudReview&page=1&pageSize=20'
curl  http://localhost:8083/api/payments/{id}/timeline
curl -X POST http://localhost:8083/api/payments/{id}/review \
  -H 'Content-Type: application/json' -d '{"decision":"APPROVE","reviewer":"alice","note":"known customer"}'
curl  http://localhost:8083/api/stats
curl  http://localhost:8083/api/dead-letters
curl -X POST http://localhost:8083/api/dead-letters/{id}/replay
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

```bash
dotnet test tests/SettleFlow.Tests              # unit tests: fraud rules, acquirer, validation (no Docker)
dotnet test tests/SettleFlow.IntegrationTests   # end-to-end: real Postgres + Redpanda via Testcontainers
```

The integration suite runs the API and every worker in-process against real containers. It covers the settle, decline, review, acquirer decline and DLQ/replay flows, plus idempotency (including 10 concurrent requests with one key), validation, paging and health. CI runs both suites, checks that migrations match the model, builds the dashboard, and builds all Docker images, pushing them to GHCR on `main`.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Kafka:MaxHandlerAttempts` | 5 | Attempts before dead-lettering (non-transient errors) |
| `Kafka:TopicPartitions` | 3 | Partitions for auto-created topics |
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

Authentication and authorization, a real acquirer integration, schema-registry contracts (Avro/Protobuf), and outbound merchant webhooks. The structure is designed so each can be added without changing the payment API contract.
