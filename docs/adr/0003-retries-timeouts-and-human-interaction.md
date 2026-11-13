# 0003: Bounded forward retries, unbounded compensations, and how people talk to a running saga

- Status: accepted
- Date: 2026-11-13

## Context

Temporal retries a failed activity by policy. The policy decides whether a flaky dependency is ridden out or the order
is unwound, and whether a failing refund is retried, abandoned, or escalated. A running saga also has to hear from the
outside: the courier platform reports an assignment minutes later, the customer cancels, support asks "where is order X".

## Options considered

**Retry policy**

1. *One policy for everything.* Either compensations give up as fast as forward steps (a refund abandoned after five
   tries), or forward steps retry forever (an order stuck "in progress" for hours while the kitchen waits).
2. *Split policies* (chosen):
   - forward steps: 200 ms initial interval, x2 backoff, 2 s cap, **5 attempts**, start-to-close 5 s. A dependency that
     stays down for ~3 s turns into an unwind, which is the right outcome for a hot meal;
   - compensations: same backoff with a 10 s cap and **no attempt limit**. A workflow that cannot refund keeps trying and
     stays visible as running (alert on long-running sagas or growing attempt counts) instead of closing as if it had
     cleaned up.
3. *Classify errors.* Activities map downstream answers to `ApplicationFailure` types: `Rejected` (business "no"),
   `AlreadyCompensated` (a late call found a tombstone) and `IdempotencyConflict` (a bug) are non-retryable; `Unavailable`
   (connection refused, answer lost) is retried. Retrying a declined card five times is noise; not retrying a 503 is an
   outage turned into lost orders.

**Courier assignment.** The courier platform may accept a job without a courier. Options: poll its API from an activity;
block an activity until assignment (holds a worker slot for minutes); or *wait in the workflow* for a signal with a
durable timer (chosen): `Workflow.WaitConditionAsync(courier assigned or cancel requested, timeout)`. The timer survives
worker restarts; when it fires, the order unwinds.

**Customer cancellation.** A *signal* is fire-and-forget: the API could not tell the customer whether the cancellation
took effect. A *workflow update* with a validator (chosen) answers: accepted ("will be unwound after the step in flight")
or rejected (empty reason, already unwinding, already finished). A rejected update leaves no event in the history.
Cancellation never interrupts an activity in flight: cancelling a capture mid-call is exactly the ambiguous outcome the
saga is built to avoid, so the flag is checked between steps and during the courier wait.

**Operator cancellation** (Temporal UI or CLI) cancels the workflow's cancellation token. Compensations run on a token
that never fires (`CancellationToken.None` in their activity options), so an operator cancel unwinds the order instead
of abandoning it half-done.

## Decision

As above. Each behaviour has an integration test against a real Temporal server: transient bursts on every step,
retries exhausted after a lost response, a refund that fails three times then loses its answer, pending courier plus
signal, courier timeout, update accepted, update rejected by the validator, operator cancel.

## Consequences

- An order whose payment provider goes fully down is unwound 4.7 s (p50) after it was placed: 3 s of retry backoff plus
  the workflow round trips ([benchmarks.md](../benchmarks.md)). Tune the forward policy per step if a dependency is known to have longer blips.
- A compensation that can never succeed keeps its workflow open forever. That is deliberate; the alternative is silent
  money left with the merchant. Operations needs an alert on saga age.
- **Crash recovery is dominated by a worker setting, not by the saga.** When a worker dies, the workflow task for the
  next step is first offered on that worker's *sticky* queue and only moves to the shared queue after
  `StickyQueueScheduleToStartTimeout`. The .NET SDK default is 10 s (the general Temporal docs describe 5 s). With 5 s
  activity start-to-close that gave 15.8 s (p50 of 5 runs) from kill to completion; with the sticky timeout at 1 s,
  6.9 s ([benchmarks.md](../benchmarks.md); the crash tests show the same split). The host exposes it as `Temporal:StickyQueueTimeout`. A shorter value costs
  more cache misses (full history replays) when a healthy worker is briefly slow.

## Sources

- Temporal, "Sticky Execution": https://docs.temporal.io/sticky-execution
- Temporal .NET API, `TemporalWorkerOptions.StickyQueueScheduleToStartTimeout` (default 10 s, in the SDK XML docs)
- Temporal, "Troubleshoot Workflow and Activity execution failures":
  https://docs.temporal.io/troubleshooting/execution-failures
- Temporal community, "Handling workflow task timeout due to sticky queue task timeout":
  https://community.temporal.io/t/handling-workflow-task-timeout-due-to-sticky-queue-task-timeout/16443
