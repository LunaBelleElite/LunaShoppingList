namespace LunaShoppingList.Core;

/// <summary>A suggestion with the detail the plain line list cannot carry (see <see cref="Suggester.SuggestDetailed"/>).</summary>
/// <param name="Lines">The same lines <see cref="Suggester.Suggest"/> returns.</param>
/// <param name="ApproximateItemIds">Items whose stacks were chosen by the greedy fallback because the exact search was too large, ascending; empty when every item was solved exactly.</param>
public sealed record SuggestionResult(IReadOnlyList<AllocationLine> Lines, IReadOnlyList<uint> ApproximateItemIds);
