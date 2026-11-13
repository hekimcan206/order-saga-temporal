namespace OrderSaga.Core;

/// <summary>One line of a delivery order. Prices are in kuruş (1/100 TRY) so money never touches floating point.</summary>
public sealed record OrderLine(string Sku, string Name, int Quantity, long UnitPriceKurus);

/// <summary>
/// A delivery order as it arrives from the ordering platform. It is the workflow input, so it is part of every
/// recorded history: changing its shape is a workflow-versioning concern (ADR 0004), not just an API change.
/// </summary>
/// <param name="CourierWaitSeconds">How long the saga waits for a courier assignment before it gives up and unwinds.</param>
public sealed record PlaceOrder(
    string OrderId,
    string StoreId,
    string CustomerRef,
    IReadOnlyList<OrderLine> Lines,
    int CourierWaitSeconds = 300)
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 99;
    public const long MaxTotalKurus = 10_000_000; // 100,000 TRY: anything larger is a typo or abuse, not a meal.

    /// <summary>Order total in kuruş.</summary>
    public long TotalKurus() => Lines.Sum(l => l.Quantity * l.UnitPriceKurus);

    /// <summary>The workflow id: one order maps to exactly one saga, which makes a re-sent order a no-op.</summary>
    public string WorkflowId() => WorkflowIdFor(OrderId);

    public static string WorkflowIdFor(string orderId) => $"order-{orderId}";

    /// <summary>Validates at the edge, before a workflow exists; the workflow trusts its input.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Identifiers.IsValid(OrderId))
        {
            errors.Add("orderId must be 1-64 characters of [A-Za-z0-9_-].");
        }

        if (!Identifiers.IsValid(StoreId))
        {
            errors.Add("storeId must be 1-64 characters of [A-Za-z0-9_-].");
        }

        if (!Identifiers.IsValid(CustomerRef))
        {
            errors.Add("customerRef must be an opaque 1-64 character reference, not personal data.");
        }

        if (Lines is null || Lines.Count == 0 || Lines.Count > MaxLines)
        {
            errors.Add($"an order needs 1-{MaxLines} lines.");
            return errors;
        }

        foreach (var line in Lines)
        {
            if (line is null || !Identifiers.IsValid(line.Sku) || string.IsNullOrWhiteSpace(line.Name) || line.Name.Length > 120)
            {
                errors.Add("each line needs a valid sku and a name of at most 120 characters.");
                continue;
            }

            if (line.Quantity is < 1 or > MaxQuantity || line.UnitPriceKurus is < 1 or > MaxTotalKurus)
            {
                errors.Add($"each line needs quantity 1-{MaxQuantity} and a unit price of 1-{MaxTotalKurus} kuruş.");
            }
        }

        if (errors.Count == 0 && TotalKurus() > MaxTotalKurus)
        {
            errors.Add("order total exceeds the configured maximum.");
        }

        if (CourierWaitSeconds is < 1 or > 3600)
        {
            errors.Add("courierWaitSeconds must be 1-3600.");
        }

        return errors;
    }
}

/// <summary>Identifier rules shared by the API, the workflow and the downstream services.</summary>
public static class Identifiers
{
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
