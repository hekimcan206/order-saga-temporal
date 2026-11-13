# 0001: An orchestrated saga on Temporal, not choreography or a hand-rolled state machine

- Status: accepted
- Date: 2026-11-13

## Context

A delivery order touches four systems that share no transaction: the ordering platform accepts the order, the kitchen
confirms a ticket, the payment provider captures the money, the courier platform dispatches a courier. If any step
fails, the steps already done must be undone, in reverse order: refund the payment, cancel the ticket, tell the platform.
This is the saga from Garcia-Molina and Salem (1987): a long-lived business transaction split into local transactions,
each with a compensating transaction that semantically undoes it.

What makes it hard is not the happy path. It is that every step can fail *ambiguously* (the effect happened, the answer
was lost), workers crash in the middle, compensations fail too, a courier assignment arrives minutes later, and the
customer can cancel at any time. The saga's progress has to survive all of that.

## Options considered

1. **Choreography**: each service reacts to the previous one's event (outbox + broker). No central coordinator, but the
   saga's logic is spread over four consumers; "which steps ran for order X, and what is left to undo" has no single
   answer, and compensation order is an emergent property of event handlers. Hard to test as a whole.
2. **Hand-rolled orchestrator**: a state machine row per order in PostgreSQL, a poller that advances it, retries with
   backoff, timers for the courier wait, a lease so two pollers do not run one order. Every one of those pieces is a
   project of its own (see outbox-lab and webhook-relay in this portfolio), and each is a place to get durability wrong.
3. **A message-bus saga framework** (for example MassTransit state machines). Durable, but the saga is a declarative
   state machine driven by messages: sequential logic with compensation becomes event/state tables, and timers and
   retries are configured around it rather than written in it.
4. **Temporal workflow** (chosen). The saga is ordinary sequential C#: call step 1, step 2, ...; on failure, unwind the
   compensations. Temporal persists every decision in the workflow's event history and replays it after a crash, so the
   code *is* the durable state machine. Activity retries with backoff, durable timers, signals, updates and queries are
   primitives. Temporal's own documentation presents the saga as a workflow with compensations collected as the steps
   run.

## Decision

One workflow per order (`OrderSagaWorkflow`, id `order-{orderId}`), four activities for the forward steps and four for
their compensations, a deterministic compensation log (`CompensationLog` in `OrderSaga.Core`, no Temporal dependency)
unwound newest-first. The workflow keeps no database of its own: the event history is the saga log, the current stage
is a workflow query.

The order of steps is the one the brief gives (platform, kitchen, payment, courier). Payment after the kitchen is not
the usual order for a marketplace (an authorisation often comes first), but it is a realistic one for a restaurant that
confirms capacity before charging, and it makes the most interesting unwind (refund + cancel ticket + cancel platform
order) the common failure path.

The saga has no pivot step in the classic sense: the last step (courier dispatch) is itself compensatable, and the order
is finished once a courier is assigned. Customer cancellation is therefore accepted until that moment and rejected after.

## Consequences

- The happy path and every unwind read top to bottom in one file (`OrderSagaEngine`), and the same engine runs four lab
  variants for the fault campaign (ADR 0002).
- Workflow code must be deterministic: no clock, no random, no I/O, no `ConfigureAwait(false)` (the `.editorconfig`
  disables CA2007 for this reason). Changing it is a versioning problem (ADR 0004).
- Temporal becomes infrastructure to run. This repository uses the single-binary dev server for tests and the demo
  (ADR 0005); production needs a Temporal cluster or Temporal Cloud.
- Every saga step pays for durability: measured overhead per saga is in [benchmarks.md](../benchmarks.md).

## Sources

- H. Garcia-Molina, K. Salem, "Sagas", SIGMOD 1987 (via the summaries below).
- Temporal, "Saga pattern": https://docs.temporal.io/design-patterns/saga-pattern
- Microsoft Azure Architecture Center, "Saga distributed transactions pattern" (compensable, pivot and retriable
  transactions): https://learn.microsoft.com/azure/architecture/patterns/saga
- Temporal .NET SDK: https://github.com/temporalio/sdk-dotnet
