using System.Text.RegularExpressions;
using OrderSaga.Core;
using OrderSaga.IntegrationTests.Infrastructure;
using OrderSaga.Workflows;
using Temporalio.Worker;
using static OrderSaga.IntegrationTests.Infrastructure.Orders;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// Records one event history per saga path and replays it against the current workflow code. With
/// <c>ORDER_SAGA_RECORD_HISTORIES=&lt;dir&gt;</c> the histories are also written out; the committed copies under
/// <c>tests/OrderSaga.UnitTests/Histories</c> are the golden files the unit tests replay without a server, so a change
/// that would break running sagas fails the fast test suite (ADR 0004).
/// </summary>
public sealed class HistoryRecordingTests(SagaEnvironment env)
{
    public static TheoryData<string> Scenarios() =>
    [
        "completed", "payment-rejected", "payment-lost-then-outage", "courier-pending-then-assigned",
        "customer-cancelled", "courier-timeout", "operator-cancelled",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Every_saga_path_replays_deterministically(string scenario)
    {
        var order = New($"golden-{scenario}") with { CourierWaitSeconds = scenario == "courier-timeout" ? 1 : 300 };
        var faults = scenario switch
        {
            "payment-rejected" => [Forward(SagaStep.Payment, FaultKind.Reject)],
            "payment-lost-then-outage" => [Forward(SagaStep.Payment, FaultKind.LostResponse), Forward(SagaStep.Payment, FaultKind.Transient, 10)],
            "courier-pending-then-assigned" or "customer-cancelled" or "courier-timeout" or "operator-cancelled" =>
                [Forward(SagaStep.CourierDispatch, FaultKind.Pending)],
            _ => Array.Empty<FaultRule>(),
        };
        foreach (var fault in faults)
        {
            await env.Faults.AddAsync(order.OrderId, fault);
        }

        var handle = await env.StartAsync(order);
        switch (scenario)
        {
            case "courier-pending-then-assigned":
                await OrderSagaTests.WaitForStageAsync(handle, SagaStage.AwaitingCourier);
                await handle.SignalAsync(wf => wf.CourierAssignedAsync("courier-900"));
                break;
            case "customer-cancelled":
                await OrderSagaTests.WaitForStageAsync(handle, SagaStage.AwaitingCourier);
                await handle.ExecuteUpdateAsync(wf => wf.RequestCancellationAsync("golden"));
                break;
            case "operator-cancelled":
                await OrderSagaTests.WaitForStageAsync(handle, SagaStage.AwaitingCourier);
                await handle.CancelAsync();
                break;
        }

        await handle.GetResultAsync();
        var history = await handle.FetchHistoryAsync();

        var replay = await new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<OrderSagaWorkflow>())
            .ReplayWorkflowAsync(history, throwOnReplayFailure: false);
        replay.ReplayFailure.Should().BeNull();

        var outputDir = Environment.GetEnvironmentVariable("ORDER_SAGA_RECORD_HISTORIES");
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(Path.Combine(outputDir, $"{scenario}.json"), Sanitize(history.ToJson()));
        }
    }

    /// <summary>
    /// Golden files are committed, so they must not carry the recording machine: worker identities hold the host name
    /// and failure stack traces hold local source paths. Neither takes part in replay.
    /// </summary>
    private static string Sanitize(string json)
    {
        const string JsonString = @"""(?:[^""\\]|\\.)*""";
        json = Regex.Replace(json, @"""stackTrace"": " + JsonString, @"""stackTrace"": """"");
        return Regex.Replace(json, @"""identity"": " + JsonString, @"""identity"": ""golden-recorder""");
    }
}
