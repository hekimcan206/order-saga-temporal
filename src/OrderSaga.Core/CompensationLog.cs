namespace OrderSaga.Core;

/// <summary>
/// The saga's undo log. In the production saga a step's compensation is pushed <em>before</em> the step is attempted:
/// once an activity has been scheduled its effect may exist even if the workflow never sees a success (lost response,
/// timeout, a zombie attempt still running on a dead worker). Compensations are therefore written to be safe when the
/// effect never happened (ADR 0002).
/// </summary>
/// <remarks>
/// Deterministic on purpose: it runs inside workflow code, where every decision must replay identically, so it holds
/// delegates and a list, never a clock, a random number or a thread.
/// </remarks>
public sealed class CompensationLog
{
    private readonly List<(SagaStep Step, Func<Task> Compensate)> _entries = [];

    /// <summary>Steps whose compensation is registered, in the order they will be unwound (newest first).</summary>
    public IReadOnlyList<SagaStep> Pending => [.. _entries.Select(e => e.Step).Reverse()];

    public int Count => _entries.Count;

    public void Push(SagaStep step, Func<Task> compensate)
    {
        ArgumentNullException.ThrowIfNull(compensate);
        if (_entries.Exists(e => e.Step == step))
        {
            throw new InvalidOperationException($"A compensation for {step} is already registered.");
        }

        _entries.Add((step, compensate));
    }

    /// <summary>
    /// Runs every registered compensation newest-first. A compensation that throws does not stop the others: leaving
    /// an earlier step (a captured payment) un-undone because a later undo (cancel the courier) failed would make the
    /// outcome strictly worse. Failures are reported so the workflow can end in a state an operator will look at.
    /// </summary>
    public async Task<CompensationReport> UnwindAsync()
    {
        var results = new List<CompensationResult>(_entries.Count);
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var (step, compensate) = _entries[i];
            try
            {
                await compensate();
                results.Add(new CompensationResult(step, Succeeded: true, Error: null));
            }
#pragma warning disable CA1031 // Every failure must be recorded and the unwind must continue; see the summary.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                results.Add(new CompensationResult(step, Succeeded: false, Error: ex.Message));
            }
        }

        _entries.Clear();
        return new CompensationReport(results);
    }
}

public sealed record CompensationResult(SagaStep Step, bool Succeeded, string? Error);

public sealed record CompensationReport(IReadOnlyList<CompensationResult> Results)
{
    public bool AllSucceeded => Results.All(r => r.Succeeded);
}
