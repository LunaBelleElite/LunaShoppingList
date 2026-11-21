namespace LunaShoppingList.Core;

/// <summary>The result of planning: what to buy where.</summary>
/// <param name="Worlds">Per-world purchase groups.</param>
/// <param name="Skipped">Items the user excluded.</param>
/// <param name="Shortfalls">Items not fully available in scope.</param>
public record ShoppingPlan(IReadOnlyList<WorldShoppingGroup> Worlds, IReadOnlyList<SkippedItem> Skipped, IReadOnlyList<ShortfallItem> Shortfalls);
