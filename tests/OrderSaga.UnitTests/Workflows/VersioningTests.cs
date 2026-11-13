using OrderSaga.Core;
using OrderSaga.Workflows;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace OrderSaga.UnitTests.Workflows;

/// <summary>
/// Why the v2 change (notify the customer after an unwind) is behind <c>Workflow.Patched</c>: v1 sagas are still
/// running when v2 deploys, and their histories contain no notification. The patched code replays them; the same
/// change without the patch breaks every v1 saga that unwound, while v1 sagas that completed are unaffected.
/// </summary>
public sealed class VersioningTests
{
    public static TheoryData<string> V1Compensated() =>
    [
        "payment-rejected", "payment-lost-then-outage", "customer-cancelled", "courier-timeout", "operator-cancelled",
    ];

    [Theory]
    [MemberData(nameof(V1Compensated))]
    public async Task The_v2_change_without_its_patch_breaks_unwound_v1_sagas(string scenario)
    {
        var result = await ReplayAsync<UnpatchedOrderSaga>($"Histories/v1/{scenario}.json");
        result.ReplayFailure.Should().BeOfType<WorkflowNondeterminismException>();
    }

    [Theory]
    [MemberData(nameof(V1Compensated))]
    public async Task The_patched_v2_code_replays_unwound_v1_sagas(string scenario)
    {
        var result = await ReplayAsync<OrderSagaWorkflow>($"Histories/v1/{scenario}.json");
        result.ReplayFailure.Should().BeNull();
    }

    [Fact]
    public async Task Completed_v1_sagas_never_reach_the_changed_code()
    {
        (await ReplayAsync<UnpatchedOrderSaga>("Histories/v1/completed.json")).ReplayFailure.Should().BeNull();
    }

    [Fact]
    public void V2_histories_carry_the_patch_marker_and_v1_histories_do_not()
    {
        var v1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Histories/v1/payment-rejected.json"));
        var v2 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Histories/v2/payment-rejected.json"));

        v1.Should().NotContain("EVENT_TYPE_MARKER_RECORDED");
        v2.Should().Contain("EVENT_TYPE_MARKER_RECORDED");
    }

    private static Task<WorkflowReplayResult> ReplayAsync<TWorkflow>(string file) =>
        new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<TWorkflow>())
            .ReplayWorkflowAsync(ReplayTests.Load(file), throwOnReplayFailure: false, TestContext.Current.CancellationToken);

    /// <summary>The v2 behaviour exactly, minus the patch marker: the mistake the patch exists to prevent.</summary>
    [Workflow("OrderSagaWorkflow")]
    public sealed class UnpatchedOrderSaga
    {
        private OrderSagaEngine? _engine;

        [WorkflowRun]
        public async Task<SagaResult> RunAsync(PlaceOrder order)
        {
            _engine = new OrderSagaEngine(order, SagaVariant.Correct, guardWithPatch: false);
            return await _engine.RunAsync();
        }

        [WorkflowSignal("CourierAssigned")]
        public Task CourierAssignedAsync(string courierId)
        {
            _engine?.CourierAssigned(courierId);
            return Task.CompletedTask;
        }

        [WorkflowUpdate("RequestCancellation")]
        public Task<CancelDecision> RequestCancellationAsync(string reason) => Task.FromResult(_engine!.RequestCancellation(reason));
    }
}
