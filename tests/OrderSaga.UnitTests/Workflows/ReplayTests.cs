using OrderSaga.Core;
using OrderSaga.Workflows;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace OrderSaga.UnitTests.Workflows;

/// <summary>
/// Golden replay tests. Each file is the event history of a real saga run on a Temporal server (recorded by
/// HistoryRecordingTests). Replaying it against today's workflow code must reproduce the exact same commands; if it
/// does not, deploying the change would break every saga of that shape still running in production. No server needed.
/// </summary>
public sealed class ReplayTests
{
    public static TheoryData<string> Histories() =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Histories"), "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AppContext.BaseDirectory, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task Recorded_history_replays_against_the_current_workflow(string file)
    {
        var history = Load(file);

        var result = await new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<OrderSagaWorkflow>())
            .ReplayWorkflowAsync(history, throwOnReplayFailure: false, TestContext.Current.CancellationToken);

        result.ReplayFailure.Should().BeNull();
    }

    [Fact]
    public void Every_saga_path_has_a_golden_history()
    {
        Histories().Select(h => Path.GetFileNameWithoutExtension((string)h.Data))
            .Should().Contain(["completed", "payment-rejected", "payment-lost-then-outage", "courier-pending-then-assigned",
                "customer-cancelled", "courier-timeout", "operator-cancelled"]);
    }

    /// <summary>The replayer is not a rubber stamp: reordering two steps is caught on the very first recorded history.</summary>
    [Fact]
    public async Task Reordering_steps_is_detected_as_non_determinism()
    {
        var history = Load("Histories/v1/completed.json");

        var result = await new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<ReorderedSaga>())
            .ReplayWorkflowAsync(history, throwOnReplayFailure: false, TestContext.Current.CancellationToken);

        result.ReplayFailure.Should().BeOfType<WorkflowNondeterminismException>();
    }

    internal static WorkflowHistory Load(string relativePath) =>
        WorkflowHistory.FromJson("golden", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath)));

    /// <summary>A plausible-looking refactor that confirms the kitchen ticket before the platform accepts.</summary>
    [Workflow("OrderSagaWorkflow")]
    public sealed class ReorderedSaga
    {
        [WorkflowRun]
        public async Task<SagaResult> RunAsync(PlaceOrder order)
        {
            var options = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(5) };
            await Workflow.ExecuteActivityAsync(
                (OrderActivities a) => a.ConfirmKitchenTicketAsync(new StepInput(order.OrderId, order.StoreId, "k")), options);
            await Workflow.ExecuteActivityAsync(
                (OrderActivities a) => a.AcceptOnPlatformAsync(new StepInput(order.OrderId, order.StoreId, "p")), options);
            return new SagaResult(order.OrderId, SagaStatus.Completed, null, null, [], [], null, order.TotalKurus());
        }
    }
}
