namespace OrderSaga.Workflows;

/// <summary>Where the Temporal service is. Bound from the <c>Temporal</c> configuration section.</summary>
public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    public string TaskQueue { get; set; } = OrderSagaWorkflow.TaskQueue;

    /// <summary>
    /// How long a workflow task waits on a dead worker's sticky queue before any worker may take it. The SDK default
    /// (10 s) dominates crash recovery for a saga with short activities; see docs/benchmarks.md. Null keeps the default.
    /// </summary>
    public TimeSpan? StickyQueueTimeout { get; set; }
}
