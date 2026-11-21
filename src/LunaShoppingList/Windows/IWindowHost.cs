using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>What a list window needs from whoever owns the windows: open its results window, and tell it when the list window closes.</summary>
internal interface IWindowHost
{
    /// <summary>
    /// Opens the results window for the list <paramref name="key"/> with <paramref name="book"/>, or updates and focuses it if it is already open.
    /// Called on the UI thread.
    /// </summary>
    void ShowResults(string key, string title, OfferBook book);

    /// <summary>The list window for <paramref name="key"/> was closed: dispose it and cancel its build. Its results window stays open.</summary>
    void CloseList(string key);
}
