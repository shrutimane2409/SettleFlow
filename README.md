# SettleFlow

A production-oriented, event-driven payment processing and fraud-screening platform built with ASP.NET Core, PostgreSQL, Kafka-compatible Redpanda, Redis, Docker, OpenTelemetry and React.

## Architecture

```text
React Dashboard
      |
      v
Payments API ---- PostgreSQL
      |                |
      |          Transactional Outbox
      |                |
      +----------------+
               |
               v
          Redpanda/Kafka
        /       |       \
       v        v        v
    Fraud   Settlement Notification
       |        |          |
       +--------+----------+
                |
          payment events
```

### Engineering features
- Transactional outbox for reliable event publication
- Idempotency keys on payment creation
- Idempotent Kafka consumers with inbox/deduplication records
- Saga-style payment lifecycle and compensation events
- Retry and dead-letter topics
- Configurable fraud risk scoring
- PostgreSQL persistence
- Redis-ready caching layer
- OpenTelemetry tracing and Prometheus metrics
- Health/readiness endpoints
- React + TypeScript operations dashboard
- Docker Compose local environment
- Kubernetes/Helm deployment scaffolding
- Integration-test foundation with Testcontainers
- GitHub Actions CI

## Run

Prerequisites: Docker Desktop.

```bash
docker compose up --build
```

Then open:
- Frontend: http://localhost:3000
- API: http://localhost:8080/swagger
- Health: http://localhost:8080/health
- Metrics: http://localhost:8080/metrics
- Redpanda console: http://localhost:8081
- Grafana: http://localhost:3001
- Prometheus: http://localhost:9090

The first startup applies the EF Core database automatically.

## API examples

```bash
curl -X POST http://localhost:8080/api/payments \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-001' \
  -d '{"amount":1250.50,"currency":"EUR","customerId":"cust_1001","merchantId":"merchant_demo","country":"IE"}'
```

## Event topics

- payments.created
- payments.fraud.completed
- payments.settlement.completed
- payments.failed
- notifications.requested
- payments.dlq

## Project structure

```text
src/
  SettleFlow.Api              HTTP API + application orchestration
  SettleFlow.Contracts        Event contracts
  SettleFlow.Infrastructure   EF Core, outbox, Kafka, observability
  SettleFlow.FraudWorker      Fraud consumer
  SettleFlow.SettlementWorker Settlement consumer
  SettleFlow.NotificationWorker Notification consumer
frontend/                     React operations dashboard
tests/                        Integration tests
docs/                         Architecture decisions and diagrams
monitoring/                   Prometheus + Grafana
```

## Production evolution

The code is deliberately designed so schema-registry-backed Avro contracts, distributed cache, authentication/authorization, Kubernetes autoscaling, and external payment providers can be introduced without changing the core payment API contract.
