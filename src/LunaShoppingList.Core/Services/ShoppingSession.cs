namespace LunaShoppingList.Core;

/// <summary>
/// The current ingredient requirements plus the user's include/exclude choices.
/// UI-independent so the toggle rules are unit-tested. Everything is included by default.
/// </summary>
public sealed class ShoppingSession
{
    private readonly MaterialsCalculator _calc;
    private readonly IItemInfo _items;
    private IReadOnlyList<Requirement> _requirements = Array.Empty<Requirement>();
    private readonly HashSet<uint> _explicitIncludes = new();

    /// <summary>Creates a session.</summary>
    /// <param name="calc">Computes requirements for an Artisan list.</param>
    /// <param name="items">Item data, used to recognise crystals, shards and clusters.</param>
    public ShoppingSession(MaterialsCalculator calc, IItemInfo items)
    {
        _calc = calc ?? throw new ArgumentNullException(nameof(calc));
        _items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>Current requirements, ordered by item id.</summary>
    public IReadOnlyList<Requirement> Requirements => _requirements;

    /// <summary>The exclusion state, as passed to the planner.</summary>
    public ExclusionState Exclusions { get; } = new();

    /// <summary>Item ids of the current requirements that are crystals, shards or clusters.</summary>
    public IReadOnlyList<uint> CrystalLikeItemIds
        => _requirements.Where(r => _items.IsCrystalLike(r.ItemId)).Select(r => r.ItemId).ToList();

    /// <summary>
    /// Replaces the requirements with those of an Artisan list. Exclusions are left as they are, so call
    /// <see cref="ImportExclusions"/> first to apply a list's remembered choices.
    /// </summary>
    /// <param name="list">The Artisan list.</param>
    public void LoadFromList(CraftingList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _requirements = _calc.Compute(list).OrderBy(r => r.ItemId).ToList();
    }

    /// <summary>
    /// Replaces the requirements with those of an Artisan list and the exclusions with the list's remembered
    /// choices, all or nothing: if computing the requirements throws, neither is changed.
    /// </summary>
    /// <param name="list">The Artisan list.</param>
    /// <param name="excludedItemIds">The list's remembered excluded item ids.</param>
    /// <exception cref="InvalidOperationException">The list references an unknown recipe.</exception>
    /// <exception cref="OverflowException">A quantity exceeds the int range.</exception>
    public void LoadFromList(CraftingList list, IEnumerable<uint> excludedItemIds)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(excludedItemIds);
        var requirements = _calc.Compute(list).OrderBy(r => r.ItemId).ToList();
        var excluded = excludedItemIds.ToList();
        _requirements = requirements;
        Exclusions.ExcludedItemIds.Clear();
        foreach (var id in excluded) Exclusions.ExcludedItemIds.Add(id);
    }

    /// <summary>
    /// Unticks every ingredient that has a recipe (<paramref name="recipes"/> finds one), on top of the exclusions already
    /// set; nothing is ever re-included. Crystals, shards and clusters are never touched, and neither is an item the
    /// user explicitly ticked on (<see cref="ExportIncludes"/>).
    /// </summary>
    /// <param name="recipes">Finds the recipe that makes an item.</param>
    public void ExcludeCrafted(IRecipeLookup recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        var crafted = _requirements
            .Where(r => !_explicitIncludes.Contains(r.ItemId) && !_items.IsCrystalLike(r.ItemId) && recipes.FindRecipeForItem(r.ItemId) != null)
            .Select(r => r.ItemId)
            .ToList();
        Exclusions.ExcludeAll(crafted, true);
    }

    /// <summary>Includes or excludes every current requirement, crystals too; exclusions for items not in the list are kept.</summary>
    /// <param name="included">True to buy everything, false to skip everything.</param>
    public void SetAllIncluded(bool included)
    {
        var ids = _requirements.Select(r => r.ItemId).ToList();
        Exclusions.ExcludeAll(ids, !included);
        if (included) foreach (var id in ids) _explicitIncludes.Add(id);
        else _explicitIncludes.Clear();
    }

    /// <summary>True only if there is at least one requirement and all of them are included.</summary>
    public bool AreAllIncluded => _requirements.Count > 0 && _requirements.All(r => IsIncluded(r.ItemId));

    /// <summary>Replaces the requirements with pasted ones and clears all exclusions (paste choices are not remembered).</summary>
    /// <param name="paste">The parsed paste.</param>
    public void LoadFromPaste(PasteResult paste)
    {
        ArgumentNullException.ThrowIfNull(paste);
        _requirements = paste.Requirements.OrderBy(r => r.ItemId).ToList();
        Exclusions.ExcludedItemIds.Clear();
        _explicitIncludes.Clear();
    }

    /// <summary>Includes or excludes one item; other items are unaffected.</summary>
    /// <param name="itemId">Item row id.</param>
    /// <param name="included">True to buy it, false to skip it.</param>
    public void SetIncluded(uint itemId, bool included)
    {
        Exclusions.SetExcluded(itemId, !included);
        if (included) _explicitIncludes.Add(itemId);
        else _explicitIncludes.Remove(itemId);
    }

    /// <summary>True unless the item is excluded.</summary>
    /// <param name="itemId">Item row id.</param>
    public bool IsIncluded(uint itemId) => !Exclusions.IsExcluded(itemId);

    /// <summary>Excludes or re-includes every crystal-like requirement; other items are unaffected.</summary>
    /// <param name="excluded">True to exclude them all.</param>
    public void SetCrystalsExcluded(bool excluded)
    {
        var crystals = CrystalLikeItemIds;
        Exclusions.ExcludeAll(crystals, excluded);
        if (excluded) foreach (var id in crystals) _explicitIncludes.Remove(id);
    }

    /// <summary>True only if there is at least one crystal-like requirement and all of them are excluded.</summary>
    public bool AreAllCrystalsExcluded
    {
        get
        {
            var ids = CrystalLikeItemIds;
            return ids.Count > 0 && ids.All(Exclusions.IsExcluded);
        }
    }

    /// <summary>A copy of the item ids the user explicitly ticked on (see <see cref="SetIncluded"/>), for saving beside the exclusions.</summary>
    public IReadOnlySet<uint> ExportIncludes() => new HashSet<uint>(_explicitIncludes);

    /// <summary>
    /// Replaces the explicitly ticked ids with the given ones; duplicates are merged and a null sequence means none.
    /// </summary>
    /// <param name="itemIds">Explicitly ticked item ids.</param>
    public void ImportIncludes(IEnumerable<uint> itemIds)
    {
        var copy = itemIds?.ToList() ?? new List<uint>();
        _explicitIncludes.Clear();
        foreach (var id in copy) _explicitIncludes.Add(id);
    }

    /// <summary>A copy of the excluded item ids, including ids not in the current requirements.</summary>
    public IReadOnlySet<uint> ExportExclusions() => new HashSet<uint>(Exclusions.ExcludedItemIds);

    /// <summary>Replaces the exclusions with the given ids (ids not in the current requirements are kept).</summary>
    /// <param name="itemIds">Excluded item ids.</param>
    public void ImportExclusions(IEnumerable<uint> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var copy = itemIds.ToList();
        Exclusions.ExcludedItemIds.Clear();
        foreach (var id in copy) Exclusions.ExcludedItemIds.Add(id);
    }
}
