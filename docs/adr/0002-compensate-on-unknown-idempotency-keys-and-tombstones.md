# 0002: Compensate on unknown outcome, with stable idempotency keys and tombstoning undo

- Status: accepted
- Date: 2026-11-13

## Context

The textbook saga registers a step's compensation after the step succeeds: "if the payment went through, remember to
refund it". That rule assumes the orchestrator always learns whether a step took effect. It does not:

- **Lost response.** The provider captured the money, the answer never arrived. Temporal retries the activity; if the
  provider then stays down until retries run out, the workflow has seen only failures for a capture that happened.
- **Retries without a stable key.** Each retry is a new request to the provider. One lost response becomes two charges.
- **Zombie attempt.** An attempt hangs past its start-to-close timeout (a stalled connection, a paused worker). Temporal
  moves on, the saga gives up and refunds, and only then does the old attempt reach the provider and capture. The refund
  ran before the charge it was meant to undo.

All three leave money with the merchant while the workflow reports "compensated".

## Options considered

1. *Register after success* (the textbook shape). Correct only when every outcome is known. It is not.
2. *Query the provider before compensating* ("did order X get charged?"). Needs a read API with read-your-writes
   semantics on every downstream, and still loses to the zombie that arrives after the query.
3. *Distributed transaction / two-phase commit across the four systems.* Not offered by real payment or courier APIs.
4. *Compensate on unknown* (chosen), three parts that only work together:
   - **register the compensation before the step is scheduled**, so every step that *may* have run is undone;
   - **one stable idempotency key per order and step** (`{orderId}:payment`), identical across retries, replays and the
     compensation, so a retry returns the original result and the undo names the exact effect;
   - **undo that is safe before the effect**: the undo first writes a tombstone for the key, then undoes whatever exists.
     A forward call that arrives later finds the tombstone and refuses (`AlreadyCompensated`, non-retryable). Undoing
     something that never happened is a recorded no-op.

## Decision

Option 4. In the simulators (`DownstreamSystems`) the tombstone insert and the undo run in one transaction, tombstone
first: a capture that has not committed yet blocks on the key's unique index, then sees the tombstone and refuses
itself. A key reused with a different amount is refused (`IdempotencyConflict`) rather than silently returning the old
result.

Each part is proven necessary by removing it. The lab workflow runs the same engine with one defence off, against the
same seeded fault plans (140 sagas per variant, 7 shapes: none, reject, transient burst, lost response, lost response
then outage, zombie then outage, failing undo, 20 each, seed 27). Full results are in
[benchmarks.md](../benchmarks.md#fault-campaign):

| Variant | Inconsistent outcomes (of 140) | Broken shapes | Violations |
|---|---|---|---|
| Correct (production) | **0** | none | none |
| Compensate after success | 40 | lost then outage 20/20, zombie then outage 20/20 | 26 leaked charges, 7 platform orders left accepted, 5 orphan tickets, 2 orphan couriers |
| No idempotency key | 25 | zombie then outage 20/20, lost response 5/20 | 20 leaked charges, 5 double charges (each also the wrong amount) |
| No tombstone | 20 | zombie then outage 20/20 | 20 leaked charges |

Two results were not obvious before measuring. Without stable keys, the tombstone stops working too: the refund writes
it under the base key while the zombie captures under its own attempt key, so every zombie leaked. And without keys
the 15 lost responses on platform, kitchen and courier steps write a second row under a second key (a second ticket,
a second dispatch); the checker judges money and effect state, not row counts, so only the 5 payment cases show up as
violations. The real damage of that variant is larger than its count.

The same three failures are also fixed integration tests (`SagaDefenceTests`), and the concurrent charge-versus-refund
race is run 60 times against PostgreSQL in `DownstreamTests`.

## Consequences

- Every forward step gets a compensation call even when it was rejected outright; the undo is a cheap tombstone insert.
  Unwinds after a rejection therefore show the failed step in the compensation list, by design.
- Real downstream APIs must offer three things: idempotency keys that outlive the retry and compensation window,
  "void or cancel by key" that works before the original call is seen, and refusal of a key reused with another body.
  Where an API lacks the second, the saga needs a reconciliation sweep for late captures; that is not built here.
- Tombstones and keys are retained forever in the simulators. Real retention policies bound how late a zombie may land.

## Sources

- Stripe API, "Idempotent requests" (keys identify a request across retries; reuse with different parameters is an
  error): https://docs.stripe.com/api/idempotent_requests
- Temporal, "Saga pattern": https://docs.temporal.io/design-patterns/saga-pattern
- Temporal, activity timeouts and retries (start-to-close timeout; an attempt may still be running after it):
  https://docs.temporal.io/encyclopedia/detecting-activity-failures
- Microsoft Azure Architecture Center, "Saga distributed transactions pattern":
  https://learn.microsoft.com/azure/architecture/patterns/saga
