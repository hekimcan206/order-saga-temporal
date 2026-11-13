namespace OrderSaga.Workflows;

/// <summary>Where the Temporal service is. Bound from the <c>Temporal</c> configuration section.</summary>
public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    public string TaskQueue { get; set; } = OrderSagaWorkflow.TaskQueue;
}
