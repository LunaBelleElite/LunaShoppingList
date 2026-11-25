namespace LunaShoppingList.Core;

/// <summary>The plan options with what the search could not finish (see <see cref="PlanFrontier.BuildDetailed(OfferBook, long)"/>).</summary>
/// <param name="Options">The same options <see cref="PlanFrontier.Build"/> returns.</param>
/// <param name="BudgetExhausted">
/// True when the search ran out of its work budget before it finished: the options are still complete, valid plans (the cheapest
/// plan first, unchanged), but some world counts may be missing or dearer than a full search would have found.
/// </param>
public sealed record PlanFrontierResult(IReadOnlyList<PlanOption> Options, bool BudgetExhausted)
{
    /// <summary>
    /// The largest k such that, for every world count up to k, the cheapest option with that many worlds (or fewer) is proven to be the
    /// true cheapest total. The number of worlds in the book when the whole result is proven (a book of at most ten worlds that was
    /// searched to the end, or an exact search that finished); 0 when nothing is proven (more than 64 worlds, an item the exact solver
    /// cannot take, or the search was cut before the first world count was settled). Above this the options are good plans, not proven.
    /// </summary>
    public int ProvenWorldCount { get; init; }
}
