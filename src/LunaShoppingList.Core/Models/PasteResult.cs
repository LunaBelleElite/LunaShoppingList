namespace LunaShoppingList.Core;

/// <summary>Result of parsing pasted Artisan "Export Remaining Ingredients" text.</summary>
/// <param name="Requirements">Recognised item quantities.</param>
/// <param name="UnmatchedLines">Non-blank lines that could not be parsed or whose item name was not found.</param>
public record PasteResult(IReadOnlyList<Requirement> Requirements, IReadOnlyList<string> UnmatchedLines);
