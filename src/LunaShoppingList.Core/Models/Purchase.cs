namespace LunaShoppingList.Core;

/// <summary>A planned purchase of part or all of one listing.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="WorldId">World row id to buy on.</param>
/// <param name="WorldName">World name to buy on.</param>
/// <param name="Quantity">Units to buy from this listing.</param>
/// <param name="PricePerUnit">Price of one unit in gil.</param>
/// <param name="Hq">True if the purchased listing is high quality.</param>
/// <param name="LastReview">Last-review time of the source listing (UTC).</param>
public record Purchase(uint ItemId, uint WorldId, string WorldName, int Quantity, int PricePerUnit, bool Hq, DateTimeOffset LastReview);
