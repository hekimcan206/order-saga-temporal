using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OrderSaga.Core;

namespace OrderSaga.UnitTests.Core;

public sealed class OrderTests
{
    private static PlaceOrder Valid() => new(
        "ord-1001", "store-01", "cust-7f3a",
        [new OrderLine("adana-kebap", "Adana kebap", 2, 32_000), new OrderLine("ayran", "Ayran", 2, 4_500)]);

    [Fact]
    public void Total_is_the_sum_of_lines_in_kurus()
    {
        Valid().TotalKurus().Should().Be(73_000);
    }

    [Fact]
    public void A_valid_order_has_no_errors()
    {
        Valid().Validate().Should().BeEmpty();
    }

    [Fact]
    public void Workflow_id_is_derived_from_the_order_id()
    {
        Valid().WorkflowId().Should().Be("order-ord-1001");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("ünicode")]
    public void Order_ids_outside_the_safe_alphabet_are_rejected(string orderId)
    {
        (Valid() with { OrderId = orderId }).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Empty_orders_and_absurd_quantities_are_rejected()
    {
        (Valid() with { Lines = [] }).Validate().Should().NotBeEmpty();
        (Valid() with { Lines = [new OrderLine("x", "X", 0, 100)] }).Validate().Should().NotBeEmpty();
        (Valid() with { Lines = [new OrderLine("x", "X", 100, 100)] }).Validate().Should().NotBeEmpty();
        (Valid() with { Lines = [new OrderLine("x", "X", 1, 0)] }).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Totals_above_the_ceiling_are_rejected()
    {
        var lines = Enumerable.Range(0, 20).Select(i => new OrderLine($"s{i}", "Catering tray", 99, 9_000)).ToList();
        (Valid() with { Lines = lines }).Validate().Should().Contain(e => e.Contains("maximum", StringComparison.Ordinal));
    }

    [Fact]
    public void Courier_wait_must_be_bounded()
    {
        (Valid() with { CourierWaitSeconds = 0 }).Validate().Should().NotBeEmpty();
        (Valid() with { CourierWaitSeconds = 3601 }).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Idempotency_keys_are_stable_per_order_and_step()
    {
        SagaSteps.IdempotencyKey("ord-1", SagaStep.Payment).Should().Be("ord-1:payment");
        foreach (var step in SagaSteps.ForwardOrder)
        {
            SagaSteps.Parse(SagaSteps.Slug(step)).Should().Be(step);
        }
    }

    [Property(MaxTest = 200)]
    public Property Valid_orders_never_overflow_the_total()
    {
        var line =
            from qty in Gen.Choose(1, PlaceOrder.MaxQuantity)
            from price in Gen.Choose(1, 500_000)
            select new OrderLine("sku", "Item", qty, price);
        var gen = Gen.ListOf(line, 50).Select(lines => Valid() with { Lines = [.. lines] });

        return Prop.ForAll(gen.ToArbitrary(), order =>
            order.Validate().Count > 0 || (order.TotalKurus() > 0 && order.TotalKurus() <= PlaceOrder.MaxTotalKurus));
    }
}
