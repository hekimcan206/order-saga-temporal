using System.Runtime.InteropServices;
using System.Text.Json;
using OrderSaga.Benchmarks;

// Usage: dotnet run -c Release --project benchmarks/OrderSaga.Benchmarks -- [scenario...] [--out benchmark-results]
// Scenarios: latency throughput compensation campaign replay recovery (default: all)
var all = new[] { "latency", "throughput", "compensation", "campaign", "replay", "recovery" };
var outIndex = Array.IndexOf(args, "--out");
var outDir = outIndex >= 0 ? args[outIndex + 1] : "benchmark-results";
var selected = args.Where((a, i) => all.Contains(a) && (outIndex < 0 || i != outIndex + 1)).ToArray();
if (selected.Length == 0)
{
    selected = all;
}

Directory.CreateDirectory(outDir);
var json = new JsonSerializerOptions { WriteIndented = true };
var environment = new
{
    os = RuntimeInformation.OSDescription,
    runtime = RuntimeInformation.FrameworkDescription,
    processors = Environment.ProcessorCount,
    containers = "postgres:17-alpine and temporalio/temporal:1.9.1, each capped at 2 CPUs",
    harness = Environment.GetEnvironmentVariable("BENCH_TEMPORAL") is { Length: > 0 } ? "linux container on the server network" : "host process via published ports",
    startedUtc = DateTime.UtcNow,
};
Console.WriteLine(JsonSerializer.Serialize(environment));

// BENCH_TEMPORAL + BENCH_POSTGRES: use running servers (harness inside a container); otherwise start them here.
var externalTemporal = Environment.GetEnvironmentVariable("BENCH_TEMPORAL");
var externalPostgres = Environment.GetEnvironmentVariable("BENCH_POSTGRES");
await using var env = externalTemporal is { Length: > 0 } && externalPostgres is { Length: > 0 }
    ? await BenchEnvironment.ConnectExternalAsync(externalTemporal, externalPostgres)
    : await BenchEnvironment.StartAsync();
env.AddWorker();

foreach (var scenario in selected)
{
    Console.WriteLine($"== {scenario}");
    object result = scenario switch
    {
        "latency" => await Scenarios.LatencyAsync(env, 300),
        "throughput" => await Scenarios.ThroughputAsync(env, [1, 4, 16, 64], TimeSpan.FromSeconds(15)),
        "compensation" => await Scenarios.CompensationAsync(env, 40),
        "campaign" => await Scenarios.CampaignAsync(env, 140, seed: 27),
        "replay" => await Scenarios.ReplayAsync(300),
        "recovery" => await Scenarios.RecoveryAsync(env, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(args), scenario, null),
    };
    var text = JsonSerializer.Serialize(new { environment, scenario, result }, json);
    Console.WriteLine(text);
    await File.WriteAllTextAsync(Path.Combine(outDir, $"{scenario}.json"), text);
}
