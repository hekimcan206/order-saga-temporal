# 0005: Temporal CLI dev server for tests and the demo; downstream systems simulated on PostgreSQL

- Status: accepted
- Date: 2026-11-13

## Context

The saga needs a Temporal service and four downstream systems. Tests must judge outcomes by what the downstream systems
recorded, inject the failures that make sagas hard, and run on a shared laptop and on CI runners without much memory.

## Options considered

**Temporal**

1. *Full server via docker compose* (frontend, history, matching, worker services plus PostgreSQL or Cassandra, UI).
   Closest to production, but several containers and about a gigabyte of memory for a test suite.
2. *SDK test environments.* `WorkflowEnvironment.StartLocalAsync` downloads the CLI binary at test time;
   `StartTimeSkippingAsync` downloads the Java test server. Both fetch an executable on first use, outside the pinned
   dependency set.
3. *Temporal CLI dev server image* (chosen): `temporalio/temporal:1.9.1` running `server start-dev`, a single process
   with the real server (1.32.0) and in-memory persistence. About 75 MB of memory idle, starts in seconds, pinned by tag,
   and the same image serves Testcontainers, the benchmarks and `docker compose up`.

**Downstream systems**

1. *Mocks in the activity layer.* Fast, but they test the workflow against a model of failure, and "did the money come
   back" becomes "was the mock called".
2. *Simulators on PostgreSQL* (chosen): platform orders, kitchen tickets, payments with an append-only ledger, courier
   dispatches. Each enforces idempotency keys and tombstones like a real API would (ADR 0002). The consistency check reads
   these tables, so a test asserts on money and tickets, not on calls.
3. *Fault injection in the database.* Rules (`reject`, `transient`, `lost-response`, `slow`, `pending`, for forward
   steps or compensations, N times) live in `sim.fault_rules` and are consumed under a row lock. They work across
   processes, which the crash tests and the compose demo need: the fault is set by the test or the API, the activity
   runs on whichever worker picks it up.

## Decision

As chosen above. Workers and the API connect as `saga_app` (DML on the simulator schema only; the ledger is
append-only); migrations run as `saga_migrator` in a one-shot process. The fault endpoint and the downstream-truth
endpoint exist only when `Api:SimulationEnabled` is true.

## Consequences

- The dev server keeps state in memory: restarting it loses every workflow. It is a development and test server, not a
  deployment target; production needs a Temporal cluster with real persistence or Temporal Cloud.
- Benchmarks measure this single-process server capped at 2 CPUs, not a production cluster. Absolute numbers are a
  floor for the saga logic plus Temporal round trips, not a capacity plan.
- The simulators are realistic about idempotency and failure, not about latency: a downstream call is a local
  PostgreSQL statement (`Downstream:Latency` can add a fixed delay).

## Sources

- Temporal, "Install and configure the CLI" and "Run a development server":
  https://docs.temporal.io/cli/setup-cli, https://docs.temporal.io/develop/run-a-development-server
- Docker Hub, `temporalio/temporal`: https://hub.docker.com/r/temporalio/temporal
- Temporal, ".NET testing suite": https://docs.temporal.io/develop/dotnet/best-practices/testing-suite
