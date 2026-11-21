namespace LunaShoppingList.Core;

/// <summary>How far the user is willing to travel to shop.</summary>
public enum TravelScopeKind
{
    /// <summary>Worlds reachable from a main-city aetheryte: the same region as the current world.</summary>
    CrossWorld,

    /// <summary>Every public world; needs Data Center Travel.</summary>
    CrossDataCenter,
}
