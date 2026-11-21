namespace LunaShoppingList.Core;

/// <summary>One market-board listing.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="WorldId">World row id the listing is on.</param>
/// <param name="WorldName">World name the listing is on.</param>
/// <param name="PricePerUnit">Price of one unit in gil (excluding tax).</param>
/// <param name="Quantity">Units in the listing.</param>
/// <param name="Hq">True if the listing is high quality.</param>
/// <param name="LastReview">When the listing was last seen by Universalis contributors (UTC); used to flag stale data.</param>
public record Listing(uint ItemId, uint WorldId, string WorldName, int PricePerUnit, int Quantity, bool Hq, DateTimeOffset LastReview);
