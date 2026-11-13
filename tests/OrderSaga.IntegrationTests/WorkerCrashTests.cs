using System.Diagnostics;
using OrderSaga.Core;
using OrderSaga.IntegrationTests.Infrastructure;
using OrderSaga.Workflows;
using Temporalio.Client;
using static OrderSaga.IntegrationTests.Infrastructure.Orders;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// The durability claim, tested with real process kills: a worker dies in the middle of a saga, another worker
/// process picks the saga up from the event history, and the downstream systems see every effect exactly once.
/// </summary>
public sealed class WorkerCrashTests(SagaEnvironment env, ITestOutputHelper output)
{
    [Theory]
    [InlineData(null)]
    [InlineData("00:00:01")]
    public async Task A_worker_killed_during_the_payment_step_is_replaced_and_the_payment_happens_once(string? stickyQueueTimeout)
    {
        var queue = $"crash-{Guid.NewGuid():N}";
        var order = New("crash");
        // The first payment attempt hangs (and dies with its process); the next attempt runs on the replacement worker.
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.Payment, FaultKind.Slow, times: 1, delayMs: 3_000));

        await using var first = await WorkerProcess.StartAsync(env, queue, stickyQueueTimeout);
        var handle = await StartAsync(order, queue);
        await OrderSagaTests.WaitForStageAsync(handle, SagaStage.KitchenConfirmed);
        await Task.Delay(500);

        first.Kill();
        var killedAt = Stopwatch.GetTimestamp();
        await using var second = await WorkerProcess.StartAsync(env, queue, stickyQueueTimeout);
        var result = await handle.GetResultAsync();
        var recovery = Stopwatch.GetElapsedTime(killedAt);
        output.WriteLine($"sticky timeout {stickyQueueTimeout ?? "default (10 s)"}: kill -> saga completed in {recovery.TotalMilliseconds:F0} ms");

        var run = await env.ObserveAsync(order, result);
        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Snapshot.Charges.Should().Be(1);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_worker_killed_while_the_saga_waits_for_a_courier_resumes_on_another_worker_from_history()
    {
        var queue = $"crash-{Guid.NewGuid():N}";
        var order = New("crash");
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));

        await using var first = await WorkerProcess.StartAsync(env, queue);
        var handle = await StartAsync(order, queue);
        await OrderSagaTests.WaitForStageAsync(handle, SagaStage.AwaitingCourier);

        first.Kill();
        await using var second = await WorkerProcess.StartAsync(env, queue);
        // The replacement has never seen this saga: it rebuilds the state by replaying the history, then takes the signal.
        var state = await OrderSagaTests.WaitForStageAsync(handle, SagaStage.AwaitingCourier);
        state.Timeline.Count(e => e.Kind == "step-succeeded").Should().Be(4);
        await handle.SignalAsync(wf => wf.CourierAssignedAsync("courier-555"));
        var run = await env.ObserveAsync(order, await handle.GetResultAsync());

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Result.CourierId.Should().Be("courier-555");
        run.Snapshot.Charges.Should().Be(1);
        run.Violations.Should().BeEmpty();
    }

    private Task<WorkflowHandle<OrderSagaWorkflow, SagaResult>> StartAsync(PlaceOrder order, string queue) =>
        env.Client.StartWorkflowAsync(
            (OrderSagaWorkflow wf) => wf.RunAsync(order),
            new WorkflowOptions(order.WorkflowId(), queue) { ExecutionTimeout = TimeSpan.FromMinutes(5) });
}
