# 0004: Workflow versioning with patch markers, guarded by golden replay histories

- Status: accepted
- Date: 2026-11-13

## Context

A saga can run for minutes (courier wait) or, while a refund keeps failing, for days. When new workflow code deploys,
running sagas continue on it: the new worker rebuilds each one by replaying its event history through the new code. If
the new code issues a different command than the history recorded (a new activity, a reordered step), the replay fails
with a non-determinism error and that saga is stuck until someone intervenes. Ordinary unit tests do not catch this:
the new code is correct for new sagas.

v2 of this saga adds one behaviour: after an unwind, tell the customer (one idempotent notice saying whether money is
coming back).

## Options considered

1. *Ship it and hope no saga is mid-unwind during the deploy.* Fails exactly when it matters: at peak, during an outage
   that is unwinding many orders.
2. *A new workflow type (`OrderSagaWorkflowV2`).* Clean for new sagas, but every caller (API, signals, updates) must know
   which type an order runs on, and the old type lives on until its last saga closes.
3. *Worker versioning / pinned deployments.* Each saga stays on the build that started it. Strong, but it needs the
   deployment and versioning features of the server and an extra deployment dimension; it is the better choice for large
   rewrites.
4. *Patch marker* (chosen): `if (Workflow.Patched("notify-customer-after-unwind")) { notify }`. A new saga records a
   marker and runs the new code; replaying an old history, `Patched` returns false and the old path is taken. Later, once
   no pre-patch saga can still be running, the call is replaced by `DeprecatePatch` and finally removed (Temporal's
   documented three-step process).

And to *prove* compatibility rather than reason about it:

5. *Golden histories* (chosen): one recorded event history per saga path (completed, rejected, lost response then outage,
   pending courier, customer cancel, courier timeout, operator cancel), from real runs on a Temporal server, committed
   under `tests/OrderSaga.UnitTests/Histories/v1` and `/v2`. The unit suite replays all of them through
   `WorkflowReplayer`: no server, p50 4.1 ms for a 29-event history and 8.8 ms for a 68-event one (measured in
   [benchmarks.md](../benchmarks.md)).

## Decision

Patch marker for the v2 change, golden histories for every release. The recording test (`HistoryRecordingTests`) replays
fresh histories on every integration run and writes them out when `ORDER_SAGA_RECORD_HISTORIES` is set. Recorded files
are stripped of worker identities and stack traces, which carry host names and local paths; neither takes part in replay.

The tests make the argument concrete:

- the patched v2 code replays all 14 histories (7 v1, 7 v2);
- the same v2 change *without* the patch fails all 5 unwound v1 histories with `WorkflowNondeterminismException`, while
  the v1 history that completed still replays (it never reaches the changed code);
- a plausible refactor that confirms the kitchen ticket before the platform accepts fails on the first history.

## Consequences

- A change that would strand running sagas fails the fast unit suite, before any deploy.
- New saga paths need a new golden history; the recording test makes that a one-command job.
- The v1 histories stay in the repository until the patch is deprecated; removing them is part of that later change.
- Golden histories are coupled to the payload shape: renaming a field of `PlaceOrder` or `SagaResult` is also a
  versioning change, and the replay tests will flag it.

## Sources

- Temporal, ".NET SDK versioning (patching)": https://docs.temporal.io/develop/dotnet/workflows/versioning
- Temporal, "Patching": https://docs.temporal.io/patching
- Temporal, ".NET testing suite (replay)": https://docs.temporal.io/develop/dotnet/best-practices/testing-suite
- Temporal, "Safe deployments": https://docs.temporal.io/develop/safe-deployments
- `WorkflowReplayer` API: https://dotnet.temporal.io/api/Temporalio.Worker.WorkflowReplayer.html
