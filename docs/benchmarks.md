# Benchmarks

Every number here was measured on the machine below. Raw output is in [`benchmark-results/`](../benchmark-results).
Latencies are nearest-rank percentiles of observed values; "p50" is the median.

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores, 16 threads), laptop |
| RAM | 16 GB host; Docker Desktop VM with 16 vCPU and 7.6 GiB |
| OS | Windows 11 (26200) host; Docker Desktop with the WSL 2 engine |
| .NET | 10.0 (harness and workers run on .NET 10.0.15 in `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`) |
| Temporal | `temporalio/temporal:1.9.1` dev server (server 1.32.0, in-memory persistence), **capped at 2 CPUs** |
| PostgreSQL | `postgres:17-alpine`, **capped at 2 CPUs** |
| SDK | Temporalio 1.21.1, one in-process worker with default options unless stated |
| Host load | 8 containers running, host CPU 43 % at start, another agent building and testing on the same machine ([host-load.txt](../benchmark-results/host-load.txt)) |

**Where the harness runs matters.** The first run used the harness on the Windows host, talking to the containers
through Docker Desktop's published ports. Every workflow task and activity response then cost about 48 ms, and a saga
took 480 ms (p50). Re-run inside a Linux container on the same Docker network, the same saga takes 250 ms. All headline
numbers below come from the containerised run; the host run is kept in
[`benchmark-results/windows-host/`](../benchmark-results/windows-host) for comparison.

## Results

### Latency: what durability costs per saga

Sequential happy-path sagas (300 after 30 warm-up), against the same four downstream calls made directly in process.

| | p50 | p95 | p99 |
|---|---|---|---|
| Saga on Temporal (4 steps, 4 activities, 5 workflow tasks) | **249.9 ms** | 254.5 ms | 258.2 ms |
| Same 4 downstream calls, direct, no durability | 14.6 ms | 17.3 ms | 18.4 ms |
| Saga, harness on the Windows host via published ports | 480.1 ms | 492.4 ms | 501.1 ms |

Where the time goes, read from a recorded event history of this run: each **workflow task waits about 40 ms between
`WorkflowTaskScheduled` and `WorkflowTaskStarted`** (five of them: about 200 ms), each activity runs in 6-7 ms, each
workflow task is processed in about 4 ms. The 40 ms is spent on the server side before a poller receives the task. It
was unchanged with the sticky cache disabled (`MaxCachedWorkflows = 0`: 250.0 ms) and with 20 workflow task pollers
(250.0 ms), and with the CPU caps lifted (250.0 ms; the two worker experiments also ran uncapped), so it is neither a
worker setting nor CPU starvation. It is a property of this dev server on this machine; a production cluster was
not measured and may differ.

### Throughput: closed loop

C clients each start a saga and wait for its result, 15 s per level, one worker.

| Concurrency | Sagas/s | p50 | p95 | p99 |
|---|---|---|---|---|
| 1 | 4.2 | 249.4 ms | 254.4 ms | 256.7 ms |
| 4 | 15.3 | 251.1 ms | 306.2 ms | 351.4 ms |
| 16 | 28.9 | 529.0 ms | 806.4 ms | 966.4 ms |
| 64 | 32.1 | 958.8 ms | 5,665.3 ms | 5,969.0 ms |

Throughput plateaus at about 30 sagas/s: the 2-CPU dev server is the bottleneck. The 5.7 s tail at 64 clients is not
retries: every activity in the slow sagas ran on attempt 1. The slow sagas waited up to 4 s for their **first** workflow
task, which only non-sticky pollers pick up (with defaults, about one poller in five). With 20 pollers and a 1:1
non-sticky ratio (`benchmark-results/tuned-pollers`): 28.3 sagas/s, p50 2,151 ms, p95 2,696 ms at 64 clients. Fairer,
not faster; more pollers cannot add server CPU.

### Unwind latency

40 sagas each, sequential.

| Scenario | p50 | p95 |
|---|---|---|
| Courier rejects: 4 compensations in reverse (courier, refund, ticket, platform) | 497.7 ms | 501.8 ms |
| Payment captured, response lost, provider down until 5 attempts are spent, then unwind | 4,707.7 ms | 4,745.4 ms |

The second row is dominated by the forward retry policy (200 ms initial, x2, 5 attempts: 3.0 s of backoff), as
intended in [ADR 0003](adr/0003-retries-timeouts-and-human-interaction.md).

### Fault campaign

The same 140 seeded fault plans (seed 27; 20 each of: no fault, rejection, transient burst, lost response, lost
response then outage, zombie capture then outage, failing undo; random step) against the production saga and three lab
variants with one defence removed. All sagas in a variant run concurrently; the downstream tables are read 12 s after
the last saga closed, so zombie captures (10 s sleep) have landed.

| Variant | Completed | Compensated | Inconsistent | Broken shapes | Violations |
|---|---|---|---|---|---|
| **Correct** (production) | 60 | 80 | **0 / 140** | none | none |
| Compensate after success | 60 | 80 | 40 / 140 | lost then outage 20/20, zombie then outage 20/20 | 26 leaked charges, 7 platform orders left accepted, 5 orphan tickets, 2 orphan couriers |
| No idempotency key | 60 | 80 | 25 / 140 | zombie then outage 20/20, lost response 5/20 | 20 leaked charges, 5 double charges (also wrong amount) |
| No tombstone | 60 | 80 | 20 / 140 | zombie then outage 20/20 | 20 leaked charges |

Every variant reports the same completed/compensated split: the workflows all *believe* they finished correctly. Only
reading the downstream systems shows the difference. Interpretation in
[ADR 0002](adr/0002-compensate-on-unknown-idempotency-keys-and-tombstones.md).

### Replay (the cost of the determinism check)

`WorkflowReplayer`, in process, no server, 300 replays after 20 warm-up.

| Golden history | Events | p50 | p95 |
|---|---|---|---|
| v2 completed | 29 | 4.1 ms | 6.2 ms |
| v2 customer cancelled (full unwind + notice) | 68 | 8.8 ms | 14.1 ms |

### Crash recovery

The real host (`OrderSaga.App.dll`, Roles=worker) as a separate process; the first payment attempt hangs, the process
is killed (no drain) 300 ms after the kitchen step completed, a replacement process starts. 5 runs each.

| `StickyQueueScheduleToStartTimeout` | Kill to saga completed, p50 | p95 | Replacement ready, p50 |
|---|---|---|---|
| SDK default (10 s) | **15,806.8 ms** | 15,984.3 ms | 1,095.5 ms |
| 1 s | **6,851.5 ms** | 6,868.1 ms | 1,136.5 ms |

Every run completed with exactly one charge. With defaults, recovery is 5 s (activity start-to-close timeout of the
lost attempt) plus 10 s (the next workflow task waiting on the dead worker's sticky queue). See ADR 0003.

## Not measured

- A production Temporal cluster (multi-service, real persistence) or Temporal Cloud: the dev server is a test server.
- Real downstream latency: simulators are local PostgreSQL statements (`Downstream:Latency` can add a fixed delay).
- Long-running soak behaviour, memory growth of the sticky cache, and multi-namespace setups.

## Reproduce

```bash
dotnet publish benchmarks/OrderSaga.Benchmarks -c Release -o artifacts/bench
dotnet publish src/OrderSaga.App -c Release -o artifacts/app

docker network create osb-net
docker run -d --name osb-pg --network osb-net --cpus 2 -e POSTGRES_DB=ordersaga -e POSTGRES_PASSWORD=pg-bench \
  -e MIGRATOR_PASSWORD=migrator-bench -e APP_PASSWORD=app-bench \
  -v "$PWD/deploy/postgres/init:/docker-entrypoint-initdb.d:ro" postgres:17-alpine
docker run -d --name osb-temporal --network osb-net --cpus 2 temporalio/temporal:1.9.1 \
  server start-dev --ip 0.0.0.0 --headless --log-level error

docker run --rm --network osb-net \
  -e BENCH_TEMPORAL=osb-temporal:7233 \
  -e "BENCH_POSTGRES=Host=osb-pg;Database=ordersaga;Username=postgres;Password=pg-bench" \
  -e BENCH_APP_DLL=/apphost/OrderSaga.App.dll \
  -v "$PWD/artifacts/bench:/bench:ro" -v "$PWD/artifacts/app:/apphost:ro" -v "$PWD/benchmark-results:/out" -w /bench \
  mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled /bench/OrderSaga.Benchmarks.dll \
  latency throughput compensation campaign replay recovery --out /out

docker rm -f osb-pg osb-temporal && docker network rm osb-net
```

Without `BENCH_TEMPORAL`/`BENCH_POSTGRES` the harness starts both containers itself through Testcontainers and runs on
the host (`dotnet run -c Release --project benchmarks/OrderSaga.Benchmarks`), which on Docker Desktop for Windows
includes the port-forwarding cost described above. Worker experiment knobs: `BENCH_WF_POLLS`, `BENCH_STICKY_RATIO`,
`BENCH_MAX_CACHED`.
