namespace LunaShoppingList.Core;

/// <summary>
/// The user's editable buying decisions: how many units of each item to buy on each world. A market listing can only be bought as a
/// whole stack, so every stored quantity is a legal total for its world (a sum of whole stacks) and can be more than the item needs.
/// </summary>
public sealed class AllocationState
{
    private readonly Dictionary<uint, ItemOffers> _items = new();
    private Dictionary<(uint Item, uint World), int> _quantities = new();

    /// <summary>Creates an empty allocation over <paramref name="book"/>.</summary>
    public AllocationState(OfferBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        Book = book;
        foreach (var item in book.Items)
            _items.TryAdd(item.ItemId, item);
    }

    /// <summary>The offers the allocation is made against.</summary>
    public OfferBook Book { get; }

    /// <summary>Replaces the whole allocation with <paramref name="lines"/> (for example a suggestion). Unknown items or worlds are ignored; lines for the same item and world are summed first; each sum snaps up to a legal total for that world, capped at the world's supply (see <see cref="LinePricer.Price"/>).</summary>
    public void Apply(IEnumerable<AllocationLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var summed = new Dictionary<(uint Item, uint World), long>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || FindWorld(line.ItemId, line.WorldId) is null)
                continue;
            var key = (line.ItemId, line.WorldId);
            summed[key] = summed.GetValueOrDefault(key) + line.Quantity;
        }

        var next = new Dictionary<(uint Item, uint World), int>();
        foreach (var (key, requested) in summed)
        {
            var priced = LinePricer.Price(FindWorld(key.Item, key.World)!, requested);
            if (priced is not null)
                next[key] = priced.Quantity;
        }
        _quantities = next;
    }

    /// <summary>Units currently allocated to buy of <paramref name="itemId"/> on <paramref name="worldId"/> (0 if none); always a legal total.</summary>
    public int GetQuantity(uint itemId, uint worldId)
        => _quantities.TryGetValue((itemId, worldId), out var q) ? q : 0;

    /// <summary>
    /// Sets the units to buy. The quantity snaps UP to the next legal total for that world (a sum of whole stacks) and is capped at the
    /// world's largest legal total (<see cref="MaxQuantity"/>); zero or less removes the line; an unknown item or world is ignored.
    /// </summary>
    public void SetQuantity(uint itemId, uint worldId, int quantity)
    {
        var offer = FindWorld(itemId, worldId);
        if (offer is null)
            return;

        var priced = LinePricer.Price(offer, quantity);
        var next = new Dictionary<(uint Item, uint World), int>(_quantities);
        if (priced is null)
            next.Remove((itemId, worldId));
        else
            next[(itemId, worldId)] = priced.Quantity;
        _quantities = next;
    }

    /// <summary>The largest quantity that can be bought of <paramref name="itemId"/> on <paramref name="worldId"/>: the whole supply (always a legal total). 0 for an unknown item or world.</summary>
    public int MaxQuantity(uint itemId, uint worldId)
        => FindWorld(itemId, worldId)?.SnapDown(int.MaxValue) ?? 0;

    /// <summary>
    /// The legal total just above the current quantity (the next stepping position up), without changing anything. From nothing it is
    /// the world's smallest legal total; at the top it stays at the top. 0 for an unknown item or world.
    /// </summary>
    public int NextLegalUp(uint itemId, uint worldId)
    {
        var offer = FindWorld(itemId, worldId);
        if (offer is null)
            return 0;
        var current = GetQuantity(itemId, worldId);
        return current >= MaxQuantity(itemId, worldId) ? current : offer.SnapUp(current + 1);
    }

    /// <summary>
    /// The legal total just below the current quantity (the next stepping position down), without changing anything; 0 when there is
    /// none (that removes the line). For a world beyond its exact limit the answer is the largest legal total at or below
    /// <see cref="WorldOffer.ExactTotalsLimit"/>, which can be a long way down. 0 for an unknown item or world.
    /// </summary>
    public int NextLegalDown(uint itemId, uint worldId)
    {
        var offer = FindWorld(itemId, worldId);
        if (offer is null)
            return 0;
        var current = GetQuantity(itemId, worldId);
        return current <= 0 ? 0 : offer.SnapDown(current - 1);
    }

    /// <summary>Moves the quantity one legal total up (<paramref name="direction"/> above zero) or down (zero or below) and returns the new quantity; going down from the lowest legal total removes the line (returns 0). Unknown item or world: nothing changes, returns 0.</summary>
    public int StepQuantity(uint itemId, uint worldId, int direction)
    {
        if (FindWorld(itemId, worldId) is null)
            return 0;
        SetQuantity(itemId, worldId, direction > 0 ? NextLegalUp(itemId, worldId) : NextLegalDown(itemId, worldId));
        return GetQuantity(itemId, worldId);
    }

    /// <summary>Total units allocated for <paramref name="itemId"/> over all worlds.</summary>
    public int Allocated(uint itemId)
    {
        long total = 0;
        foreach (var (key, q) in _quantities)
            if (key.Item == itemId)
                total += q;
        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>Units actually bought of <paramref name="itemId"/> over all worlds (the same number as <see cref="Allocated"/>); can exceed what the item needs.</summary>
    public int Bought(uint itemId) => Allocated(itemId);

    /// <summary>Units still missing for <paramref name="itemId"/>: needed minus bought, never negative.</summary>
    public int Unmet(uint itemId)
        => _items.TryGetValue(itemId, out var item) ? (int)Math.Max(0L, (long)item.Needed - Allocated(itemId)) : 0;

    /// <summary>Units bought beyond the need for <paramref name="itemId"/>: bought minus needed, never negative (stacks cannot be split). 0 for an unknown item.</summary>
    public int Surplus(uint itemId)
        => _items.TryGetValue(itemId, out var item) ? (int)Math.Max(0L, Allocated(itemId) - (long)item.Needed) : 0;

    /// <summary>Gil for the whole stacks allocated to <paramref name="itemId"/> on <paramref name="worldId"/> (the exact price of that legal total); 0 when nothing is allocated.</summary>
    public long Cost(uint itemId, uint worldId) => PriceOf(itemId, worldId)?.Cost ?? 0;

    /// <summary>Number of stacks (listings) bought for <paramref name="itemId"/> on <paramref name="worldId"/>; 0 when nothing is allocated.</summary>
    public int StackCount(uint itemId, uint worldId) => PriceOf(itemId, worldId)?.StackCount ?? 0;

    /// <summary>Quantity of each stack bought for <paramref name="itemId"/> on <paramref name="worldId"/>, in ascending listing order; empty when nothing is allocated.</summary>
    public IReadOnlyList<int> StackSizes(uint itemId, uint worldId)
        => PriceOf(itemId, worldId)?.StackSizes ?? Array.Empty<int>();

    /// <summary>The current allocation lines, ordered by item id then world id.</summary>
    public IReadOnlyList<AllocationLine> Lines
        => _quantities
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key.Item)
            .ThenBy(kv => kv.Key.World)
            .Select(kv => new AllocationLine(kv.Key.Item, kv.Key.World, kv.Value))
            .ToList()
            .AsReadOnly();

    /// <summary>The route for the current allocation.</summary>
    public RouteSummary Summarize(Func<uint, string?>? dataCenterOf = null) => RouteSummarizer.Summarize(Book, Lines, dataCenterOf);

    private LinePrice? PriceOf(uint itemId, uint worldId)
    {
        var quantity = GetQuantity(itemId, worldId);
        var offer = quantity > 0 ? FindWorld(itemId, worldId) : null;
        return offer is null ? null : LinePricer.Price(offer, quantity);
    }

    private WorldOffer? FindWorld(uint itemId, uint worldId)
    {
        if (!_items.TryGetValue(itemId, out var item))
            return null;
        foreach (var w in item.Worlds)
            if (w.WorldId == worldId)
                return w;
        return null;
    }
}
