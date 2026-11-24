namespace LunaShoppingList.Core;

/// <summary>The outcome of <see cref="OwnedSubtraction.Apply"/>.</summary>
/// <param name="Remaining">What is still to buy: one requirement per item, ordered by item id, items fully covered left out.</param>
/// <param name="FullyCovered">Items whose whole need is held, with the quantity that was needed, ordered by item id.</param>
/// <param name="Held">Per item, how many of the units needed are covered by what is owned (never more than the need); only items with at least one unit covered.</param>
public record OwnedSubtractionResult(
    IReadOnlyList<Requirement> Remaining,
    IReadOnlyList<Requirement> FullyCovered,
    IReadOnlyDictionary<uint, int> Held);

/// <summary>Reduces what must be bought by what the player already owns (bags, saddlebag, retainers; never the house).</summary>
public static class OwnedSubtraction
{
    /// <summary>
    /// Requirements are merged per item (zero or less ignored, clamped to <c>int.MaxValue</c>). For each item the owned count is
    /// subtracted: an unknown count (<c>null</c>), zero or less leaves the quantity unchanged; a count that reaches the need drops
    /// the item and lists it in <see cref="OwnedSubtractionResult.FullyCovered"/>. A quantity is never increased or made negative.
    /// </summary>
    /// <param name="requirements">Quantities needed.</param>
    /// <param name="ownedTotal">The owned count of an item (the same notion as <c>ShoppingSession.ExcludeOwned</c>), or <c>null</c> when unknown.</param>
    public static OwnedSubtractionResult Apply(IReadOnlyList<Requirement> requirements, Func<uint, long?> ownedTotal)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(ownedTotal);

        var needed = new SortedDictionary<uint, long>();
        foreach (var r in requirements)
        {
            if (r.Quantity <= 0) continue;
            needed[r.ItemId] = needed.GetValueOrDefault(r.ItemId) + r.Quantity;
        }

        var remaining = new List<Requirement>();
        var covered = new List<Requirement>();
        var held = new Dictionary<uint, int>();
        foreach (var (itemId, total) in needed)
        {
            var need = (int)Math.Min(total, int.MaxValue);
            if (ownedTotal(itemId) is not long have || have <= 0)
            {
                remaining.Add(new Requirement(itemId, need));
                continue;
            }

            var used = (int)Math.Min(have, need);
            held[itemId] = used;
            if (used >= need)
                covered.Add(new Requirement(itemId, need));
            else
                remaining.Add(new Requirement(itemId, need - used));
        }

        return new OwnedSubtractionResult(remaining, covered, held);
    }
}
