# Event Catalog

Every message is an `EventEnvelope<T>(EventId, EventType, Version, OccurredAt, Data)` serialized as JSON. The Kafka key is the payment id, so all events for one payment land on the same partition, in order. A W3C `traceparent` header carries the trace context.

| Topic | Payload | Producer | Consumers | Purpose |
|---|---|---|---|---|
| `payments.created` | `PaymentCreated` | API | FraudWorker | Start risk evaluation |
| `payments.fraud.completed` | `FraudCompleted` (Decision APPROVED/REVIEW/DECLINED, `ReviewedBy` when manual) | FraudWorker, API (manual review) | SettlementWorker (APPROVED only), ReviewNotificationWorker (REVIEW only) | Route the payment after scoring |
| `payments.settlement.completed` | `SettlementCompleted` | SettlementWorker | SettlementNotificationWorker | Confirm successful settlement |
| `payments.failed` | `PaymentFailed` (Stage: fraud / review / settlement) | FraudWorker, API, SettlementWorker | FailureNotificationWorker | Terminal failure |
| `notifications.requested` | `NotificationRequested` (SETTLED / FAILED / REVIEW_REQUIRED) | Notification workers | NotificationSinkWorker | Deliver a notification (stored in `Notifications`) |
| `payments.dlq` | `DeadLetterRecorded` | Any consumer | (ops tooling) | A consumer gave up on a message; details in `DeadLetters` |

Consumer group per handler: `{Kafka:GroupId}.{HandlerClassName}`.
