using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OrderSaga.App.Api;
using OrderSaga.Core;
using OrderSaga.IntegrationTests.Infrastructure;
using static OrderSaga.IntegrationTests.Infrastructure.Orders;

namespace OrderSaga.IntegrationTests;

/// <summary>The HTTP API (Roles=api) in-process, starting sagas that the test environment's worker runs.</summary>
public sealed class ApiTests(SagaEnvironment env) : IAsyncLifetime
{
    private const string Key = "api-test-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private ApiFactory _factory = null!;
    private HttpClient _http = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(env);
        _http = _factory.CreateClient();
        _http.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Requests_without_the_api_key_are_unauthorized()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/orders/x", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_need_no_key_and_report_ready()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Placing_an_order_runs_the_saga_and_the_result_is_readable()
    {
        var order = New("api");

        var placed = await PostAsync("/api/v1/orders", new PlaceOrderRequest(order, null));
        placed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        placed.Headers.Location!.ToString().Should().Be($"/api/v1/orders/{order.OrderId}");

        var view = await WaitForResultAsync(order.OrderId);
        view.WorkflowStatus.Should().Be("Completed");
        view.Result!.Status.Should().Be(SagaStatus.Completed);
        view.State!.Stage.Should().Be(SagaStage.Completed);

        var truth = await _http.GetFromJsonAsync<DownstreamView>($"/api/v1/orders/{order.OrderId}/downstream", Json);
        truth!.Violations.Should().BeEmpty();
        truth.Snapshot.Charges.Should().Be(1);
    }

    [Fact]
    public async Task The_same_order_twice_joins_and_a_different_body_under_the_same_id_conflicts()
    {
        var order = New("api");

        (await PostAsync("/api/v1/orders", new PlaceOrderRequest(order, null))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var again = await PostAsync("/api/v1/orders", new PlaceOrderRequest(order, null));
        var changed = await PostAsync("/api/v1/orders",
            new PlaceOrderRequest(order with { Lines = [new OrderLine("doner", "Döner", 1, 25_000)] }, null));

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<PlaceOrderResponse>(Json))!.Joined.Should().BeTrue();
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await WaitForResultAsync(order.OrderId);
        (await env.Downstream.SnapshotAsync(order.OrderId)).Charges.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_orders_and_invalid_faults_are_rejected_before_any_workflow_starts()
    {
        var invalid = New("api") with { Lines = [] };
        var badFault = new PlaceOrderRequest(New("api"), [new FaultRule(SagaStep.Payment, FaultPhase.Forward, FaultKind.Pending)]);

        (await PostAsync("/api/v1/orders", new PlaceOrderRequest(invalid, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync("/api/v1/orders", badFault)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _http.GetAsync(new Uri($"/api/v1/orders/{invalid.OrderId}", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_while_waiting_is_accepted_and_cancel_after_the_end_is_a_conflict()
    {
        var order = New("api");
        var request = new PlaceOrderRequest(order, [Forward(SagaStep.CourierDispatch, FaultKind.Pending)]);
        (await PostAsync("/api/v1/orders", request)).EnsureSuccessStatusCode();
        await WaitForStageAsync(order.OrderId, SagaStage.AwaitingCourier);

        var cancel = await PostAsync($"/api/v1/orders/{order.OrderId}/cancel", new CancelRequest("changed my mind"));
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await WaitForResultAsync(order.OrderId)).Result!.Status.Should().Be(SagaStatus.Compensated);

        var late = await PostAsync($"/api/v1/orders/{order.OrderId}/cancel", new CancelRequest("again"));
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_courier_webhook_completes_a_pending_dispatch()
    {
        var order = New("api");
        (await PostAsync("/api/v1/orders", new PlaceOrderRequest(order, [Forward(SagaStep.CourierDispatch, FaultKind.Pending)])))
            .EnsureSuccessStatusCode();
        await WaitForStageAsync(order.OrderId, SagaStage.AwaitingCourier);

        (await PostAsync($"/api/v1/orders/{order.OrderId}/courier-assigned", new CourierAssignment("courier-321")))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await WaitForResultAsync(order.OrderId)).Result!.CourierId.Should().Be("courier-321");
    }

    private Task<HttpResponseMessage> PostAsync<T>(string path, T body) => _http.PostAsJsonAsync(path, body, Json);

    private async Task<OrderView> WaitForResultAsync(string orderId)
    {
        for (var i = 0; i < 300; i++)
        {
            var view = await _http.GetFromJsonAsync<OrderView>($"/api/v1/orders/{orderId}", Json);
            if (view?.Result is not null)
            {
                return view;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(orderId);
    }

    private async Task WaitForStageAsync(string orderId, SagaStage stage)
    {
        for (var i = 0; i < 300; i++)
        {
            using var response = await _http.GetAsync(new Uri($"/api/v1/orders/{orderId}", UriKind.Relative));
            if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<OrderView>(Json))?.State?.Stage == stage)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"{orderId} never reached {stage}");
    }

    private sealed class ApiFactory(SagaEnvironment env) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Roles", "api");
            builder.UseSetting("ConnectionStrings:Downstream", env.AppConnectionString);
            builder.UseSetting("Temporal:Address", env.TemporalAddress);
            builder.UseSetting("Temporal:TaskQueue", env.TaskQueue);
            builder.UseSetting("Api:Key", Key);
            builder.UseSetting("Api:SimulationEnabled", "true");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
