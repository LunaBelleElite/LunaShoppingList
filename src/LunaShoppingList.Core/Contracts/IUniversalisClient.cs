namespace LunaShoppingList.Core;

/// <summary>Fetches market-board listings from Universalis.</summary>
public interface IUniversalisClient
{
    /// <summary>Fetches current listings for items across one Universalis scope.</summary>
    /// <param name="itemIds">Item row ids to fetch.</param>
    /// <param name="scope">Universalis path segment: a world name, data-centre name or region name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One ItemListings per requested item (empty Listings if none).</returns>
    Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct);

    /// <summary>Fetches Universalis's own list of data centers and the world ids in each.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct);
}
