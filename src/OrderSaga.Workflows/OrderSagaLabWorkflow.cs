using OrderSaga.Core;
using Temporalio.Workflows;

namespace OrderSaga.Workflows;

/// <summary>Input of the lab workflow: an order plus the saga variant to run it with.</summary>
public sealed record LabOrder(PlaceOrder Order, SagaVariant Variant);

/// <summary>
/// The same saga with one defence switched off, for the fault campaign that measures what each defence buys. It is
/// registered only on the lab worker (tests, benchmarks), never by the application host.
/// </summary>
[Workflow]
public sealed class OrderSagaLabWorkflow
{
    private OrderSagaEngine? _engine;

    [WorkflowRun]
    public async Task<SagaResult> RunAsync(LabOrder input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _engine = new OrderSagaEngine(input.Order, input.Variant);
        return await _engine.RunAsync();
    }

    [WorkflowQuery]
    public SagaState? State() => _engine?.Snapshot();

    [WorkflowSignal]
    public Task CourierAssignedAsync(string courierId)
    {
        _engine?.CourierAssigned(courierId);
        return Task.CompletedTask;
    }
}
