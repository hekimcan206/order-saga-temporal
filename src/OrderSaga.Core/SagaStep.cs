namespace OrderSaga.Core;

/// <summary>
/// The forward steps, in execution order. The order is the business order from the brief: the platform accepts the
/// order, the kitchen confirms a ticket, the payment is captured, a courier is dispatched. Compensation runs the
/// completed (or possibly completed) steps in exactly the reverse order.
/// </summary>
public enum SagaStep
{
    PlatformAccept = 1,
    KitchenTicket = 2,
    Payment = 3,
    CourierDispatch = 4,
}

public static class SagaSteps
{
    public static IReadOnlyList<SagaStep> ForwardOrder { get; } =
        [SagaStep.PlatformAccept, SagaStep.KitchenTicket, SagaStep.Payment, SagaStep.CourierDispatch];

    /// <summary>
    /// The downstream idempotency key for one step of one order. Deterministic, so every retry of the activity, a
    /// replay of the workflow and the compensation all name the same effect.
    /// </summary>
    public static string IdempotencyKey(string orderId, SagaStep step) => $"{orderId}:{Slug(step)}";

    public static string Slug(SagaStep step) => step switch
    {
        SagaStep.PlatformAccept => "platform",
        SagaStep.KitchenTicket => "kitchen",
        SagaStep.Payment => "payment",
        SagaStep.CourierDispatch => "courier",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    public static SagaStep Parse(string slug) => slug switch
    {
        "platform" => SagaStep.PlatformAccept,
        "kitchen" => SagaStep.KitchenTicket,
        "payment" => SagaStep.Payment,
        "courier" => SagaStep.CourierDispatch,
        _ => throw new ArgumentOutOfRangeException(nameof(slug), slug, "unknown step"),
    };
}
