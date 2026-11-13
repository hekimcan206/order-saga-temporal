# Contributing

Thanks for taking the time to improve order-saga-temporal.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL and the Temporal CLI dev server through Testcontainers;
  `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden replay and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable, make the
hand-written CHANGELOG easy to assemble at release time, and show which version bump a change needs:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing workflow code

Running sagas replay their history through new code. Read [ADR 0004](docs/adr/0004-workflow-versioning-with-patches-and-golden-histories.md)
first. In short:

1. Anything that adds, removes or reorders a command (activity, timer, signal wait, child workflow) goes behind
   `Workflow.Patched("<descriptive-id>")`.
2. `dotnet test --project tests/OrderSaga.UnitTests` replays every golden history; it must stay green.
3. Record histories for the new code: `ORDER_SAGA_RECORD_HISTORIES=<repo>/tests/OrderSaga.UnitTests/Histories/vN
   dotnet test --project tests/OrderSaga.IntegrationTests --filter-class "*HistoryRecordingTests"`, and commit them.
4. Workflow code stays deterministic: no `DateTime.Now`, `Guid.NewGuid()`, `Random`, `Task.Run`, I/O or
   `ConfigureAwait(false)`. Use `Workflow.UtcNow`, `Workflow.NewGuid()`, `Workflow.DelayAsync` and activities.

## Changing downstream behaviour

The simulators in `src/OrderSaga.Downstream` are the contract the saga relies on: idempotent forward calls, undo safe
before the effect, tombstones. A change there needs a test in `DownstreamTests` and a run of the fault campaign
(`SagaDefenceTests` and `dotnet run -c Release --project benchmarks/OrderSaga.Benchmarks -- campaign`).

## Database migrations

Numbered SQL files in `src/OrderSaga.Downstream/Migrations/` (`NNNN_name.sql`), embedded and applied by
`--migrate-only`. Never edit an applied migration: the runner compares checksums and refuses to start. Add a new file.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
