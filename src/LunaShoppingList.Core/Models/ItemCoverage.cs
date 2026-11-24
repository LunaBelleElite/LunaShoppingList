namespace LunaShoppingList.Core;

/// <summary>
/// How well an allocation covers one item. Market listings are bought as whole stacks, so what is bought can be more than what is needed.
/// </summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Needed">Units the item needs (<see cref="ItemOffers.Needed"/>).</param>
/// <param name="Bought">Units actually bought: the sum of the line quantities over all worlds.</param>
public record ItemCoverage(uint ItemId, int Needed, int Bought)
{
    /// <summary>Units still missing: needed minus bought, never negative.</summary>
    public int Unmet => (int)Math.Max(0L, (long)Needed - Bought);

    /// <summary>Units bought beyond the need (the stack surplus): bought minus needed, never negative.</summary>
    public int Surplus => (int)Math.Max(0L, (long)Bought - Needed);
}
