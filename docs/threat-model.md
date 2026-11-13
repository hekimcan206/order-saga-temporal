# Threat model

Scope: the HTTP API, the Temporal workers, the Temporal service, and the simulated downstream database, as run by
`docker compose up` and as they would be deployed. STRIDE-style. All data in this repository is synthetic.

## Assets

| Asset | Why it matters |
|---|---|
| Money movements (payment ledger) | A leaked charge or a double charge is a direct loss and a customer complaint |
| Saga state (Temporal event histories) | The only record of which steps ran for an order and what is left to undo |
| Order content | Store, items, totals, an opaque customer reference. No names, addresses or card data are modelled |
| Fault-injection control | Whoever can inject faults can make orders fail on purpose |
| Workflow code determinism | A bad deploy can strand every running saga (ADR 0004) |

## Actors

- Ordering platform and courier platform (callers of the API: place order, courier webhook).
- Customer support / customer app (cancel, inspect).
- Operator with access to Temporal (UI, CLI).
- An attacker on the network, or with a leaked API key.
- A buggy or malicious deploy of workflow code.

## Threats and mitigations

| STRIDE | Threat | Mitigation in this repository | Residual risk / production note |
|---|---|---|---|
| Spoofing | Anyone calls the API | `X-Api-Key` checked in constant time on every `/api` route; health endpoints are unauthenticated and expose no details | A shared key is demo-grade. Production: per-caller credentials (mTLS or OAuth client credentials), separate scopes for "place order", "courier webhook", "cancel" |
| Spoofing | Forged courier webhook assigns a fake courier | Courier ids are validated; the signal only completes a dispatch the saga itself requested | Production: signed webhooks (HMAC with timestamp, as in webhook-relay) from the courier platform |
| Tampering | Re-sent order with altered items or amount | One workflow per order id; the request body hash is stored in the workflow memo; same id with a different body is a 409 | The hash is over the canonical JSON of the request model, so whitespace changes do not matter but a reordered line list does |
| Tampering | Activity retry or replay double-charges | Stable idempotency key per order and step; payment refuses a reused key with another amount (`IdempotencyConflict`, non-retryable) | Real providers differ in key retention windows; keys must outlive the longest retry and compensation window |
| Tampering | Late (zombie) capture after the refund | Undo writes a tombstone first; the late capture finds it and refuses. Measured in the fault campaign | Requires the provider to support "void by key before capture"; if it does not, the saga needs a reconciliation sweep |
| Repudiation | "The refund never happened" | Append-only payment ledger (`saga_app` has no UPDATE/DELETE on it); the workflow timeline records each compensation and its outcome; Temporal keeps the full history | The dev server keeps history in memory only. Production retention must cover dispute windows |
| Information disclosure | Order data in workflow histories, logs and traces | Only an opaque customer reference is part of the input; validation rejects anything that is not a short id; logs are structured and carry ids, not payloads | Production: a Temporal payload codec (encryption) if inputs ever carry personal data (KVKK); keep the UI behind SSO |
| Information disclosure | Golden history files leak the build machine | Recorder strips worker identities (host names) and failure stack traces (local paths) before writing; test client uses a fixed identity | Review history diffs like code |
| Denial of service | Floods of orders or huge bodies | Validation caps lines (50), quantities (99), totals and id lengths before a workflow exists | No rate limiting in this repository; put one in front of `/api/v1/orders` |
| Denial of service | A downstream outage makes every saga retry at once | Capped exponential backoff; forward steps give up after ~3 s and unwind | Compensations retry without limit by design (ADR 0003); alert on saga age so a stuck refund is seen |
| Elevation of privilege | Fault injection used in production | Faults are accepted only when `Api:SimulationEnabled` is true; the downstream-truth endpoint is also off by default | Ship production images with simulation disabled and the simulator schema absent |
| Elevation of privilege | Workers have broad database rights | Workers and API run as `saga_app` (DML on `sim` only); DDL runs as `saga_migrator` in a one-shot container | Real downstream systems are separate services with their own credentials |
| Elevation of privilege | Temporal dev server reachable from outside | Compose publishes 7233/8233 on loopback only; the dev server has no authentication | Production: Temporal with mTLS and namespace authorisation, or Temporal Cloud |
| (Integrity of the system) | Deploy breaks running sagas | Golden histories replayed in the unit suite; v2 change behind `Workflow.Patched`; the unpatched variant is proven to fail | Patches must be deprecated deliberately, after the last old saga closed |

## Out of scope

Real payment provider integration (PCI DSS), customer identity, authentication of end users, multi-tenant isolation
between restaurants, and Temporal cluster hardening.
