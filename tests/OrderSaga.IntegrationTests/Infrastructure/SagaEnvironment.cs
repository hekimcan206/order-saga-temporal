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

[assembly: AssemblyFixture(typeof(OrderSaga.IntegrationTests.Infrastructure.SagaEnvironment))]

namespace OrderSaga.IntegrationTests.Infrastructure;

/// <summary>
/// Everything real: PostgreSQL 17 initialised with the compose role script and migrated as <c>saga_migrator</c>, the
/// Temporal CLI dev server (the same image the compose demo runs), and an in-process Temporal worker whose activities
/// connect as the least-privileged <c>saga_app</c>. Each test uses fresh order ids, so tests never need a reset.
/// </summary>
public sealed class SagaEnvironment : IAsyncLifetime
{
    public const string TemporalImage = "temporalio/temporal:1.9.1";
    public const string PostgresImage = "postgres:17-alpine";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("ordersaga")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    private readonly IContainer _temporal = new ContainerBuilder(TemporalImage)
        .WithCommand("server", "start-dev", "--ip", "0.0.0.0", "--headless", "--log-level", "warn")
        .WithPortBinding(7233, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Temporal Server:"))
        .Build();

    private TemporalWorker? _worker;
    private Task? _workerRun;
    private readonly CancellationTokenSource _workerStop = new();

    public string TaskQueue { get; } = $"order-saga-tests-{Guid.NewGuid():N}";

    public TemporalClient Client { get; private set; } = null!;

    public string TemporalAddress => $"127.0.0.1:{_temporal.GetMappedPublicPort(7233)}";

    public NpgsqlDataSource AppDb { get; private set; } = null!;

    public NpgsqlDataSource SuperuserDb { get; private set; } = null!;

    public DownstreamSystems Downstream { get; private set; } = null!;

    public FaultInjector Faults { get; private set; } = null!;

    public string AppConnectionString => WithCredentials("saga_app", AppPassword);

    public string MigratorConnectionString => WithCredentials("saga_migrator", MigratorPassword);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _temporal.StartAsync());

        await using (var migrator = NpgsqlDataSource.Create(MigratorConnectionString))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        AppDb = NpgsqlDataSource.Create(AppConnectionString);
        SuperuserDb = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        Faults = new FaultInjector(AppDb);
        Downstream = new DownstreamSystems(AppDb, Faults, Options.Create(new DownstreamOptions()));

        Client = await ConnectAsync(TemporalAddress);
        _worker = new TemporalWorker(Client, new TemporalWorkerOptions(TaskQueue)
            .AddAllActivities(new OrderActivities(Downstream))
            .AddWorkflow<OrderSagaWorkflow>()
            .AddWorkflow<OrderSagaLabWorkflow>());
        _workerRun = _worker.ExecuteAsync(_workerStop.Token);

        // Warm up: the first workflow pays for JIT, gRPC channel setup and connection pools; no test should.
        var warm = await RunAsync(Orders.New("warmup"));
        if (warm.Result.Status != SagaStatus.Completed)
        {
            throw new InvalidOperationException("warm-up saga did not complete");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _workerStop.CancelAsync();
        if (_workerRun is not null)
        {
            try
            {
                await _workerRun;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _worker?.Dispose();
        _workerStop.Dispose();
        await AppDb.DisposeAsync();
        await SuperuserDb.DisposeAsync();
        await _temporal.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public static async Task<TemporalClient> ConnectAsync(string address)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(address)
                {
                    Namespace = "default",
                    // The default identity is pid@hostname; recorded golden histories must not carry the machine name.
                    Identity = "order-saga-tests",
                });
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Injects faults, runs the production saga to its end, and reads what the downstream systems recorded.</summary>
    public async Task<SagaRun> RunAsync(PlaceOrder order, params FaultRule[] faults)
    {
        foreach (var fault in faults)
        {
            await Faults.AddAsync(order.OrderId, fault);
        }

        var handle = await StartAsync(order);
        var result = await handle.GetResultAsync();
        return await ObserveAsync(order, result);
    }

    public Task<WorkflowHandle<OrderSagaWorkflow, SagaResult>> StartAsync(PlaceOrder order) =>
        Client.StartWorkflowAsync(
            (OrderSagaWorkflow wf) => wf.RunAsync(order),
            new WorkflowOptions(order.WorkflowId(), TaskQueue) { ExecutionTimeout = TimeSpan.FromMinutes(5) });

    /// <summary>Same as <see cref="RunAsync"/> but with a lab variant of the saga.</summary>
    public async Task<SagaRun> RunLabAsync(PlaceOrder order, SagaVariant variant, params FaultRule[] faults)
    {
        foreach (var fault in faults)
        {
            await Faults.AddAsync(order.OrderId, fault);
        }

        var result = await Client.ExecuteWorkflowAsync(
            (OrderSagaLabWorkflow wf) => wf.RunAsync(new LabOrder(order, variant)),
            new WorkflowOptions($"lab-{order.OrderId}", TaskQueue) { ExecutionTimeout = TimeSpan.FromMinutes(5) });
        return await ObserveAsync(order, result);
    }

    public async Task<SagaRun> ObserveAsync(PlaceOrder order, SagaResult result)
    {
        var snapshot = await Downstream.SnapshotAsync(order.OrderId);
        return new SagaRun(result, snapshot, ConsistencyChecker.Check(result.Status, order.TotalKurus(), snapshot));
    }

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = username,
            Password = password,
            MaxPoolSize = 60,
            // Generous connect timeout: the host is shared with other projects' containers.
            Timeout = 60,
        }.ConnectionString;
}

public sealed record SagaRun(SagaResult Result, DownstreamSnapshot Snapshot, IReadOnlyList<Violation> Violations);
