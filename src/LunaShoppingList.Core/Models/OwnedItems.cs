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

/// <summary>
/// What one retainer held the last time it was open. Counts are per item id, already summed by the caller
/// (high-quality and normal copies of an item are the caller's concern, not this type's).
/// <paramref name="Stored"/> is the retainer's inventory, crystals and equipment-free storage;
/// <paramref name="Market"/> is what it has listed on the market board.
/// </summary>
public sealed record RetainerSnapshot(
    ulong Id,
    string Name,
    DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<uint, int> Stored,
    IReadOnlyDictionary<uint, int> Market);

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
