namespace LunaShoppingList.Core;

/// <summary>Where a retainer figure came from.</summary>
public enum RetainerSource
{
    /// <summary>No retainer was ever captured and no external source answered: the count is unknown, not zero.</summary>
    NotAvailable,

    /// <summary>The sum over the retainer snapshots this plugin captured itself.</summary>
    Cache,

    /// <summary>An external source (for example another plugin) that tracks the retainers itself.</summary>
    External,
}

/// <summary>
/// What was in the saddlebag the last time it was open. Counts are per item id, already summed by the caller
/// (high-quality and normal copies of an item are the caller's concern, not this type's).
/// </summary>
public sealed record SaddlebagSnapshot(DateTimeOffset CapturedUtc, IReadOnlyDictionary<uint, int> Items);

/// <summary>The containers of a retainer that are counted separately.</summary>
public enum RetainerPlace
{
    /// <summary>Retainer inventory page 1.</summary>
    Bag1,
    /// <summary>Retainer inventory page 2.</summary>
    Bag2,
    /// <summary>Retainer inventory page 3.</summary>
    Bag3,
    /// <summary>Retainer inventory page 4.</summary>
    Bag4,
    /// <summary>Retainer inventory page 5.</summary>
    Bag5,
    /// <summary>Retainer inventory page 6.</summary>
    Bag6,
    /// <summary>Retainer inventory page 7.</summary>
    Bag7,
    /// <summary>The retainer crystal container.</summary>
    Crystals,

    /// <summary>The retainer market board listings.</summary>
    Market,
}

/// <summary>
/// What one retainer held the last time it was open. Counts are per item id, already summed by the caller
/// (high-quality and normal copies of an item are the caller's concern, not this type's).
/// <paramref name="Stored"/> is the retainer's bags and crystals (equipment is not counted);
/// <paramref name="Market"/> is what it has listed on the market board. <paramref name="Places"/> says which container held what;
/// it is <c>null</c> for a record made before containers were told apart, whose counts are kept but have no known place.
/// When <paramref name="Places"/> is present, <paramref name="Stored"/> and <paramref name="Market"/> are sums of it.
/// </summary>
public sealed record RetainerSnapshot(
    ulong Id,
    string Name,
    DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<uint, int> Stored,
    IReadOnlyDictionary<uint, int> Market,
    IReadOnlyDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>? Places = null)
{
    /// <summary>Whether the container of each item is known (false for a record made before containers were told apart).</summary>
    public bool LocationRecorded => Places is not null;
}

/// <summary>
/// Where one retainer holds an item. <paramref name="Places"/> maps each container that holds it to its count, in container
/// order, and is empty when <paramref name="LocationRecorded"/> is false. <paramref name="Total"/> is bags, crystals and market
/// listings together, so the totals over all retainers add up to <see cref="OwnedCache.RetainerTotal"/>.
/// </summary>
public sealed record RetainerLocation(
    ulong RetainerId,
    string Name,
    DateTimeOffset CapturedUtc,
    bool LocationRecorded,
    IReadOnlyDictionary<RetainerPlace, int> Places,
    long Total);

/// <summary>
/// How many of one item the player owns, by place. A <c>null</c> count means "unknown", never zero: show a dash,
/// not 0. Sums are <c>long</c>, so adding many <see cref="int.MaxValue"/> counts cannot overflow.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="OnYou">Bags plus crystals on the character right now (read live); 0 when none; <c>null</c> until the bags have been read.</param>
/// <param name="Saddlebag">Cached saddlebag count; <c>null</c> when the saddlebag was never captured.</param>
/// <param name="SaddlebagCapturedUtc">When the saddlebag was captured; <c>null</c> when never.</param>
/// <param name="Retainers">The retainer figure chosen by the rule (the larger of the external value and the cache when both exist, else whichever exists, else <c>null</c>). The two sources are never added together.</param>
/// <param name="RetainerSource">Where <paramref name="Retainers"/> came from (on a tie, the external source).</param>
/// <param name="RetainersCached">The cache's retainer total (stored plus market); <c>null</c> when no retainer was ever captured. Kept for tooltips.</param>
/// <param name="RetainersExternal">The external source's count; <c>null</c> when it is unavailable or did not know the item. Kept for tooltips.</param>
/// <param name="RetainersCapturedOldestUtc">Oldest retainer capture time in the cache; <c>null</c> when no retainer was captured.</param>
/// <param name="RetainersCapturedNewestUtc">Newest retainer capture time in the cache; <c>null</c> when no retainer was captured.</param>
public sealed record OwnedSummary(
    uint ItemId,
    int? OnYou,
    int? Saddlebag,
    DateTimeOffset? SaddlebagCapturedUtc,
    long? Retainers,
    RetainerSource RetainerSource,
    long? RetainersCached,
    long? RetainersExternal,
    DateTimeOffset? RetainersCapturedOldestUtc,
    DateTimeOffset? RetainersCapturedNewestUtc);
