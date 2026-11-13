using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrderSaga.App.Api;
using OrderSaga.Core;

namespace OrderSaga.App.Demo;

/// <summary>
/// The <c>docker compose up</c> demo: drives the public HTTP API through eight scenarios (happy path, rejections, a
/// payment captured behind a lost response, flaky dependencies, a pending courier, customer cancellation, a courier
/// timeout), then asks the downstream systems what really happened. Exits 0 only if every scenario ended as expected
/// and no consistency rule is broken.
/// </summary>
internal sealed class DemoRunner(HttpClient http, TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Scenario(
        string Name,
        SagaStatus Expected,
        FaultRule[] Faults,
        Func<string, Task<HttpResponseMessage>>? WhileRunning = null,
        int CourierWaitSeconds = 300);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await WaitForApiAsync(cancellationToken);
        var runId = Guid.NewGuid().ToString("N")[..8];

        Scenario[] scenarios =
        [
            new("happy-path", SagaStatus.Completed, []),
            new("card-declined", SagaStatus.Compensated, [Fault(SagaStep.Payment, FaultKind.Reject)]),
            new("kitchen-out-of-stock", SagaStatus.Compensated, [Fault(SagaStep.KitchenTicket, FaultKind.Reject)]),
            new("payment-captured-response-lost-then-outage", SagaStatus.Compensated,
                [Fault(SagaStep.Payment, FaultKind.LostResponse), Fault(SagaStep.Payment, FaultKind.Transient, 10)]),
            new("flaky-everything", SagaStatus.Completed,
                [.. SagaSteps.ForwardOrder.Select(s => Fault(s, FaultKind.Transient, 2))]),
            new("courier-pending-then-assigned", SagaStatus.Completed,
                [Fault(SagaStep.CourierDispatch, FaultKind.Pending)],
                id => PostAsync($"api/v1/orders/{id}/courier-assigned", new CourierAssignment("courier-042"), cancellationToken)),
            new("customer-cancels-while-waiting", SagaStatus.Compensated,
                [Fault(SagaStep.CourierDispatch, FaultKind.Pending)],
                id => PostAsync($"api/v1/orders/{id}/cancel", new CancelRequest("customer changed their mind"), cancellationToken)),
            new("no-courier-within-3s", SagaStatus.Compensated,
                [Fault(SagaStep.CourierDispatch, FaultKind.Pending)], CourierWaitSeconds: 3),
        ];

        var failures = new List<string>();
        var violations = 0;
        foreach (var scenario in scenarios)
        {
            var started = Stopwatch.GetTimestamp();
            var orderId = $"demo-{runId}-{scenario.Name}";
            var order = new PlaceOrder(orderId, "store-01", "cust-demo",
                [new OrderLine("iskender", "İskender", 1, 42_000), new OrderLine("salgam", "Şalgam", 2, 3_500)],
                scenario.CourierWaitSeconds);

            (await PostAsync("api/v1/orders", new PlaceOrderRequest(order, scenario.Faults), cancellationToken)).EnsureSuccessStatusCode();
            if (scenario.WhileRunning is not null)
            {
                await WaitForStageAsync(orderId, SagaStage.AwaitingCourier, cancellationToken);
                (await scenario.WhileRunning(orderId)).EnsureSuccessStatusCode();
            }

            var view = await WaitForCloseAsync(orderId, cancellationToken);
            var truth = await http.GetFromJsonAsync<DownstreamView>($"api/v1/orders/{orderId}/downstream", Json, cancellationToken);
            var ok = view.Result!.Status == scenario.Expected && truth!.Violations.Count == 0;
            violations += truth!.Violations.Count;
            if (!ok)
            {
                failures.Add(scenario.Name);
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                scenario = scenario.Name,
                ok,
                status = view.Result.Status,
                expected = scenario.Expected,
                failedStep = view.Result.FailedStep,
                reason = view.Result.Reason,
                compensations = view.Result.Compensations.Select(c => c.Step),
                downstream = truth.Snapshot,
                violations = truth.Violations.Select(v => v.Code),
                ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            }, Json));
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            summary = true,
            scenarios = scenarios.Length,
            passed = scenarios.Length - failures.Count,
            violations,
            failures,
        }, Json));
        return failures.Count == 0 ? 0 : 1;
    }

    private static FaultRule Fault(SagaStep step, FaultKind kind, int times = 1) => new(step, FaultPhase.Forward, kind, times);

    private Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(path, body, Json, cancellationToken);

    private async Task WaitForApiAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("health/ready", UriKind.Relative), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (attempt < 120)
            {
            }

            if (attempt >= 120)
            {
                throw new TimeoutException("the API did not become ready");
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task WaitForStageAsync(string orderId, SagaStage stage, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 300; i++)
        {
            var view = await http.GetFromJsonAsync<OrderView>($"api/v1/orders/{orderId}", Json, cancellationToken);
            if (view?.State?.Stage == stage)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"{orderId} never reached {stage}");
    }

    private async Task<OrderView> WaitForCloseAsync(string orderId, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 600; i++)
        {
            var view = await http.GetFromJsonAsync<OrderView>($"api/v1/orders/{orderId}", Json, cancellationToken);
            if (view?.Result is not null)
            {
                return view;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"{orderId} did not finish");
    }
}
