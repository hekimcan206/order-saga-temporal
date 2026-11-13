using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderSaga.Core;
using OrderSaga.Downstream;
using OrderSaga.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace OrderSaga.App.Api;

/// <summary>Request body for placing an order. <c>Faults</c> is honoured only when the simulation is enabled.</summary>
public sealed record PlaceOrderRequest(PlaceOrder Order, IReadOnlyList<FaultRule>? Faults);

public sealed record PlaceOrderResponse(string OrderId, string WorkflowId, string? RunId, bool Joined);

public sealed record OrderView(string OrderId, string WorkflowStatus, SagaState? State, SagaResult? Result);

public sealed record CancelRequest(string Reason);

public sealed record CourierAssignment(string CourierId);

public sealed record DownstreamView(DownstreamSnapshot Snapshot, IReadOnlyList<Violation> Violations);

public sealed class ApiOptions
{
    public const string Section = "Api";

    /// <summary>When set, every /api call needs it in X-Api-Key. Empty disables the check (local development only).</summary>
    public string Key { get; set; } = "";

    /// <summary>Accept injected faults on order placement and expose the downstream truth endpoint (demo and tests).</summary>
    public bool SimulationEnabled { get; set; }
}

internal static class OrderEndpoints
{
    private const string BodyHashMemo = "bodySha256";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1").AddEndpointFilter<ApiKeyFilter>();

        api.MapPost("/orders", PlaceAsync)
            .WithSummary("Place an order: starts (or joins) the saga for this order id.");
        api.MapGet("/orders/{orderId}", GetAsync)
            .WithSummary("Saga stage, timeline and, once closed, its result.");
        api.MapPost("/orders/{orderId}/cancel", CancelAsync)
            .WithSummary("Customer cancellation (workflow update): accepted while the saga can still unwind.");
        api.MapPost("/orders/{orderId}/courier-assigned", CourierAssignedAsync)
            .WithSummary("Courier platform webhook: a courier was assigned to a pending dispatch (workflow signal).");
        api.MapGet("/orders/{orderId}/downstream", DownstreamAsync)
            .WithSummary("Simulation only: what the downstream systems recorded, judged against the saga invariant.");
        return routes;
    }

    private static async Task<IResult> PlaceAsync(
        PlaceOrderRequest request,
        ITemporalClient client,
        FaultInjector faults,
        IOptions<ApiOptions> api,
        IOptions<TemporalOptions> temporal,
        CancellationToken cancellationToken)
    {
        if (request?.Order is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = ["order is required"] });
        }

        var errors = request.Order.Validate().ToList();
        if (request.Faults is { Count: > 0 })
        {
            if (!api.Value.SimulationEnabled)
            {
                errors.Add("faults are only accepted when the simulation is enabled");
            }
            else if (request.Faults.Count > 10)
            {
                errors.Add("at most 10 faults per order");
            }
            else
            {
                errors.AddRange(request.Faults.SelectMany(f => f.Validate()));
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [.. errors.Distinct()] });
        }

        var order = request.Order;
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(order, JsonSerializerOptions.Web)));
        var handle = client.GetWorkflowHandle<OrderSagaWorkflow>(order.WorkflowId());

        // A re-sent order with the same id joins the existing saga; the same id with a different body is a client bug.
        var existing = await TryDescribeAsync(handle, cancellationToken);
        if (existing is not null)
        {
            return await SameBodyAsync(existing, bodyHash)
                ? Results.Ok(new PlaceOrderResponse(order.OrderId, handle.Id, existing.RunId, Joined: true))
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "order id reused with a different order");
        }

        if (api.Value.SimulationEnabled && request.Faults is { Count: > 0 })
        {
            foreach (var fault in request.Faults)
            {
                await faults.AddAsync(order.OrderId, fault, cancellationToken);
            }
        }

        try
        {
            var started = await client.StartWorkflowAsync(
                (OrderSagaWorkflow wf) => wf.RunAsync(order),
                new WorkflowOptions(order.WorkflowId(), temporal.Value.TaskQueue)
                {
                    // Two concurrent identical requests: both land on the one saga instead of one of them failing.
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    // A closed saga is never restarted for the same order id.
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    Memo = new Dictionary<string, object> { [BodyHashMemo] = bodyHash },
                    Rpc = new() { CancellationToken = cancellationToken },
                });
            return Results.Accepted($"/api/v1/orders/{order.OrderId}",
                new PlaceOrderResponse(order.OrderId, started.Id, started.ResultRunId, Joined: false));
        }
        catch (WorkflowAlreadyStartedException)
        {
            return Results.Ok(new PlaceOrderResponse(order.OrderId, handle.Id, null, Joined: true));
        }
    }

    private static async Task<IResult> GetAsync(string orderId, ITemporalClient client, CancellationToken cancellationToken)
    {
        if (!Identifiers.IsValid(orderId))
        {
            return Results.NotFound();
        }

        var handle = client.GetWorkflowHandle<OrderSagaWorkflow>(PlaceOrder.WorkflowIdFor(orderId));
        var description = await TryDescribeAsync(handle, cancellationToken);
        if (description is null)
        {
            return Results.NotFound();
        }

        var state = await handle.QueryAsync(wf => wf.State(), new() { Rpc = new() { CancellationToken = cancellationToken } });
        SagaResult? result = null;
        if (description.Status == WorkflowExecutionStatus.Completed)
        {
            result = await handle.GetResultAsync<SagaResult>(rpcOptions: new() { CancellationToken = cancellationToken });
        }

        return Results.Ok(new OrderView(orderId, description.Status.ToString(), state, result));
    }

    private static async Task<IResult> CancelAsync(
        string orderId, CancelRequest request, ITemporalClient client, CancellationToken cancellationToken)
    {
        if (!Identifiers.IsValid(orderId))
        {
            return Results.NotFound();
        }

        var handle = client.GetWorkflowHandle<OrderSagaWorkflow>(PlaceOrder.WorkflowIdFor(orderId));
        var reason = request?.Reason ?? "";
        try
        {
            var decision = await handle.ExecuteUpdateAsync(
                wf => wf.RequestCancellationAsync(reason),
                new() { Rpc = new() { CancellationToken = cancellationToken } });
            return Results.Ok(decision);
        }
        catch (WorkflowUpdateFailedException ex)
        {
            return Results.Conflict(new CancelDecision(false, ex.InnerException?.Message ?? ex.Message));
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            // Updates are only delivered to running workflows: a closed saga can no longer be cancelled.
            return await TryDescribeAsync(handle, cancellationToken) is null
                ? Results.NotFound()
                : Results.Conflict(new CancelDecision(false, "the order has already finished"));
        }
    }

    private static async Task<IResult> CourierAssignedAsync(
        string orderId, CourierAssignment assignment, ITemporalClient client, CancellationToken cancellationToken)
    {
        if (!Identifiers.IsValid(orderId) || assignment is null || !Identifiers.IsValid(assignment.CourierId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["courierId"] = ["invalid order or courier id"] });
        }

        try
        {
            await client.GetWorkflowHandle<OrderSagaWorkflow>(PlaceOrder.WorkflowIdFor(orderId))
                .SignalAsync(wf => wf.CourierAssignedAsync(assignment.CourierId), new() { Rpc = new() { CancellationToken = cancellationToken } });
            return Results.Accepted();
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> DownstreamAsync(
        string orderId, DownstreamSystems downstream, ITemporalClient client, IOptions<ApiOptions> api, CancellationToken cancellationToken)
    {
        if (!api.Value.SimulationEnabled || !Identifiers.IsValid(orderId))
        {
            return Results.NotFound();
        }

        var handle = client.GetWorkflowHandle<OrderSagaWorkflow>(PlaceOrder.WorkflowIdFor(orderId));
        var description = await TryDescribeAsync(handle, cancellationToken);
        if (description is null || description.Status != WorkflowExecutionStatus.Completed)
        {
            return Results.Conflict(new { error = "the saga has not finished" });
        }

        var result = await handle.GetResultAsync<SagaResult>(rpcOptions: new() { CancellationToken = cancellationToken });
        var snapshot = await downstream.SnapshotAsync(orderId, cancellationToken);
        return Results.Ok(new DownstreamView(snapshot, ConsistencyChecker.Check(result.Status, result.TotalKurus, snapshot)));
    }

    private static async Task<WorkflowExecutionDescription?> TryDescribeAsync(WorkflowHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            return await handle.DescribeAsync(new() { Rpc = new() { CancellationToken = cancellationToken } });
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<bool> SameBodyAsync(WorkflowExecutionDescription description, string bodyHash) =>
        description.Memo.TryGetValue(BodyHashMemo, out var memo) &&
        string.Equals(await memo.ToValueAsync<string>(), bodyHash, StringComparison.Ordinal);
}

/// <summary>Constant-time API key check. A shared key is a demo-grade control; see docs/threat-model.md.</summary>
internal sealed class ApiKeyFilter(IOptions<ApiOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.Key;
        if (expected.Length > 0)
        {
            var given = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(expected)))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
