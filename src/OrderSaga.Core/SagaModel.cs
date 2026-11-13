namespace OrderSaga.Core;

/// <summary>How a saga ended.</summary>
public enum SagaStatus
{
    /// <summary>Every step succeeded: the order is accepted, cooked, paid and has a courier.</summary>
    Completed,

    /// <summary>A step failed (or the order was cancelled) and every compensation succeeded.</summary>
    Compensated,

    /// <summary>A compensation failed permanently. The workflow ends loudly; an operator has to look at it.</summary>
    CompensationFailed,
}

/// <summary>Where a running saga is. Exposed through a workflow query, so it never needs a database of its own.</summary>
public enum SagaStage
{
    Started,
    PlatformAccepted,
    KitchenConfirmed,
    PaymentCaptured,
    AwaitingCourier,
    Completed,
    Compensating,
    Compensated,
    CompensationFailed,
}

/// <summary>One line of the saga's own timeline (workflow time, so it replays identically).</summary>
public sealed record SagaEvent(DateTime AtUtc, string Kind, SagaStep? Step, string? Detail);

/// <summary>The query result: current stage plus the timeline so far.</summary>
public sealed record SagaState(
    string OrderId,
    SagaStage Stage,
    bool CancelRequested,
    string? CourierId,
    IReadOnlyList<SagaEvent> Timeline);

/// <summary>The workflow result.</summary>
/// <param name="FailedStep">The step whose failure started the unwind; null when completed or cancelled by the customer.</param>
/// <param name="Completed">Steps that returned success, in order.</param>
/// <param name="Compensations">Compensations in the order they ran (the reverse of registration).</param>
/// <param name="TotalKurus">The order total the saga charged (or would have charged), for checking the outcome.</param>
public sealed record SagaResult(
    string OrderId,
    SagaStatus Status,
    SagaStep? FailedStep,
    string? Reason,
    IReadOnlyList<SagaStep> Completed,
    IReadOnlyList<CompensationResult> Compensations,
    string? CourierId,
    long TotalKurus);

/// <summary>Answer to a customer cancellation request (a workflow update, so the caller learns the outcome).</summary>
public sealed record CancelDecision(bool Accepted, string Reason);
