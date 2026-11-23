using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaShoppingList.Core;

/// <summary>
/// What one character was last seen to hold in the places that can only be read while they are open: the saddlebag
/// and each retainer. A fresh capture replaces the previous snapshot of that place completely. Counts are per item id and
/// already summed by the caller: high-quality and normal copies of an item are the caller's concern, not this type's.
/// Times are passed in by the caller (this type never reads a clock). Non-positive counts are ignored everywhere.
/// </summary>
public sealed class OwnedCache
{
    // The file stays Version 1 on purpose: a reader that only knows Version 1 skips the optional Places member and keeps every
    // count, so a downgrade loses locations and nothing else. Version 2 files are still read.
    private const int CurrentVersion = 1;
    private const int NewestReadableVersion = 2;

    private static readonly Dictionary<string, RetainerPlace> PlaceNames = Enum.GetValues<RetainerPlace>().ToDictionary(p => p.ToString());

    private static readonly Dictionary<string, HousePlace> HousePlaceNames = Enum.GetValues<HousePlace>().ToDictionary(p => p.ToString());

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<ulong, RetainerSnapshot> _retainers = new();
    private readonly Dictionary<ulong, HouseSnapshot> _houses = new();

    /// <summary>The saddlebag as last captured; <c>null</c> if it never was.</summary>
    public SaddlebagSnapshot? Saddlebag { get; private set; }

    /// <summary>Every retainer snapshot, ordered by retainer id.</summary>
    public IReadOnlyList<RetainerSnapshot> Retainers => _retainers.Values.OrderBy(r => r.Id).ToList();

    /// <summary>Every house snapshot, ordered by house id.</summary>
    public IReadOnlyList<HouseSnapshot> Houses => _houses.Values.OrderBy(h => h.Id).ToList();

    /// <summary>How many houses have been captured.</summary>
    public int HouseCount => _houses.Count;

    /// <summary>How many retainers have been captured.</summary>
    public int RetainerCount => _retainers.Count;

    /// <summary>The oldest capture time over the saddlebag and all retainers; <c>null</c> when nothing was captured.</summary>
    public DateTimeOffset? OldestCapture => Times(includeSaddlebag: true).Min(t => (DateTimeOffset?)t);

    /// <summary>The newest capture time over the saddlebag and all retainers; <c>null</c> when nothing was captured.</summary>
    public DateTimeOffset? NewestCapture => Times(includeSaddlebag: true).Max(t => (DateTimeOffset?)t);

    /// <summary>The oldest retainer capture time; <c>null</c> when no retainer was captured.</summary>
    public DateTimeOffset? OldestRetainerCapture => Times(includeSaddlebag: false).Min(t => (DateTimeOffset?)t);

    /// <summary>The newest retainer capture time; <c>null</c> when no retainer was captured.</summary>
    public DateTimeOffset? NewestRetainerCapture => Times(includeSaddlebag: false).Max(t => (DateTimeOffset?)t);

    private IEnumerable<DateTimeOffset> Times(bool includeSaddlebag)
    {
        foreach (var r in _retainers.Values)
            yield return r.CapturedUtc;
        if (includeSaddlebag && Saddlebag is not null)
            yield return Saddlebag.CapturedUtc;
    }

    /// <summary>
    /// Records what retainer <paramref name="id"/> held when it was opened, replacing its old snapshot completely: items no longer
    /// present vanish. <paramref name="stored"/> is the retainer's inventory and crystals, <paramref name="market"/> its market listings.
    /// </summary>
    public void ReplaceRetainer(ulong id, string name, DateTimeOffset capturedUtc, IEnumerable<KeyValuePair<uint, int>> stored, IEnumerable<KeyValuePair<uint, int>> market)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(market);
        _retainers[id] = new RetainerSnapshot(id, name ?? "", capturedUtc.ToUniversalTime(), Positive(stored), Positive(market));
    }

    /// <summary>
    /// Records what retainer <paramref name="id"/> held when it was opened, container by container, replacing its old snapshot completely.
    /// </summary>
    public void ReplaceRetainer(ulong id, string name, DateTimeOffset capturedUtc, IReadOnlyDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        var placed = new SortedDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>();
        var stored = new Dictionary<uint, int>();
        var market = new Dictionary<uint, int>();
        foreach (var (place, counts) in places)
        {
            if (!Enum.IsDefined(place) || counts is null)
                continue;
            var positive = Positive(counts);
            if (positive.Count == 0)
                continue;
            placed[place] = positive;
            var sum = place == RetainerPlace.Market ? market : stored;
            foreach (var (item, count) in positive)
                sum[item] = sum.TryGetValue(item, out var have) ? (int)Math.Min((long)have + count, int.MaxValue) : count;
        }
        _retainers[id] = new RetainerSnapshot(id, name ?? "", capturedUtc.ToUniversalTime(), Positive(stored), Positive(market), placed);
    }

    /// <summary>
    /// Records what house <paramref name="id"/> held when its Furnishings window was open, place by place, replacing its old snapshot
    /// completely (a recapture of identical contents is stored again, with the new time, like <c>ReplaceRetainer</c>). Non-positive counts
    /// and empty places are dropped.
    /// </summary>
    public void ReplaceHouse(ulong id, string label, DateTimeOffset capturedUtc, IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        var placed = new SortedDictionary<HousePlace, IReadOnlyDictionary<uint, int>>();
        foreach (var (place, counts) in places)
        {
            if (!Enum.IsDefined(place) || counts is null)
                continue;
            var positive = Positive(counts);
            if (positive.Count > 0)
                placed[place] = positive;
        }
        _houses[id] = new HouseSnapshot(id, label ?? "", capturedUtc.ToUniversalTime(), placed);
    }

    /// <summary>
    /// How many of <paramref name="itemId"/> every remembered house held, placed plus storeroom, summed in a <c>long</c>;
    /// <c>null</c> when no house was ever captured (unknown, not zero).
    /// </summary>
    public long? HouseTotal(uint itemId)
    {
        if (_houses.Count == 0)
            return null;
        long total = 0;
        foreach (var h in _houses.Values)
            total += h.Total(itemId);
        return total;
    }

    /// <summary>Records what the saddlebag held when it was opened, replacing the old snapshot completely.</summary>
    public void ReplaceSaddlebag(DateTimeOffset capturedUtc, IEnumerable<KeyValuePair<uint, int>> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        Saddlebag = new SaddlebagSnapshot(capturedUtc.ToUniversalTime(), Positive(counts));
    }

    /// <summary>Forgets a retainer (for example one that was dismissed). Returns whether it was known.</summary>
    public bool RemoveRetainer(ulong id) => _retainers.Remove(id);

    /// <summary>How many of <paramref name="itemId"/> all captured retainers held, stored plus market listings, summed in a <c>long</c> (no overflow).</summary>
    public long RetainerTotal(uint itemId)
    {
        long total = 0;
        foreach (var r in _retainers.Values)
            total += (long)r.Stored.GetValueOrDefault(itemId) + r.Market.GetValueOrDefault(itemId);
        return total;
    }

    /// <summary>How many of <paramref name="itemId"/> the saddlebag held when last captured (0 if none or never captured).</summary>
    public int SaddlebagCount(uint itemId) => Saddlebag?.Items.GetValueOrDefault(itemId) ?? 0;

    private static SortedDictionary<uint, int> Positive(IEnumerable<KeyValuePair<uint, int>> counts)
    {
        var result = new SortedDictionary<uint, int>();
        foreach (var (item, count) in counts)
        {
            if (count > 0)
                result[item] = result.TryGetValue(item, out var have) ? (int)Math.Min((long)have + count, int.MaxValue) : count;
        }
        return result;
    }

    /// <summary>Serializes the cache as stable, sorted, indented JSON.</summary>
    public string ToJson()
    {
        var dto = new CacheDto
        {
            Version = CurrentVersion,
            Saddlebag = Saddlebag is null ? null : new SaddlebagDto { CapturedUtc = Saddlebag.CapturedUtc, Items = Sorted(Saddlebag.Items) },
            Houses = _houses.Count == 0 ? null : Houses.Select(h => (HouseDto?)new HouseDto
            {
                Id = h.Id,
                Label = h.Label,
                CapturedUtc = h.CapturedUtc,
                Places = new SortedDictionary<string, SortedDictionary<uint, int>?>(h.Places.ToDictionary(p => p.Key.ToString(), p => (SortedDictionary<uint, int>?)Sorted(p.Value)), StringComparer.Ordinal),
            }).ToList(),
            Retainers = Retainers.Select(r => (RetainerDto?)new RetainerDto
            {
                Id = r.Id,
                Name = r.Name,
                CapturedUtc = r.CapturedUtc,
                Stored = Sorted(r.Stored),
                Market = Sorted(r.Market),
                Places = PlacesDto(r),
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static SortedDictionary<string, SortedDictionary<uint, int>?>? PlacesDto(RetainerSnapshot r)
        => r.Places is null ? null : new(r.Places.ToDictionary(p => p.Key.ToString(), p => (SortedDictionary<uint, int>?)Sorted(p.Value)), StringComparer.Ordinal);

    private static SortedDictionary<uint, int> Sorted(IReadOnlyDictionary<uint, int> counts)
        => new(counts.ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Reads a cache from <see cref="ToJson"/> output. Empty, corrupt or unknown-version text gives an empty cache; this never throws.</summary>
    public static OwnedCache FromJson(string? json)
    {
        TryFromJson(json, out var cache);
        return cache;
    }

    /// <summary>
    /// Reads a cache from <see cref="ToJson"/> output. Returns <c>false</c>, with an empty <paramref name="cache"/>, for anything that is
    /// not a record of the current version: empty, corrupt, wrongly shaped or unknown-version text. Null retainer entries are skipped
    /// and the rest is kept. Never throws.
    /// </summary>
    public static bool TryFromJson(string? json, out OwnedCache cache)
    {
        cache = new OwnedCache();
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var dto = JsonSerializer.Deserialize<CacheDto>(json);
            if (dto is null || dto.Version is < 1 or > NewestReadableVersion)
                return false;

            var loaded = new OwnedCache();
            if (dto.Saddlebag is { } s)
                loaded.ReplaceSaddlebag(s.CapturedUtc, s.Items ?? new());
            foreach (var r in dto.Retainers ?? new())
            {
                if (r is null)
                    continue;
                if (TryReadPlaces(r.Places, out var places))
                {
                    loaded.ReplaceRetainer(r.Id, r.Name ?? "", r.CapturedUtc, places);
                }
                else
                {
                    loaded.ReplaceRetainer(r.Id, r.Name ?? "", r.CapturedUtc, r.Stored ?? new(), r.Market ?? new());
                }
            }
            foreach (var h in dto.Houses ?? new())
            {
                if (h is null)
                    continue;
                var places = new Dictionary<HousePlace, IReadOnlyDictionary<uint, int>>();
                foreach (var (name, counts) in h.Places ?? new())
                {
                    // An unknown or oddly cased place name is skipped on its own: a house has no always-written sums to fall back on.
                    if (counts is not null && HousePlaceNames.TryGetValue(name, out var place))
                        places[place] = counts;
                }
                loaded.ReplaceHouse(h.Id, h.Label ?? "", h.CapturedUtc, places);
            }
            cache = loaded;
            return true;
        }
        catch (Exception)
        {
            // Whatever the file holds, a bad record is an empty one: loading must never take the plugin down.
            return false;
        }
    }

    /// <summary>
    /// Reads a retainer's optional Places member. Anything unrecognised (a missing member, an unknown or oddly cased place name, a null
    /// entry) gives <c>false</c>, so the caller falls back to the always-written Stored and Market sums and no count is lost.
    /// </summary>
    private static bool TryReadPlaces(SortedDictionary<string, SortedDictionary<uint, int>?>? dto, out Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> places)
    {
        places = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>();
        if (dto is null)
            return false;
        foreach (var (name, counts) in dto)
        {
            if (counts is null || !PlaceNames.TryGetValue(name, out var place))
                return false;
            places[place] = counts;
        }
        return true;
    }

    private sealed class CacheDto
    {
        [JsonPropertyName("Version")] public int Version { get; set; }
        [JsonPropertyName("Saddlebag")] public SaddlebagDto? Saddlebag { get; set; }
        [JsonPropertyName("Retainers")] public List<RetainerDto?>? Retainers { get; set; }
        [JsonPropertyName("Houses")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<HouseDto?>? Houses { get; set; }
    }

    private sealed class HouseDto
    {
        [JsonPropertyName("Id")] public ulong Id { get; set; }
        [JsonPropertyName("Label")] public string? Label { get; set; }
        [JsonPropertyName("CapturedUtc")] public DateTimeOffset CapturedUtc { get; set; }
        [JsonPropertyName("Places")] public SortedDictionary<string, SortedDictionary<uint, int>?>? Places { get; set; }
    }

    private sealed class SaddlebagDto
    {
        [JsonPropertyName("CapturedUtc")] public DateTimeOffset CapturedUtc { get; set; }
        [JsonPropertyName("Items")] public SortedDictionary<uint, int>? Items { get; set; }
    }

    private sealed class RetainerDto
    {
        [JsonPropertyName("Id")] public ulong Id { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("CapturedUtc")] public DateTimeOffset CapturedUtc { get; set; }
        [JsonPropertyName("Stored")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SortedDictionary<uint, int>? Stored { get; set; }
        [JsonPropertyName("Market")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SortedDictionary<uint, int>? Market { get; set; }
        [JsonPropertyName("Places")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SortedDictionary<string, SortedDictionary<uint, int>?>? Places { get; set; }
    }
}

/// <summary>
/// Merges what is on the character right now, the cache and an optional external retainer source into one answer per item.
/// Retainer rule: when both the external source and the cache give a value, use the larger and report where it came from; when only
/// one does, use it; otherwise the figure is unknown. The two sources are never added together, which would count every retainer
/// item twice.
/// </summary>
public static class OwnedLookup
{
    /// <summary>
    /// Summarizes <paramref name="itemId"/>. <paramref name="live"/> is bags plus crystals read now, or <c>null</c> when they have not
    /// been read yet (the on-you count is then unknown, not zero). <paramref name="external"/> returns the external source's retainer
    /// count for an item, or <c>null</c> when it is unavailable or does not know the item (a negative count is treated as <c>null</c>);
    /// pass <c>null</c> when there is no such source.
    /// </summary>
    public static OwnedSummary Summarize(uint itemId, IReadOnlyDictionary<uint, int>? live, OwnedCache cache, Func<uint, int?>? external)
    {
        ArgumentNullException.ThrowIfNull(cache);

        int? onYou = live is null ? null : Math.Max(0, live.GetValueOrDefault(itemId));

        int? saddlebag = cache.Saddlebag is null ? null : cache.SaddlebagCount(itemId);
        DateTimeOffset? saddlebagAt = cache.Saddlebag?.CapturedUtc;

        long? cached = cache.RetainerCount > 0 ? cache.RetainerTotal(itemId) : null;

        var asked = external?.Invoke(itemId);
        long? ext = asked is >= 0 ? asked : null;

        RetainerSource source;
        long? retainers;
        // Both sources can lag behind (the external one answers 0 for retainers it has not seen), so the larger wins; a tie reports
        // the external source. They are never added together.
        if (ext is not null && (cached is null || ext >= cached)) { retainers = ext; source = RetainerSource.External; }
        else if (cached is not null) { retainers = cached; source = RetainerSource.Cache; }
        else { retainers = null; source = RetainerSource.NotAvailable; }

        return new OwnedSummary(itemId, onYou, saddlebag, saddlebagAt, retainers, source, cached, ext,
            cache.OldestRetainerCapture, cache.NewestRetainerCapture, cache.HouseTotal(itemId));
    }

    /// <summary>Where each remembered house holds <paramref name="itemId"/>; only houses that hold it, largest total first, then label, then id.</summary>
    public static IReadOnlyList<HouseLocation> HouseLocations(uint itemId, OwnedCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var found = new List<HouseLocation>();
        foreach (var h in cache.Houses)
        {
            var total = h.Total(itemId);
            if (total <= 0)
                continue;
            found.Add(new HouseLocation(h.Id, h.Label, h.CapturedUtc, h.Count(HousePlace.Placed, itemId), h.Count(HousePlace.Storeroom, itemId), total));
        }
        return found.OrderByDescending(l => l.Total).ThenBy(l => l.Label, StringComparer.Ordinal).ThenBy(l => l.HouseId).ToList();
    }

    /// <summary>Where each retainer holds <paramref name="itemId"/>.</summary>
    public static IReadOnlyList<RetainerLocation> Locations(uint itemId, OwnedCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var found = new List<RetainerLocation>();
        foreach (var r in cache.Retainers)
        {
            // Same formula as OwnedCache.RetainerTotal, so the totals over all retainers add up to it exactly.
            var total = (long)r.Stored.GetValueOrDefault(itemId) + r.Market.GetValueOrDefault(itemId);
            if (total <= 0)
                continue;
            var places = new SortedDictionary<RetainerPlace, int>();
            if (r.Places is not null)
            {
                foreach (var (place, counts) in r.Places)
                {
                    if (counts.TryGetValue(itemId, out var n) && n > 0)
                        places[place] = n;
                }
            }
            found.Add(new RetainerLocation(r.Id, r.Name, r.CapturedUtc, r.Places is not null, places, total));
        }
        return found.OrderByDescending(l => l.Total).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.RetainerId).ToList();
    }
}
