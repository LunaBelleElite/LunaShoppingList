namespace LunaShoppingList.Core;

/// <summary>Which items are monster drops (outside data, downloaded at runtime; a small curated list, not every drop in the game).</summary>
public interface IDropSources
{
    /// <summary>
    /// False while the drop list is still being loaded, or after loading it failed; <see cref="IsDrop"/> then returns false.
    /// Reading this never waits for the load.
    /// </summary>
    bool IsReady { get; }

    /// <summary>True once loading the drop list has failed for good; it will not become ready.</summary>
    bool HasFailed { get; }

    /// <summary>True if the item is on the drop list; always false while the list is not ready.</summary>
    /// <param name="itemId">Item row id.</param>
    bool IsDrop(uint itemId);
}
