namespace LunaShoppingList.Core;

/// <summary>The whole stacks bought on one world as part of an <see cref="ItemCover"/>.</summary>
/// <param name="WorldId">World row id.</param>
/// <param name="Units">Units bought on that world: a legal total for it (a sum of its stack quantities).</param>
/// <param name="Cost">Gil for those stacks.</param>
/// <param name="StackIndices">Indices into that world's <see cref="WorldOffer.Steps"/>, ascending.</param>
public sealed record WorldBuy(uint WorldId, int Units, long Cost, IReadOnlyList<int> StackIndices);

/// <summary>
/// The cheapest set of whole stacks, across several worlds, that covers a need (see <see cref="StackCoverSolver.Cover"/>).
/// </summary>
/// <param name="Worlds">One entry per world that has at least one chosen stack, ordered by world id.</param>
/// <param name="Units">Total units bought (clamped to <see cref="int.MaxValue"/>); at least the need when the worlds have that many, otherwise everything they sell.</param>
/// <param name="Cost">Total gil.</param>
/// <param name="Approximate">True when the exact search was too large and a greedy cover was used instead (still whole stacks covering the need, not proven cheapest).</param>
public sealed record ItemCover(IReadOnlyList<WorldBuy> Worlds, int Units, long Cost, bool Approximate);
