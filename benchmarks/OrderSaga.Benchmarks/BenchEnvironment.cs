using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderSaga.Core;
using OrderSaga.Downstream;
using OrderSaga.Workflows;
using Temporalio.Client;
using Temporalio.Worker;
using Testcontainers.PostgreSql;

namespace OrderSaga.Benchmarks;

/// <summary>
/// PostgreSQL 17 and the Temporal CLI dev server in containers capped at 2 CPUs each (so numbers do not depend on how
/// idle the rest of the machine happens to be), plus an in-process worker. Same images and role script as the tests.
/// </summary>
internal sealed class BenchEnvironment : IAsyncDisposable
{
    private const long TwoCpus = 2_000_000_000;
    private readonly PostgreSqlContainer? _postgres;
    private readonly IContainer? _temporal;
    private readonly string _temporalAddress;
    private readonly string _baseConnectionString;
    private readonly List<(TemporalWorker Worker, CancellationTokenSource Stop, Task Run)> _workers = [];

    private BenchEnvironment(PostgreSqlContainer? postgres, IContainer? temporal, string temporalAddress, string baseConnectionString)
    {
        _postgres = postgres;
        _temporal = temporal;
        _temporalAddress = temporalAddress;
        _baseConnectionString = baseConnectionString;
    }

    public TemporalClient Client { get; private set; } = null!;

    public DownstreamSystems Downstream { get; private set; } = null!;

    public FaultInjector Faults { get; private set; } = null!;

    public NpgsqlDataSource AppDb { get; private set; } = null!;

    public string TaskQueue { get; } = $"bench-{Guid.NewGuid():N}";

    public string TemporalAddress => _temporalAddress;

    public string AppConnectionString => Credentials("saga_app", "app-bench");

    public static async Task<BenchEnvironment> StartAsync()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("ordersaga")
            .WithEnvironment("MIGRATOR_PASSWORD", "migrator-bench")
            .WithEnvironment("APP_PASSWORD", "app-bench")
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
                "/docker-entrypoint-initdb.d/01-roles.sh")
            .WithCreateParameterModifier(p => p.HostConfig!.NanoCPUs = TwoCpus)
            .Build();
        var temporal = new ContainerBuilder("temporalio/temporal:1.9.1")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0", "--headless", "--log-level", "error")
            .WithPortBinding(7233, true)
            .WithCreateParameterModifier(p => p.HostConfig!.NanoCPUs = TwoCpus)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Temporal Server:"))
            .Build();
        await Task.WhenAll(postgres.StartAsync(), temporal.StartAsync());

        var env = new BenchEnvironment(postgres, temporal, $"127.0.0.1:{temporal.GetMappedPublicPort(7233)}", postgres.GetConnectionString());
        await env.InitializeAsync();
        return env;
    }

    /// <summary>
    /// Uses an already running Temporal and PostgreSQL (initialised with the compose role script). This is how the
    /// harness runs inside a Linux container on the same Docker network as the servers, away from the Windows host's
    /// port forwarding (see docs/benchmarks.md).
    /// </summary>
    public static async Task<BenchEnvironment> ConnectExternalAsync(string temporalAddress, string postgresConnectionString)
    {
        var env = new BenchEnvironment(null, null, temporalAddress, postgresConnectionString);
        await env.InitializeAsync();
        return env;
    }

    private async Task InitializeAsync()
    {
        var env = this;
        await using (var migrator = NpgsqlDataSource.Create(env.Credentials("saga_migrator", "migrator-bench")))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        env.AppDb = NpgsqlDataSource.Create(env.AppConnectionString);
        env.Faults = new FaultInjector(env.AppDb);
        env.Downstream = new DownstreamSystems(env.AppDb, env.Faults, Options.Create(new DownstreamOptions()));
        env.Client = await ConnectAsync(env.TemporalAddress);
    }

    /// <summary>Adds an in-process worker on the benchmark task queue (call twice for two competing workers).</summary>
    public void AddWorker()
    {
        var options = new TemporalWorkerOptions(TaskQueue)
            .AddAllActivities(new OrderActivities(Downstream))
            .AddWorkflow<OrderSagaWorkflow>()
            .AddWorkflow<OrderSagaLabWorkflow>();
        // Experiment knobs (docs/benchmarks.md, "where the time goes"); unset means SDK defaults.
        if (int.TryParse(Environment.GetEnvironmentVariable("BENCH_WF_POLLS"), out var polls))
        {
            options.MaxConcurrentWorkflowTaskPolls = polls;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("BENCH_MAX_CACHED"), out var cached))
        {
            options.MaxCachedWorkflows = cached;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("BENCH_STICKY_RATIO"), System.Globalization.CultureInfo.InvariantCulture, out var ratio))
        {
            options.NonStickyToStickyPollRatio = (float)ratio;
        }

        var worker = new TemporalWorker(Client, options);
        var stop = new CancellationTokenSource();
        _workers.Add((worker, stop, worker.ExecuteAsync(stop.Token)));
    }

    public Task<SagaResult> RunAsync(PlaceOrder order) =>
        Client.ExecuteWorkflowAsync((OrderSagaWorkflow wf) => wf.RunAsync(order), new WorkflowOptions(order.WorkflowId(), TaskQueue));

    public Task<SagaResult> RunLabAsync(PlaceOrder order, SagaVariant variant) =>
        Client.ExecuteWorkflowAsync(
            (OrderSagaLabWorkflow wf) => wf.RunAsync(new LabOrder(order, variant)),
            new WorkflowOptions($"lab-{order.OrderId}", TaskQueue));

    public static async Task<TemporalClient> ConnectAsync(string address)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TemporalClient.ConnectAsync(new(address) { Namespace = "default", Identity = "order-saga-bench" });
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (worker, stop, run) in _workers)
        {
            await stop.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }

            worker.Dispose();
            stop.Dispose();
        }

        if (AppDb is not null)
        {
            await AppDb.DisposeAsync();
        }

        if (_temporal is not null)
        {
            await _temporal.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private string Credentials(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Username = user,
            Password = password,
            MaxPoolSize = 100,
            Timeout = 60,
        }.ConnectionString;
}
