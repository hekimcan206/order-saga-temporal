using OrderSaga.Core;
using Temporalio.Workflows;

namespace OrderSaga.Workflows;

/// <summary>
/// The delivery order saga: platform accept, kitchen ticket, payment capture, courier dispatch. On any failure, or a
/// customer cancellation while it can still unwind, the steps that ran (or may have run) are compensated in reverse.
/// </summary>
/// <remarks>
/// The workflow holds no database of its own: Temporal's event history is the saga log, and the current state is
/// served by a query. One workflow id per order (<c>order-{orderId}</c>) makes a re-sent order land on the same saga.
/// </remarks>
[Workflow]
public sealed class OrderSagaWorkflow
{
    public const string TaskQueue = "order-saga";

    private OrderSagaEngine? _engine;

    [WorkflowRun]
    public async Task<SagaResult> RunAsync(PlaceOrder order)
    {
        _engine = new OrderSagaEngine(order, SagaVariant.Correct);
        return await _engine.RunAsync();
    }

    /// <summary>Current stage and timeline. Answered from workflow memory; works while the saga is running.</summary>
    [WorkflowQuery]
    public SagaState State() => _engine?.Snapshot()
        ?? new SagaState(Workflow.Info.WorkflowId, SagaStage.Started, false, null, []);

    /// <summary>The courier platform reports an assignment for a dispatch that was accepted as pending.</summary>
    [WorkflowSignal]
    public Task CourierAssignedAsync(string courierId)
    {
        _engine?.CourierAssigned(courierId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Customer cancellation. An update rather than a signal so the caller gets an answer: "accepted, it will be
    /// unwound" or a rejection from the validator once the order has completed or is already unwinding.
    /// </summary>
    [WorkflowUpdate]
    public Task<CancelDecision> RequestCancellationAsync(string reason) =>
        Task.FromResult(_engine!.RequestCancellation(reason));

    [WorkflowUpdateValidator(nameof(RequestCancellationAsync))]
    public void ValidateCancellation(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200)
        {
            throw new ArgumentException("a reason of 1-200 characters is required");
        }

        if (_engine is null || !_engine.CanCancel())
        {
            throw new InvalidOperationException($"the order can no longer be cancelled (stage {_engine?.Stage})");
        }
    }
}
