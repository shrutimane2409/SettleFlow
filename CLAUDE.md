# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

.NET 10 solution (`SettleFlow.sln`) with central package management (`Directory.Packages.props`: add versions there, not in `.csproj` files). The frontend is React + Vite + TypeScript in `frontend/`.

```bash
docker compose up --build                       # full stack (see README for ports: dashboard 3000, API 8083)
dotnet build SettleFlow.sln
dotnet test tests/SettleFlow.Tests              # unit tests, no Docker
dotnet test tests/SettleFlow.IntegrationTests   # end-to-end via Testcontainers (needs Docker)
dotnet test tests/SettleFlow.Tests --filter "FullyQualifiedName~FraudScorerTests"   # one class
cd frontend && npm install && npm run dev        # Vite dev server; proxies /api to VITE_PROXY_TARGET (default localhost:8083)
```

Schema changes need a migration (dotnet-ef is a local tool: `dotnet tool restore` first):
```bash
dotnet dotnet-ef migrations add <Name> --project src/SettleFlow.Infrastructure --startup-project src/SettleFlow.Infrastructure --output-dir Migrations
```
CI fails if the model and migrations drift (`has-pending-model-changes`). It also builds with `-warnaserror`.

## Architecture

All services share **one Postgres database** through `SettleFlowDbContext` (`src/SettleFlow.Infrastructure/Persistence.cs`). Every service calls `InitializeSettleFlowAsync()` before `Run()`: this applies migrations under a Postgres advisory lock and creates the Kafka topics.

- `SettleFlow.Infrastructure/ServiceDefaults.cs`: `AddSettleFlowDefaults(serviceName)` wires Serilog, EF, Kafka options, the outbox publisher, OpenTelemetry and health checks, and is used by the API and every worker. `MapSettleFlowDefaults()` maps `/health`, `/health/live`, `/health/ready` and `/metrics`.
- `SettleFlow.Processing`: the fraud scorer, the simulated acquirer and **all event handlers**. The `*Worker` projects are thin hosts that call `AddFraudProcessing()` and similar. The integration tests host these handlers in-process.
- `SettleFlow.Api`: endpoints live in `Payments/PaymentEndpoints.cs` and `Operations/OperationsEndpoints.cs`. Validation and the request hash are in `Payments/PaymentModels.cs`.

### Event flow
```
POST /api/payments → Payment(PendingFraud) + IdempotencyRecord + Outbox + timeline   [one SaveChanges]
payments.created         → FraudWorker: score; APPROVED/REVIEW/DECLINED; emits fraud.completed (+ payments.failed if DECLINED)
POST /{id}/review        → API emits fraud.completed APPROVED, or DECLINED + payments.failed
payments.fraud.completed → SettlementWorker (APPROVED and status FraudApproved only) → settlement.completed or payments.failed
                         → ReviewNotificationWorker (REVIEW only) → notifications.requested
settlement.completed / payments.failed → notification workers → notifications.requested → NotificationSinkWorker stores a Notification row
```

### Security (`src/SettleFlow.Api/Security/`)
- Deny-by-default fallback policy. A new endpoint needs `.RequireAuthorization(Policies.X)`, or `.AllowAnonymous()` only for probes. Policies: `Staff`, `Reviewer` (admin+analyst), `Admin`, `PaymentsAccess` (staff+merchant).
- Two schemes picked per request: an `X-Api-Key` header → `ApiKeyAuthenticationHandler` (merchant, cached for 30 s; evict on rotate/disable), otherwise a JWT bearer (staff). Use `Actor.From(httpContext)` to find out who is calling.
- Merchant isolation: payment queries go through `Visible(db, http)`. Any new merchant-facing query must filter by `actor.MerchantId`.
- Record human actions with `db.Audit(...)` **before** `SaveChanges`, so they commit together. `AuditLog` is append-only (DB trigger), so never update rows in it.
- `SecurityBootstrapper` seeds the first admin and, with `Bootstrap:DemoData`, demo users and merchants. Integration tests sign in as admin via `SettleFlowFixture.Client`; use `ClientForUserAsync` or `ClientForApiKey` for other roles.
- `ConnectionStrings:Postgres` and `Auth:JwtSigningKey` (≥32 chars) are required, with no defaults in code. Compose reads them from `.env`.

### Key patterns
- **Emit events only through the outbox**: call `db.Enqueue(topic, key, data)` inside the handler and never produce to Kafka directly. `db.RecordTimeline(...)` adds a timeline entry. Every service runs `OutboxPublisher`, which claims rows with `FOR UPDATE SKIP LOCKED` and publishes a row only when no earlier unpublished row has the same (Topic, Key).
- **New consumer**: subclass `PaymentEventWorker<T>` and implement `HandleAsync(envelope, db, ct)`, which mutates `db` without calling `SaveChanges`. The base class handles inbox dedup keyed by `(Consumer = class name, EventId)`, retries, transient-error waits, dead-lettering and offset storage. **Renaming a handler class changes its inbox key and its consumer group** (`{Kafka:GroupId}.{ClassName}`).
- **Concurrency model**: each consumer runs one sequential *lane per assigned partition*, and lanes run in parallel. So a handler instance may run concurrently for **different payments**, but never for two events of the same payment on the same topic (same key → same partition). Only the poll loop touches the Kafka consumer; lanes report finished offsets back to it. `Kafka:TopicPartitions` (default 12) caps parallelism, and startup grows existing topics to it.
- **Handlers must be idempotent on state**: check the payment's current `Status` before acting.
- **Exceptions**: throw for retryable or business failures. After `MaxHandlerAttempts` the message goes to `DeadLetters` and `payments.dlq`. Infrastructure errors (`IsTransient`) retry forever.
- `Payment.Version` maps to `xmin` (optimistic concurrency). `Status` is stored as a string.

### Payments lifecycle (Phase 2)
- Capture modes: `automatic` (default) settles after fraud approval. `manual` → `Authorized`, then `POST /capture` emits `payments.capture.requested` → `CaptureWorker`. Both paths use `SettlementProcessor`. `FraudCompleted.AwaitingCapture` tells `SettlementWorker` to skip.
- Refunds: the API reserves the amount on `Payment.RefundedAmount` (xmin makes concurrent refunds conflict), then `refunds.requested` → `RefundWorker`. On failure the reservation is released.
- `ISettlementGateway` = `ResilientSettlementGateway` (timeout + per-merchant circuit breaker) wrapping `SimulatedSettlementGateway`. The simulator is idempotent via `AcquirerTransactions`, written in **its own scope** on purpose, because it plays an external system.
- Webhooks: `WebhookEnqueueWorker` (a second consumer of `notifications.requested`) creates `WebhookDeliveries`; `WebhookDispatcher` leases rows (`UPDATE … RETURNING` with SKIP LOCKED), signs with `WebhookSignature`, and sends through the SSRF-guarded `"webhooks"` HttpClient. Secrets are protected with Data Protection (key ring in the `DataProtectionKeys` table, shared by all services, application name `SettleFlow`).
- `RetentionService` (hosted in the API) purges old rows and voids expired authorizations. Tests call `RunOnceAsync` directly, with `Retention:Enabled=false`.
- Policies: `PaymentsWrite` (admin, analyst, merchant) for create/capture/void/refund; `PaymentsAccess` for reads.

### Money (Phase 3)
- Post money movements with `db.PostLedgerAsync(type, reference, currency, description, ct, (account, amount)…)` **inside the same unit of work** as the state change. Debit is +, credit is −, and the lines must sum to 0 (checked in code **and** by a deferred DB trigger at COMMIT). It's idempotent per (type, reference). Account codes come from `LedgerAccounts.*`.
- Ledger tables are append-only (DB triggers). Never update or delete; post a reversing transaction instead.
- `PayoutService` and `ReconciliationService` (in `Finance.cs`) are hosted in the API, each run is single-instance via a Postgres advisory lock, and both are callable on demand from the API. Tests disable the schedules (`Payouts:Enabled=false`, `Reconciliation:Enabled=false`, `Reconciliation:Grace=0`) and call the endpoints.
- `AcquirerTransactions` is "the bank's" data: reconciliation treats it as the external source of truth, and tests tamper with it via SQL to create discrepancies.

### Observability & real-time (Phase 4)
- `PostgresListener.ListenAsync` (LISTEN with reconnect) is woken by triggers (`settleflow_outbox`, `settleflow_payments`; migration `RealtimeNotifications`). `OutboxPublisher` waits on NOTIFY, with `Outbox:PollInterval` as the fallback (5 s in tests on purpose).
- Live stream: `PaymentChangeFeed` (one LISTEN per API instance, fan-out) → `GET /api/stream` (SSE via `TypedResults.ServerSentEvents`). Auth uses a 60 s token with the `settleflow-stream` audience from `POST /api/stream/token`. `OnTokenValidated` enforces that stream tokens only work on the stream, and that the stream only accepts stream tokens.
- New metrics: `settleflow.consumer.lag` (computed per message from cached watermarks), `settleflow.payment.time_to_settle` (explicit buckets via `AddView`). Prometheus 3 stores `le` as `"10.0"`, so match buckets with `le=~"10(\\.0)?"`. Error-ratio rules need `or vector(0)`, otherwise "no errors" shows as "no data".
- Alert/SLO rules live in `monitoring/alerts.yml` (CI validates them with promtool). Dashboards are generated JSON in `monitoring/grafana/provisioning/dashboards/`.
- `Database:MigrateOnStartup=false` makes services wait for migrations; `dotnet SettleFlow.Api.dll --migrate-only` migrates, seeds, and exits (the Helm Job).
- Loki shipping is on when `Loki:Url` is set; `TraceContextEnricher` adds TraceId/SpanId to every log event.

### Demo hooks (`SettlementOptions`)
`merchant_outage` always throws (circuit opens, then the DLQ). `merchant_slow` exceeds the timeout. `blocked_*` merchants are declined, and only EUR/GBP/USD are supported. Random transient failures run at `TransientFailureRate` (set to 0 in the integration tests).

### Other
- In Docker, nginx (`frontend/nginx.conf.template`, rendered by envsubst; `NGINX_RESOLVER`, `API_UPSTREAM`) proxies `/api` and `/health` to the API, re-resolving its address every 10 s, so the dashboard uses relative URLs. CORS is only needed for the Vite dev server.
- Traces are exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The outbox polling loop suppresses instrumentation so it doesn't flood Jaeger.
- The Grafana dashboard is `monitoring/grafana/provisioning/dashboards/settleflow.json`. It uses Prometheus metric names like `settleflow_fraud_decisions_total`, generated from the instruments in `Telemetry.cs`.
