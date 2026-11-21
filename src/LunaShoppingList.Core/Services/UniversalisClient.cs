using System.Net;

namespace LunaShoppingList.Core;

/// <summary>Universalis client with chunking, retry/backoff and a short cache.</summary>
public class UniversalisClient : IUniversalisClient
{
    /// <summary>Maximum item ids per request (Universalis documents 100).</summary>
    internal const int MaxIdsPerRequest = 100;

    /// <summary>Retries after the first attempt for 429 / 5xx.</summary>
    internal const int MaxRetries = 3;

    /// <summary>Base backoff; doubles each retry.</summary>
    internal static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);

    /// <summary>Minimum gap between consecutive requests.</summary>
    internal static readonly TimeSpan MinRequestGap = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Deliberately null: the listings=N query parameter is documented as "number of listings per item" but
    /// it is UNVERIFIED that it returns the cheapest N, so it is never sent.
    /// </summary>
    internal static readonly int? ListingsLimit = null;

    /// <summary>How long the data-center list is reused.</summary>
    internal static readonly TimeSpan DataCenterTtl = TimeSpan.FromHours(6);

    private const string BaseUrl = "https://universalis.app/api/v2/";

    // Per the v2 swagger, fields paths differ by shape: multi-item responses nest under "items.".
    private const string ListingFields = "listings.pricePerUnit,listings.quantity,listings.hq,listings.worldName,listings.worldID,listings.lastReviewTime";
    private const string SingleFields = "itemID,worldID,worldName," + ListingFields;
    private const string MultiFields = "unresolvedItems,worldID,worldName,items.itemID,items.worldID,items.worldName,"
        + "items.listings.pricePerUnit,items.listings.quantity,items.listings.hq,items.listings.worldName,items.listings.worldID,items.listings.lastReviewTime";

    private readonly IHttpGetter _http;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _cacheLock = new();
    private (DateTimeOffset At, IReadOnlyList<UniversalisDataCenter> Value)? _dataCenters;
    private readonly Dictionary<(string Scope, uint ItemId), (DateTimeOffset At, ItemListings Value)> _cache = new();

    /// <summary>Creates the client.</summary>
    /// <param name="http">HTTP GET abstraction.</param>
    /// <param name="cacheTtl">How long a fetched result is reused.</param>
    /// <param name="clock">Current time source (injectable for tests).</param>
    /// <param name="delay">Delay used for backoff and request spacing; defaults to Task.Delay.</param>
    public UniversalisClient(IHttpGetter http, TimeSpan cacheTtl, Func<DateTimeOffset> clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _cacheTtl = cacheTtl;
        _clock = clock;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return Array.Empty<ItemListings>();
        }
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope must be a world, data-centre or region name.", nameof(scope));
        }
        ct.ThrowIfCancellationRequested();

        var distinct = itemIds.Distinct().ToList();
        var scopeKey = scope.ToUpperInvariant();
        var found = new Dictionary<uint, ItemListings>();
        var toFetch = new List<uint>();

        var now = _clock();
        lock (_cacheLock)
        {
            foreach (var id in distinct)
            {
                if (_cache.TryGetValue((scopeKey, id), out var hit) && now - hit.At < _cacheTtl)
                {
                    found[id] = hit.Value;
                }
                else
                {
                    toFetch.Add(id);
                }
            }
        }

        bool first = true;
        for (int i = 0; i < toFetch.Count; i += MaxIdsPerRequest)
        {
            var chunk = toFetch.Skip(i).Take(MaxIdsPerRequest).ToList();
            if (!first)
            {
                await _delay(MinRequestGap, ct).ConfigureAwait(false);
            }
            first = false;

            var json = await GetWithRetryAsync(BuildUrl(scope, chunk), ct).ConfigureAwait(false);
            var parsed = UniversalisResponseParser.Parse(json, chunk);
            var stamp = _clock();
            lock (_cacheLock)
            {
                foreach (var p in parsed)
                {
                    found[p.ItemId] = p;
                    // An item with no listings is not cached, so a transient empty answer is queried again next time.
                    if (p.Listings.Count > 0)
                    {
                        _cache[(scopeKey, p.ItemId)] = (stamp, p);
                    }
                }
            }
        }

        return distinct.Select(id => found[id]).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            if (_dataCenters is { } hit && _clock() - hit.At < DataCenterTtl)
            {
                return hit.Value;
            }
        }

        var json = await GetWithRetryAsync(BaseUrl + "data-centers", ct).ConfigureAwait(false);
        var parsed = UniversalisResponseParser.ParseDataCenters(json);
        if (parsed.Count > 0)
        {
            var stamp = _clock();
            lock (_cacheLock)
            {
                _dataCenters = (stamp, parsed);
            }
        }
        return parsed;
    }

    private static string BuildUrl(string scope, IReadOnlyList<uint> ids)
    {
        var fields = ids.Count == 1 ? SingleFields : MultiFields;
        var url = BaseUrl + Uri.EscapeDataString(scope) + "/" + string.Join(",", ids) + "?entries=0&fields=" + fields;
        if (ListingsLimit is { } n)
        {
            url += "&listings=" + n;
        }
        return url;
    }

    private async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && IsTransient(ex.StatusCode))
            {
                await _delay(TimeSpan.FromTicks(BaseBackoff.Ticks << attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode? code) =>
        code is not null && ((int)code == 429 || (int)code >= 500);
}
