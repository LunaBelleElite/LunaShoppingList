namespace LunaShoppingList.Core;

/// <summary>Small pure helpers for turning inventory slots into per-item counts and comparing or copying them.</summary>
public static class OwnedCounting
{
    /// <summary>
    /// Adds one stack to <paramref name="counts"/>. Item 0 (an empty slot) and non-positive quantities are ignored;
    /// a sum past <see cref="int.MaxValue"/> stays at <see cref="int.MaxValue"/>.
    /// </summary>
    public static void Add(Dictionary<uint, int> counts, uint itemId, int quantity)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (itemId == 0 || quantity <= 0)
            return;
        counts[itemId] = counts.TryGetValue(itemId, out var have) ? (int)Math.Min((long)have + quantity, int.MaxValue) : quantity;
    }

    /// <summary>Whether both hold exactly the same items with the same counts.</summary>
    public static bool SameCounts(IReadOnlyDictionary<uint, int> a, IReadOnlyDictionary<uint, int> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
            return false;
        foreach (var (item, count) in a)
        {
            if (!b.TryGetValue(item, out var other) || other != count)
                return false;
        }
        return true;
    }

    /// <summary>A new cache holding the same snapshots as <paramref name="source"/>; later changes to either do not affect the other.</summary>
    public static OwnedCache Copy(OwnedCache source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new OwnedCache();
        if (source.Saddlebag is { } s)
            copy.ReplaceSaddlebag(s.CapturedUtc, s.Items);
        foreach (var r in source.Retainers)
            copy.ReplaceRetainer(r.Id, r.Name, r.CapturedUtc, r.Stored, r.Market);
        return copy;
    }
}

/// <summary>
/// Decides when what an open retainer holds may be read and filed under that retainer. Its containers can still hold the previous
/// retainer's items for a moment after a switch, so after a retainer opens (or the open retainer changes, including through none)
/// nothing is read for <see cref="SettleMs"/>; after that, the first capture of the visit needs two identical reads
/// <see cref="StableIntervalMs"/> apart. Once confirmed, every read is filed, one every <see cref="RecheckMs"/>. Times are passed in
/// (this type never reads a clock). Offered dictionaries are kept for comparison and must not be changed afterwards.
/// </summary>
public sealed class RetainerVisitGate
{
    /// <summary>How long nothing is read after the open retainer changes.</summary>
    public const long SettleMs = 2_000;

    /// <summary>The gap between the two identical reads that confirm a visit.</summary>
    public const long StableIntervalMs = 1_000;

    /// <summary>The gap between reads once a visit is confirmed.</summary>
    public const long RecheckMs = 3_000;

    private IReadOnlyDictionary<uint, int>? _lastStored;
    private IReadOnlyDictionary<uint, int>? _lastMarket;
    private long _nextReadMs;

    /// <summary>The retainer currently open (0 = none).</summary>
    public ulong RetainerId { get; private set; }

    /// <summary>Whether this visit's contents have been confirmed (the first capture may be filed).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// Reports the open retainer (<paramref name="retainerId"/>, 0 when none) and whether its containers are ready to read.
    /// Returns whether the caller should read them now and pass the result to <see cref="Offer"/>.
    /// </summary>
    public bool ShouldRead(ulong retainerId, bool ready, long nowMs)
    {
        if (retainerId != RetainerId)
        {
            RetainerId = retainerId;
            Confirmed = false;
            ForgetSample();
            _nextReadMs = nowMs + SettleMs;
            return false;
        }
        if (retainerId == 0)
            return false;
        if (!ready)
        {
            // A retainer that is (re)loading must show the same contents twice again before anything is filed.
            ForgetSample();
            return false;
        }
        return nowMs >= _nextReadMs;
    }

    /// <summary>
    /// Offers what was just read. Returns whether it may be filed under <see cref="RetainerId"/>: before confirmation only when it
    /// matches the previous read exactly, afterwards always.
    /// </summary>
    public bool Offer(IReadOnlyDictionary<uint, int> stored, IReadOnlyDictionary<uint, int> market, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(market);
        if (RetainerId == 0)
            return false;
        if (Confirmed)
        {
            _nextReadMs = nowMs + RecheckMs;
            return true;
        }
        var stable = _lastStored is not null && _lastMarket is not null
            && OwnedCounting.SameCounts(_lastStored, stored) && OwnedCounting.SameCounts(_lastMarket, market);
        if (stable)
        {
            Confirmed = true;
            ForgetSample();
            _nextReadMs = nowMs + RecheckMs;
            return true;
        }
        _lastStored = stored;
        _lastMarket = market;
        _nextReadMs = nowMs + StableIntervalMs;
        return false;
    }

    private void ForgetSample()
    {
        _lastStored = null;
        _lastMarket = null;
    }
}
