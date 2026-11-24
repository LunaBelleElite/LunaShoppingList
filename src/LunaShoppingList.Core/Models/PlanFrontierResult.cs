namespace LunaShoppingList.Core;

/// <summary>The plan options with what the search could not finish (see <see cref="PlanFrontier.BuildDetailed(OfferBook, long)"/>).</summary>
/// <param name="Options">The same options <see cref="PlanFrontier.Build"/> returns.</param>
/// <param name="BudgetExhausted">
/// True when the search ran out of its work budget before it finished: the options are still complete, valid plans (the cheapest
/// plan first, unchanged), but some world counts may be missing or dearer than a full search would have found.
/// </param>
public sealed record PlanFrontierResult(IReadOnlyList<PlanOption> Options, bool BudgetExhausted);
