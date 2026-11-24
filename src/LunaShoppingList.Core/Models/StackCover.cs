namespace LunaShoppingList.Core;

/// <summary>
/// The whole stacks one world sells that cover a need at the lowest cost (see <see cref="WorldOffer.Cover"/>). A market listing can
/// only be bought as a whole stack, so <see cref="Units"/> can be more than the need (surplus).
/// </summary>
/// <param name="Units">Units bought: the sum of the chosen stacks. At least the need when the world has that many, otherwise everything it sells.</param>
/// <param name="Cost">Gil for the chosen stacks (each stack's quantity times its unit price).</param>
/// <param name="StackIndices">Indices into <see cref="WorldOffer.Steps"/> of the chosen stacks, ascending. Empty when nothing is bought.</param>
/// <param name="MinUnitPrice">Lowest unit price among the chosen stacks; 0 when none.</param>
/// <param name="MaxUnitPrice">Highest unit price among the chosen stacks; 0 when none.</param>
/// <param name="OldestReview">Oldest <see cref="PriceStep.LastReview"/> among the chosen stacks; <see cref="DateTimeOffset.MaxValue"/> when none.</param>
/// <param name="Approximate">True when the exact search was too large (see <see cref="StackCoverSolver.MaxCellUpdates"/>) and a greedy cover was used instead: still whole stacks covering the need, but not proven cheapest.</param>
public sealed record StackCover(
    int Units,
    long Cost,
    IReadOnlyList<int> StackIndices,
    int MinUnitPrice,
    int MaxUnitPrice,
    DateTimeOffset OldestReview,
    bool Approximate);
