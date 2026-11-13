using OrderSaga.Downstream;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace OrderSaga.Workflows;

/// <summary>Input of every saga activity. One shape keeps histories readable and the activity surface small.</summary>
/// <param name="IdempotencyKey">The key that names this step's effect downstream (<c>orderId:step</c>).</param>
/// <param name="KeyPerAttempt">Lab only: append the attempt number, which is what a client without stable keys does.</param>
/// <param name="WriteTombstone">Whether an undo that finds nothing leaves a tombstone (lab variants switch it off).</param>
public sealed record StepInput(
    string OrderId,
    string StoreId,
    string IdempotencyKey,
    long AmountKurus = 0,
    int LineCount = 0,
    bool KeyPerAttempt = false,
    bool WriteTombstone = true);

/// <summary>Input of the v2 customer notification after an unwind.</summary>
public sealed record CustomerNotice(string OrderId, string IdempotencyKey, string Kind);

/// <summary>Error types the workflow and its retry policy distinguish.</summary>
public static class SagaErrors
{
    /// <summary>A business "no". Never retried; it starts the unwind.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Downstream unreachable or answer lost. Retried with backoff.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>A late forward call found its compensation's tombstone. Never retried.</summary>
    public const string AlreadyCompensated = "AlreadyCompensated";

    /// <summary>Idempotency key reused with another body: a bug, never retried.</summary>
    public const string IdempotencyConflict = "IdempotencyConflict";
}

/// <summary>
/// The saga's activities: thin adapters from Temporal to the downstream systems. They hold no state and make no
/// decisions; translating downstream errors into retryable or non-retryable failures is their only job, because that
/// classification is what the retry policy and the workflow act on.
/// </summary>
public sealed class OrderActivities(DownstreamSystems downstream)
{
    [Activity]
    public Task<string> AcceptOnPlatformAsync(StepInput input) => Run(async (call, ct) =>
    {
        await downstream.AcceptOnPlatformAsync(call, ct);
        return "accepted";
    }, input);

    [Activity]
    public Task<string> CancelOnPlatformAsync(StepInput input) =>
        Run(async (call, ct) => (await downstream.CancelOnPlatformAsync(call, input.WriteTombstone, ct)).ToString(), input, compensation: true);

    [Activity]
    public Task<long> ConfirmKitchenTicketAsync(StepInput input) =>
        Run((call, ct) => downstream.ConfirmKitchenTicketAsync(call, input.LineCount, ct), input);

    [Activity]
    public Task<string> CancelKitchenTicketAsync(StepInput input) =>
        Run(async (call, ct) => (await downstream.CancelKitchenTicketAsync(call, input.WriteTombstone, ct)).ToString(), input, compensation: true);

    [Activity]
    public Task<string> ChargePaymentAsync(StepInput input) => Run(async (call, ct) =>
    {
        await downstream.ChargeAsync(call, input.AmountKurus, ct);
        return "captured";
    }, input);

    [Activity]
    public Task<string> RefundPaymentAsync(StepInput input) =>
        Run(async (call, ct) => (await downstream.RefundAsync(call, input.WriteTombstone, ct)).ToString(), input, compensation: true);

    [Activity]
    public Task<DispatchResult> DispatchCourierAsync(StepInput input) =>
        Run((call, ct) => downstream.DispatchCourierAsync(call, ct), input);

    [Activity]
    public Task<string> CancelCourierAsync(StepInput input) =>
        Run(async (call, ct) => (await downstream.CancelCourierAsync(call, input.WriteTombstone, ct)).ToString(), input, compensation: true);

    /// <summary>Saga v2: tell the customer the order was unwound (and whether money is coming back).</summary>
    [Activity]
    public async Task<bool> NotifyCustomerAsync(CustomerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return await downstream.NotifyCustomerAsync(
            notice.OrderId, notice.IdempotencyKey, notice.Kind, ActivityExecutionContext.Current.CancellationToken);
    }

    private static async Task<T> Run<T>(Func<EffectCall, CancellationToken, Task<T>> action, StepInput input, bool compensation = false)
    {
        var context = ActivityExecutionContext.Current;
        // A compensation always addresses the base key: it must undo the effect whatever key the forward attempts used.
        var key = input.KeyPerAttempt && !compensation
            ? $"{input.IdempotencyKey}#{context.Info.Attempt}"
            : input.IdempotencyKey;
        var call = new EffectCall(input.OrderId, input.StoreId, key);
        try
        {
            return await action(call, context.CancellationToken);
        }
        catch (DownstreamRejectedException ex)
        {
            throw new ApplicationFailureException(ex.Message, SagaErrors.Rejected, nonRetryable: true);
        }
        catch (EffectAlreadyCompensatedException ex)
        {
            throw new ApplicationFailureException(ex.Message, SagaErrors.AlreadyCompensated, nonRetryable: true);
        }
        catch (IdempotencyConflictException ex)
        {
            throw new ApplicationFailureException(ex.Message, SagaErrors.IdempotencyConflict, nonRetryable: true);
        }
        catch (DownstreamUnavailableException ex)
        {
            throw new ApplicationFailureException(ex.Message, SagaErrors.Unavailable);
        }
    }
}
