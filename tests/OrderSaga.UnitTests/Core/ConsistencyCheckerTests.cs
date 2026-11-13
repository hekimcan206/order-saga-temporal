using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OrderSaga.Core;

namespace OrderSaga.UnitTests.Core;

public sealed class ConsistencyCheckerTests
{
    private const long Total = 45_000;

    [Fact]
    public void Completed_order_with_every_effect_and_one_charge_is_consistent()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Active, EffectState.Active, EffectState.Active, 1, Total);
        ConsistencyChecker.Check(SagaStatus.Completed, Total, snapshot).Should().BeEmpty();
    }

    [Fact]
    public void Compensated_order_with_charge_and_refund_is_consistent()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Undone, EffectState.Undone, EffectState.None, 1, 0);
        ConsistencyChecker.Check(SagaStatus.Compensated, Total, snapshot).Should().BeEmpty();
    }

    [Fact]
    public void Compensated_order_that_never_reached_the_payment_is_consistent()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Undone, EffectState.None, EffectState.None, 0, 0);
        ConsistencyChecker.Check(SagaStatus.Compensated, Total, snapshot).Should().BeEmpty();
    }

    [Fact]
    public void A_charge_left_behind_by_compensation_is_a_leak()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Undone, EffectState.Undone, EffectState.None, 1, Total);
        ConsistencyChecker.Check(SagaStatus.Compensated, Total, snapshot)
            .Should().ContainSingle().Which.Code.Should().Be("leaked-charge");
    }

    [Fact]
    public void Two_charges_are_flagged_even_if_the_net_amount_looks_right()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Active, EffectState.Active, EffectState.Active, 2, Total);
        ConsistencyChecker.Check(SagaStatus.Completed, Total, snapshot)
            .Should().ContainSingle().Which.Code.Should().Be("double-charge");
    }

    [Fact]
    public void Completed_but_charged_twice_reports_both_problems()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Active, EffectState.Active, EffectState.Active, 2, 2 * Total);
        ConsistencyChecker.Check(SagaStatus.Completed, Total, snapshot)
            .Select(v => v.Code).Should().BeEquivalentTo(["double-charge", "wrong-amount"]);
    }

    [Theory]
    [InlineData(EffectState.Active, EffectState.Undone, EffectState.None, "platform-still-accepted")]
    [InlineData(EffectState.Undone, EffectState.Active, EffectState.None, "orphan-ticket")]
    [InlineData(EffectState.Undone, EffectState.Undone, EffectState.Active, "orphan-courier")]
    public void Effects_left_in_force_after_compensation_are_named(EffectState platform, EffectState kitchen, EffectState courier, string code)
    {
        var snapshot = new DownstreamSnapshot("o1", platform, kitchen, courier, 0, 0);
        ConsistencyChecker.Check(SagaStatus.Compensated, Total, snapshot)
            .Should().ContainSingle().Which.Code.Should().Be(code);
    }

    [Fact]
    public void Compensation_failed_is_reported_not_judged()
    {
        var snapshot = new DownstreamSnapshot("o1", EffectState.Undone, EffectState.Undone, EffectState.None, 1, Total);
        ConsistencyChecker.Check(SagaStatus.CompensationFailed, Total, snapshot).Should().BeEmpty();
    }

    /// <summary>An outcome can never be both a valid completion and a valid compensation of a non-zero order.</summary>
    [Property(MaxTest = 500)]
    public Property No_snapshot_satisfies_both_completed_and_compensated()
    {
        var states = Gen.Elements(EffectState.None, EffectState.Active, EffectState.Undone);
        var gen =
            from p in states
            from k in states
            from c in states
            from charges in Gen.Choose(0, 3)
            from net in Gen.Elements(0L, Total, 2 * Total, -Total)
            select new DownstreamSnapshot("o", p, k, c, charges, net);

        return Prop.ForAll(gen.ToArbitrary(), s =>
            ConsistencyChecker.Check(SagaStatus.Completed, Total, s).Count > 0 ||
            ConsistencyChecker.Check(SagaStatus.Compensated, Total, s).Count > 0);
    }
}
