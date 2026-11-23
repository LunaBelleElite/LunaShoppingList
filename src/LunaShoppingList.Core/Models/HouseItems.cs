namespace LunaShoppingList.Core;

/// <summary>The house containers that are counted separately.</summary>
public enum HousePlace
{
    /// <summary>Furniture placed in the house interior (the twelve interior furnishing containers).</summary>
    Placed,

    /// <summary>The storeroom (the eleven storeroom containers).</summary>
    Storeroom,
}

/// <summary>
/// What one house held the last time its Furnishings window was open there. <paramref name="Id"/> identifies the house (it includes the
/// world). <paramref name="Label"/> is a display name such as "W5 P12". Counts are per item id and already summed by the caller
/// (high-quality and normal copies are the caller's concern); <paramref name="Places"/> holds only places that hold something.
/// </summary>
public sealed record HouseSnapshot(
    ulong Id,
    string Label,
    DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>> Places)
{
    /// <summary>How many of <paramref name="itemId"/> this house holds in <paramref name="place"/>.</summary>
    public int Count(HousePlace place, uint itemId)
        => Places.TryGetValue(place, out var counts) ? counts.GetValueOrDefault(itemId) : 0;

    /// <summary>How many of <paramref name="itemId"/> this house holds in all places together (a <c>long</c>, no overflow).</summary>
    public long Total(uint itemId)
    {
        long total = 0;
        foreach (var counts in Places.Values)
            total += counts.GetValueOrDefault(itemId);
        return total;
    }

    /// <summary>Per item, the count over all places together.</summary>
    public IReadOnlyDictionary<uint, long> Totals
    {
        get
        {
            var sum = new SortedDictionary<uint, long>();
            foreach (var counts in Places.Values)
            {
                foreach (var (item, count) in counts)
                    sum[item] = sum.GetValueOrDefault(item) + count;
            }
            return sum;
        }
    }
}

/// <summary>
/// Where one house holds an item: <paramref name="Placed"/> (furniture in the interior) and <paramref name="Storeroom"/> counts,
/// <paramref name="Total"/> = both together, so the totals over all houses add up to <see cref="OwnedCache.HouseTotal"/>.
/// </summary>
public sealed record HouseLocation(
    ulong HouseId,
    string Label,
    DateTimeOffset CapturedUtc,
    int Placed,
    int Storeroom,
    long Total);
