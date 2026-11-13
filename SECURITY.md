# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| API authentication | `X-Api-Key` compared in constant time (demo-grade; replace with per-caller credentials in production) |
| Duplicate and altered orders | One workflow per order id; body hash in the workflow memo; same id with another body is rejected (409) |
| Money integrity | Stable idempotency keys, tombstoning refunds, append-only ledger (the app role has no UPDATE/DELETE on it) |
| Fault injection | Accepted only when `Api:SimulationEnabled` is true; off by default |
| Database access | Workers and API run as `saga_app` (DML only). Migrations run as `saga_migrator` in a one-shot process |
| Temporal | Dev server for tests and the demo only, published on loopback; production needs mTLS and namespace auth |
| Input limits | Validated ids, at most 50 lines, quantity 1-99, bounded totals, bounded courier wait, at most 10 faults |
| Committed test data | Synthetic only; golden histories are stripped of host names and local paths |
| Secrets | None in the repository. `.env.example` holds clearly marked development-only values |
