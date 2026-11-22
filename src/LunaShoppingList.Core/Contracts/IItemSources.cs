namespace LunaShoppingList.Core;

/// <summary>Where an item can be had other than the market board (game vendor data).</summary>
public interface IItemSources
{
    /// <summary>
    /// False while the vendor index is still being built, or after building it failed; <see cref="For"/> then returns nothing.
    /// Reading this never waits for the build.
    /// </summary>
    bool IsReady { get; }

    /// <summary>True once building the vendor index has failed for good; it will not become ready.</summary>
    bool HasFailed { get; }

    /// <summary>
    /// Every vendor source of an item, in no particular order; empty if none is known or the index is not ready.
    /// A source's NPC may have no known zone or coordinates.
    /// </summary>
    /// <param name="itemId">Item row id.</param>
    IReadOnlyList<ItemSource> For(uint itemId);
}
