# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-13

### Added

- Delivery order saga on Temporal (.NET SDK 1.21.1): platform accept, kitchen ticket, payment capture, courier dispatch,
  with compensations unwound newest-first. Each compensation is registered before its step is scheduled, so a step
  that took effect without the workflow seeing success is still undone.
- Deterministic, Temporal-free domain core: order validation, idempotency keys, compensation log that keeps unwinding
  past a failed undo, fault model, and the consistency invariant every finished saga is judged by.
- Downstream simulators on PostgreSQL 17 (platform, kitchen, payment with an append-only ledger, courier): idempotent
  forward calls, key reuse with another body refused, undo safe before the effect via tombstones. Database-backed fault
  injection (reject, transient, lost response, slow zombie, pending courier) for forward steps and compensations.
- Retry policy split: bounded forward retries, unbounded compensations, non-retryable business errors. Pending courier
  dispatch waits on a durable timer for an assignment signal. Customer cancellation as a validated workflow update.
  Operator cancellation still unwinds the order.
- Saga v2: one idempotent customer notice after an unwind, behind `Workflow.Patched`.
- Golden event histories (7 saga paths, v1 and v2) replayed without a server in the unit suite; the unpatched v2 change
  and a reordered-steps refactor are shown to fail replay.
- Lab workflow running the same engine with one defence removed (compensate after success, no idempotency key, no
  tombstone) for a seeded fault campaign.
- HTTP API (place, inspect, cancel, courier webhook, simulation-only downstream truth) with one saga per order id,
  body-hash conflict detection, API key, problem details, OpenAPI + Scalar, health probes, Serilog JSON and
  OpenTelemetry traces through Temporal's tracing interceptor. One image with migrate, api, worker and demo roles.
- `docker compose up` demo: PostgreSQL, Temporal CLI dev server, API, two worker replicas and eight scenarios checked
  against the downstream truth.
- Tests: unit, FsCheck property, golden replay, and Testcontainers integration tests including hard kills of worker
  processes mid-saga and a random fault campaign.
- Benchmark harness: latency vs direct calls, closed-loop throughput, unwind latency, fault campaign per variant, replay
  cost, crash recovery with default and tuned sticky-queue timeout.
- CI (build, vulnerable-package audit, format, tests with coverage, container image, compose demo) and CodeQL.

[Unreleased]: https://github.com/hekimcan206/order-saga-temporal/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/order-saga-temporal/releases/tag/v1.0.0
