using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class UniversalisResponseParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "UniversalisResponseParser", name));

    [Fact]
    public void Multi_item_shape_maps_each_item_to_its_own_listings_in_requested_order()
    {
        // Requested order deliberately differs from the response order.
        var r = UniversalisResponseParser.Parse(Fixture("multi-dc.json"), new uint[] { 300, 100 });

        Assert.Equal(2, r.Count);
        Assert.Equal(300u, r[0].ItemId);
        Assert.Equal(100u, r[1].ItemId);
        var a = Assert.Single(r[0].Listings);
        Assert.Equal(new Listing(300, 22, "Beta", 7, 99, false, DateTimeOffset.FromUnixTimeSeconds(1700007200)), a);
        Assert.Equal(2, r[1].Listings.Count);
        Assert.All(r[1].Listings, l => Assert.Equal(100u, l.ItemId));
        Assert.Equal(new Listing(100, 11, "Alpha", 150, 3, true, DateTimeOffset.FromUnixTimeSeconds(1700000000)), r[1].Listings[0]);
        Assert.Equal(new Listing(100, 22, "Beta", 90, 20, false, DateTimeOffset.FromUnixTimeSeconds(1700003600)), r[1].Listings[1]);
    }

    [Fact]
    public void Unresolved_and_empty_and_absent_items_get_an_entry_with_empty_listings()
    {
        // 200 has an empty listings array, 400 is unresolved, 999 is not in the response at all.
        var r = UniversalisResponseParser.Parse(Fixture("multi-dc.json"), new uint[] { 200, 400, 999 });

        Assert.Equal(new uint[] { 200, 400, 999 }, r.Select(x => x.ItemId).ToArray());
        Assert.All(r, x => Assert.Empty(x.Listings));
    }

    [Fact]
    public void Single_item_shape_is_handled()
    {
        var r = UniversalisResponseParser.Parse(Fixture("single-dc.json"), new uint[] { 555 });

        var item = Assert.Single(r);
        Assert.Equal(555u, item.ItemId);
        Assert.Equal(2, item.Listings.Count);
        Assert.Equal(new Listing(555, 11, "Alpha", 1000, 1, true, DateTimeOffset.FromUnixTimeSeconds(1700000000)), item.Listings[0]);
        Assert.Equal(new Listing(555, 22, "Beta", 800, 5, false, DateTimeOffset.FromUnixTimeSeconds(1700000100)), item.Listings[1]);
    }

    [Fact]
    public void Single_item_shape_does_not_assign_listings_to_a_different_requested_item()
    {
        var r = UniversalisResponseParser.Parse(Fixture("single-dc.json"), new uint[] { 1, 555 });

        Assert.Empty(r[0].Listings);
        Assert.Equal(2, r[1].Listings.Count);
    }

    [Fact]
    public void Single_world_query_falls_back_to_the_view_world_when_listings_carry_none()
    {
        var r = UniversalisResponseParser.Parse(Fixture("single-world-no-listing-world.json"), new uint[] { 777 });

        var l = Assert.Single(Assert.Single(r).Listings);
        Assert.Equal(33u, l.WorldId);
        Assert.Equal("Gamma", l.WorldName);
    }

    [Fact]
    public void Missing_world_id_and_name_become_zero_and_empty_and_unusable_listings_are_skipped()
    {
        var r = UniversalisResponseParser.Parse(Fixture("sparse-listings.json"), new uint[] { 1, 2 });

        // Listings lacking price or quantity, and non-object entries, are dropped.
        Assert.Equal(2, r[0].Listings.Count);
        var bare = r[0].Listings[0];
        Assert.Equal(new Listing(1, 0, "", 10, 4, false, DateTimeOffset.UnixEpoch), bare);
        Assert.Equal(new Listing(1, 44, "Delta", 12, 6, true, DateTimeOffset.FromUnixTimeSeconds(1700000000)), r[0].Listings[1]);
        Assert.Empty(r[1].Listings);
    }

    [Fact]
    public void Empty_object_yields_empty_listings_for_every_requested_item()
    {
        var r = UniversalisResponseParser.Parse("{}", new uint[] { 5, 6 });

        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Empty(x.Listings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"items\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Invalid_json_throws_a_clear_FormatException(string body)
    {
        var ex = Assert.Throws<FormatException>(() => UniversalisResponseParser.Parse(body, new uint[] { 1 }));
        Assert.Contains("Universalis", ex.Message);
    }

    [Fact]
    public void No_requested_items_yields_an_empty_result()
    {
        Assert.Empty(UniversalisResponseParser.Parse(Fixture("multi-dc.json"), Array.Empty<uint>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Listing_priced_zero_or_below_is_skipped(int price)
    {
        var json = "{\"itemID\":1,\"listings\":[{\"pricePerUnit\":" + price + ",\"quantity\":3,\"worldID\":11}]}";
        var r = UniversalisResponseParser.Parse(json, new uint[] { 1 });
        Assert.Empty(Assert.Single(r).Listings);
    }

    [Fact]
    public void Mixed_prices_keep_only_the_positive_priced_listings_in_both_shapes()
    {
        const string rows = "[{\"pricePerUnit\":0,\"quantity\":1,\"worldID\":11},{\"pricePerUnit\":7,\"quantity\":2,\"worldID\":11},"
            + "{\"pricePerUnit\":-1,\"quantity\":1,\"worldID\":11},{\"pricePerUnit\":1,\"quantity\":4,\"worldID\":11}]";
        var single = UniversalisResponseParser.Parse("{\"itemID\":1,\"listings\":" + rows + "}", new uint[] { 1 });
        var multi = UniversalisResponseParser.Parse("{\"items\":{\"1\":{\"listings\":" + rows + "}}}", new uint[] { 1 });
        foreach (var r in new[] { single, multi })
        {
            var l = Assert.Single(r).Listings;
            Assert.Equal([(7, 2), (1, 4)], l.Select(x => (x.PricePerUnit, x.Quantity)).ToArray());
        }
    }

    [Fact]
    public void DataCenters_array_maps_name_region_and_world_ids()
    {
        var r = UniversalisResponseParser.ParseDataCenters("[{\"name\":\"Uni1\",\"region\":\"R1\",\"worlds\":[1,2]},{\"name\":\"Uni2\",\"region\":\"R2\",\"worlds\":[3]}]");
        Assert.Equal(2, r.Count);
        Assert.Equal("Uni2", r[1].Name);
        Assert.Equal("R2", r[1].Region);
        Assert.Equal(new uint[] { 1, 2 }, r[0].WorldIds.ToArray());
    }

    [Fact]
    public void DataCenters_skip_nameless_entries_ignore_unknown_fields_and_drop_non_uint_worlds()
    {
        var r = UniversalisResponseParser.ParseDataCenters("[{\"worlds\":[1]},{\"name\":\"A\",\"x\":[1],\"worlds\":[4,\"5\",-2,1.5,6]},{\"name\":\"B\",\"worlds\":\"no\"}]");
        Assert.Equal(new[] { "A", "B" }, r.Select(x => x.Name).ToArray());
        Assert.Equal(new uint[] { 4, 6 }, r[0].WorldIds.ToArray());
        Assert.Empty(r[1].WorldIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("\"str\"")]
    public void DataCenters_bad_body_or_non_array_root_throws_FormatException(string body)
        => Assert.Throws<FormatException>(() => UniversalisResponseParser.ParseDataCenters(body));

    [Fact]
    public void DataCenters_empty_array_is_an_empty_list()
        => Assert.Empty(UniversalisResponseParser.ParseDataCenters("[]"));
}
