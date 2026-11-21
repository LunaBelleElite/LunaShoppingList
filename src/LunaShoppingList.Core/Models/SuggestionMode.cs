namespace LunaShoppingList.Core;

/// <summary>How a starting buying plan is chosen from an <see cref="OfferBook"/>.</summary>
public enum SuggestionMode
{
    /// <summary>The lowest total cost: each unit comes from the cheapest listing in scope, splitting an item across worlds when that is cheaper.</summary>
    Cheapest,

    /// <summary>As few worlds to visit as possible, accepting higher prices; never more worlds than the cheapest plan.</summary>
    FewestWorlds,
}
