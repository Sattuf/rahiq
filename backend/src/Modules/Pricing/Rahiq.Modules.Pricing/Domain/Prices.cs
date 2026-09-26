namespace Rahiq.Modules.Pricing.Domain;

/// <summary>One row of price history. Prices are never overwritten: a change closes the current row and opens a new one.</summary>
internal sealed class PriceEntry
{
    public Guid VariantId { get; init; }

    public required string Currency { get; init; }

    public long Amount { get; init; }

    public DateTimeOffset ValidFrom { get; init; }

    public DateTimeOffset? ValidTo { get; set; }

    public Guid? SetBy { get; init; }

    public bool IsActiveAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || ValidTo > at);
}

/// <summary>
/// Turkish discount display rule (compliance.md §3): the "previous price" next to a discounted price is the lowest
/// price applied in the 30 days before the current price took effect. It is computed, never typed in.
/// </summary>
internal static class PreviousPriceRule
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <returns>The previous price to show, or null when the current price is not a discount.</returns>
    public static long? Compute(IReadOnlyList<PriceEntry> history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        var current = history.FirstOrDefault(p => p.IsActiveAt(now));
        if (current is null)
        {
            return null;
        }

        var windowStart = current.ValidFrom - Window;
        var windowEnd = current.ValidFrom;
        var lowest = history
            .Where(p => !ReferenceEquals(p, current) && p.ValidFrom < windowEnd && (p.ValidTo ?? DateTimeOffset.MaxValue) > windowStart)
            .Select(p => (long?)p.Amount)
            .Min();

        return lowest > current.Amount ? lowest : null;
    }
}
