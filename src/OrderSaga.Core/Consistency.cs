namespace OrderSaga.Core;

/// <summary>State of one downstream effect as the downstream system itself records it.</summary>
public enum EffectState
{
    /// <summary>The downstream system has never heard of this order (or only holds a tombstone written first).</summary>
    None,

    /// <summary>The effect is in force: order accepted, ticket confirmed, courier assigned or pending.</summary>
    Active,

    /// <summary>The effect was undone by a compensation.</summary>
    Undone,
}

/// <summary>
/// What the four downstream systems say about one order, read from their own tables rather than from the workflow.
/// The saga is judged against this, because "the workflow thinks it refunded" is not the same as "the money is back".
/// </summary>
/// <param name="Charges">Number of charge entries in the payment ledger (a correct saga never exceeds one).</param>
/// <param name="NetChargedKurus">Sum of charges minus refunds in the payment ledger.</param>
public sealed record DownstreamSnapshot(
    string OrderId,
    EffectState Platform,
    EffectState Kitchen,
    EffectState Courier,
    int Charges,
    long NetChargedKurus);

/// <summary>A broken invariant, with a stable code for counting in fault campaigns.</summary>
public sealed record Violation(string Code, string Detail);

/// <summary>
/// The saga's correctness contract, checked after a workflow has closed:
/// <list type="bullet">
/// <item>Completed: every effect is in force and the customer was charged the order total exactly once.</item>
/// <item>Compensated: no effect is left in force and the net charge is zero.</item>
/// <item>In every outcome: never more than one charge per order.</item>
/// </list>
/// "Compensation failed" is not judged as consistent or inconsistent: it is the saga admitting it could not finish, and
/// the leftovers are reported so the operator knows what to undo by hand.
/// </summary>
public static class ConsistencyChecker
{
    public static IReadOnlyList<Violation> Check(SagaStatus status, long orderTotalKurus, DownstreamSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var violations = new List<Violation>();

        if (snapshot.Charges > 1)
        {
            violations.Add(new("double-charge", $"{snapshot.Charges} charges recorded for one order"));
        }

        switch (status)
        {
            case SagaStatus.Completed:
                Expect(snapshot.Platform == EffectState.Active, "missing-effect", "platform order is not accepted");
                Expect(snapshot.Kitchen == EffectState.Active, "missing-effect", "kitchen ticket is not confirmed");
                Expect(snapshot.Courier == EffectState.Active, "missing-effect", "no courier is assigned");
                Expect(snapshot.NetChargedKurus == orderTotalKurus, "wrong-amount",
                    $"net charge {snapshot.NetChargedKurus} != order total {orderTotalKurus}");
                break;

            case SagaStatus.Compensated:
                Expect(snapshot.NetChargedKurus == 0, "leaked-charge",
                    $"customer is still charged {snapshot.NetChargedKurus} kuruş after compensation");
                Expect(snapshot.Kitchen != EffectState.Active, "orphan-ticket", "kitchen still holds a confirmed ticket");
                Expect(snapshot.Courier != EffectState.Active, "orphan-courier", "a courier is still dispatched");
                Expect(snapshot.Platform != EffectState.Active, "platform-still-accepted",
                    "the platform still shows the order as accepted");
                break;

            case SagaStatus.CompensationFailed:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        return violations;

        void Expect(bool condition, string code, string detail)
        {
            if (!condition)
            {
                violations.Add(new(code, detail));
            }
        }
    }
}
