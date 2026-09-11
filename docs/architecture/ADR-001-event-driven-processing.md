# ADR-001: Event-driven payment processing

## Decision
Use Kafka-compatible Redpanda as the event backbone and PostgreSQL as the system of record.

## Why
Payment processing crosses independently deployable boundaries. Events allow fraud, settlement and notification to evolve independently while preserving an auditable payment lifecycle.

## Reliability model
The system accepts Kafka's at-least-once delivery semantics and achieves an exactly-once *business effect* through idempotency and inbox records. Payment state changes and outbound events are committed transactionally through the outbox.

## Trade-offs
This introduces eventual consistency and operational complexity. Those costs are intentional because they demonstrate patterns used in distributed financial systems.
