namespace LunaShoppingList.Core;

/// <summary>One item to buy: how many are needed in total and what every world in scope can supply.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Needed">Total units needed over the whole list (always above zero).</param>
/// <param name="Worlds">Worlds with at least one unit for sale, ordered in two groups. First the worlds that can supply every needed unit (<c>UnitsForSale &gt;= Needed</c>), by <c>CostFor(Needed)</c> ascending. Then the worlds that cannot, by units for sale descending, then average unit price for the units they can supply ascending. Within each group ties go to world name (ordinal ignore case), then world id.</param>
public record ItemOffers(uint ItemId, int Needed, IReadOnlyList<WorldOffer> Worlds);
