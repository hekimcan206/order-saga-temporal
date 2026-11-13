using System.Linq.Expressions;
using OrderSaga.Core;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace OrderSaga.Workflows;

/// <summary>
/// Saga variants. Only <see cref="Correct"/> is used by the production workflow; each other variant removes exactly one
/// defence so the fault campaign can measure what that defence is worth (docs/benchmarks.md).
/// </summary>
public enum SagaVariant
{
    /// <summary>Compensation registered before the step, stable idempotency keys, tombstoning undo.</summary>
    Correct,

    /// <summary>The common textbook shape: register the compensation only after the step reports success.</summary>
    CompensateAfterSuccess,

    /// <summary>A fresh downstream key per attempt, as a client that generates keys per request does.</summary>
    NoIdempotencyKey,

    /// <summary>Undo only changes what exists; it leaves no tombstone for a late forward call.</summary>
    NoTombstone,
}

/// <summary>
/// The order saga itself, shared by <see cref="OrderSagaWorkflow"/> and the lab workflow. Runs inside workflow code,
/// so everything here is deterministic: workflow time, workflow timers, no I/O except through activities.
/// </summary>
internal sealed class OrderSagaEngine(PlaceOrder order, SagaVariant variant)
{
    /// <summary>Forward steps retry transient failures for a few seconds, then the saga gives up and unwinds.</summary>
    internal static readonly ActivityOptions ForwardOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(200),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(2),
            MaximumAttempts = 5,
            NonRetryableErrorTypes = [SagaErrors.Rejected, SagaErrors.AlreadyCompensated, SagaErrors.IdempotencyConflict],
        },
    };

    /// <summary>
    /// Compensations retry without an attempt limit: giving up on a refund is not an option a workflow should take on
    /// its own. A downstream that stays down shows up as a long-running workflow with a growing attempt count, which is
    /// alertable, instead of a silently closed one.
    /// </summary>
    internal static readonly ActivityOptions CompensationOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(200),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 0,
            NonRetryableErrorTypes = [SagaErrors.IdempotencyConflict],
        },
    };

    private readonly List<SagaEvent> _timeline = [];
    private readonly List<SagaStep> _completed = [];
    private readonly List<CompensationResult> _compensations = [];

    public SagaStage Stage { get; private set; } = SagaStage.Started;

    public bool CancelRequested { get; private set; }

    public string? CourierId { get; private set; }

    public SagaState Snapshot() => new(order.OrderId, Stage, CancelRequested, CourierId, [.. _timeline]);

    /// <summary>Customer cancellation (via a workflow update). Accepted only while the saga can still unwind.</summary>
    public CancelDecision RequestCancellation(string reason)
    {
        CancelRequested = true;
        Record("cancel-requested", null, reason);
        return new CancelDecision(true, "accepted: the order will be unwound after the step in flight finishes");
    }

    public bool CanCancel() => Stage is SagaStage.Started or SagaStage.PlatformAccepted or SagaStage.KitchenConfirmed
        or SagaStage.PaymentCaptured or SagaStage.AwaitingCourier && !CancelRequested;

    public void CourierAssigned(string courierId)
    {
        if (CourierId is null && Identifiers.IsValid(courierId))
        {
            CourierId = courierId;
            Record("courier-assigned", SagaStep.CourierDispatch, courierId);
        }
    }

    public async Task<SagaResult> RunAsync()
    {
        var log = new CompensationLog();
        SagaStep? current = null;
        SagaStep? failedStep = null;
        string reason;

        try
        {
            current = SagaStep.PlatformAccept;
            await StepAsync(log, current.Value, a => a.AcceptOnPlatformAsync(Input(SagaStep.PlatformAccept)),
                a => a.CancelOnPlatformAsync(Input(SagaStep.PlatformAccept)));
            Advance(SagaStage.PlatformAccepted);

            current = SagaStep.KitchenTicket;
            await StepAsync(log, current.Value, a => a.ConfirmKitchenTicketAsync(Input(SagaStep.KitchenTicket)),
                a => a.CancelKitchenTicketAsync(Input(SagaStep.KitchenTicket)));
            Advance(SagaStage.KitchenConfirmed);

            current = SagaStep.Payment;
            await StepAsync(log, current.Value, a => a.ChargePaymentAsync(Input(SagaStep.Payment)),
                a => a.RefundPaymentAsync(Input(SagaStep.Payment)));
            Advance(SagaStage.PaymentCaptured);

            current = SagaStep.CourierDispatch;
            var dispatch = await StepAsync(log, current.Value, a => a.DispatchCourierAsync(Input(SagaStep.CourierDispatch)),
                a => a.CancelCourierAsync(Input(SagaStep.CourierDispatch)));
            if (dispatch.Assigned)
            {
                CourierAssigned(dispatch.CourierId!);
            }
            else
            {
                // The courier platform accepted the job but has nobody yet; it reports the assignment later (a signal).
                // A durable timer bounds the wait, so a forgotten order unwinds instead of waiting forever.
                Stage = SagaStage.AwaitingCourier;
                Record("awaiting-courier", SagaStep.CourierDispatch, $"{order.CourierWaitSeconds}s");
                var assigned = await Workflow.WaitConditionAsync(
                    () => CourierId is not null || CancelRequested, TimeSpan.FromSeconds(order.CourierWaitSeconds));
                if (!assigned)
                {
                    throw new SagaAbortException(SagaStep.CourierDispatch, $"no courier assigned within {order.CourierWaitSeconds}s");
                }

                ThrowIfCancelRequested();
            }

            Stage = SagaStage.Completed;
            Record("completed", null, CourierId);
            return Result(SagaStatus.Completed, null, null);
        }
        catch (SagaAbortException ex)
        {
            failedStep = ex.Step;
            reason = ex.Message;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            failedStep = current;
            reason = Describe(ex);
        }
        catch (Exception ex) when (TemporalException.IsCanceledException(ex))
        {
            // Operator cancelled the workflow (for example from the Temporal UI). The order is unwound, not abandoned.
            failedStep = null;
            reason = "workflow cancelled";
        }

        Stage = SagaStage.Compensating;
        Record("compensating", failedStep, reason);
        var report = await log.UnwindAsync();
        _compensations.AddRange(report.Results);

        var status = report.AllSucceeded ? SagaStatus.Compensated : SagaStatus.CompensationFailed;
        Stage = report.AllSucceeded ? SagaStage.Compensated : SagaStage.CompensationFailed;
        Record(status == SagaStatus.Compensated ? "compensated" : "compensation-failed", failedStep, reason);
        return Result(status, failedStep, reason);
    }

    private async Task<T> StepAsync<T>(
        CompensationLog log,
        SagaStep step,
        Expression<Func<OrderActivities, Task<T>>> forward,
        Expression<Func<OrderActivities, Task<string>>> undo)
    {
        ThrowIfCancelRequested();

        // Register first: from the moment the activity is scheduled its effect may exist, even if we never see a reply.
        if (variant != SagaVariant.CompensateAfterSuccess)
        {
            log.Push(step, () => CompensateAsync(step, undo));
        }

        var result = await Workflow.ExecuteActivityAsync(forward, ForwardOptions);

        if (variant == SagaVariant.CompensateAfterSuccess)
        {
            log.Push(step, () => CompensateAsync(step, undo));
        }

        _completed.Add(step);
        Record("step-succeeded", step, null);
        return result;
    }

    private async Task CompensateAsync(SagaStep step, Expression<Func<OrderActivities, Task<string>>> undo)
    {
        var options = (ActivityOptions)CompensationOptions.Clone();
        // Compensations must run even while the workflow itself is being cancelled, so they do not inherit the
        // workflow cancellation token (the default) and use a token that never fires.
        options.CancellationToken = CancellationToken.None;
        try
        {
            var outcome = await Workflow.ExecuteActivityAsync(undo, options);
            Record("compensated", step, outcome);
        }
        catch (ActivityFailureException ex)
        {
            Record("compensation-failed", step, Describe(ex));
            throw;
        }
    }

    private StepInput Input(SagaStep step) => new(
        order.OrderId,
        order.StoreId,
        SagaSteps.IdempotencyKey(order.OrderId, step),
        AmountKurus: step == SagaStep.Payment ? order.TotalKurus() : 0,
        LineCount: step == SagaStep.KitchenTicket ? order.Lines.Count : 0,
        KeyPerAttempt: variant == SagaVariant.NoIdempotencyKey,
        WriteTombstone: variant != SagaVariant.NoTombstone);

    private void Advance(SagaStage stage)
    {
        Stage = stage;
        ThrowIfCancelRequested();
    }

    private void ThrowIfCancelRequested()
    {
        if (CancelRequested)
        {
            throw new SagaAbortException(null, "cancelled by the customer");
        }
    }

    private void Record(string kind, SagaStep? step, string? detail) =>
        _timeline.Add(new SagaEvent(Workflow.UtcNow, kind, step, detail));

    private SagaResult Result(SagaStatus status, SagaStep? failedStep, string? reason) =>
        new(order.OrderId, status, failedStep, reason, [.. _completed], [.. _compensations], CourierId, order.TotalKurus());

    private static string Describe(ActivityFailureException ex) => ex.InnerException switch
    {
        ApplicationFailureException app => $"{app.ErrorType}: {app.Message}",
        TimeoutFailureException timeout => $"timeout: {timeout.TimeoutType}",
        { } inner => inner.Message,
        null => ex.Message,
    };
}

/// <summary>Ends the forward path without an activity failure (customer cancel, courier wait expired).</summary>
internal sealed class SagaAbortException : Exception
{
    public SagaAbortException(SagaStep? step, string message) : base(message) => Step = step;

    public SagaAbortException() { }

    public SagaAbortException(string message) : base(message) { }

    public SagaAbortException(string message, Exception inner) : base(message, inner) { }

    public SagaStep? Step { get; }
}
