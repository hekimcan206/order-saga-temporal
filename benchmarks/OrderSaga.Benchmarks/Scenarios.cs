using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using OrderSaga.Core;
using OrderSaga.Downstream;
using OrderSaga.Workflows;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;

namespace OrderSaga.Benchmarks;

internal sealed record LatencySummary(int N, double P50Ms, double P95Ms, double P99Ms, double MeanMs);

internal static class Stats
{
    public static LatencySummary Summarize(IReadOnlyCollection<double> ms)
    {
        var sorted = ms.Order().ToArray();
        return new LatencySummary(sorted.Length, Pct(sorted, 50), Pct(sorted, 95), Pct(sorted, 99), Math.Round(sorted.Average(), 2));
    }

    private static double Pct(double[] sorted, double p)
    {
        // Nearest-rank percentile: always an observed value, never an interpolation.
        var rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)], 2);
    }

    public static double Ms(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

internal static class Orders
{
    public static PlaceOrder New(string prefix, int courierWaitSeconds = 300) => new(
        $"{prefix}-{Guid.NewGuid():N}",
        "store-01",
        "cust-bench",
        [new OrderLine("lahmacun", "Lahmacun", 2, 12_000), new OrderLine("ayran", "Ayran", 2, 4_500)],
        courierWaitSeconds);
}

internal static class Scenarios
{
    /// <summary>Sequential happy-path sagas vs the same four downstream calls made directly (no durability).</summary>
    public static async Task<object> LatencyAsync(BenchEnvironment env, int n)
    {
        for (var i = 0; i < 30; i++)
        {
            await env.RunAsync(Orders.New("warm"));
        }

        var saga = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await env.RunAsync(Orders.New("lat"));
            saga.Add(Stats.Ms(start));
        }

        var direct = new List<double>(n);
        for (var i = 0; i < n + 30; i++)
        {
            var order = Orders.New("direct");
            var start = Stopwatch.GetTimestamp();
            await env.Downstream.AcceptOnPlatformAsync(Call(order, SagaStep.PlatformAccept));
            await env.Downstream.ConfirmKitchenTicketAsync(Call(order, SagaStep.KitchenTicket), order.Lines.Count);
            await env.Downstream.ChargeAsync(Call(order, SagaStep.Payment), order.TotalKurus());
            await env.Downstream.DispatchCourierAsync(Call(order, SagaStep.CourierDispatch));
            if (i >= 30)
            {
                direct.Add(Stats.Ms(start));
            }
        }

        return new { saga = Stats.Summarize(saga), direct = Stats.Summarize(direct) };
    }

    /// <summary>Closed loop: C clients each start a saga, wait for its result, repeat, for a fixed time.</summary>
    public static async Task<object> ThroughputAsync(BenchEnvironment env, int[] concurrencies, TimeSpan duration)
    {
        var rows = new List<object>();
        foreach (var c in concurrencies)
        {
            var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            var started = Stopwatch.GetTimestamp();
            await Task.WhenAll(Enumerable.Range(0, c).Select(async _ =>
            {
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    var t = Stopwatch.GetTimestamp();
                    await env.RunAsync(Orders.New("tp"));
                    latencies.Add(Stats.Ms(t));
                }
            }));
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var row = new { concurrency = c, sagas = latencies.Count, sagasPerSecond = Math.Round(latencies.Count / seconds, 1), latency = Stats.Summarize([.. latencies]) };
            Console.WriteLine($"  concurrency {c}: {row.sagasPerSecond}/s, p50 {row.latency.P50Ms} ms, p95 {row.latency.P95Ms} ms");
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>How long an unwind takes: a 4-step reverse unwind after a rejection, and the lost-response case.</summary>
    public static async Task<object> CompensationAsync(BenchEnvironment env, int n)
    {
        async Task<LatencySummary> Measure(Func<PlaceOrder, Task> setup)
        {
            var ms = new List<double>(n);
            for (var i = 0; i < n; i++)
            {
                var order = Orders.New("comp");
                await setup(order);
                var start = Stopwatch.GetTimestamp();
                var result = await env.RunAsync(order);
                ms.Add(Stats.Ms(start));
                if (result.Status != SagaStatus.Compensated)
                {
                    throw new InvalidOperationException($"expected compensation, got {result.Status}");
                }
            }

            return Stats.Summarize(ms);
        }

        var courierRejected = await Measure(o => env.Faults.AddAsync(o.OrderId, Rule(SagaStep.CourierDispatch, FaultKind.Reject)));
        var lostThenOutage = await Measure(async o =>
        {
            await env.Faults.AddAsync(o.OrderId, Rule(SagaStep.Payment, FaultKind.LostResponse));
            await env.Faults.AddAsync(o.OrderId, Rule(SagaStep.Payment, FaultKind.Transient, 10));
        });
        return new { courierRejectedFullUnwind = courierRejected, paymentLostThenOutage = lostThenOutage };
    }

    /// <summary>
    /// The same seeded fault plans against every saga variant. Counts what each removed defence costs. Zombie captures
    /// sleep 10 s, so the snapshot is taken after every saga closed and a further grace period passed.
    /// </summary>
    public static async Task<object> CampaignAsync(BenchEnvironment env, int n, int seed)
    {
        var plans = FaultPlan.Generate(n, seed);
        var shapes = plans.GroupBy(p => p.Shape).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<object>();
        foreach (var variant in Enum.GetValues<SagaVariant>())
        {
            var orders = new List<(PlaceOrder Order, FaultPlan Plan)>();
            foreach (var plan in plans)
            {
                var order = Orders.New($"camp-{variant.ToString().ToLowerInvariant()}");
                foreach (var rule in plan.Rules)
                {
                    await env.Faults.AddAsync(order.OrderId, rule);
                }

                orders.Add((order, plan));
            }

            var start = Stopwatch.GetTimestamp();
            var results = await Task.WhenAll(orders.Select(o => env.RunLabAsync(o.Order, variant)));
            var elapsed = Stats.Ms(start);
            await Task.Delay(TimeSpan.FromSeconds(12)); // let zombie attempts land

            var violations = new Dictionary<string, int>();
            var broken = 0;
            var brokenByShape = new Dictionary<string, int>();
            for (var i = 0; i < orders.Count; i++)
            {
                var snapshot = await env.Downstream.SnapshotAsync(orders[i].Order.OrderId);
                var found = ConsistencyChecker.Check(results[i].Status, orders[i].Order.TotalKurus(), snapshot);
                if (found.Count > 0 || results[i].Status == SagaStatus.CompensationFailed)
                {
                    broken++;
                    brokenByShape[orders[i].Plan.Shape] = brokenByShape.GetValueOrDefault(orders[i].Plan.Shape) + 1;
                }

                foreach (var v in found)
                {
                    violations[v.Code] = violations.GetValueOrDefault(v.Code) + 1;
                }
            }

            var row = new
            {
                variant = variant.ToString(),
                sagas = results.Length,
                completed = results.Count(r => r.Status == SagaStatus.Completed),
                compensated = results.Count(r => r.Status == SagaStatus.Compensated),
                compensationFailed = results.Count(r => r.Status == SagaStatus.CompensationFailed),
                inconsistent = broken,
                violations,
                brokenByShape,
                wallClockMs = Math.Round(elapsed),
            };
            Console.WriteLine($"  {variant}: {broken}/{results.Length} inconsistent ({string.Join(", ", violations.Select(kv => $"{kv.Key} {kv.Value}"))})");
            rows.Add(row);
        }

        return new { seed, planShapes = shapes, variants = rows };
    }

    /// <summary>Cost of the determinism check: replaying a recorded history in process.</summary>
    public static async Task<object> ReplayAsync(int iterations)
    {
        var rows = new List<object>();
        foreach (var name in new[] { "v2/completed.json", "v2/customer-cancelled.json" })
        {
            var history = WorkflowHistory.FromJson("bench", await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Histories", name)));
            var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<OrderSagaWorkflow>());
            for (var i = 0; i < 20; i++)
            {
                await replayer.ReplayWorkflowAsync(history);
            }

            var ms = new List<double>(iterations);
            for (var i = 0; i < iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await replayer.ReplayWorkflowAsync(history);
                ms.Add(Stats.Ms(start));
            }

            rows.Add(new { history = name, events = history.Events.Count, replay = Stats.Summarize(ms) });
        }

        return rows;
    }

    /// <summary>Kill a worker process during the payment step and time until another worker process finishes the saga.</summary>
    public static async Task<object> RecoveryAsync(BenchEnvironment env, int runs)
    {
        var rows = new List<object>();
        foreach (var sticky in new string?[] { null, "00:00:01" })
        {
            var recovery = new List<double>();
            var ready = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                var queue = $"recovery-{Guid.NewGuid():N}";
                var order = Orders.New("recovery");
                await env.Faults.AddAsync(order.OrderId, Rule(SagaStep.Payment, FaultKind.Slow, 1, 3_000));
                using var first = await WorkerProcess.StartAsync(env, queue, sticky);
                var handle = await env.Client.StartWorkflowAsync((OrderSagaWorkflow wf) => wf.RunAsync(order), new WorkflowOptions(order.WorkflowId(), queue));
                while ((await handle.QueryAsync(wf => wf.State())).Stage != SagaStage.KitchenConfirmed)
                {
                    await Task.Delay(20);
                }

                await Task.Delay(300);
                first.Kill();
                var killed = Stopwatch.GetTimestamp();
                using var second = await WorkerProcess.StartAsync(env, queue, sticky);
                ready.Add(Stats.Ms(killed));
                var result = await handle.GetResultAsync();
                recovery.Add(Stats.Ms(killed));
                var snapshot = await env.Downstream.SnapshotAsync(order.OrderId);
                if (result.Status != SagaStatus.Completed || snapshot.Charges != 1)
                {
                    throw new InvalidOperationException("recovery run did not complete with exactly one charge");
                }
            }

            var row = new { stickyQueueTimeout = sticky ?? "default", runs, killToCompleted = Stats.Summarize(recovery), killToReplacementReady = Stats.Summarize(ready) };
            Console.WriteLine($"  sticky {row.stickyQueueTimeout}: kill->completed p50 {row.killToCompleted.P50Ms} ms (replacement ready p50 {row.killToReplacementReady.P50Ms} ms)");
            rows.Add(row);
        }

        return rows;
    }

    private static EffectCall Call(PlaceOrder order, SagaStep step) => new(order.OrderId, order.StoreId, SagaSteps.IdempotencyKey(order.OrderId, step));

    internal static FaultRule Rule(SagaStep step, FaultKind kind, int times = 1, int delayMs = 0) => new(step, FaultPhase.Forward, kind, times, delayMs);
}

/// <summary>A seeded fault plan: the same seed gives every variant the same faults in the same order.</summary>
internal sealed record FaultPlan(string Shape, FaultRule[] Rules)
{
    public static IReadOnlyList<FaultPlan> Generate(int n, int seed)
    {
        var random = new Random(seed);
        var steps = SagaSteps.ForwardOrder.ToArray();
        var plans = new List<FaultPlan>(n);
        for (var i = 0; i < n; i++)
        {
            var step = steps[random.Next(steps.Length)];
            plans.Add((i % 7) switch
            {
                0 => new("none", []),
                1 => new("reject", [Scenarios.Rule(step, FaultKind.Reject)]),
                2 => new("transient", [Scenarios.Rule(step, FaultKind.Transient, random.Next(1, 4))]),
                3 => new("lost-response", [Scenarios.Rule(step, FaultKind.LostResponse)]),
                4 => new("lost-then-outage", [Scenarios.Rule(step, FaultKind.LostResponse), Scenarios.Rule(step, FaultKind.Transient, 10)]),
                5 => new("zombie-then-outage", [Scenarios.Rule(SagaStep.Payment, FaultKind.Slow, 1, 10_000), Scenarios.Rule(SagaStep.Payment, FaultKind.Transient, 10)]),
                _ => new("failing-undo", [new(step, FaultPhase.Compensate, FaultKind.Transient, random.Next(1, 4)), Scenarios.Rule(SagaStep.CourierDispatch, FaultKind.Reject)]),
            });
        }

        return plans;
    }
}

/// <summary>The application host as a separate process (Roles=worker), killed without a graceful drain.</summary>
internal sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;

    private WorkerProcess(Process process) => _process = process;

    public static async Task<WorkerProcess> StartAsync(BenchEnvironment env, string queue, string? sticky)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var info = new ProcessStartInfo("dotnet", $"\"{AppDll()}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["Roles"] = "worker";
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["ConnectionStrings__Downstream"] = env.AppConnectionString;
        info.Environment["Temporal__Address"] = env.TemporalAddress;
        info.Environment["Temporal__TaskQueue"] = queue;
        info.Environment["Serilog__MinimumLevel__Default"] = "Warning";
        if (sticky is not null)
        {
            info.Environment["Temporal__StickyQueueTimeout"] = sticky;
        }

        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var i = 0; i < 200; i++)
        {
            try
            {
                if ((await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health/ready"))).IsSuccessStatusCode)
                {
                    return new WorkerProcess(process);
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("worker process not ready");
    }

    public void Kill()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            Kill();
        }

        _process.Dispose();
    }

    private static string AppDll()
    {
        if (Environment.GetEnvironmentVariable("BENCH_APP_DLL") is { Length: > 0 } explicitDll)
        {
            return explicitDll;
        }

        var configuration = typeof(WorkerProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderSaga.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "OrderSaga.App", "bin", configuration, "net10.0", "OrderSaga.App.dll");
    }
}
