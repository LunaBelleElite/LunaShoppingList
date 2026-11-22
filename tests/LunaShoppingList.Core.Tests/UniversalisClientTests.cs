using System.Net;
using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class UniversalisClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "UniversalisClient", name));

    /// <summary>Records URLs; answers each request with a synthetic response built from the ids in the URL.</summary>
    private sealed class FakeHttp : IHttpGetter
    {
        public readonly List<string> Urls = new();
        public Func<string, int, string>? Responder;   // (url, callNumber starting at 1) -> body, may throw
        public Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            Urls.Add(url);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult((Responder ?? ((u, _) => Generate(u)))(url, Urls.Count));
        }

        public static IReadOnlyList<uint> IdsIn(string url)
        {
            var path = url.Substring(url.IndexOf("/api/v2/", StringComparison.Ordinal) + 8);
            var segs = path.Split('?')[0].Split('/');
            return segs[1].Split(',').Select(uint.Parse).ToList();
        }

        public static string Generate(string url)
        {
            var ids = IdsIn(url);
            string Listing(uint id) =>
                "{\"pricePerUnit\":" + id + ",\"quantity\":1,\"hq\":false,\"worldName\":\"Alpha\",\"worldID\":11,\"lastReviewTime\":1700000000}";
            if (ids.Count == 1)
            {
                return "{\"itemID\":" + ids[0] + ",\"listings\":[" + Listing(ids[0]) + "]}";
            }
            return "{\"items\":{" + string.Join(",", ids.Select(i => "\"" + i + "\":{\"itemID\":" + i + ",\"listings\":[" + Listing(i) + "]}")) + "},\"unresolvedItems\":[]}";
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
    }

    private static (UniversalisClient Client, FakeHttp Http, List<TimeSpan> Delays, Clock Clock) Make(TimeSpan? ttl = null)
    {
        var http = new FakeHttp();
        var delays = new List<TimeSpan>();
        var clock = new Clock();
        var client = new UniversalisClient(http, ttl ?? TimeSpan.FromMinutes(5), clock.Get,
            (d, ct) => { ct.ThrowIfCancellationRequested(); delays.Add(d); return Task.CompletedTask; });
        return (client, http, delays, clock);
    }

    private static uint[] Range(uint from, int count) => Enumerable.Range(0, count).Select(i => from + (uint)i).ToArray();

    [Fact]
    public async Task Empty_ids_returns_empty_without_any_request()
    {
        var (c, http, _, _) = Make();
        var r = await c.GetListingsAsync(Array.Empty<uint>(), "Crystal", CancellationToken.None);
        Assert.Empty(r);
        Assert.Empty(http.Urls);
    }

    [Fact]
    public async Task Builds_the_documented_url_for_multiple_items()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None);

        var url = Assert.Single(http.Urls);
        Assert.StartsWith("https://universalis.app/api/v2/Crystal/10,20?", url);
        Assert.Contains("entries=0", url);
        Assert.Contains("fields=", url);
        Assert.Contains("items.listings.pricePerUnit", url);
        Assert.Contains("items.listings.lastReviewTime", url);
        Assert.Contains("unresolvedItems", url);
        // listings=N is never sent (UNVERIFIED that it returns the cheapest N).
        Assert.DoesNotContain("&listings=", url);
        Assert.DoesNotContain("?listings=", url);
    }

    [Fact]
    public async Task Single_item_request_uses_single_shape_field_paths()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);

        var url = Assert.Single(http.Urls);
        Assert.Contains("fields=itemID,", url);
        Assert.DoesNotContain("items.", url);
        Assert.Contains("listings.pricePerUnit", url);
    }

    [Fact]
    public async Task Scope_with_a_space_is_escaped()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "North America", CancellationToken.None);
        Assert.StartsWith("https://universalis.app/api/v2/North%20America/10?", Assert.Single(http.Urls));
    }

    [Fact]
    public async Task Scope_with_a_slash_cannot_break_out_of_its_path_segment()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "a/b", CancellationToken.None);
        Assert.StartsWith("https://universalis.app/api/v2/a%2Fb/10?", Assert.Single(http.Urls));
    }

    [Fact]
    public async Task Blank_scope_is_rejected_before_any_request()
    {
        var (c, http, _, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetListingsAsync(new uint[] { 1 }, " ", CancellationToken.None));
        Assert.Empty(http.Urls);
    }

    [Fact]
    public async Task Parses_the_multi_item_fixture_response()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => Fixture("two-items.json");
        var r = await c.GetListingsAsync(new uint[] { 20, 10 }, "Crystal", CancellationToken.None);

        Assert.Equal(new uint[] { 20, 10 }, r.Select(x => x.ItemId).ToArray());
        Assert.Equal(9, Assert.Single(r[0].Listings).PricePerUnit);
        Assert.Equal(5, Assert.Single(r[1].Listings).PricePerUnit);
    }

    [Fact]
    public async Task Parses_the_single_item_fixture_response()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => Fixture("single-item.json");
        var r = await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        Assert.Equal(5, Assert.Single(Assert.Single(r).Listings).PricePerUnit);
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(99, new[] { 99 })]
    [InlineData(100, new[] { 100 })]
    [InlineData(101, new[] { 100, 1 })]
    [InlineData(250, new[] { 100, 100, 50 })]
    public async Task Chunks_at_most_100_ids_per_request_sequentially(int count, int[] expectedChunkSizes)
    {
        var (c, http, _, _) = Make();
        var ids = Range(1, count);
        var r = await c.GetListingsAsync(ids, "Crystal", CancellationToken.None);

        Assert.Equal(expectedChunkSizes, http.Urls.Select(u => FakeHttp.IdsIn(u).Count).ToArray());
        Assert.Equal(ids, r.Select(x => x.ItemId).ToArray());
        // Each item's listing must be its own (price == id in the generator).
        Assert.All(r, x => Assert.Equal((int)x.ItemId, Assert.Single(x.Listings).PricePerUnit));
        // Chunks cover the ids in order with no overlap.
        Assert.Equal(ids, http.Urls.SelectMany(FakeHttp.IdsIn).ToArray());
    }

    [Fact]
    public async Task Waits_the_minimum_gap_between_requests_but_not_before_the_first()
    {
        var (c, _, delays, _) = Make();
        await c.GetListingsAsync(Range(1, 250), "Crystal", CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50) }, delays);

        var (c2, _, delays2, _) = Make();
        await c2.GetListingsAsync(Range(1, 5), "Crystal", CancellationToken.None);
        Assert.Empty(delays2);
    }

    [Fact]
    public async Task Duplicate_ids_are_requested_once_and_returned_once_in_first_seen_order()
    {
        var (c, http, _, _) = Make();
        var r = await c.GetListingsAsync(new uint[] { 30, 10, 30, 20, 10 }, "Crystal", CancellationToken.None);

        Assert.Equal(new uint[] { 30, 10, 20 }, FakeHttp.IdsIn(Assert.Single(http.Urls)).ToArray());
        Assert.Equal(new uint[] { 30, 10, 20 }, r.Select(x => x.ItemId).ToArray());
    }

    [Fact]
    public async Task Cache_hit_within_ttl_makes_no_request()
    {
        var (c, http, _, clock) = Make(TimeSpan.FromMinutes(5));
        await c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(4);
        var r = await c.GetListingsAsync(new uint[] { 20, 10 }, "Crystal", CancellationToken.None);

        Assert.Single(http.Urls);
        Assert.Equal(new uint[] { 20, 10 }, r.Select(x => x.ItemId).ToArray());
        Assert.Equal(20, Assert.Single(r[0].Listings).PricePerUnit);
    }

    [Fact]
    public async Task Cache_entry_at_or_past_ttl_is_refetched()
    {
        var (c, http, _, clock) = Make(TimeSpan.FromMinutes(5));
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(5);
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task Cache_is_per_scope()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        await c.GetListingsAsync(new uint[] { 10 }, "Light", CancellationToken.None);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task Only_uncached_ids_are_requested_and_order_follows_the_argument()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        var r = await c.GetListingsAsync(new uint[] { 30, 10, 20 }, "Crystal", CancellationToken.None);

        Assert.Equal(new uint[] { 30, 20 }, FakeHttp.IdsIn(http.Urls[1]).ToArray());
        Assert.Equal(new uint[] { 30, 10, 20 }, r.Select(x => x.ItemId).ToArray());
        Assert.All(r, x => Assert.Equal((int)x.ItemId, Assert.Single(x.Listings).PricePerUnit));
    }

    [Fact]
    public async Task Item_with_no_listings_is_requested_again_within_the_ttl()
    {
        var (c, http, _, clock) = Make(TimeSpan.FromMinutes(5));
        http.Responder = (_, n) => n == 1 ? "{}" : FakeHttp.Generate("/api/v2/x/10");
        var first = await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(30);
        var second = await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);

        Assert.Empty(Assert.Single(first).Listings);
        Assert.Equal(2, http.Urls.Count);
        Assert.Single(Assert.Single(second).Listings);
    }

    [Fact]
    public async Task Item_with_listings_is_still_cached_after_an_empty_one_is_not()
    {
        var (c, http, _, _) = Make();
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task Multi_item_response_caches_only_the_items_that_have_listings()
    {
        var (c, http, _, _) = Make();
        const string body = "{\"items\":{\"10\":{\"listings\":[{\"pricePerUnit\":5,\"quantity\":1,\"worldID\":11}]},\"20\":{\"listings\":[]}}}";
        http.Responder = (_, n) => n == 1 ? body : FakeHttp.Generate("/api/v2/x/20");
        var first = await c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None);
        var second = await c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None);

        Assert.Empty(first[1].Listings);
        Assert.Equal(2, http.Urls.Count);
        Assert.Equal(new uint[] { 20 }, FakeHttp.IdsIn(http.Urls[1]).ToArray());
        Assert.Equal(5, second[0].Listings[0].PricePerUnit);
        Assert.Equal(20, Assert.Single(second[1].Listings).PricePerUnit);
    }

    [Fact]
    public async Task Failed_fetch_is_not_cached()
    {
        // SUPERSEDED 2026-11-22: was HttpStatusCode.NotFound; a 404 for a single-id chunk is now "no listings", so a
        // different non-transient status (BadRequest) drives the failure.
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("boom", null, HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None));
        http.Responder = null;
        var r = await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        Assert.Single(Assert.Single(r).Listings);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Retries_transient_status_then_succeeds_with_exponential_backoff(HttpStatusCode code)
    {
        var (c, http, delays, _) = Make();
        http.Responder = (url, n) => n <= 2 ? throw new HttpRequestException("busy", null, code) : FakeHttp.Generate(url);

        var r = await c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None);

        Assert.Equal(3, http.Urls.Count);
        Assert.Equal(2, r.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
        Assert.Single(http.Urls.Distinct());
    }

    [Fact]
    public async Task Retry_exhaustion_propagates_after_one_plus_three_attempts()
    {
        var (c, http, delays, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, http.Urls.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Non_transient_status_propagates_without_retry(HttpStatusCode code)
    {
        // SUPERSEDED 2026-11-22: this theory used ONE id ({ 10 }); a 404 for a single-id chunk now means "item not
        // sellable on the market board" and no longer propagates (see Single_id_404_*). It now uses TWO ids so the 404
        // arm is still pinned on a multi-id chunk, which keeps today's behaviour.
        var (c, http, delays, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("no", null, code);

        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(new uint[] { 10, 20 }, "Crystal", CancellationToken.None));
        Assert.Single(http.Urls);
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Non_404_non_transient_status_on_a_single_id_chunk_still_propagates(HttpStatusCode code)
    {
        var (c, http, delays, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("no", null, code);

        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None));
        Assert.Single(http.Urls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Single_id_404_means_no_listings_for_that_item_not_a_failed_build()
    {
        // Universalis answers 404 on the single-item URL for an item that cannot be sold on the market board.
        var (c, http, delays, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("not sellable", null, HttpStatusCode.NotFound);

        var r = await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);

        var entry = Assert.Single(r);
        Assert.Equal(10u, entry.ItemId);
        Assert.Empty(entry.Listings);
        Assert.Single(http.Urls);   // not retried
        Assert.Empty(delays);

        // Decision: an empty answer is not cached (same as any item with no listings), so it is asked again.
        await c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task Last_chunk_of_a_single_unsellable_id_does_not_fail_the_whole_request_nor_a_repeat()
    {
        // 101 ids split 100 + 1; the last (single-id URL) is unsellable and answers 404.
        var (c, http, _, _) = Make();
        http.Responder = (url, _) => FakeHttp.IdsIn(url).Count == 1
            ? throw new HttpRequestException("not sellable", null, HttpStatusCode.NotFound)
            : FakeHttp.Generate(url);
        var ids = Range(1, 101);

        var r = await c.GetListingsAsync(ids, "Crystal", CancellationToken.None);

        Assert.Equal(ids, r.Select(x => x.ItemId).ToArray());
        Assert.All(r.Take(100), x => Assert.Single(x.Listings));
        Assert.Empty(r[100].Listings);
        Assert.Equal(2, http.Urls.Count);

        // Repeat build: the 100 are cached, only the leftover uncached item is fetched (single-id again) and still survives.
        var again = await c.GetListingsAsync(ids, "Crystal", CancellationToken.None);
        Assert.Equal(101, again.Count);
        Assert.Empty(again[100].Listings);
        Assert.Equal(3, http.Urls.Count);
        Assert.Equal(new uint[] { 101 }, FakeHttp.IdsIn(http.Urls[2]).ToArray());
    }

    [Fact]
    public async Task Transport_failure_without_status_propagates_without_retry()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("dns");

        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None));
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_and_makes_no_request()
    {
        var (c, http, _, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", cts.Token));
        Assert.Empty(http.Urls);
    }

    [Fact]
    public async Task Cancellation_between_chunks_stops_further_requests()
    {
        var (c, http, _, _) = Make();
        using var cts = new CancellationTokenSource();
        http.Responder = (url, n) => { if (n == 1) cts.Cancel(); return FakeHttp.Generate(url); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetListingsAsync(Range(1, 250), "Crystal", cts.Token));
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retrying()
    {
        var http = new FakeHttp { Responder = (_, _) => throw new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests) };
        using var cts = new CancellationTokenSource();
        var c = new UniversalisClient(http, TimeSpan.FromMinutes(1), () => DateTimeOffset.UnixEpoch,
            (d, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", cts.Token));
        Assert.Single(http.Urls);
    }

    // ---- qa-tester additions (2026-11-21) ----

    [Fact]
    public async Task Cancellation_is_honoured_before_the_next_attempt_even_when_the_delay_ignores_the_token()
    {
        // Pins the ct.ThrowIfCancellationRequested() at the top of the retry loop. With the real Task.Delay (and a
        // real HttpClient) the token is also honoured inside those calls, so the check is defence in depth; it is only
        // observable with a non-cooperative delay, which is what this injects.
        var http = new FakeHttp { Responder = (_, _) => throw new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests) };
        using var cts = new CancellationTokenSource();
        var c = new UniversalisClient(http, TimeSpan.FromMinutes(1), () => DateTimeOffset.UnixEpoch,
            (d, ct) => { cts.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", cts.Token));
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_the_next_chunk_even_when_the_delay_ignores_the_token()
    {
        var http = new FakeHttp();
        using var cts = new CancellationTokenSource();
        http.Responder = (url, n) => { if (n == 1) cts.Cancel(); return FakeHttp.Generate(url); };
        var c = new UniversalisClient(http, TimeSpan.FromMinutes(1), () => DateTimeOffset.UnixEpoch, (d, ct) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetListingsAsync(Range(1, 250), "Crystal", cts.Token));
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task Chunks_fetched_before_a_later_chunk_fails_stay_cached()
    {
        var (c, http, _, _) = Make();
        http.Responder = (url, n) => n == 2 ? throw new HttpRequestException("gone", null, HttpStatusCode.NotFound) : FakeHttp.Generate(url);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetListingsAsync(Range(1, 150), "Crystal", CancellationToken.None));
        Assert.Equal(2, http.Urls.Count);

        http.Responder = null;
        var r = await c.GetListingsAsync(Range(1, 150), "Crystal", CancellationToken.None);

        Assert.Equal(3, http.Urls.Count);
        Assert.Equal(Range(101, 50), FakeHttp.IdsIn(http.Urls[2]).ToArray());
        Assert.Equal(150, r.Count);
    }

    [Fact]
    public async Task Invalid_json_from_the_server_surfaces_as_FormatException_and_is_not_retried()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => "<html>gateway</html>";
        await Assert.ThrowsAsync<FormatException>(() => c.GetListingsAsync(new uint[] { 10 }, "Crystal", CancellationToken.None));
        Assert.Single(http.Urls);
    }

    // ---- data centers ----

    private const string DcBody = "[{\"name\":\"Uni1\",\"region\":\"R1\",\"worlds\":[1,2]},{\"name\":\"Uni2\",\"region\":\"R2\",\"worlds\":[3]}]";

    [Fact]
    public async Task DataCenters_use_the_exact_documented_url_and_parse_the_shape()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => DcBody;
        var r = await c.GetDataCentersAsync(CancellationToken.None);

        Assert.Equal("https://universalis.app/api/v2/data-centers", Assert.Single(http.Urls));
        Assert.Equal(2, r.Count);
        Assert.Equal("Uni1", r[0].Name);
        Assert.Equal("R1", r[0].Region);
        Assert.Equal(new uint[] { 1, 2 }, r[0].WorldIds.ToArray());
        Assert.Equal(new uint[] { 3 }, r[1].WorldIds.ToArray());
    }

    [Fact]
    public async Task DataCenters_parsing_is_tolerant()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) =>
            "[{\"region\":\"R\",\"worlds\":[1]},{\"name\":null,\"worlds\":[2]},{\"name\":\"  \",\"worlds\":[3]},5,"
            + "{\"name\":\"Ok\",\"extra\":{\"a\":1},\"worlds\":[7,\"8\",-1,2.5,null,9]},{\"name\":\"NoWorlds\"}]";
        var r = await c.GetDataCentersAsync(CancellationToken.None);

        Assert.Equal(new[] { "Ok", "NoWorlds" }, r.Select(x => x.Name).ToArray());
        Assert.Equal(new uint[] { 7, 9 }, r[0].WorldIds.ToArray());
        Assert.Empty(r[1].WorldIds);
        Assert.Equal(string.Empty, r[0].Region);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("")]
    public async Task DataCenters_invalid_json_or_non_array_root_throws_FormatException(string body)
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => body;
        var ex = await Assert.ThrowsAsync<FormatException>(() => c.GetDataCentersAsync(CancellationToken.None));
        Assert.Contains("data-center", ex.Message);
    }

    [Fact]
    public async Task DataCenters_cache_hit_within_six_hours_makes_no_request()
    {
        var (c, http, _, clock) = Make(TimeSpan.FromMinutes(5));
        http.Responder = (_, _) => DcBody;
        await c.GetDataCentersAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59);
        var r = await c.GetDataCentersAsync(CancellationToken.None);
        Assert.Single(http.Urls);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public async Task DataCenters_refetched_at_six_hours_and_independent_of_the_listings_ttl()
    {
        var (c, http, _, clock) = Make(TimeSpan.FromDays(30));
        http.Responder = (_, _) => DcBody;
        await c.GetDataCentersAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(6);
        await c.GetDataCentersAsync(CancellationToken.None);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task DataCenters_failed_result_is_not_cached()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("gone", null, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetDataCentersAsync(CancellationToken.None));
        http.Responder = (_, _) => DcBody;
        var r = await c.GetDataCentersAsync(CancellationToken.None);
        Assert.Equal(2, r.Count);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task DataCenters_invalid_body_is_not_cached()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => "{}";
        await Assert.ThrowsAsync<FormatException>(() => c.GetDataCentersAsync(CancellationToken.None));
        http.Responder = (_, _) => DcBody;
        Assert.Equal(2, (await c.GetDataCentersAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task DataCenters_empty_list_is_not_cached()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => "[]";
        Assert.Empty(await c.GetDataCentersAsync(CancellationToken.None));
        http.Responder = (_, _) => DcBody;
        Assert.Equal(2, (await c.GetDataCentersAsync(CancellationToken.None)).Count);
        Assert.Equal(2, http.Urls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DataCenters_retry_transient_status_with_exponential_backoff(HttpStatusCode code)
    {
        var (c, http, delays, _) = Make();
        http.Responder = (_, n) => n <= 2 ? throw new HttpRequestException("busy", null, code) : DcBody;
        var r = await c.GetDataCentersAsync(CancellationToken.None);
        Assert.Equal(2, r.Count);
        Assert.Equal(3, http.Urls.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task DataCenters_non_transient_status_propagates_without_retry()
    {
        var (c, http, delays, _) = Make();
        http.Responder = (_, _) => throw new HttpRequestException("no", null, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => c.GetDataCentersAsync(CancellationToken.None));
        Assert.Single(http.Urls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task DataCenters_pre_cancelled_token_throws_and_makes_no_request()
    {
        var (c, http, _, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetDataCentersAsync(cts.Token));
        Assert.Empty(http.Urls);
    }

    [Fact]
    public async Task DataCenters_cancelled_token_is_honoured_even_when_the_result_is_cached()
    {
        var (c, http, _, _) = Make();
        http.Responder = (_, _) => DcBody;
        await c.GetDataCentersAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetDataCentersAsync(cts.Token));
    }
}
