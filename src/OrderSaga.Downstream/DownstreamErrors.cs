namespace OrderSaga.Downstream;

/// <summary>A business refusal from a downstream system. Retrying does not change the answer.</summary>
public sealed class DownstreamRejectedException : Exception
{
    public DownstreamRejectedException() : base("rejected") { }

    public DownstreamRejectedException(string message) : base(message) { }

    public DownstreamRejectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The downstream system could not be reached, or its answer was lost. The caller cannot tell whether the effect
/// happened; that ambiguity is the whole reason compensations must be safe for effects that never happened.
/// </summary>
public sealed class DownstreamUnavailableException : Exception
{
    public DownstreamUnavailableException() : base("unavailable") { }

    public DownstreamUnavailableException(string message) : base(message) { }

    public DownstreamUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The forward call arrived after its compensation: the undo left a tombstone, so the effect is refused. Seen by
/// zombie attempts (an activity attempt that outlived its timeout); never retryable.
/// </summary>
public sealed class EffectAlreadyCompensatedException : Exception
{
    public EffectAlreadyCompensatedException() : base("already compensated") { }

    public EffectAlreadyCompensatedException(string message) : base(message) { }

    public EffectAlreadyCompensatedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The same idempotency key was reused with a different request body (for example another amount).</summary>
public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException() : base("idempotency key reused with a different request") { }

    public IdempotencyConflictException(string message) : base(message) { }

    public IdempotencyConflictException(string message, Exception inner) : base(message, inner) { }
}
