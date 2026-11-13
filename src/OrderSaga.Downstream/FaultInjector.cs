using Npgsql;
using OrderSaga.Core;

namespace OrderSaga.Downstream;

/// <summary>
/// Stores injected faults in the simulator database and hands each one out exactly once. Living in PostgreSQL rather
/// than in memory means a fault set by a test, the demo or the API is seen by whichever worker process runs the
/// activity, including a worker started after another one was killed.
/// </summary>
public sealed class FaultInjector(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(string orderId, FaultRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = rule.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(rule));
        }

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO sim.fault_rules (order_id, step, phase, kind, remaining, delay_ms)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        cmd.Parameters.Add(new() { Value = orderId });
        cmd.Parameters.Add(new() { Value = SagaSteps.Slug(rule.Step) });
        cmd.Parameters.Add(new() { Value = PhaseSlug(rule.Phase) });
        cmd.Parameters.Add(new() { Value = KindSlug(rule.Kind) });
        cmd.Parameters.Add(new() { Value = rule.Times });
        cmd.Parameters.Add(new() { Value = rule.DelayMs });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Consumes the next fault for this call, oldest rule first, or returns null when the call should succeed.</summary>
    public async Task<(FaultKind Kind, int DelayMs)?> NextAsync(
        string orderId, SagaStep step, FaultPhase phase, CancellationToken cancellationToken = default)
    {
        // The row lock serialises concurrent attempts (a retry racing a zombie), so a rule with remaining = 1 fires once.
        await using var cmd = dataSource.CreateCommand("""
            UPDATE sim.fault_rules SET remaining = remaining - 1
            WHERE rule_id = (
                SELECT rule_id FROM sim.fault_rules
                WHERE order_id = $1 AND step = $2 AND phase = $3 AND remaining > 0
                ORDER BY rule_id
                LIMIT 1
                FOR UPDATE)
            RETURNING kind, delay_ms
            """);
        cmd.Parameters.Add(new() { Value = orderId });
        cmd.Parameters.Add(new() { Value = SagaSteps.Slug(step) });
        cmd.Parameters.Add(new() { Value = PhaseSlug(phase) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (ParseKind(reader.GetString(0)), reader.GetInt32(1));
    }

    public static string PhaseSlug(FaultPhase phase) => phase == FaultPhase.Forward ? "forward" : "compensate";

    public static string KindSlug(FaultKind kind) => kind switch
    {
        FaultKind.Reject => "reject",
        FaultKind.Transient => "transient",
        FaultKind.LostResponse => "lost-response",
        FaultKind.Slow => "slow",
        FaultKind.Pending => "pending",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static FaultKind ParseKind(string slug) => slug switch
    {
        "reject" => FaultKind.Reject,
        "transient" => FaultKind.Transient,
        "lost-response" => FaultKind.LostResponse,
        "slow" => FaultKind.Slow,
        "pending" => FaultKind.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(slug), slug, null),
    };
}
