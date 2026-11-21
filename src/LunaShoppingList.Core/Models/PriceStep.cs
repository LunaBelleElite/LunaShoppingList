namespace LunaShoppingList.Core;

/// <summary>A block of units for sale on one world at one unit price.</summary>
/// <param name="UnitPrice">Price of one unit in gil (excluding tax). Always above zero.</param>
/// <param name="Quantity">Units available at that price. Always above zero.</param>
/// <param name="Hq">True if these units are high quality.</param>
/// <param name="LastReview">When the listing was last seen (UTC); older means staler.</param>
public record PriceStep(int UnitPrice, int Quantity, bool Hq, DateTimeOffset LastReview);
