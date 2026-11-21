namespace LunaShoppingList.Core;

/// <summary>Game world data (World sheet and player state).</summary>
public interface IWorldData
{
    /// <summary>The world the player is currently on, or null if not logged in.</summary>
    WorldInfo? CurrentWorld { get; }

    /// <summary>Every world known to the game, public or not.</summary>
    IReadOnlyList<WorldInfo> AllWorlds { get; }
}
