using OrderSaga.Core;

namespace OrderSaga.IntegrationTests.Infrastructure;

/// <summary>Synthetic orders. Ids are unique per call, so every test works on its own rows and workflows.</summary>
public static class Orders
{
    public static PlaceOrder New(string prefix = "ord", int courierWaitSeconds = 300) => new(
        $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(64, prefix.Length + 33)],
        "store-01",
        $"cust-{Random.Shared.Next(1000, 9999)}",
        [
            new OrderLine("lahmacun", "Lahmacun", 2, 12_000),
            new OrderLine("ayran", "Ayran", 2, 4_500),
            new OrderLine("kunefe", "Künefe", 1, 18_000),
        ],
        courierWaitSeconds);

    public static FaultRule Forward(SagaStep step, FaultKind kind, int times = 1, int delayMs = 0) =>
        new(step, FaultPhase.Forward, kind, times, delayMs);

    public static FaultRule Undo(SagaStep step, FaultKind kind, int times = 1, int delayMs = 0) =>
        new(step, FaultPhase.Compensate, kind, times, delayMs);
}
