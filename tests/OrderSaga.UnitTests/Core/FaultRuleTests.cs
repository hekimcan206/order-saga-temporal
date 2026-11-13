using OrderSaga.Core;

namespace OrderSaga.UnitTests.Core;

public sealed class FaultRuleTests
{
    [Fact]
    public void Ordinary_rules_are_valid()
    {
        new FaultRule(SagaStep.Payment, FaultPhase.Forward, FaultKind.LostResponse, 5).Validate().Should().BeEmpty();
        new FaultRule(SagaStep.Payment, FaultPhase.Compensate, FaultKind.Transient, 3).Validate().Should().BeEmpty();
        new FaultRule(SagaStep.CourierDispatch, FaultPhase.Forward, FaultKind.Pending).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Pending_only_makes_sense_for_the_courier_dispatch()
    {
        new FaultRule(SagaStep.Payment, FaultPhase.Forward, FaultKind.Pending).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void A_compensation_cannot_be_rejected()
    {
        new FaultRule(SagaStep.Payment, FaultPhase.Compensate, FaultKind.Reject).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Times_and_delay_are_bounded()
    {
        new FaultRule(SagaStep.Payment, FaultPhase.Forward, FaultKind.Transient, 0).Validate().Should().NotBeEmpty();
        new FaultRule(SagaStep.Payment, FaultPhase.Forward, FaultKind.Slow, 1, 60_001).Validate().Should().NotBeEmpty();
        new FaultRule((SagaStep)99, FaultPhase.Forward, FaultKind.Slow).Validate().Should().NotBeEmpty();
    }
}
