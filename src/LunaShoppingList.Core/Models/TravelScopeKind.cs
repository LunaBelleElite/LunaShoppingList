namespace LunaShoppingList.Core;

/// <summary>How far the user is willing to travel to shop.</summary>
public enum TravelScopeKind
{
    /// <summary>Worlds reachable from a main-city aetheryte: the same region as the current world.</summary>
    CrossWorld,

    /// <summary>The same region as CrossWorld plus the Oceania worlds, which need Data Center Travel. Other regions can't be travelled to.</summary>
    CrossDataCenter,
}
