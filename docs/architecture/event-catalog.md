# Event Catalog

| Topic | Producer | Consumer | Purpose |
|---|---|---|---|
| payments.created | Payments API | Fraud | Start risk evaluation |
| payments.fraud.completed | Fraud | Settlement | Route approved payments |
| payments.settlement.completed | Settlement | Notification | Confirm successful settlement |
| payments.failed | Fraud | Notification | Notify about rejected/reviewed flow |
| notifications.requested | Notification | Notification sink | Deliver operational notification |
