namespace LunaShoppingList.Core;

/// <summary>The user's editable buying decisions: how many units of each item to buy on each world.</summary>
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

    /// <summary>Replaces the whole allocation with <paramref name="lines"/> (for example a suggestion). Unknown items or worlds are ignored; quantities are clamped to the units for sale; lines for the same item and world are summed first.</summary>
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
            var quantity = (int)Math.Min(requested, FindWorld(key.Item, key.World)!.UnitsForSale);
            if (quantity > 0)
                next[key] = quantity;
        }
        _quantities = next;
    }

    /// <summary>Units currently allocated to buy of <paramref name="itemId"/> on <paramref name="worldId"/> (0 if none).</summary>
    public int GetQuantity(uint itemId, uint worldId)
        => _quantities.TryGetValue((itemId, worldId), out var q) ? q : 0;

    /// <summary>Sets the units to buy; clamped to [0, units for sale on that world]; unknown item or world is ignored. Zero removes the line.</summary>
    public void SetQuantity(uint itemId, uint worldId, int quantity)
    {
        var offer = FindWorld(itemId, worldId);
        if (offer is null)
            return;

        var clamped = Math.Clamp(quantity, 0, offer.UnitsForSale);
        var next = new Dictionary<(uint Item, uint World), int>(_quantities);
        if (clamped == 0)
            next.Remove((itemId, worldId));
        else
            next[(itemId, worldId)] = clamped;
        _quantities = next;
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

    /// <summary>Units still missing for <paramref name="itemId"/>: needed minus allocated, never negative; an over-allocation reports 0.</summary>
    public int Unmet(uint itemId)
        => _items.TryGetValue(itemId, out var item) ? (int)Math.Max(0L, (long)item.Needed - Allocated(itemId)) : 0;

    /// <summary>Gil for the units allocated to <paramref name="itemId"/> on <paramref name="worldId"/>, cheapest-first from that world's ladder.</summary>
    public long Cost(uint itemId, uint worldId)
        => FindWorld(itemId, worldId)?.CostFor(GetQuantity(itemId, worldId)) ?? 0;

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
