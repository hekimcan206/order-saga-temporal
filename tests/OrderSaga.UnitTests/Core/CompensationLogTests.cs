using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OrderSaga.Core;

namespace OrderSaga.UnitTests.Core;

public sealed class CompensationLogTests
{
    [Fact]
    public async Task Unwinds_in_exact_reverse_order_of_registration()
    {
        var ran = new List<SagaStep>();
        var stack = new CompensationLog();
        foreach (var step in SagaSteps.ForwardOrder)
        {
            stack.Push(step, () => { ran.Add(step); return Task.CompletedTask; });
        }

        stack.Pending.Should().Equal(SagaStep.CourierDispatch, SagaStep.Payment, SagaStep.KitchenTicket, SagaStep.PlatformAccept);

        var report = await stack.UnwindAsync();

        ran.Should().Equal(SagaStep.CourierDispatch, SagaStep.Payment, SagaStep.KitchenTicket, SagaStep.PlatformAccept);
        report.AllSucceeded.Should().BeTrue();
        stack.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_compensation_does_not_stop_the_ones_before_it()
    {
        var ran = new List<SagaStep>();
        var stack = new CompensationLog();
        stack.Push(SagaStep.PlatformAccept, () => { ran.Add(SagaStep.PlatformAccept); return Task.CompletedTask; });
        stack.Push(SagaStep.KitchenTicket, () => { ran.Add(SagaStep.KitchenTicket); return Task.CompletedTask; });
        stack.Push(SagaStep.Payment, () => throw new InvalidOperationException("refund API gone"));

        var report = await stack.UnwindAsync();

        ran.Should().Equal(SagaStep.KitchenTicket, SagaStep.PlatformAccept);
        report.AllSucceeded.Should().BeFalse();
        report.Results.Should().ContainSingle(r => !r.Succeeded)
            .Which.Should().Be(new CompensationResult(SagaStep.Payment, false, "refund API gone"));
    }

    [Fact]
    public void The_same_step_cannot_be_registered_twice()
    {
        var stack = new CompensationLog();
        stack.Push(SagaStep.Payment, () => Task.CompletedTask);

        var act = () => stack.Push(SagaStep.Payment, () => Task.CompletedTask);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Unwinding_an_empty_stack_is_a_no_op()
    {
        var report = await new CompensationLog().UnwindAsync();
        report.Results.Should().BeEmpty();
        report.AllSucceeded.Should().BeTrue();
    }

    /// <summary>
    /// For any prefix of registered steps and any subset of compensations that throw: every registered compensation is
    /// attempted exactly once, strictly newest-first, and the report names exactly the ones that threw.
    /// </summary>
    [Property(MaxTest = 300)]
    public Property Every_registered_compensation_runs_once_newest_first_whatever_fails()
    {
        var gen =
            from registered in Gen.Choose(0, 4)
            from failing in Gen.ListOf(Gen.Elements(true, false), 4)
            select (registered, failing: failing.ToArray());

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var steps = SagaSteps.ForwardOrder.Take(input.registered).ToArray();
            var attempts = new List<SagaStep>();
            var stack = new CompensationLog();
            foreach (var step in steps)
            {
                var fails = input.failing[(int)step - 1];
                stack.Push(step, () =>
                {
                    attempts.Add(step);
                    return fails ? throw new InvalidOperationException(step.ToString()) : Task.CompletedTask;
                });
            }

            var report = stack.UnwindAsync().GetAwaiter().GetResult();

            var expectedOrder = steps.Reverse().ToArray();
            var expectedFailed = expectedOrder.Where(s => input.failing[(int)s - 1]).ToArray();
            return attempts.SequenceEqual(expectedOrder)
                && report.Results.Select(r => r.Step).SequenceEqual(expectedOrder)
                && report.Results.Where(r => !r.Succeeded).Select(r => r.Step).SequenceEqual(expectedFailed)
                && report.AllSucceeded == (expectedFailed.Length == 0);
        });
    }
}
