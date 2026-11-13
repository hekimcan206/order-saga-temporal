namespace OrderSaga.Core;

/// <summary>
/// The failure modes a downstream call can show. They are the cases that make sagas hard, not just "the call threw".
/// </summary>
public enum FaultKind
{
    /// <summary>A business "no": platform rejects, kitchen is out of stock, card declined, no courier. Not retryable.</summary>
    Reject,

    /// <summary>Fails before any effect (connection refused, 503). Retrying is safe and eventually helps.</summary>
    Transient,

    /// <summary>
    /// The effect is applied, then the response is lost. The caller sees a failure for something that happened. Only an
    /// idempotency key makes the retry safe, and only compensate-on-unknown keeps the saga correct when retries run out.
    /// </summary>
    LostResponse,

    /// <summary>
    /// The attempt hangs past its start-to-close timeout and finishes later: a zombie that may commit an effect after
    /// the workflow has moved on, even after the compensation ran. Only a tombstone written by the compensation stops it.
    /// </summary>
    Slow,

    /// <summary>Courier only: the dispatch is accepted but no courier is assigned yet; the saga must wait for a signal.</summary>
    Pending,
}

/// <summary>Whether a fault hits the forward step or its compensation.</summary>
public enum FaultPhase
{
    Forward,
    Compensate,
}

/// <summary>
/// One injected fault: the next <paramref name="Times"/> calls of <paramref name="Phase"/> on <paramref name="Step"/>
/// for an order fail in mode <paramref name="Kind"/>. Stored in the simulator's database, so it works across worker
/// processes (compose demo, crash tests) and is consumed atomically by concurrent attempts.
/// </summary>
/// <param name="DelayMs">For <see cref="FaultKind.Slow"/>: how long the attempt sleeps before applying its effect.</param>
public sealed record FaultRule(SagaStep Step, FaultPhase Phase, FaultKind Kind, int Times = 1, int DelayMs = 0)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Step) || !Enum.IsDefined(Phase) || !Enum.IsDefined(Kind))
        {
            errors.Add("unknown step, phase or kind.");
        }

        if (Times is < 1 or > 1000)
        {
            errors.Add("times must be 1-1000.");
        }

        if (DelayMs is < 0 or > 60_000)
        {
            errors.Add("delayMs must be 0-60000.");
        }

        if (Kind == FaultKind.Pending && (Step != SagaStep.CourierDispatch || Phase != FaultPhase.Forward))
        {
            errors.Add("pending only applies to the forward courier dispatch.");
        }

        if (Kind == FaultKind.Reject && Phase == FaultPhase.Compensate)
        {
            errors.Add("a compensation cannot be rejected by the business; use transient to make it fail.");
        }

        return errors;
    }
}
