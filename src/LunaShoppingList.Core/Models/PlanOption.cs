namespace LunaShoppingList.Core;

/// <summary>One point on the trade-off between how many worlds to visit and how much to pay: a complete buying plan.</summary>
/// <param name="WorldCount">Number of worlds the plan visits (distinct worlds with at least one line).</param>
/// <param name="Total">Gil for the whole plan (excluding tax): the exact price of the whole stacks it buys.</param>
/// <param name="ExtraGil">Total minus the total of the cheapest plan; zero for the cheapest plan itself, never negative.</param>
/// <param name="ExtraPercent">ExtraGil as a percentage of the cheapest plan's total; zero when that total is zero.</param>
/// <param name="Lines">The allocation, ordered by item id then world id, quantities above zero. Each quantity is the units bought on that world, a legal total of whole stacks there (it can exceed the item's need). Per item, capped at the need, it covers exactly what the cheapest plan covers.</param>
public record PlanOption(int WorldCount, long Total, long ExtraGil, double ExtraPercent, IReadOnlyList<AllocationLine> Lines);
