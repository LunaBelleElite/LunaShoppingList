namespace LunaShoppingList.Core;

/// <summary>
/// The one place that turns "I want about this many units from this world" into whole stacks and their price, so
/// <see cref="AllocationState"/> and <see cref="RouteSummarizer"/> can never disagree.
/// </summary>
public static class LinePricer
{
    /// <summary>
    /// Prices <paramref name="requested"/> units on <paramref name="offer"/> as whole stacks. Null when nothing can be bought (zero or less
    /// asked, or the world has no stacks). Otherwise:
    /// <list type="number">
    /// <item>The request is capped at the world's full supply.</item>
    /// <item>A legal total (see <see cref="WorldOffer.ExactCost"/>) is priced exactly, with the stacks <see cref="WorldOffer.Stacks"/> names.</item>
    /// <item>A total that whole stacks cannot make snaps UP to the next legal total (<see cref="WorldOffer.SnapUp"/>) and that is priced exactly.</item>
    /// <item>Fallback, only for worlds beyond <see cref="WorldOffer.ExactTotalsLimit"/> where exactness is not available: the
    /// <see cref="WorldOffer.Cover"/> of the request is used, <see cref="LinePrice.Quantity"/> is the cover's units (at least the request,
    /// or the full supply) and <see cref="LinePrice.Approximate"/> is true. The full supply is always exact.</item>
    /// </list>
    /// </summary>
    public static LinePrice? Price(WorldOffer offer, long requested)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (requested <= 0)
            return null;
        var supply = offer.SnapDown(int.MaxValue);
        if (supply <= 0)
            return null;
        var q = (int)Math.Min(requested, supply);

        var exact = offer.ExactCost(q);
        if (exact is null)
        {
            var snapped = offer.SnapUp(q);
            if (snapped != q && offer.ExactCost(snapped) is not null)
            {
                q = snapped;
                exact = offer.ExactCost(q);
            }
        }

        if (exact is { } cost && offer.Stacks(q) is { } indices)
            return Build(offer, q, cost, indices, approximate: false);

        var cover = offer.Cover(q);
        return Build(offer, cover.Units, cover.Cost, cover.StackIndices, approximate: true);
    }

    private static LinePrice Build(WorldOffer offer, int quantity, long cost, IReadOnlyList<int> indices, bool approximate)
    {
        var sizes = new int[indices.Count];
        int min = 0, max = 0;
        var oldest = DateTimeOffset.MaxValue;
        for (var k = 0; k < indices.Count; k++)
        {
            var step = offer.Steps[indices[k]];
            sizes[k] = step.Quantity;
            min = k == 0 ? step.UnitPrice : Math.Min(min, step.UnitPrice);
            max = Math.Max(max, step.UnitPrice);
            if (step.LastReview < oldest)
                oldest = step.LastReview;
        }
        return new LinePrice(quantity, cost, indices, sizes, min, max, oldest, approximate);
    }
}
