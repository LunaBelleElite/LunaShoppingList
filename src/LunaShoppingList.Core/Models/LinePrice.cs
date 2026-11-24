namespace LunaShoppingList.Core;

/// <summary>
/// What buying some units of one item on one world really costs, as whole stacks (see <see cref="LinePricer.Price"/>).
/// </summary>
/// <param name="Quantity">Units actually bought: the sum of the chosen stacks, a legal total for the world.</param>
/// <param name="Cost">Gil for the chosen stacks (each stack's quantity times its unit price).</param>
/// <param name="StackIndices">Indices into <see cref="WorldOffer.Steps"/> of the chosen stacks, ascending.</param>
/// <param name="StackSizes">Quantity of each chosen stack, in the same (ascending listing) order as <paramref name="StackIndices"/>.</param>
/// <param name="MinUnitPrice">Lowest unit price among the chosen stacks.</param>
/// <param name="MaxUnitPrice">Highest unit price among the chosen stacks.</param>
/// <param name="OldestReview">Oldest review time among the chosen stacks.</param>
/// <param name="Approximate">True when the world is beyond its <see cref="WorldOffer.ExactTotalsLimit"/> and the greedy <see cref="WorldOffer.Cover"/> was used: whole stacks, but not proven the cheapest for that quantity.</param>
public sealed record LinePrice(
    int Quantity,
    long Cost,
    IReadOnlyList<int> StackIndices,
    IReadOnlyList<int> StackSizes,
    int MinUnitPrice,
    int MaxUnitPrice,
    DateTimeOffset OldestReview,
    bool Approximate)
{
    /// <summary>Number of stacks (listings) bought.</summary>
    public int StackCount => StackIndices.Count;
}
