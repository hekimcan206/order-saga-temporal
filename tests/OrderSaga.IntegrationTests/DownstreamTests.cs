using Npgsql;
using OrderSaga.Core;
using OrderSaga.Downstream;
using OrderSaga.IntegrationTests.Infrastructure;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// The downstream contract the saga relies on, against real PostgreSQL: idempotent forward calls, undo that is safe
/// before the effect, tombstones that stop late forward calls, and the least-privilege role setup.
/// </summary>
public sealed class DownstreamTests(SagaEnvironment env)
{
    private DownstreamSystems Systems => env.Downstream;

    private static EffectCall Call(string orderId, SagaStep step) =>
        new(orderId, "store-01", SagaSteps.IdempotencyKey(orderId, step));

    [Fact]
    public async Task A_retried_charge_moves_money_once()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.Payment);

        await Systems.ChargeAsync(call, 30_000);
        await Systems.ChargeAsync(call, 30_000);
        await Systems.ChargeAsync(call, 30_000);

        var snapshot = await Systems.SnapshotAsync(order.OrderId);
        snapshot.Charges.Should().Be(1);
        snapshot.NetChargedKurus.Should().Be(30_000);
    }

    [Fact]
    public async Task A_key_reused_with_another_amount_is_refused()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.Payment);
        await Systems.ChargeAsync(call, 30_000);

        var act = () => Systems.ChargeAsync(call, 31_000);

        await act.Should().ThrowAsync<IdempotencyConflictException>();
    }

    [Fact]
    public async Task Refund_before_the_charge_leaves_a_tombstone_that_refuses_the_late_charge()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.Payment);

        (await Systems.RefundAsync(call, writeTombstone: true)).Should().Be(UndoOutcome.Tombstoned);
        var late = () => Systems.ChargeAsync(call, 30_000);

        await late.Should().ThrowAsync<EffectAlreadyCompensatedException>();
        var snapshot = await Systems.SnapshotAsync(order.OrderId);
        snapshot.Charges.Should().Be(0);
        snapshot.NetChargedKurus.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_tombstone_the_late_charge_goes_through()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.Payment);

        (await Systems.RefundAsync(call, writeTombstone: false)).Should().Be(UndoOutcome.NothingToUndo);
        await Systems.ChargeAsync(call, 30_000);

        (await Systems.SnapshotAsync(order.OrderId)).NetChargedKurus.Should().Be(30_000);
    }

    [Fact]
    public async Task A_retried_refund_returns_money_once()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.Payment);
        await Systems.ChargeAsync(call, 30_000);

        (await Systems.RefundAsync(call, true)).Should().Be(UndoOutcome.Undone);
        (await Systems.RefundAsync(call, true)).Should().Be(UndoOutcome.AlreadyUndone);

        var snapshot = await Systems.SnapshotAsync(order.OrderId);
        snapshot.Charges.Should().Be(1);
        snapshot.NetChargedKurus.Should().Be(0);
    }

    /// <summary>
    /// The zombie race, many times over: a charge and its refund issued at the same moment. Whatever wins, money never
    /// stays with the merchant, and it never moves more than once.
    /// </summary>
    [Fact]
    public async Task Concurrent_charge_and_refund_never_leave_money_behind()
    {
        for (var i = 0; i < 60; i++)
        {
            var order = Orders.New("race");
            var call = Call(order.OrderId, SagaStep.Payment);
            using var start = new SemaphoreSlim(0);
            var charge = Task.Run(async () =>
            {
                await start.WaitAsync();
                try
                {
                    await Systems.ChargeAsync(call, 30_000);
                }
                catch (EffectAlreadyCompensatedException)
                {
                }
            });
            var refund = Task.Run(async () =>
            {
                await start.WaitAsync();
                return await Systems.RefundAsync(call, true);
            });
            start.Release(2);
            await Task.WhenAll(charge, refund);

            var snapshot = await Systems.SnapshotAsync(order.OrderId);
            snapshot.NetChargedKurus.Should().Be(0, $"iteration {i}: refund outcome {await refund}");
            snapshot.Charges.Should().BeLessThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task Kitchen_confirm_is_idempotent_and_cancel_is_safe_before_and_after()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.KitchenTicket);

        var first = await Systems.ConfirmKitchenTicketAsync(call, 3);
        var again = await Systems.ConfirmKitchenTicketAsync(call, 3);
        again.Should().Be(first);

        (await Systems.CancelKitchenTicketAsync(call, true)).Should().Be(UndoOutcome.Undone);
        (await Systems.CancelKitchenTicketAsync(call, true)).Should().Be(UndoOutcome.AlreadyUndone);
        (await Systems.SnapshotAsync(order.OrderId)).Kitchen.Should().Be(EffectState.Undone);
    }

    [Fact]
    public async Task A_retried_dispatch_reports_the_same_courier()
    {
        var order = Orders.New();
        var call = Call(order.OrderId, SagaStep.CourierDispatch);

        var first = await Systems.DispatchCourierAsync(call);
        var again = await Systems.DispatchCourierAsync(call);

        first.Assigned.Should().BeTrue();
        again.Should().Be(first);
        first.CourierId.Should().MatchRegex("^courier-[1-9][0-9]{2}$");
    }

    [Fact]
    public async Task A_lost_response_applies_the_effect_and_still_fails_the_call()
    {
        var order = Orders.New();
        await env.Faults.AddAsync(order.OrderId, Orders.Forward(SagaStep.Payment, FaultKind.LostResponse));
        var call = Call(order.OrderId, SagaStep.Payment);

        var act = () => Systems.ChargeAsync(call, 30_000);

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
        (await Systems.SnapshotAsync(order.OrderId)).NetChargedKurus.Should().Be(30_000);
    }

    [Fact]
    public async Task Each_fault_fires_exactly_as_many_times_as_configured_under_concurrency()
    {
        var order = Orders.New();
        await env.Faults.AddAsync(order.OrderId, Orders.Forward(SagaStep.Payment, FaultKind.Transient, times: 7));

        var draws = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            env.Faults.NextAsync(order.OrderId, SagaStep.Payment, FaultPhase.Forward)));

        draws.Count(d => d is not null).Should().Be(7);
    }

    [Fact]
    public async Task Snapshot_reads_none_for_unknown_orders()
    {
        var snapshot = await Systems.SnapshotAsync("never-seen");
        snapshot.Should().Be(new DownstreamSnapshot("never-seen", EffectState.None, EffectState.None, EffectState.None, 0, 0));
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_the_payment_ledger()
    {
        var act = async () =>
        {
            await using var cmd = env.AppDb.CreateCommand("UPDATE sim.payment_ledger SET amount_kurus = 1");
            await cmd.ExecuteNonQueryAsync();
        };

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_application_role_cannot_see_the_migration_history()
    {
        var act = async () =>
        {
            await using var cmd = env.AppDb.CreateCommand("SELECT count(*) FROM saga_admin.schema_migrations");
            await cmd.ExecuteScalarAsync();
        };

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        await using var migrator = NpgsqlDataSource.Create(env.MigratorConnectionString);
        var applied = await new MigrationRunner(migrator, Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance)
            .MigrateAsync();
        applied.Should().Be(0);
    }
}
