using OrderSaga.Core;
using OrderSaga.IntegrationTests.Infrastructure;
using OrderSaga.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using static OrderSaga.IntegrationTests.Infrastructure.Orders;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// The production saga on a real Temporal server with real downstream tables. Every test ends by checking what the
/// downstream systems recorded, not what the workflow believes.
/// </summary>
public sealed class OrderSagaTests(SagaEnvironment env)
{
    private static readonly SagaStep[] AllReversed =
        [SagaStep.CourierDispatch, SagaStep.Payment, SagaStep.KitchenTicket, SagaStep.PlatformAccept];

    [Fact]
    public async Task Happy_path_completes_every_step_and_charges_once()
    {
        var order = New();

        var run = await env.RunAsync(order);

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Result.Completed.Should().Equal(SagaSteps.ForwardOrder);
        run.Result.Compensations.Should().BeEmpty();
        run.Result.CourierId.Should().StartWith("courier-");
        run.Snapshot.Should().Be(new DownstreamSnapshot(
            order.OrderId, EffectState.Active, EffectState.Active, EffectState.Active, 1, order.TotalKurus()));
        run.Violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SagaStep.PlatformAccept)]
    [InlineData(SagaStep.KitchenTicket)]
    [InlineData(SagaStep.Payment)]
    [InlineData(SagaStep.CourierDispatch)]
    public async Task A_rejection_unwinds_every_step_up_to_and_including_the_failed_one_in_reverse(SagaStep failing)
    {
        var order = New();

        var run = await env.RunAsync(order, Forward(failing, FaultKind.Reject));

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Result.FailedStep.Should().Be(failing);
        run.Result.Reason.Should().StartWith(SagaErrors.Rejected);
        run.Result.Completed.Should().Equal(SagaSteps.ForwardOrder.Where(s => s < failing));
        // The failed step is compensated too: its compensation was registered before it ran, and it is a no-op
        // (a tombstone) when the rejection means nothing happened.
        run.Result.Compensations.Select(c => c.Step).Should().Equal(AllReversed.Where(s => s <= failing));
        run.Result.Compensations.Should().OnlyContain(c => c.Succeeded);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Card_declined_after_the_kitchen_confirmed_cancels_the_ticket_and_the_platform_order()
    {
        var order = New();

        var run = await env.RunAsync(order, Forward(SagaStep.Payment, FaultKind.Reject));

        run.Snapshot.Platform.Should().Be(EffectState.Undone);
        run.Snapshot.Kitchen.Should().Be(EffectState.Undone);
        run.Snapshot.Charges.Should().Be(0);
        run.Snapshot.Courier.Should().Be(EffectState.None);
    }

    [Fact]
    public async Task No_courier_available_refunds_the_captured_payment()
    {
        var order = New();

        var run = await env.RunAsync(order, Forward(SagaStep.CourierDispatch, FaultKind.Reject));

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Snapshot.Charges.Should().Be(1);
        run.Snapshot.NetChargedKurus.Should().Be(0);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Transient_failures_on_every_step_are_retried_to_completion_without_duplicates()
    {
        var order = New();

        var run = await env.RunAsync(order, [.. SagaSteps.ForwardOrder.Select(s => Forward(s, FaultKind.Transient, times: 3))]);

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Snapshot.Charges.Should().Be(1);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lost_payment_response_is_retried_with_the_same_key_and_charges_once()
    {
        var order = New();

        var run = await env.RunAsync(order, Forward(SagaStep.Payment, FaultKind.LostResponse));

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Snapshot.Charges.Should().Be(1);
        run.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// The case that separates a correct saga from a textbook one: the provider captured the money, the answer was lost,
    /// and the provider then stayed down until retries ran out. The workflow never saw a success, yet it must refund.
    /// </summary>
    [Fact]
    public async Task Captured_but_unconfirmed_payment_is_refunded_when_retries_run_out()
    {
        var order = New();

        var run = await env.RunAsync(order,
            Forward(SagaStep.Payment, FaultKind.LostResponse),
            Forward(SagaStep.Payment, FaultKind.Transient, times: 10));

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Result.FailedStep.Should().Be(SagaStep.Payment);
        run.Result.Completed.Should().NotContain(SagaStep.Payment);
        run.Result.Compensations.Should().Contain(new CompensationResult(SagaStep.Payment, true, null));
        run.Snapshot.Charges.Should().Be(1);
        run.Snapshot.NetChargedKurus.Should().Be(0);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_refund_is_retried_until_the_money_is_back()
    {
        var order = New();

        var run = await env.RunAsync(order,
            Forward(SagaStep.CourierDispatch, FaultKind.Reject),
            Undo(SagaStep.Payment, FaultKind.Transient, times: 3),
            Undo(SagaStep.Payment, FaultKind.LostResponse));

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Snapshot.NetChargedKurus.Should().Be(0);
        run.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// A zombie attempt: the first capture hangs past its start-to-close timeout, the retries fail, the saga refunds,
    /// and only then does the zombie reach the provider. The refund's tombstone makes the provider refuse it.
    /// </summary>
    [Fact]
    public async Task A_zombie_capture_that_lands_after_the_refund_is_refused()
    {
        var order = New();

        var run = await env.RunAsync(order,
            Forward(SagaStep.Payment, FaultKind.Slow, times: 1, delayMs: 10_000),
            Forward(SagaStep.Payment, FaultKind.Transient, times: 10));
        run.Result.Status.Should().Be(SagaStatus.Compensated);

        // Give the zombie time to wake up and try (it sleeps 10 s from the first attempt).
        await Task.Delay(TimeSpan.FromSeconds(4));
        var after = await env.ObserveAsync(order, run.Result);
        after.Snapshot.Charges.Should().Be(0);
        after.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pending_dispatch_completes_when_the_courier_platform_signals_an_assignment()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));

        var handle = await env.StartAsync(order);
        await WaitForStageAsync(handle, SagaStage.AwaitingCourier);
        await handle.SignalAsync(wf => wf.CourierAssignedAsync("courier-777"));
        var run = await env.ObserveAsync(order, await handle.GetResultAsync());

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Result.CourierId.Should().Be("courier-777");
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task No_assignment_within_the_wait_unwinds_the_order_on_a_durable_timer()
    {
        var order = New(courierWaitSeconds: 2);

        var run = await env.RunAsync(order, Forward(SagaStep.CourierDispatch, FaultKind.Pending));

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Result.FailedStep.Should().Be(SagaStep.CourierDispatch);
        run.Result.Reason.Should().Contain("no courier assigned within 2s");
        run.Snapshot.Courier.Should().Be(EffectState.Undone);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_customer_cancellation_is_accepted_and_unwinds_everything()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));

        var handle = await env.StartAsync(order);
        await WaitForStageAsync(handle, SagaStage.AwaitingCourier);
        var decision = await handle.ExecuteUpdateAsync(wf => wf.RequestCancellationAsync("customer changed their mind"));
        var run = await env.ObserveAsync(order, await handle.GetResultAsync());

        decision.Accepted.Should().BeTrue();
        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Result.FailedStep.Should().BeNull();
        run.Result.Reason.Should().Be("cancelled by the customer");
        run.Result.Compensations.Select(c => c.Step).Should().Equal(AllReversed);
        run.Violations.Should().BeEmpty();
        (await handle.QueryAsync(wf => wf.State())).Timeline.Should().Contain(e => e.Kind == "cancel-requested");
    }

    [Fact]
    public async Task A_cancellation_without_a_reason_is_rejected_by_the_validator_and_leaves_no_trace()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));
        var handle = await env.StartAsync(order);
        await WaitForStageAsync(handle, SagaStage.AwaitingCourier);

        var act = () => handle.ExecuteUpdateAsync(wf => wf.RequestCancellationAsync(""));

        await act.Should().ThrowAsync<WorkflowUpdateFailedException>();
        (await handle.QueryAsync(wf => wf.State())).CancelRequested.Should().BeFalse();
        await handle.SignalAsync(wf => wf.CourierAssignedAsync("courier-123"));
        (await handle.GetResultAsync()).Status.Should().Be(SagaStatus.Completed);
    }

    [Fact]
    public async Task An_operator_cancelling_the_workflow_still_gets_the_order_unwound()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));
        var handle = await env.StartAsync(order);
        await WaitForStageAsync(handle, SagaStage.AwaitingCourier);

        await handle.CancelAsync();
        var run = await env.ObserveAsync(order, await handle.GetResultAsync());

        run.Result.Status.Should().Be(SagaStatus.Compensated);
        run.Result.Reason.Should().Be("workflow cancelled");
        run.Result.Compensations.Select(c => c.Step).Should().Equal(AllReversed);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Submitting_the_same_order_twice_joins_the_running_saga()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));
        var options = new WorkflowOptions(order.WorkflowId(), env.TaskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        };

        var first = await env.Client.StartWorkflowAsync((OrderSagaWorkflow wf) => wf.RunAsync(order), options);
        var second = await env.Client.StartWorkflowAsync((OrderSagaWorkflow wf) => wf.RunAsync(order), options);
        await WaitForStageAsync(first, SagaStage.AwaitingCourier);
        await first.SignalAsync(wf => wf.CourierAssignedAsync("courier-101"));

        second.ResultRunId.Should().Be(first.ResultRunId);
        var run = await env.ObserveAsync(order, await second.GetResultAsync());
        run.Snapshot.Charges.Should().Be(1);
        run.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Querying_a_running_saga_shows_its_stage_and_timeline()
    {
        var order = New();
        await env.Faults.AddAsync(order.OrderId, Forward(SagaStep.CourierDispatch, FaultKind.Pending));
        var handle = await env.StartAsync(order);

        var state = await WaitForStageAsync(handle, SagaStage.AwaitingCourier);

        state.Timeline.Where(e => e.Kind == "step-succeeded").Select(e => e.Step)
            .Should().Equal(SagaStep.PlatformAccept, SagaStep.KitchenTicket, SagaStep.Payment, SagaStep.CourierDispatch);
        await handle.SignalAsync(wf => wf.CourierAssignedAsync("courier-202"));
        await handle.GetResultAsync();
    }

    [Fact]
    public async Task An_unwound_order_notifies_the_customer_once_and_says_whether_money_comes_back()
    {
        var completed = New();
        var declined = New();
        var noCourier = New();

        await env.RunAsync(completed);
        await env.RunAsync(declined, Forward(SagaStep.Payment, FaultKind.Reject));
        await env.RunAsync(noCourier, Forward(SagaStep.CourierDispatch, FaultKind.Reject), Undo(SagaStep.CourierDispatch, FaultKind.LostResponse));

        (await env.Downstream.NotificationsAsync(completed.OrderId)).Should().BeEmpty();
        (await env.Downstream.NotificationsAsync(declined.OrderId)).Should().Equal("order-cancelled");
        (await env.Downstream.NotificationsAsync(noCourier.OrderId)).Should().Equal("order-cancelled-refund-issued");
    }

    internal static async Task<SagaState> WaitForStageAsync(WorkflowHandle<OrderSagaWorkflow, SagaResult> handle, SagaStage stage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var state = await handle.QueryAsync(wf => wf.State());
            if (state.Stage == stage)
            {
                return state;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"saga stayed in {state.Stage}, expected {stage}");
            }

            await Task.Delay(50);
        }
    }
}
