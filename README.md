# SettleFlow

> Production-oriented event-driven payment processing and fraud-screening platform built with .NET 10, PostgreSQL, Kafka-compatible Redpanda, Docker, Kubernetes, OpenTelemetry, Prometheus, Grafana and React.

[![CI](https://github.com/shrutii09/SettleFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/shrutii09/SettleFlow/actions/workflows/ci.yml)

---

## Overview

SettleFlow is a distributed payment-processing system designed around asynchronous event-driven workflows.

A payment enters through the ASP.NET Core API, is persisted to PostgreSQL, and is published through a transactional outbox to Redpanda/Kafka. Independent workers then process fraud screening, settlement and notification workflows.

The project focuses on backend engineering concerns such as:

- reliable event publication
- idempotent payment creation
- asynchronous processing
- fraud decisioning
- payment state transitions
- retries and dead-letter handling
- observability
- containerized deployment
- automated testing

---

## Architecture

```text
                         ┌──────────────────┐
                         │  React Dashboard  │
                         └────────┬─────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │   Payments API   │
                         │   ASP.NET Core   │
                         └────────┬─────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │    PostgreSQL    │
                         │ Payment + Outbox │
                         └────────┬─────────┘
                                  │
                         Transactional Outbox
                                  │
                                  ▼
                       ┌─────────────────────┐
                       │   Redpanda / Kafka  │
                       └──────┬──────┬───────┘
                              │      │
                 ┌────────────┘      └────────────┐
                 ▼                                ▼
        ┌─────────────────┐              ┌─────────────────┐
        │   Fraud Worker  │              │Settlement Worker│
        └────────┬────────┘              └────────┬────────┘
                 │                                │
                 └──────────────┬─────────────────┘
                                ▼
                     ┌────────────────────┐
                     │ Notification Worker│
                     └────────────────────┘

       OpenTelemetry → Prometheus → Grafana
```

## Payment Created

```text

      │
      ▼
Fraud Screening
      │
 ┌────┴───────────────┐
 ▼                    ▼
APPROVED             REVIEW / DECLINED
 │                    │
 ▼                    ▼
Settlement          Payment Failed
 │
 ▼
Settlement Completed
 │
 ▼
Notification
