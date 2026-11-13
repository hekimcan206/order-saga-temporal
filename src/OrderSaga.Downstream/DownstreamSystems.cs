using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderSaga.Core;

namespace OrderSaga.Downstream;

/// <summary>Identifies one downstream effect: which order, which store, and the idempotency key that names it.</summary>
/// <param name="IdempotencyKey">
/// Normally <c>orderId:step</c>. The lab variant without idempotency keys appends <c>#attempt</c>, which is exactly what
/// a client that generates a fresh key per HTTP request does.
/// </param>
public sealed record EffectCall(string OrderId, string StoreId, string IdempotencyKey);

/// <summary>What an undo found.</summary>
public enum UndoOutcome
{
    /// <summary>The effect existed and is now undone.</summary>
    Undone,

    /// <summary>The effect did not exist; a tombstone now blocks a late forward call.</summary>
    Tombstoned,

    /// <summary>Already undone (a retried compensation). Nothing changed.</summary>
    AlreadyUndone,

    /// <summary>No effect and no tombstone written (only in the lab variant that skips tombstones).</summary>
    NothingToUndo,
}

/// <summary>Courier dispatch answer: assigned at once, or accepted and waiting for a courier.</summary>
public sealed record DispatchResult(bool Assigned, string? CourierId);

public sealed class DownstreamOptions
{
    public const string Section = "Downstream";

    /// <summary>Simulated network and processing time added to every downstream call.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
}

/// <summary>
/// The ordering platform, the kitchen display system, the payment provider and the courier platform, simulated on
/// PostgreSQL. They behave the way the real APIs a saga meets are expected to behave, and the fault injector makes them
/// misbehave the way real ones do:
/// <list type="bullet">
/// <item>Every forward call is idempotent on its key: a retry returns the original result, a key reused with another
/// body is refused.</item>
/// <item>Every undo is idempotent and safe when the effect never happened. It first writes a tombstone, then undoes
/// whatever exists, so a forward call that arrives late (a zombie attempt) finds the tombstone and does nothing.</item>
/// </list>
/// Table names are constants in this class, never input, so the interpolated SQL below is not an injection vector.
/// </summary>
public sealed class DownstreamSystems(NpgsqlDataSource dataSource, FaultInjector faults, IOptions<DownstreamOptions> options)
{
    private readonly TimeSpan _latency = options.Value.Latency;

    // ---- Ordering platform --------------------------------------------------------------------------------------

    public Task AcceptOnPlatformAsync(EffectCall call, CancellationToken cancellationToken = default) =>
        ForwardAsync(call, SagaStep.PlatformAccept, async (_, ct) =>
        {
            await ApplySimpleAsync("sim.platform_orders", "accepted", call, extraColumn: null, extraValue: null, ct);
            return true;
        }, cancellationToken);

    public Task<UndoOutcome> CancelOnPlatformAsync(EffectCall call, bool writeTombstone, CancellationToken cancellationToken = default) =>
        UndoAsync(call, SagaStep.PlatformAccept, ct => UndoSimpleAsync("sim.platform_orders", call, writeTombstone, ct), cancellationToken);

    // ---- Kitchen ------------------------------------------------------------------------------------------------

    public Task<long> ConfirmKitchenTicketAsync(EffectCall call, int lineCount, CancellationToken cancellationToken = default) =>
        ForwardAsync(call, SagaStep.KitchenTicket, async (_, ct) =>
        {
            await ApplySimpleAsync("sim.kitchen_tickets", "confirmed", call, "lines", lineCount, ct);
            await using var cmd = dataSource.CreateCommand("SELECT ticket_no FROM sim.kitchen_tickets WHERE idempotency_key = $1");
            cmd.Parameters.Add(new() { Value = call.IdempotencyKey });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, cancellationToken);

    public Task<UndoOutcome> CancelKitchenTicketAsync(EffectCall call, bool writeTombstone, CancellationToken cancellationToken = default) =>
        UndoAsync(call, SagaStep.KitchenTicket, ct => UndoSimpleAsync("sim.kitchen_tickets", call, writeTombstone, ct), cancellationToken);

    // ---- Payment provider ---------------------------------------------------------------------------------------

    public Task ChargeAsync(EffectCall call, long amountKurus, CancellationToken cancellationToken = default) =>
        ForwardAsync(call, SagaStep.Payment, async (_, ct) =>
        {
            // The capture and its ledger entry are one statement: there is no window where money moved without a record.
            await using (var cmd = dataSource.CreateCommand("""
                WITH ins AS (
                    INSERT INTO sim.payments (idempotency_key, order_id, amount_kurus, status)
                    VALUES ($1, $2, $3, 'captured')
                    ON CONFLICT (idempotency_key) DO NOTHING
                    RETURNING idempotency_key, order_id, amount_kurus)
                INSERT INTO sim.payment_ledger (idempotency_key, order_id, kind, amount_kurus)
                SELECT idempotency_key, order_id, 'charge', amount_kurus FROM ins
                RETURNING entry_id
                """))
            {
                cmd.Parameters.Add(new() { Value = call.IdempotencyKey });
                cmd.Parameters.Add(new() { Value = call.OrderId });
                cmd.Parameters.Add(new() { Value = amountKurus });
                if (await cmd.ExecuteScalarAsync(ct) is not null)
                {
                    return true;
                }
            }

            // The key exists: this is a retry (return the original result), a reuse with another amount, or a late call
            // that lost the race against its own refund.
            await using var read = dataSource.CreateCommand("SELECT status, amount_kurus FROM sim.payments WHERE idempotency_key = $1");
            read.Parameters.Add(new() { Value = call.IdempotencyKey });
            await using var reader = await read.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var status = reader.GetString(0);
            var amount = reader.GetInt64(1);
            if (status != "captured")
            {
                throw new EffectAlreadyCompensatedException($"payment {call.IdempotencyKey} is {status}; the capture is refused");
            }

            if (amount != amountKurus)
            {
                throw new IdempotencyConflictException($"payment {call.IdempotencyKey} was captured for {amount}, not {amountKurus}");
            }

            return true;
        }, cancellationToken);

    public Task<UndoOutcome> RefundAsync(EffectCall call, bool writeTombstone, CancellationToken cancellationToken = default) =>
        UndoAsync(call, SagaStep.Payment, async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            var tombstoned = false;
            if (writeTombstone)
            {
                // Tombstone first: a capture that has not committed yet waits on this key and then refuses itself.
                await using var tomb = new NpgsqlCommand("""
                    INSERT INTO sim.payments (idempotency_key, order_id, amount_kurus, status)
                    VALUES ($1, $2, 0, 'voided')
                    ON CONFLICT (idempotency_key) DO NOTHING
                    RETURNING 1
                    """, connection, tx);
                tomb.Parameters.Add(new() { Value = call.IdempotencyKey });
                tomb.Parameters.Add(new() { Value = call.OrderId });
                tombstoned = await tomb.ExecuteScalarAsync(ct) is not null;
            }

            await using var refund = new NpgsqlCommand("""
                WITH r AS (
                    UPDATE sim.payments SET status = 'refunded', updated_at = now()
                    WHERE (idempotency_key = $1 OR starts_with(idempotency_key, $1 || '#')) AND status = 'captured'
                    RETURNING idempotency_key, order_id, amount_kurus)
                INSERT INTO sim.payment_ledger (idempotency_key, order_id, kind, amount_kurus)
                SELECT idempotency_key, order_id, 'refund', amount_kurus FROM r
                RETURNING entry_id
                """, connection, tx);
            refund.Parameters.Add(new() { Value = call.IdempotencyKey });
            var refunded = 0;
            await using (var reader = await refund.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    refunded++;
                }
            }

            await tx.CommitAsync(ct);
            return Outcome(refunded, tombstoned, writeTombstone);
        }, cancellationToken);

    // ---- Courier platform ---------------------------------------------------------------------------------------

    public Task<DispatchResult> DispatchCourierAsync(EffectCall call, CancellationToken cancellationToken = default) =>
        ForwardAsync(call, SagaStep.CourierDispatch, async (fault, ct) =>
        {
            var pending = fault == FaultKind.Pending;
            var courierId = pending ? null : CourierFor(call.IdempotencyKey);
            await using (var cmd = dataSource.CreateCommand("""
                INSERT INTO sim.courier_dispatches (idempotency_key, order_id, status, courier_id)
                VALUES ($1, $2, $3, $4)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING 1
                """))
            {
                cmd.Parameters.Add(new() { Value = call.IdempotencyKey });
                cmd.Parameters.Add(new() { Value = call.OrderId });
                cmd.Parameters.Add(new() { Value = pending ? "pending" : "assigned" });
                cmd.Parameters.Add(new() { Value = (object?)courierId ?? DBNull.Value });
                if (await cmd.ExecuteScalarAsync(ct) is not null)
                {
                    return new DispatchResult(!pending, courierId);
                }
            }

            await using var read = dataSource.CreateCommand("SELECT status, courier_id FROM sim.courier_dispatches WHERE idempotency_key = $1");
            read.Parameters.Add(new() { Value = call.IdempotencyKey });
            await using var reader = await read.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var status = reader.GetString(0);
            return status == "cancelled"
                ? throw new EffectAlreadyCompensatedException($"dispatch {call.IdempotencyKey} is cancelled; the dispatch is refused")
                : new DispatchResult(status == "assigned", reader.IsDBNull(1) ? null : reader.GetString(1));
        }, cancellationToken);

    public Task<UndoOutcome> CancelCourierAsync(EffectCall call, bool writeTombstone, CancellationToken cancellationToken = default) =>
        UndoAsync(call, SagaStep.CourierDispatch, ct => UndoSimpleAsync("sim.courier_dispatches", call, writeTombstone, ct), cancellationToken);

    // ---- Reading the truth --------------------------------------------------------------------------------------

    /// <summary>What the downstream systems themselves record for an order (used by tests, the demo and benchmarks).</summary>
    public async Task<DownstreamSnapshot> SnapshotAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT
                (SELECT CASE WHEN bool_or(status = 'accepted') THEN 1
                             WHEN bool_or(status = 'cancelled' AND NOT tombstone) THEN 2 ELSE 0 END
                 FROM sim.platform_orders WHERE order_id = $1),
                (SELECT CASE WHEN bool_or(status = 'confirmed') THEN 1
                             WHEN bool_or(status = 'cancelled' AND NOT tombstone) THEN 2 ELSE 0 END
                 FROM sim.kitchen_tickets WHERE order_id = $1),
                (SELECT CASE WHEN bool_or(status IN ('assigned', 'pending')) THEN 1
                             WHEN bool_or(status = 'cancelled' AND NOT tombstone) THEN 2 ELSE 0 END
                 FROM sim.courier_dispatches WHERE order_id = $1),
                (SELECT count(*) FILTER (WHERE kind = 'charge') FROM sim.payment_ledger WHERE order_id = $1),
                (SELECT COALESCE(sum(CASE kind WHEN 'charge' THEN amount_kurus ELSE -amount_kurus END), 0)
                 FROM sim.payment_ledger WHERE order_id = $1)
            """);
        cmd.Parameters.Add(new() { Value = orderId });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DownstreamSnapshot(
            orderId,
            (EffectState)reader.GetInt32(0),
            (EffectState)reader.GetInt32(1),
            (EffectState)reader.GetInt32(2),
            (int)reader.GetInt64(3),
            Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture));
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------------

    /// <summary>Applies the injected fault (if any) around a forward effect.</summary>
    private async Task<T> ForwardAsync<T>(
        EffectCall call, SagaStep step, Func<FaultKind?, CancellationToken, Task<T>> effect, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);
        var fault = await faults.NextAsync(call.OrderId, step, FaultPhase.Forward, cancellationToken);
        switch (fault?.Kind)
        {
            case FaultKind.Reject:
                throw new DownstreamRejectedException($"{SagaSteps.Slug(step)} rejected order {call.OrderId}");

            case FaultKind.Transient:
                throw new DownstreamUnavailableException($"{SagaSteps.Slug(step)} is unavailable");

            case FaultKind.LostResponse:
                await effect(null, cancellationToken);
                throw new DownstreamUnavailableException($"{SagaSteps.Slug(step)} applied the effect but the response was lost");

            case FaultKind.Slow:
                // The request already reached the remote side: cancelling the caller does not stop it. That is what makes
                // the attempt a zombie, so the delay and the effect deliberately ignore the caller's token.
                await Task.Delay(fault.Value.DelayMs, CancellationToken.None);
                return await effect(null, CancellationToken.None);

            default:
                return await effect(fault?.Kind, cancellationToken);
        }
    }

    private async Task<UndoOutcome> UndoAsync(
        EffectCall call, SagaStep step, Func<CancellationToken, Task<UndoOutcome>> undo, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);
        var fault = await faults.NextAsync(call.OrderId, step, FaultPhase.Compensate, cancellationToken);
        switch (fault?.Kind)
        {
            case FaultKind.Transient:
                throw new DownstreamUnavailableException($"{SagaSteps.Slug(step)} undo is unavailable");

            case FaultKind.LostResponse:
                await undo(cancellationToken);
                throw new DownstreamUnavailableException($"{SagaSteps.Slug(step)} undo applied but the response was lost");

            case FaultKind.Slow:
                await Task.Delay(fault.Value.DelayMs, CancellationToken.None);
                return await undo(CancellationToken.None);

            default:
                return await undo(cancellationToken);
        }
    }

    private async Task ApplySimpleAsync(
        string table, string activeStatus, EffectCall call, string? extraColumn, int? extraValue, CancellationToken ct)
    {
        var columns = extraColumn is null ? "" : $", {extraColumn}";
        var values = extraColumn is null ? "" : ", $5";
        await using (var cmd = dataSource.CreateCommand($"""
            INSERT INTO {table} (idempotency_key, order_id, status, store_id{columns})
            VALUES ($1, $2, $3, $4{values})
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING 1
            """))
        {
            cmd.Parameters.Add(new() { Value = call.IdempotencyKey });
            cmd.Parameters.Add(new() { Value = call.OrderId });
            cmd.Parameters.Add(new() { Value = activeStatus });
            cmd.Parameters.Add(new() { Value = call.StoreId });

            if (extraValue is not null)
            {
                cmd.Parameters.Add(new() { Value = extraValue.Value });
            }

            if (await cmd.ExecuteScalarAsync(ct) is not null)
            {
                return;
            }
        }

        // A new statement takes a new snapshot, so it sees a row committed by a concurrent attempt we conflicted with.
        await using var read = dataSource.CreateCommand($"SELECT status FROM {table} WHERE idempotency_key = $1");
        read.Parameters.Add(new() { Value = call.IdempotencyKey });
        var status = (string)(await read.ExecuteScalarAsync(ct))!;
        if (status == "cancelled")
        {
            throw new EffectAlreadyCompensatedException($"{table} {call.IdempotencyKey} is cancelled; the call is refused");
        }
    }

    private async Task<UndoOutcome> UndoSimpleAsync(string table, EffectCall call, bool writeTombstone, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var hasStore = table != "sim.courier_dispatches";

        var tombstoned = false;
        if (writeTombstone)
        {
            await using var tomb = new NpgsqlCommand($"""
                INSERT INTO {table} (idempotency_key, order_id, status, tombstone{(hasStore ? ", store_id" : "")})
                VALUES ($1, $2, 'cancelled', true{(hasStore ? ", $3" : "")})
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING 1
                """, connection, tx);
            tomb.Parameters.Add(new() { Value = call.IdempotencyKey });
            tomb.Parameters.Add(new() { Value = call.OrderId });
            if (hasStore)
            {
                tomb.Parameters.Add(new() { Value = call.StoreId });
            }

            tombstoned = await tomb.ExecuteScalarAsync(ct) is not null;
        }

        await using var cancel = new NpgsqlCommand($"""
            UPDATE {table} SET status = 'cancelled', updated_at = now()
            WHERE (idempotency_key = $1 OR starts_with(idempotency_key, $1 || '#')) AND status <> 'cancelled'
            """, connection, tx);
        cancel.Parameters.Add(new() { Value = call.IdempotencyKey });
        var cancelled = await cancel.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return Outcome(cancelled, tombstoned, writeTombstone);
    }

    private static UndoOutcome Outcome(int undone, bool tombstoned, bool writeTombstone) =>
        undone > 0 ? UndoOutcome.Undone
        : tombstoned ? UndoOutcome.Tombstoned
        : writeTombstone ? UndoOutcome.AlreadyUndone
        : UndoOutcome.NothingToUndo;

    private Task SimulateLatencyAsync(CancellationToken cancellationToken) =>
        _latency > TimeSpan.Zero ? Task.Delay(_latency, cancellationToken) : Task.CompletedTask;

    /// <summary>A stable synthetic courier for a dispatch, so a retried dispatch reports the same courier.</summary>
    internal static string CourierFor(string idempotencyKey)
    {
        // FNV-1a: tiny, stable across processes and runtimes (string.GetHashCode is randomised per process).
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(idempotencyKey))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return $"courier-{100 + (hash % 900):D3}";
    }
}
