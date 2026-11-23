namespace LunaShoppingList.Core;

/// <summary>
/// Decides when what an open house holds may be read and filed under that house. Same shape as <see cref="RetainerVisitGate"/>, with its own
/// state: after the house id changes (including through 0, not in a house) nothing is read for <see cref="SettleMs"/>; the first capture
/// of a visit then needs two identical reads <see cref="StableIntervalMs"/> apart; once confirmed, one read every <see cref="RecheckMs"/>.
/// Times are passed in. Offered dictionaries are kept for comparison and must not be changed afterwards.
/// </summary>
public sealed class HouseVisitGate
{
    /// <summary>How long nothing is read after the house id changes.</summary>
    public const long SettleMs = 2_000;

    /// <summary>The gap between the two identical reads that confirm a visit.</summary>
    public const long StableIntervalMs = 1_000;

    /// <summary>The gap between reads once a visit is confirmed.</summary>
    public const long RecheckMs = 3_000;

    private IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>>? _lastPlaces;
    private long _nextReadMs;

    /// <summary>The house currently open (0 = none).</summary>
    public ulong HouseId { get; private set; }

    /// <summary>Whether this visit's contents have been confirmed.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Reports the current house and whether its containers are ready; returns whether to read them now.</summary>
    public bool ShouldRead(ulong houseId, bool ready, long nowMs)
    {
        if (houseId != HouseId)
        {
            HouseId = houseId;
            Confirmed = false;
            _lastPlaces = null;
            _nextReadMs = nowMs + SettleMs;
            return false;
        }
        if (houseId == 0)
            return false;
        if (!ready)
        {
            // A house that is (re)loading must show the same contents twice again before anything is filed.
            _lastPlaces = null;
            return false;
        }
        return nowMs >= _nextReadMs;
    }

    /// <summary>Offers what was just read; returns whether it may be filed under <see cref="HouseId"/>.</summary>
    public bool Offer(IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>> places, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(places);
        if (HouseId == 0)
            return false;
        if (Confirmed)
        {
            _nextReadMs = nowMs + RecheckMs;
            return true;
        }
        if (_lastPlaces is not null && OwnedCounting.SameHousePlaces(_lastPlaces, places))
        {
            Confirmed = true;
            _lastPlaces = null;
            _nextReadMs = nowMs + RecheckMs;
            return true;
        }
        _lastPlaces = places;
        _nextReadMs = nowMs + StableIntervalMs;
        return false;
    }
}
