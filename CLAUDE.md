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
- **Handlers must be idempotent on state**: check the payment's current `Status` before acting.
- **Exceptions**: throw for retryable or business failures. After `MaxHandlerAttempts` the message goes to `DeadLetters` and `payments.dlq`. Infrastructure errors (`IsTransient`) retry forever.
- `Payment.Version` maps to `xmin` (optimistic concurrency). `Status` is stored as a string.

### Demo hooks (`SettlementOptions`)
`merchant_outage` always throws (ends in the DLQ), `blocked_*` merchants are declined, and only EUR/GBP/USD are supported. Random transient failures run at `TransientFailureRate` (set to 0 in the integration tests).

### Other
- In Docker, nginx (`frontend/nginx.conf`) proxies `/api` and `/health` to the API, so the dashboard uses relative URLs. CORS is only needed for the Vite dev server.
- Traces are exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The outbox polling loop suppresses instrumentation so it doesn't flood Jaeger.
- The Grafana dashboard is `monitoring/grafana/provisioning/dashboards/settleflow.json`. It uses Prometheus metric names like `settleflow_fraud_decisions_total`, generated from the instruments in `Telemetry.cs`.
