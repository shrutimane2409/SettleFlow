# Performance

Measured with `make load-test` (k6, `loadtest/payments.js`) against the full `docker compose` stack on a single
laptop (Apple Silicon, Docker Desktop): all 14 containers, 1 replica of each service, Postgres and Redpanda on the same machine.

**Workload**: a constant **50 payments/second for 60 s** (3,001 payments) via `POST /api/payments`, spread over 20
merchants (API keys), random amounts of 1–4,999.99 EUR, ~11% from a high-risk country. Each payment goes through
API → outbox → Kafka → fraud scoring → settlement (simulated acquirer: 20–150 ms latency, 10% random timeouts) →
notifications → webhooks.

## Results

| Metric | Result | Target |
|---|---|---|
| API `POST /api/payments` p95 / p99 | **3.6 ms / 7.5 ms** | p95 < 300 ms |
| API errors | **0 / 3,001** | < 1% |
| Time to settle (created → settled), p50 / p95 | **0.22 s / 1.75 s** | 99% < 10 s |
| Max Kafka consumer lag during the run | **1 message** | |
| Pipeline fully drained after the load stopped | **3.0 s** | |
| Payments dead-lettered | **0** | |

All k6 thresholds passed. The fraud and acquirer outcomes were as expected: 2,718 settled, the rest held for review or declined by the fraud rules.

## What the first load test found (and what was fixed)

The first run passed every API threshold (p95 4.4 ms), but the pipeline **did not keep up**:

| | First run | After fixes |
|---|---|---|
| Max consumer lag | 2,718 | 1 |
| Time to settle p50 / p95 | 75 s / 115 s | 0.22 s / 1.75 s |
| Dead-lettered | 38 healthy payments | 0 |

Three problems, all invisible to the API-only metrics and all fixed:

1. **Consumers processed one message at a time.** Settlement makes an acquirer call (~85 ms average) plus about 8 DB
   round-trips, so one worker topped out around 10 payments/s regardless of partition count.
   **Fix:** each consumer runs one ordered *lane per partition* in parallel (per-payment ordering preserved), and topics
   went from 3 to 12 partitions (grown automatically at startup).
2. **The circuit breaker was flaky.** It could trip after only 4 calls, so the simulator's normal 10% failure rate
   occasionally looked like "50% failing" for a merchant, and the breaker opened.
   **Fix:** at least 20 calls in the sampling window before it can trip.
3. **An open circuit burned all retries.** While the breaker was open (15 s), the consumer retried 5 times in about 6 s
   and dead-lettered healthy payments.
   **Fix:** an open circuit now *pauses* the lane (`Kafka:CircuitOpenWait`, up to `Kafka:MaxCircuitWaits`) without
   counting attempts. Only a sustained outage reaches the DLQ. The 38 affected payments were replayed from the DLQ and settled.

These show up in production as the `ConsumerLagHigh` and `SettlementLatencySLO` alerts and the "Time to settle"
panels. That's why they're monitored separately from API latency.

## Reproduce

```bash
make up
make load-test                    # RATE=50 DURATION=60s
make load-test RATE=100 DURATION=2m
```

Watch it live in Grafana → "SettleFlow pipeline" (consumer lag, time to settle) and "SettleFlow SLOs".

Scaling further: throughput scales with partitions × worker replicas (KEDA scales workers on lag in Kubernetes). The
API is stateless behind the HPA, and the outbox publisher is safe to run in every replica (`FOR UPDATE SKIP LOCKED`).
