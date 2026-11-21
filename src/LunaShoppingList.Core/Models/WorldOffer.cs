namespace LunaShoppingList.Core;

/// <summary>
/// What one world can supply of one item: its listings as a price ladder, cheapest first.
/// </summary>
/// <param name="WorldId">World row id.</param>
/// <param name="WorldName">World name.</param>
/// <param name="Steps">Price ladder sorted by unit price ascending (ties: newer review first); the builder guarantees the order.</param>
public record WorldOffer(uint WorldId, string WorldName, IReadOnlyList<PriceStep> Steps)
{
    // The total is computed once per Steps list and remembered. The cache remembers which list it was computed from, so an
    // instance made with "with { Steps = ... }" recomputes. It is held in one reference so a concurrent reader never sees a
    // list paired with another list's total, and it is left out of equality below.
    private sealed record UnitsCache(IReadOnlyList<PriceStep> Source, int Units);

    private UnitsCache? _unitsCache;

    /// <summary>Total units for sale on this world (clamped to <see cref="int.MaxValue"/>).</summary>
    public int UnitsForSale
    {
        get
        {
            var cache = _unitsCache;
            if (cache is not null && ReferenceEquals(cache.Source, Steps))
                return cache.Units;
            long total = 0;
            foreach (var s in Steps)
                total += s.Quantity;
            var units = (int)Math.Min(total, int.MaxValue);
            _unitsCache = new UnitsCache(Steps, units);
            return units;
        }
    }

    /// <summary>Two offers are equal when their world id, world name and steps list are equal; the remembered total is not compared.</summary>
    public virtual bool Equals(WorldOffer? other)
        => other is not null
            && EqualityComparer<uint>.Default.Equals(WorldId, other.WorldId)
            && EqualityComparer<string>.Default.Equals(WorldName, other.WorldName)
            && EqualityComparer<IReadOnlyList<PriceStep>>.Default.Equals(Steps, other.Steps);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            EqualityComparer<uint>.Default.GetHashCode(WorldId),
            EqualityComparer<string>.Default.GetHashCode(WorldName),
            EqualityComparer<IReadOnlyList<PriceStep>>.Default.GetHashCode(Steps));

    /// <summary>Gil to buy <paramref name="quantity"/> units cheapest-first; a quantity above <see cref="UnitsForSale"/> is clamped, zero or less costs nothing.</summary>
    public long CostFor(int quantity)
    {
        long cost = 0;
        var remaining = Math.Min(quantity, UnitsForSale);
        foreach (var s in Steps)
        {
            if (remaining <= 0)
                break;
            var take = Math.Min(remaining, s.Quantity);
            cost += (long)take * s.UnitPrice;
            remaining -= take;
        }
        return cost;
    }

    /// <summary>Unit price of the cheapest unit on this world, or 0 when nothing is for sale.</summary>
    public int MinUnitPrice => Steps.Count == 0 ? 0 : Steps[0].UnitPrice;

    /// <summary>Unit price of the most expensive unit used when buying <paramref name="quantity"/> units, or 0 when none.</summary>
    public int MaxUnitPriceFor(int quantity)
    {
        var remaining = Math.Min(quantity, UnitsForSale);
        var max = 0;
        foreach (var s in Steps)
        {
            if (remaining <= 0)
                break;
            max = s.UnitPrice;
            remaining -= s.Quantity;
        }
        return max;
    }

    /// <summary>Oldest review time among the listings used when buying <paramref name="quantity"/> units; <see cref="DateTimeOffset.MaxValue"/> when none.</summary>
    public DateTimeOffset OldestReviewFor(int quantity)
    {
        var remaining = Math.Min(quantity, UnitsForSale);
        var oldest = DateTimeOffset.MaxValue;
        foreach (var s in Steps)
        {
            if (remaining <= 0)
                break;
            if (s.LastReview < oldest)
                oldest = s.LastReview;
            remaining -= s.Quantity;
        }
        return oldest;
    }
}
