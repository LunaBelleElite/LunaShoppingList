namespace LunaShoppingList.Core;

/// <summary>The trade-off between worlds to visit and gil: every worthwhile plan from the cheapest down to the fewest worlds.</summary>
public static class PlanFrontier
{
    /// <summary>
    /// Builds the options for <paramref name="book"/>, ordered by world count descending (the first is always the cheapest plan,
    /// with zero extra). Candidates come from the cheapest plan, the greedy fewest-worlds plan, and a backward-elimination path that
    /// repeatedly removes the world whose removal raises the total least (its units are reassigned cheapest-first to the remaining
    /// worlds, items may be split across worlds) down to the smallest feasible world count. A candidate is kept only if it supplies
    /// the same units per item as the cheapest plan. The result is Pareto-filtered: for each world count the cheapest candidate, and
    /// no option has more worlds and a higher total than another. Never returns an empty list for a book with at least one buyable
    /// item; an empty book gives a single empty plan with zero worlds. The options are heuristic plans, not proven optima.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a fixed set of worlds the cheapest way to supply an item is to take its units cheapest-first across those worlds (each
    /// world's ladder is sorted, so its cost is convex), so every step of the paths below re-optimises every affected item over the
    /// current set; the plan for a set is therefore never dearer than reassigning only the removed world's units. Only the choice of
    /// the set is heuristic.
    /// </para>
    /// <para>
    /// A second path runs forward from the fewest-worlds plan's worlds: it repeatedly adds the outside world that lowers the total
    /// most, until the plan visits as many worlds as the cheapest plan or no world lowers it. This finds options that elimination
    /// cannot reach, because elimination only ever visits worlds the cheapest plan uses.
    /// </para>
    /// <para>
    /// Then an improvement phase: when elimination is stuck (every single removal leaves an item short), it tries dropping two
    /// worlds for one outside world and, if that works, continues eliminating from there; then, starting from the fewest worlds,
    /// it tries swapping one visited world for one outside world in each world count's best plan while that lowers the total.
    /// </para>
    /// <para>
    /// Work is bounded: each path takes at most one step per world in the book. A removal step re-evaluates only the items the
    /// removed world supplied; an addition step prices every outside world against the units each item currently uses. The
    /// improvement phase stops after 300,000 item re-evaluations in total, so on a large book it improves
    /// the smallest world counts first and may leave larger ones as the paths found them. On a book of 100 items by 150 worlds
    /// with ladders of three to seven steps the whole build takes about two seconds.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PlanOption> Build(OfferBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        var cheapest = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        var model = new Model(book, cheapest);

        var candidates = new List<IReadOnlyList<AllocationLine>> { cheapest, fewest };
        model.Backward(model.WorldSet(cheapest), candidates.Add);
        model.Forward(model.WorldSet(fewest), WorldsIn(cheapest), candidates.Add);

        // Repeat while a round reaches fewer worlds: a swapped plan can open a shrink that the unswapped one could not.
        for (var fewestSoFar = int.MaxValue; model.HasBudget;)
        {
            while (model.HasBudget)
            {
                var smallest = BestPerWorldCount(model, candidates).First().Value.Lines;
                if (model.Shrink(model.WorldSet(smallest), candidates.Add) is not { } smaller)
                    break;
                model.Backward(smaller, candidates.Add);
            }
            foreach (var (_, (_, lines)) in BestPerWorldCount(model, candidates))
            {
                if (!model.HasBudget)
                    break;
                model.Swap(model.WorldSet(lines), candidates.Add);
            }
            var reached = BestPerWorldCount(model, candidates).First().Key;
            if (reached >= fewestSoFar)
                break;
            fewestSoFar = reached;
        }

        return Frontier(model, candidates);
    }

    /// <summary>The most item re-evaluations the improvement phase of <see cref="Build"/> may spend.</summary>
    private const int ImprovementBudget = 300_000;

    /// <summary>
    /// Returns the option with the fewest worlds whose <see cref="PlanOption.ExtraGil"/> is within <paramref name="budgetGil"/>
    /// (ties: lowest total). A budget of zero or less returns the cheapest plan (the first option). Returns null only when
    /// <paramref name="options"/> is empty.
    /// </summary>
    public static PlanOption? Pick(IReadOnlyList<PlanOption> options, long budgetGil)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
            return null;
        if (budgetGil <= 0)
            return options[0];

        PlanOption? best = null;
        foreach (var o in options)
        {
            if (o.ExtraGil > budgetGil)
                continue;
            if (best is null || o.WorldCount < best.WorldCount || (o.WorldCount == best.WorldCount && o.Total < best.Total))
                best = o;
        }
        return best ?? options[0];
    }

    private static int WorldsIn(IReadOnlyList<AllocationLine> lines) => lines.Select(l => l.WorldId).Distinct().Count();

    private static IReadOnlyList<PlanOption> Frontier(Model model, List<IReadOnlyList<AllocationLine>> candidates)
    {
        var best = BestPerWorldCount(model, candidates);

        // Fewest worlds first: an option survives only if it is strictly cheaper than every option with fewer worlds.
        var kept = new List<(int Worlds, long Total, IReadOnlyList<AllocationLine> Lines)>();
        var floor = long.MaxValue;
        foreach (var (worlds, (total, lines)) in best)
        {
            if (total >= floor)
                continue;
            floor = total;
            kept.Add((worlds, total, lines));
        }
        kept.Reverse();

        var cheapestTotal = kept[0].Total;
        return kept
            .Select(k =>
            {
                var extra = k.Total - cheapestTotal;
                return new PlanOption(k.Worlds, k.Total, extra, cheapestTotal == 0 ? 0 : extra * 100.0 / cheapestTotal, k.Lines);
            })
            .ToList();
    }

    /// <summary>
    /// Prices every candidate that supplies exactly the cheapest plan's units and keeps the cheapest per world count (ties: fewer
    /// lines, then the lines compared in order), by world count ascending.
    /// </summary>
    private static SortedDictionary<int, (long Total, IReadOnlyList<AllocationLine> Lines)> BestPerWorldCount(
        Model model, List<IReadOnlyList<AllocationLine>> candidates)
    {
        var best = new SortedDictionary<int, (long Total, IReadOnlyList<AllocationLine> Lines)>();
        foreach (var lines in candidates)
        {
            if (model.Price(lines) is not { } total)
                continue;
            var worlds = WorldsIn(lines);
            if (!best.TryGetValue(worlds, out var current) || Better(total, lines, current.Total, current.Lines))
                best[worlds] = (total, lines);
        }
        return best;
    }

    private static bool Better(long total, IReadOnlyList<AllocationLine> lines, long otherTotal, IReadOnlyList<AllocationLine> other)
    {
        if (total != otherTotal)
            return total < otherTotal;
        if (lines.Count != other.Count)
            return lines.Count < other.Count;
        for (var k = 0; k < lines.Count; k++)
        {
            var c = Compare(lines[k], other[k]);
            if (c != 0)
                return c < 0;
        }
        return false;
    }

    private static int Compare(AllocationLine a, AllocationLine b)
    {
        var c = a.ItemId.CompareTo(b.ItemId);
        if (c != 0)
            return c;
        c = a.WorldId.CompareTo(b.WorldId);
        return c != 0 ? c : a.Quantity.CompareTo(b.Quantity);
    }

    /// <summary>The book reduced to the items the cheapest plan buys, with worlds numbered by ascending id.</summary>
    private sealed class Model
    {
        private readonly uint[] _worldIds;
        private readonly Dictionary<uint, int> _worldIndex = new();
        private readonly ItemData[] _items;
        private readonly Dictionary<uint, ItemOffers> _offersById = new();
        private readonly List<int>[] _itemsByWorld;

        public Model(OfferBook book, IReadOnlyList<AllocationLine> cheapest)
        {
            foreach (var item in book.Items)
                _offersById.TryAdd(item.ItemId, item);

            _worldIds = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().OrderBy(id => id).ToArray();
            for (var w = 0; w < _worldIds.Length; w++)
                _worldIndex[_worldIds[w]] = w;
            _itemsByWorld = _worldIds.Select(_ => new List<int>()).ToArray();

            var target = new Dictionary<uint, int>();
            foreach (var line in cheapest)
                target[line.ItemId] = target.GetValueOrDefault(line.ItemId) + line.Quantity;

            var items = new List<ItemData>();
            foreach (var (itemId, units) in target.OrderBy(t => t.Key))
            {
                var offers = _offersById[itemId];
                var byWorld = new WorldOffer?[_worldIds.Length];
                foreach (var offer in offers.Worlds)
                    byWorld[_worldIndex[offer.WorldId]] ??= offer;

                var ladder = new List<Unit>();
                for (var w = 0; w < byWorld.Length; w++)
                {
                    if (byWorld[w] is not { } offer)
                        continue;
                    _itemsByWorld[w].Add(items.Count);
                    for (var s = 0; s < offer.Steps.Count; s++)
                        ladder.Add(new Unit(offer.Steps[s].UnitPrice, offer.Steps[s].Quantity, w, s));
                }
                ladder.Sort();
                items.Add(new ItemData(itemId, units, byWorld, ladder.ToArray()));
            }
            _items = items.ToArray();
        }

        public bool[] WorldSet(IReadOnlyList<AllocationLine> lines)
        {
            var set = new bool[_worldIds.Length];
            foreach (var line in lines)
                if (_worldIndex.TryGetValue(line.WorldId, out var w))
                    set[w] = true;
            return set;
        }

        /// <summary>Gil for <paramref name="lines"/>, or null when they do not supply exactly the cheapest plan's units within stock.</summary>
        public long? Price(IReadOnlyList<AllocationLine> lines)
        {
            long total = 0;
            var supplied = new Dictionary<uint, long>();
            foreach (var line in lines)
            {
                if (line.Quantity <= 0 || !_offersById.TryGetValue(line.ItemId, out var item))
                    return null;
                var offer = item.Worlds.FirstOrDefault(w => w.WorldId == line.WorldId);
                if (offer is null || line.Quantity > offer.UnitsForSale)
                    return null;
                total += offer.CostFor(line.Quantity);
                supplied[line.ItemId] = supplied.GetValueOrDefault(line.ItemId) + line.Quantity;
            }
            if (supplied.Count != _items.Length)
                return null;
            foreach (var item in _items)
                if (supplied.GetValueOrDefault(item.Id) != item.Target)
                    return null;
            return total;
        }

        /// <summary>
        /// Takes the item's target units cheapest-first from the worlds in <paramref name="set"/> other than
        /// <paramref name="excluded"/>, writing the units per world into <paramref name="alloc"/> when given. Returns the gil,
        /// or -1 when the worlds cannot supply the target.
        /// </summary>
        private static long Allocate(ItemData item, bool[] set, int excluded, SortedDictionary<int, int>? alloc)
        {
            long cost = 0;
            var need = item.Target;
            foreach (var unit in item.Ladder)
            {
                if (need == 0)
                    break;
                if (!set[unit.World] || unit.World == excluded)
                    continue;
                var take = Math.Min(need, unit.Quantity);
                cost += (long)take * unit.Price;
                if (alloc is not null)
                    alloc[unit.World] = alloc.GetValueOrDefault(unit.World) + take;
                need -= take;
            }
            return need > 0 ? -1 : cost;
        }

        private sealed class State
        {
            public required bool[] Set;
            public required long[] Cost;
            public required SortedDictionary<int, int>[] Alloc;
        }

        private State? Start(bool[] set)
        {
            var state = new State { Set = (bool[])set.Clone(), Cost = new long[_items.Length], Alloc = new SortedDictionary<int, int>[_items.Length] };
            for (var i = 0; i < _items.Length; i++)
                if (!Reallocate(state, i))
                    return null;
            return state;
        }

        private bool Reallocate(State state, int i)
        {
            var alloc = new SortedDictionary<int, int>();
            var cost = Allocate(_items[i], state.Set, -1, alloc);
            state.Alloc[i] = alloc;
            state.Cost[i] = cost;
            return cost >= 0;
        }

        private List<AllocationLine> Lines(State state)
        {
            var lines = new List<AllocationLine>();
            for (var i = 0; i < _items.Length; i++)
                foreach (var (w, q) in state.Alloc[i])
                    lines.Add(new AllocationLine(_items[i].Id, _worldIds[w], q));
            return lines;
        }

        /// <summary>From the worlds in <paramref name="start"/>, repeatedly drops the world whose removal raises the total least.</summary>
        public void Backward(bool[] start, Action<IReadOnlyList<AllocationLine>> record)
        {
            if (Start(start) is not { } state)
                return;
            record(Lines(state));

            // removal[i][w]: extra gil for item i if world w (which currently supplies it) is dropped; -1 when infeasible.
            var removal = new Dictionary<int, long>[_items.Length];
            for (var i = 0; i < _items.Length; i++)
                removal[i] = RemovalCosts(state, i);

            for (var step = 0; step < _worldIds.Length; step++)
            {
                var delta = new long[_worldIds.Length];
                var blocked = new bool[_worldIds.Length];
                for (var i = 0; i < _items.Length; i++)
                    foreach (var (w, extra) in removal[i])
                    {
                        if (extra < 0)
                            blocked[w] = true;
                        else
                            delta[w] += extra;
                    }

                var drop = -1;
                for (var w = 0; w < _worldIds.Length; w++)
                    if (state.Set[w] && !blocked[w] && (drop < 0 || delta[w] < delta[drop]))
                        drop = w;
                if (drop < 0)
                    return;

                // Every item the dropped world sells is affected: those it supplied are re-allocated, and for the others it was
                // a fallback, so the cost of removing each of their suppliers may have risen or become infeasible.
                state.Set[drop] = false;
                foreach (var i in _itemsByWorld[drop])
                {
                    if (state.Alloc[i].ContainsKey(drop))
                        Reallocate(state, i);
                    removal[i] = RemovalCosts(state, i);
                }
                record(Lines(state));
            }
        }

        private Dictionary<int, long> RemovalCosts(State state, int i)
        {
            var result = new Dictionary<int, long>();
            foreach (var w in state.Alloc[i].Keys)
            {
                var cost = Allocate(_items[i], state.Set, w, null);
                result[w] = cost < 0 ? -1 : cost - state.Cost[i];
            }
            return result;
        }

        /// <summary>From the worlds in <paramref name="start"/>, repeatedly adds the outside world that lowers the total most.</summary>
        public void Forward(bool[] start, int maxWorlds, Action<IReadOnlyList<AllocationLine>> record)
        {
            if (Start(start) is not { } state)
                return;
            record(Lines(state));

            for (var step = 0; step < _worldIds.Length; step++)
            {
                if (state.Alloc.SelectMany(a => a.Keys).Distinct().Count() >= maxWorlds)
                    return;

                var add = -1;
                long bestGain = 0;
                for (var w = 0; w < _worldIds.Length; w++)
                {
                    if (state.Set[w])
                        continue;
                    long gain = 0;
                    foreach (var i in _itemsByWorld[w])
                        gain += state.Cost[i] - CostWith(state, i, w);
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        add = w;
                    }
                }
                if (add < 0)
                    return;

                state.Set[add] = true;
                foreach (var i in _itemsByWorld[add])
                    Reallocate(state, i);
                record(Lines(state));
            }
        }

        private int _budget = ImprovementBudget;

        public bool HasBudget => _budget > 0;

        /// <summary>Extra gil if <paramref name="remove"/> left the set and <paramref name="add"/> joined it; null when infeasible.</summary>
        private long? MoveDelta(State state, int[] remove, int add)
        {
            var affected = Affected(state, remove, add);
            foreach (var r in remove)
                state.Set[r] = false;
            state.Set[add] = true;
            long? delta = 0;
            foreach (var i in affected)
            {
                _budget--;
                var cost = Allocate(_items[i], state.Set, -1, null);
                if (cost < 0)
                {
                    delta = null;
                    break;
                }
                delta += cost - state.Cost[i];
            }
            foreach (var r in remove)
                state.Set[r] = true;
            state.Set[add] = false;
            return delta;
        }

        private SortedSet<int> Affected(State state, int[] remove, int add)
        {
            var affected = new SortedSet<int>(_itemsByWorld[add]);
            for (var i = 0; i < _items.Length; i++)
                foreach (var r in remove)
                    if (state.Alloc[i].ContainsKey(r))
                        affected.Add(i);
            return affected;
        }

        private void Apply(State state, int[] remove, int add)
        {
            var affected = Affected(state, remove, add);
            foreach (var r in remove)
                state.Set[r] = false;
            state.Set[add] = true;
            foreach (var i in affected)
                Reallocate(state, i);
            Prune(state);
        }

        /// <summary>Drops worlds that supply nothing; the plan for the set does not change.</summary>
        private void Prune(State state)
        {
            var used = new bool[_worldIds.Length];
            foreach (var alloc in state.Alloc)
                foreach (var w in alloc.Keys)
                    used[w] = true;
            for (var w = 0; w < used.Length; w++)
                state.Set[w] &= used[w];
        }

        /// <summary>True when world <paramref name="v"/> sells at least one item that one of <paramref name="remove"/> supplies.</summary>
        private bool CanReplace(State state, int[] remove, int v)
        {
            foreach (var i in _itemsByWorld[v])
                foreach (var r in remove)
                    if (state.Alloc[i].ContainsKey(r))
                        return true;
            return false;
        }

        /// <summary>
        /// Replaces two worlds of <paramref name="start"/> with one outside world, choosing the cheapest such move that keeps every
        /// item supplied; returns the new set, or null when none exists or the budget ran out.
        /// </summary>
        public bool[]? Shrink(bool[] start, Action<IReadOnlyList<AllocationLine>> record)
        {
            if (Start(start) is not { } state)
                return null;
            Prune(state);
            var inside = Enumerable.Range(0, _worldIds.Length).Where(w => state.Set[w]).ToArray();

            (long Delta, int[] Remove, int Add)? best = null;
            for (var a = 0; a < inside.Length; a++)
                for (var b = a + 1; b < inside.Length; b++)
                {
                    var remove = new[] { inside[a], inside[b] };
                    for (var v = 0; v < _worldIds.Length; v++)
                    {
                        if (!HasBudget)
                            return null;
                        if (state.Set[v] || !CanReplace(state, remove, v))
                            continue;
                        if (MoveDelta(state, remove, v) is { } delta && (best is null || delta < best.Value.Delta))
                            best = (delta, remove, v);
                    }
                }
            if (best is null)
                return null;

            Apply(state, best.Value.Remove, best.Value.Add);
            record(Lines(state));
            return (bool[])state.Set.Clone();
        }

        /// <summary>Swaps one world of <paramref name="start"/> for one outside world while that lowers the total (first improvement).</summary>
        public void Swap(bool[] start, Action<IReadOnlyList<AllocationLine>> record)
        {
            if (Start(start) is not { } state)
                return;
            Prune(state);
            var improved = true;
            while (improved)
            {
                improved = false;
                for (var w = 0; w < _worldIds.Length && !improved; w++)
                {
                    if (!state.Set[w])
                        continue;
                    var remove = new[] { w };
                    for (var v = 0; v < _worldIds.Length && !improved; v++)
                    {
                        if (!HasBudget)
                            return;
                        if (state.Set[v] || !CanReplace(state, remove, v))
                            continue;
                        if (MoveDelta(state, remove, v) is < 0)
                        {
                            Apply(state, remove, v);
                            record(Lines(state));
                            improved = true;
                        }
                    }
                }
            }
        }

        /// <summary>Gil for item <paramref name="i"/> if world <paramref name="w"/> joined the set: its ladder merged with the units in use.</summary>
        private long CostWith(State state, int i, int w)
        {
            var item = _items[i];
            var steps = item.ByWorld[w]!.Steps;
            long cost = 0;
            var need = item.Target;
            var s = 0;
            var sLeft = steps.Count > 0 ? steps[0].Quantity : 0;
            var u = 0;
            var uLeft = 0;
            // The units in use are the ladder's prefix over the set; walk them in order alongside world w's ladder.
            while (need > 0)
            {
                while (uLeft == 0 && u < item.Ladder.Length)
                {
                    var unit = item.Ladder[u];
                    if (state.Set[unit.World])
                        uLeft = unit.Quantity;
                    else
                        u++;
                }
                var haveU = uLeft > 0;
                var haveS = s < steps.Count;
                if (!haveU && !haveS)
                    break;
                int take;
                if (haveS && (!haveU || steps[s].UnitPrice < item.Ladder[u].Price))
                {
                    take = Math.Min(need, sLeft);
                    cost += (long)take * steps[s].UnitPrice;
                    sLeft -= take;
                    if (sLeft == 0 && ++s < steps.Count)
                        sLeft = steps[s].Quantity;
                }
                else
                {
                    take = Math.Min(need, uLeft);
                    cost += (long)take * item.Ladder[u].Price;
                    uLeft -= take;
                    if (uLeft == 0)
                        u++;
                }
                need -= take;
            }
            return need > 0 ? state.Cost[i] : cost;
        }
    }

    private sealed record ItemData(uint Id, int Target, WorldOffer?[] ByWorld, Unit[] Ladder);

    private readonly record struct Unit(int Price, int Quantity, int World, int Step) : IComparable<Unit>
    {
        public int CompareTo(Unit other)
        {
            var c = Price.CompareTo(other.Price);
            if (c != 0)
                return c;
            c = World.CompareTo(other.World);
            return c != 0 ? c : Step.CompareTo(other.Step);
        }
    }
}
