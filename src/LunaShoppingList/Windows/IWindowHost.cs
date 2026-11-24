using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>
/// What a build knew beyond the offer book, for the results window: how many units of each item were already held and
/// subtracted (never the house), the items whose whole need was held, and the listing age limit in hours (0 = none).
/// </summary>
internal sealed record BuildContext(IReadOnlyDictionary<uint, int> Held, IReadOnlyList<Requirement> FullyCovered, int MaxAgeHours)
{
    /// <summary>Nothing subtracted and no age limit.</summary>
    public static readonly BuildContext None = new(new Dictionary<uint, int>(), [], 0);
}

/// <summary>What a list window needs from whoever owns the windows: open its results window, and tell it when the list window closes.</summary>
internal interface IWindowHost
{
    /// <summary>
    /// Opens the results window for the list <paramref name="key"/> with <paramref name="book"/>, or updates and focuses it if it is already open.
    /// Called on the UI thread.
    /// </summary>
    void ShowResults(string key, string title, OfferBook book, IReadOnlyList<PlanOption> options, BuildContext context);

    /// <summary>
    /// Removes the results window for <paramref name="key"/> if one is open (its old plan no longer matches the list). Safe if none
    /// is open. Called on the UI thread.
    /// </summary>
    void HideResults(string key);

    /// <summary>The list window for <paramref name="key"/> was closed: dispose it and cancel its build. Its results window stays open.</summary>
    void CloseList(string key);
}
