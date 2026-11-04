# ADR-002: Delivery guarantees, retries and dead letters

## Context
The first version committed Kafka offsets only after successful handling, but kept consuming after a failure. The next successful commit moved the offset past the failed message, so it was silently lost. The outbox was published only by the API, by a single unlocked loop, so scaling the API would double-publish, and a failing row could be overtaken by later events for the same payment.

## Decision
1. **Classify failures.** Transient infrastructure errors (Npgsql transient, connection/timeout, Postgres classes 08/53/57P) are retried indefinitely with backoff, and the consumer does not advance. All other errors are retried `MaxHandlerAttempts` times and then dead-lettered.
2. **Dead-letter instead of skip.** A dead-lettered message is stored in `DeadLetters`, announced on `payments.dlq`, and marked in the consumer's inbox in the same transaction. Only then is its offset stored. Replay removes that inbox row and re-publishes the original payload through the outbox.
3. **Offsets** are stored manually (`EnableAutoOffsetStore=false`) after a message is fully handled, and committed in the background. Redelivery after a crash is harmless because of the inbox.
4. **Inbox per consumer** `(Consumer, EventId)`, so several services can consume one topic independently.
5. **Outbox in every service**, claimed with `FOR UPDATE SKIP LOCKED`. A row is eligible only when no earlier unpublished row has the same `(Topic, Key)`, which preserves per-payment ordering across retries and publishers. Failed publishes back off exponentially (capped at 60 s), and published rows are purged after a retention period.
6. **Idempotent handlers.** Each handler checks the payment's current status before acting (e.g. settle only `FraudApproved`), so a replayed or duplicate event cannot move a payment backwards.

## Consequences
- A poison message blocks its partition only for its retry budget (a few seconds), not forever.
- An infrastructure outage pauses processing instead of losing data.
- Ordering is per payment, not global; that's all the domain needs.
- The settlement call happens inside the handler's DB transaction. That's acceptable with a simulated acquirer; a real one would need its own idempotency key (the deterministic settlement reference fills that role).
