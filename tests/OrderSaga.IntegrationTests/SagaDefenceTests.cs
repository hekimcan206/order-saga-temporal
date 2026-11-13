using FsCheck;
using FsCheck.Fluent;
using OrderSaga.Core;
using OrderSaga.IntegrationTests.Infrastructure;
using OrderSaga.Workflows;
using static OrderSaga.IntegrationTests.Infrastructure.Orders;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// Each defence is proven necessary by switching it off: the same fault that the production saga survives breaks the
/// lab variant without it. Then a generated fault campaign runs the production saga against random fault plans.
/// </summary>
public sealed class SagaDefenceTests(SagaEnvironment env)
{
    [Fact]
    public async Task Without_compensate_on_unknown_a_captured_but_unconfirmed_payment_leaks()
    {
        var order = New();

        var run = await env.RunLabAsync(order, SagaVariant.CompensateAfterSuccess,
            Forward(SagaStep.Payment, FaultKind.LostResponse),
            Forward(SagaStep.Payment, FaultKind.Transient, times: 10));

        run.Result.Status.Should().Be(SagaStatus.Compensated, "the workflow believes it cleaned up");
        run.Violations.Select(v => v.Code).Should().Equal("leaked-charge");
    }

    [Fact]
    public async Task Without_stable_idempotency_keys_a_lost_response_charges_twice()
    {
        var order = New();

        var run = await env.RunLabAsync(order, SagaVariant.NoIdempotencyKey, Forward(SagaStep.Payment, FaultKind.LostResponse));

        run.Result.Status.Should().Be(SagaStatus.Completed);
        run.Snapshot.Charges.Should().Be(2);
        run.Violations.Select(v => v.Code).Should().Contain("double-charge");
    }

    [Fact]
    public async Task Without_tombstones_a_zombie_capture_lands_after_the_refund()
    {
        var order = New();

        var run = await env.RunLabAsync(order, SagaVariant.NoTombstone,
            Forward(SagaStep.Payment, FaultKind.Slow, times: 1, delayMs: 10_000),
            Forward(SagaStep.Payment, FaultKind.Transient, times: 10));
        run.Result.Status.Should().Be(SagaStatus.Compensated);

        await Task.Delay(TimeSpan.FromSeconds(4));
        var after = await env.ObserveAsync(order, run.Result);
        after.Violations.Select(v => v.Code).Should().Equal("leaked-charge");
    }

    /// <summary>
    /// Random fault plans (rejections, transient bursts, lost responses, lost responses followed by outages, failing
    /// compensations) on every step, run concurrently against the production saga. Every outcome must satisfy the
    /// consistency invariant. A failure prints every broken plan, so it can be replayed as a fixed test.
    /// </summary>
    [Fact]
    public async Task Random_fault_plans_never_break_the_invariant()
    {
        var plans = Gen.Sample(FaultPlans.Plan, 40, 16).ToArray();

        var runs = await Task.WhenAll(plans.Select(async plan =>
        {
            var order = New("campaign");
            var run = await env.RunAsync(order, plan);
            return (plan, run);
        }));

        var broken = runs.Where(r => r.run.Violations.Count > 0 || r.run.Result.Status == SagaStatus.CompensationFailed).ToList();
        broken.Should().BeEmpty(string.Join("\n", broken.Select(b =>
            $"{string.Join(", ", b.plan.Select(f => $"{f.Step}/{f.Phase}/{f.Kind}x{f.Times}"))} -> " +
            $"{b.run.Result.Status}: {string.Join("; ", b.run.Violations.Select(v => v.Code))}")));
        runs.Select(r => r.run.Result.Status).Should().Contain(SagaStatus.Completed).And.Contain(SagaStatus.Compensated);
    }
}

/// <summary>Generators for fault plans, shared by the integration campaign and the benchmark harness.</summary>
public static class FaultPlans
{
    private static readonly Gen<FaultRule[]> StepFaults =
        from step in Gen.Elements(SagaSteps.ForwardOrder.ToArray())
        from shape in Gen.Choose(0, 5)
        from times in Gen.Choose(1, 3)
        select shape switch
        {
            0 => [Orders.Forward(step, FaultKind.Reject)],
            1 => [Orders.Forward(step, FaultKind.Transient, times)],
            2 => [Orders.Forward(step, FaultKind.LostResponse, times)],
            3 => [Orders.Forward(step, FaultKind.LostResponse), Orders.Forward(step, FaultKind.Transient, 10)],
            4 => [Orders.Undo(step, FaultKind.Transient, times), Orders.Forward(SagaStep.CourierDispatch, FaultKind.Reject)],
            _ => Array.Empty<FaultRule>(),
        };

    public static Gen<FaultRule[]> Plan { get; } =
        from count in Gen.Choose(0, 3)
        from parts in Gen.ArrayOf(StepFaults, count)
        select parts.SelectMany(p => p).ToArray();
}
