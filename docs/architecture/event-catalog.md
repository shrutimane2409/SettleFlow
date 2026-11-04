# Event Catalog

Every message is an `EventEnvelope<T>(EventId, EventType, Version, OccurredAt, Data)` serialized as JSON. The Kafka key is the payment id, so all events for one payment land on the same partition, in order. A W3C `traceparent` header carries the trace context.

| Topic | Payload | Producer | Consumers | Purpose |
|---|---|---|---|---|
| `payments.created` | `PaymentCreated` | API | FraudWorker | Start risk evaluation |
| `payments.fraud.completed` | `FraudCompleted` (Decision APPROVED/REVIEW/DECLINED, `ReviewedBy` when manual, `AwaitingCapture` for manual capture) | FraudWorker, API (manual review) | SettlementWorker (APPROVED only), ReviewNotificationWorker (REVIEW only) | Route the payment after scoring |
| `payments.settlement.completed` | `SettlementCompleted` | SettlementWorker | SettlementNotificationWorker | Confirm successful settlement |
| `payments.failed` | `PaymentFailed` (Stage: fraud / review / settlement) | FraudWorker, API, SettlementWorker | FailureNotificationWorker | Terminal failure |
| `notifications.requested` | `NotificationRequested` (SETTLED / FAILED / REVIEW_REQUIRED / AUTHORIZED / VOIDED / REFUNDED / REFUND_FAILED) | Notification workers | NotificationSinkWorker (stores it), WebhookEnqueueWorker (creates merchant webhook deliveries) | Fan-out of notifications |
| `payments.capture.requested` | `PaymentCaptureRequested` | API (capture endpoint) | CaptureWorker | Settle an authorized payment |
| `payments.voided` | `PaymentVoided` | API, retention job (expiry) | VoidNotificationWorker | Authorization released |
| `refunds.requested` | `RefundRequested` | API | RefundWorker | Send a refund to the acquirer |
| `refunds.completed` | `RefundCompleted` | RefundWorker | RefundNotificationWorker | Refund outcome |
| `payments.dlq` | `DeadLetterRecorded` | Any consumer | (ops tooling) | A consumer gave up on a message; details in `DeadLetters` |

Consumer group per handler: `{Kafka:GroupId}.{HandlerClassName}`.
