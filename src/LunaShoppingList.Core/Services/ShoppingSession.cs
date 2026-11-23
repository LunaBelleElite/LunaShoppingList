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
    private SelectionSnapshot? _undo;

    /// <summary>Creates a session.</summary>
    /// <param name="calc">Computes requirements for an Artisan list.</param>
    /// <param name="items">Item data, used to recognise crystals, shards and clusters.</param>
    public ShoppingSession(MaterialsCalculator calc, IItemInfo items)
    {
        _calc = calc ?? throw new ArgumentNullException(nameof(calc));
        _items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>The selection (unticked ids and hand-ticked ids) as it was before a bulk change, for <see cref="Undo"/>.</summary>
    /// <param name="Label">What the bulk change was, e.g. the button's name.</param>
    /// <param name="Excluded">The excluded item ids before the change.</param>
    /// <param name="Includes">The explicitly ticked item ids before the change.</param>
    public sealed record SelectionSnapshot(string Label, IReadOnlySet<uint> Excluded, IReadOnlySet<uint> Includes);

    /// <summary>True when <see cref="Undo"/> has a bulk change to take back.</summary>
    public bool CanUndo => _undo is not null;

    /// <summary>The label given to the bulk change <see cref="Undo"/> would take back, or null when there is none.</summary>
    public string? UndoLabel => _undo?.Label;

    /// <summary>
    /// Runs a bulk change (one of the Untick/Tick buttons) so it can be taken back. If it changed the excluded or the
    /// explicitly ticked ids, the state from before it is kept as the single undo level (replacing any earlier one) and true is
    /// returned; if it changed nothing, false is returned and an earlier undo level is left as it was.
    /// </summary>
    /// <param name="label">What the change is called.</param>
    /// <param name="change">The change, run on this session.</param>
    public bool ApplyBulk(string label, Action<ShoppingSession> change)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(change);
        var excluded = ExportExclusions();
        var includes = ExportIncludes();
        var earlier = _undo;
        change(this);
        // The change itself may call a clearing method (none of the bulk methods does); keep any earlier level either way.
        if (Exclusions.ExcludedItemIds.SetEquals(excluded) && _explicitIncludes.SetEquals(includes))
        {
            _undo = earlier;
            return false;
        }
        _undo = new SelectionSnapshot(label, excluded, includes);
        return true;
    }

    /// <summary>Restores the selection from before the last bulk change and forgets it. Returns its label, or null when there was nothing to undo.</summary>
    public string? Undo()
    {
        var snapshot = _undo;
        if (snapshot is null) return null;
        Exclusions.ExcludedItemIds.Clear();
        foreach (var id in snapshot.Excluded) Exclusions.ExcludedItemIds.Add(id);
        _explicitIncludes.Clear();
        foreach (var id in snapshot.Includes) _explicitIncludes.Add(id);
        _undo = null;
        return snapshot.Label;
    }

    /// <summary>Forgets the undo level without restoring anything.</summary>
    public void ClearUndo() => _undo = null;

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
        _undo = null;
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
        _undo = null;
        Exclusions.ExcludedItemIds.Clear();
        foreach (var id in excluded) Exclusions.ExcludedItemIds.Add(id);
    }

    /// <summary>
    /// Unticks every ingredient that has a recipe (<paramref name="recipes"/> finds one), on top of the exclusions already
    /// set; nothing is ever re-included. Crystals, shards and clusters are never touched, and neither is an item the
    /// user explicitly ticked on (<see cref="ExportIncludes"/>).
    /// </summary>
    /// <param name="recipes">Finds the recipe that makes an item.</param>
    /// <param name="overrideHandTicks">True when the player pressed a button for it: items they ticked by hand are unticked too.</param>
    public void ExcludeCrafted(IRecipeLookup recipes, bool overrideHandTicks = false)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        var crafted = _requirements
            .Where(r => (overrideHandTicks || !_explicitIncludes.Contains(r.ItemId)) && !_items.IsCrystalLike(r.ItemId) && recipes.FindRecipeForItem(r.ItemId) != null)
            .Select(r => r.ItemId)
            .ToList();
        Exclusions.ExcludeAll(crafted, true);
        if (overrideHandTicks) foreach (var id in crafted) _explicitIncludes.Remove(id);
    }

    /// <summary>
    /// Unticks every ingredient the player already holds enough of (owned count at least the quantity needed), on top of the
    /// exclusions already set; nothing is ever re-included. An unknown count (<c>null</c>) or none owned leaves the item ticked.
    /// This is an explicit request, so it also overrides an item the user ticked on by hand.
    /// </summary>
    /// <param name="ownedTotal">The owned count of an item (bags, saddlebag and retainers), or <c>null</c> when not known.</param>
    public void ExcludeOwned(Func<uint, long?> ownedTotal)
    {
        ArgumentNullException.ThrowIfNull(ownedTotal);
        var covered = _requirements
            .Where(r => r.Quantity > 0 && ownedTotal(r.ItemId) is long have && have >= r.Quantity)
            .Select(r => r.ItemId)
            .ToList();
        Exclusions.ExcludeAll(covered, true);
        foreach (var id in covered) _explicitIncludes.Remove(id);
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
        _undo = null;
        Exclusions.ExcludedItemIds.Clear();
        _explicitIncludes.Clear();
    }

    /// <summary>Includes or excludes one item; other items are unaffected.</summary>
    /// <param name="itemId">Item row id.</param>
    /// <param name="included">True to buy it, false to skip it.</param>
    public void SetIncluded(uint itemId, bool included)
    {
        _undo = null;
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
        _undo = null;
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
        _undo = null;
        Exclusions.ExcludedItemIds.Clear();
        foreach (var id in copy) Exclusions.ExcludedItemIds.Add(id);
    }
}
