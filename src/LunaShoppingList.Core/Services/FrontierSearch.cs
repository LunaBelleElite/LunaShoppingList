namespace LunaShoppingList.Core;

/// <summary>
/// The search behind <see cref="PlanFrontier"/>: chooses sets of worlds, and for each set buys every item the cheapest way with whole
/// stacks from those worlds only. Only the choice of the set is heuristic; the purchase for a set is exact (see
/// <see cref="Evaluate"/>).
/// </summary>
/// <remarks>
/// <para>
/// An item's <b>target</b> is <c>min(Needed, units for sale on every world in the book)</c>: the units the cheapest plan covers. A set of
/// worlds is feasible for an item only when it sells at least the target, so no plan ever covers less of an item than the cheapest
/// plan (a short item therefore pins every world that sells it). For a feasible set the item buys the cheapest set of whole stacks
/// from those worlds with at least the target: <see cref="StackCoverSolver.Cover"/> restricted to the set, the same answer, cost and
/// tie-breaks.
/// </para>
/// <para>
/// Speed. Each (item, set) answer is cached under the item's own worlds in the set (a bitmask over the worlds that sell it), so a
/// move that does not touch an item's worlds never re-solves it, and when a world leaves the set an exact cover that did not use it
/// is carried over unsolved (<see cref="Remember"/>). Before the exact search, stacks that cannot be in any cheapest cover
/// are dropped: a stack whose own cost plus the cheapest fractional cover of the rest of the target (every stack in the set,
/// cheapest unit price first, part stacks allowed) is above a known cover's cost (greedy whole stacks, the cheapest single stack
/// that covers, or the caller's hint) cannot be in a cheapest cover, so removing it changes neither the cost nor the stacks chosen.
/// Moves are screened with the same fractional bound before any exact search: a move whose bound cannot beat the best move found
/// so far is skipped.
/// </para>
/// <para>
/// Work is counted in units of roughly one table cell or one stack visited, deterministically (no clock), and every phase stops
/// when its share of the budget is spent (<see cref="Phase"/>); <see cref="Cut"/> then says so.
/// </para>
/// </remarks>
internal sealed class FrontierSearch
{
    /// <summary>A complete plan: its exact whole-stack total, the worlds it visits, and its lines (item id, then world id).</summary>
    internal readonly record struct Candidate(long Total, int Worlds, IReadOnlyList<AllocationLine> Lines);

    private const long Infeasible = long.MaxValue;

    private readonly uint[] _worldIds;
    private readonly Dictionary<uint, int> _worldIndex = new();
    private readonly Item[] _items;
    private readonly Dictionary<uint, int> _itemIndex = new();
    private readonly List<int>[] _itemsByWorld;
    private readonly long _budget;
    private long _work;

    /// <summary>True once a phase stopped early because the work budget was spent.</summary>
    public bool Cut { get; private set; }

    public FrontierSearch(OfferBook book, long budget)
    {
        _budget = budget;
        var raw = new List<(ItemOffers Item, List<WorldOffer> Offers, long Supply)>();
        foreach (var item in book.Items.Where(i => i.Needed > 0 && i.Worlds is { Count: > 0 }).GroupBy(i => i.ItemId).Select(g => g.First()).OrderBy(i => i.ItemId))
        {
            // One offer per world (the first), worlds with at least one stack only.
            var offers = item.Worlds.GroupBy(w => w.WorldId).Select(g => g.First())
                .Where(w => w.Steps.Any(IsStack)).OrderBy(w => w.WorldId).ToList();
            var supply = offers.Sum(w => w.Steps.Where(IsStack).Sum(s => (long)s.Quantity));
            if (supply > 0)
                raw.Add((item, offers, supply));
        }

        _worldIds = raw.SelectMany(r => r.Offers).Select(w => w.WorldId).Distinct().OrderBy(id => id).ToArray();
        for (var w = 0; w < _worldIds.Length; w++)
            _worldIndex[_worldIds[w]] = w;
        _itemsByWorld = _worldIds.Select(_ => new List<int>()).ToArray();

        _items = new Item[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var (item, offers, supply) = raw[i];
            var worlds = offers.Select(o => _worldIndex[o.WorldId]).ToArray();
            var localOf = new int[_worldIds.Length];
            Array.Fill(localOf, -1);
            var stacks = new List<StackKnapsack.Stack>();
            var stackWorld = new List<int>();
            var supplies = new long[offers.Count];
            var worldStart = new int[offers.Count + 1];
            for (var l = 0; l < offers.Count; l++)
            {
                localOf[worlds[l]] = l;
                _itemsByWorld[worlds[l]].Add(i);
                worldStart[l] = stacks.Count;
                foreach (var s in offers[l].Steps.Where(IsStack))
                {
                    stacks.Add(new StackKnapsack.Stack(s.Quantity, s.UnitPrice, s.LastReview.UtcTicks));
                    stackWorld.Add(l);
                    supplies[l] += s.Quantity;
                }
            }
            worldStart[offers.Count] = stacks.Count;
            var stackArray = stacks.ToArray();
            var worldByPrice = new int[offers.Count][];
            for (var l = 0; l < offers.Count; l++)
                worldByPrice[l] = Enumerable.Range(worldStart[l], worldStart[l + 1] - worldStart[l])
                    .OrderBy(k => stackArray[k].UnitPrice).ThenBy(k => k).ToArray();
            _items[i] = new Item
            {
                Id = item.ItemId,
                Needed = item.Needed,
                Target = (int)Math.Min(item.Needed, supply),
                Worlds = worlds,
                LocalOf = localOf,
                Offers = offers.ToArray(),
                Supply = supplies,
                Stacks = stackArray,
                StackWorld = stackWorld.ToArray(),
                ByPrice = Enumerable.Range(0, stackArray.Length).OrderBy(k => stackArray[k].UnitPrice).ThenBy(k => k).ToArray(),
                WorldStart = worldStart,
                WorldByPrice = worldByPrice,
                Words = (offers.Count + 63) / 64,
            };
            _itemIndex[item.ItemId] = i;
        }
    }

    private static bool IsStack(PriceStep s) => s.Quantity > 0 && s.UnitPrice > 0;

    /// <summary>Spends nothing; true while budget is left, otherwise records that the search was cut and returns false.</summary>
    public bool Continue()
    {
        if (_work < _limit)
            return true;
        Cut = true;
        return false;
    }

    private long _limit;

    /// <summary>
    /// Starts a phase that may run until <paramref name="percent"/> of the whole budget has been spent (counting what earlier phases
    /// spent), so one expensive phase cannot starve the ones after it.
    /// </summary>
    public void Phase(int percent) => _limit = percent >= 100 ? _budget : _budget / 100 * percent;

    /// <summary>Fixed work charged per exact solve on top of its stacks and table cells: allocation and setup, measured to cost about as much as a thousand cells.</summary>
    private const long SolveOverhead = 1_000;

    // ---- model ----

    private sealed class Item
    {
        public required uint Id;
        public required int Needed;
        public required int Target;

        /// <summary>Global world index of each world that sells the item, ascending (so ascending world id).</summary>
        public required int[] Worlds;

        /// <summary>Local index of each global world, -1 when the world does not sell the item.</summary>
        public required int[] LocalOf;

        public required WorldOffer[] Offers;
        public required long[] Supply;

        /// <summary>Every stack, ordered by world (ascending id) then position in the ladder: <see cref="StackCoverSolver"/>'s order.</summary>
        public required StackKnapsack.Stack[] Stacks;

        public required int[] StackWorld;

        /// <summary>Stack indices by unit price ascending, then index.</summary>
        public required int[] ByPrice;

        /// <summary>The stacks of local world l are <c>WorldStart[l]..WorldStart[l + 1] - 1</c>.</summary>
        public required int[] WorldStart;

        /// <summary>Per local world, its stack indices by unit price ascending, then index.</summary>
        public required int[][] WorldByPrice;

        public required int Words;
        public readonly Dictionary<MaskKey, Eval> Cache = new();
    }

    /// <summary>An item's purchase for one set of its worlds.</summary>
    private sealed class Eval
    {
        public static readonly Eval None = new(Infeasible, [], []);

        public Eval(long cost, int[] used, int[] units, bool approximate = false)
        {
            Cost = cost;
            Used = used;
            Units = units;
            Approximate = approximate;
        }

        /// <summary>
        /// True when the greedy fallback chose the stacks (past the size guard). Only an exact answer can be carried over to a
        /// smaller set (see <see cref="Remember"/>).
        /// </summary>
        public bool Approximate { get; }

        /// <summary>Gil for the chosen stacks; <see cref="Infeasible"/> when the set cannot reach the target.</summary>
        public long Cost { get; }

        public bool Feasible => Cost != Infeasible;

        /// <summary>Local indices of the worlds with chosen stacks, ascending.</summary>
        public int[] Used { get; }

        /// <summary>Units bought on each world in <see cref="Used"/>: a sum of that world's stacks.</summary>
        public int[] Units { get; }

        public bool Uses(int local) => local >= 0 && Array.IndexOf(Used, local) >= 0;
    }

    private readonly struct MaskKey : IEquatable<MaskKey>
    {
        private readonly int _hash;

        public MaskKey(ulong[] bits)
        {
            Bits = bits;
            var h = new HashCode();
            foreach (var b in bits)
                h.Add(b);
            _hash = h.ToHashCode();
        }

        public ulong[] Bits { get; }

        public bool Contains(int local) => ((Bits[local >> 6] >> (local & 63)) & 1) != 0;

        public bool Equals(MaskKey other) => Bits.AsSpan().SequenceEqual(other.Bits);

        public override bool Equals(object? obj) => obj is MaskKey k && Equals(k);

        public override int GetHashCode() => _hash;
    }

    /// <summary>A set of worlds: <see cref="Set"/> without <see cref="Rem1"/> and <see cref="Rem2"/>, plus <see cref="Add"/> (-1 for none).</summary>
    private readonly record struct Move(bool[] Set, int Add, int Rem1, int Rem2)
    {
        public bool Contains(int w) => w == Add || (Set[w] && w != Rem1 && w != Rem2);

        public static Move Of(bool[] set) => new(set, -1, -1, -1);

        public bool[] Apply()
        {
            var set = (bool[])Set.Clone();
            if (Rem1 >= 0)
                set[Rem1] = false;
            if (Rem2 >= 0)
                set[Rem2] = false;
            if (Add >= 0)
                set[Add] = true;
            return set;
        }
    }

    // ---- one item, one set ----

    /// <summary>
    /// The cheapest whole-stack purchase of item <paramref name="i"/> from the worlds of <paramref name="move"/>, cached per item and
    /// per set of its worlds. <paramref name="hint"/> is the cost of some cover known to lie inside that set (or
    /// <see cref="long.MaxValue"/>); it only speeds the search up and never changes the answer.
    /// </summary>
    private Eval Evaluate(int i, Move move, long hint = long.MaxValue)
    {
        var item = _items[i];
        var (key, supply) = KeyFor(item, move);
        if (item.Cache.TryGetValue(key, out var hit))
            return hit;
        var eval = supply < item.Target ? Eval.None : Solve(item, key, supply, hint);
        item.Cache[key] = eval;
        return eval;
    }

    private (MaskKey Key, long Supply) KeyFor(Item item, Move move)
    {
        var bits = new ulong[item.Words];
        long supply = 0;
        for (var l = 0; l < item.Worlds.Length; l++)
        {
            if (!move.Contains(item.Worlds[l]))
                continue;
            bits[l >> 6] |= 1UL << (l & 63);
            supply += item.Supply[l];
        }
        _work += item.Worlds.Length;
        return (new MaskKey(bits), supply);
    }

    /// <summary>
    /// Carries <paramref name="known"/>, an exact answer for a larger set, over to the set of <paramref name="move"/> when that smaller
    /// set still holds every world <paramref name="known"/> buys on (or <paramref name="known"/> was already infeasible): the cheapest
    /// cover of a set that lies inside a subset is also the subset's cheapest cover, so nothing needs solving. Returns the answer for
    /// the smaller set; an approximate answer, or one that buys on a removed world, is solved afresh.
    /// </summary>
    private Eval Remember(int i, Move move, Eval known)
    {
        var item = _items[i];
        if (known.Approximate)
            return Evaluate(i, move);
        if (known.Feasible)
            foreach (var l in known.Used)
                if (!move.Contains(item.Worlds[l]))
                    return Evaluate(i, move);
        var (key, _) = KeyFor(item, move);
        return item.Cache.TryAdd(key, known) ? known : item.Cache[key];
    }

    private Eval Solve(Item item, MaskKey mask, long supply, long hint)
    {
        _work += SolveOverhead;
        // The set's stacks in priority order: its worlds ascending, each world's stacks in ladder order.
        var inSet = new List<int>();
        var setStacks = new List<StackKnapsack.Stack>();
        for (var l = 0; l < item.Worlds.Length; l++)
        {
            if (!mask.Contains(l))
                continue;
            for (var s = item.WorldStart[l]; s < item.WorldStart[l + 1]; s++)
            {
                inSet.Add(s);
                setStacks.Add(item.Stacks[s]);
            }
        }
        _work += 2L * inSet.Count + item.Worlds.Length;

        if (supply <= item.Target)
            return FromStacks(item, inSet);

        // The same size guard as StackCoverSolver over the same stacks (2026-11-24: stacks that cover the target alone are weighed
        // apart, so only the stacks below it decide the table's size): past it, the same fallback.
        var size = StackKnapsack.ExactSize(setStacks, item.Target);
        if (size.Table && !StackKnapsack.Fits(size.Count, size.Cap))
            return Approximate(item, mask, inSet.Count);

        return FromStacks(item, ExactCover(item, mask, inSet, hint));
    }

    /// <summary>Exact cheapest cover of the target from <paramref name="inSet"/> (stack indices in priority order), after dropping stacks that cannot be in it.</summary>
    private List<int> ExactCover(Item item, MaskKey mask, List<int> inSet, long hint)
    {
        var target = item.Target;
        var m = inSet.Count;
        var cumQ = new long[m + 1];
        var cumC = new long[m + 1];
        var prices = new int[m];
        var bound = hint;
        var k = 0;
        // The set's stacks by unit price: sort them when the set holds few of the item's stacks, filter the item's order otherwise.
        IEnumerable<int> byPrice;
        if (4L * m < item.Stacks.Length)
        {
            var sorted = inSet.ToArray();
            Array.Sort(sorted, (a, b) => item.Stacks[a].UnitPrice != item.Stacks[b].UnitPrice
                ? item.Stacks[a].UnitPrice.CompareTo(item.Stacks[b].UnitPrice)
                : a.CompareTo(b));
            byPrice = sorted;
            _work += 8L * m;
        }
        else
        {
            byPrice = item.ByPrice.Where(s => mask.Contains(item.StackWorld[s]));
            _work += item.Stacks.Length;
        }
        foreach (var s in byPrice)
        {
            var st = item.Stacks[s];
            prices[k] = st.UnitPrice;
            cumQ[k + 1] = cumQ[k] + st.Quantity;
            cumC[k + 1] = cumC[k] + st.Cost;
            k++;
            if (cumQ[k] >= target && cumQ[k - 1] < target)
                bound = Math.Min(bound, cumC[k]); // whole stacks, cheapest unit price first, until the target is reached
            if (st.Quantity >= target)
                bound = Math.Min(bound, st.Cost); // one stack that covers alone
        }

        // Fractional cover of r units: every stack in the set, cheapest unit first, part stacks allowed. A lower bound on any
        // whole-stack cover of r units from the set (with or without a given stack).
        long FractionalRest(long r)
        {
            if (r <= 0)
                return 0;
            int lo = 1, hi = m;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (cumQ[mid] >= r)
                    hi = mid;
                else
                    lo = mid + 1;
            }
            return cumC[lo - 1] + (r - cumQ[lo - 1]) * prices[lo - 1];
        }

        var kept = new List<StackKnapsack.Stack>();
        var keptIndex = new List<int>();
        long keptSupply = 0;
        foreach (var s in inSet)
        {
            var st = item.Stacks[s];
            if (st.Cost + FractionalRest(target - st.Quantity) > bound)
                continue;
            kept.Add(st);
            keptIndex.Add(s);
            keptSupply += st.Quantity;
        }

        var (picked, _) = StackKnapsack.Cover(kept, target);
        _work += kept.Count;
        if (keptSupply > target && StackKnapsack.ExactSize(kept, target) is { Table: true } size)
            _work += size.Count * (size.Cap + 1);
        return picked.Select(p => keptIndex[p]).ToList();
    }

    /// <summary>Past the size guard: <see cref="StackCoverSolver.Cover"/> itself, which takes its greedy fallback.</summary>
    private Eval Approximate(Item item, MaskKey mask, int stackCount)
    {
        var offers = Enumerable.Range(0, item.Worlds.Length).Where(mask.Contains).Select(l => item.Offers[l]);
        var cover = StackCoverSolver.Cover(offers, item.Target);
        _work += 16L * stackCount;
        var used = new int[cover.Worlds.Count];
        var units = new int[cover.Worlds.Count];
        for (var k = 0; k < cover.Worlds.Count; k++)
        {
            used[k] = item.LocalOf[_worldIndex[cover.Worlds[k].WorldId]];
            units[k] = cover.Worlds[k].Units;
        }
        return new Eval(cover.Cost, used, units, approximate: true);
    }

    private static Eval FromStacks(Item item, IReadOnlyList<int> chosen)
    {
        var units = new SortedDictionary<int, long>();
        long cost = 0;
        foreach (var s in chosen)
        {
            var l = item.StackWorld[s];
            units[l] = units.GetValueOrDefault(l) + item.Stacks[s].Quantity;
            cost += item.Stacks[s].Cost;
        }
        return new Eval(cost, units.Keys.ToArray(), units.Values.Select(u => (int)Math.Min(u, int.MaxValue)).ToArray());
    }

    /// <summary>Cheapest fractional cover of item <paramref name="i"/>'s target from <paramref name="move"/>: a lower bound on its exact cost there; <see cref="Infeasible"/> when the set is short.</summary>
    private long Fractional(int i, Move move)
    {
        var item = _items[i];
        long need = item.Target;
        long cost = 0;
        var visited = 0;
        foreach (var s in item.ByPrice)
        {
            visited++;
            if (!move.Contains(item.Worlds[item.StackWorld[s]]))
                continue;
            var st = item.Stacks[s];
            if (st.Quantity >= need)
            {
                cost += need * st.UnitPrice;
                need = 0;
                break;
            }
            cost += st.Cost;
            need -= st.Quantity;
        }
        _work += visited;
        return need > 0 ? Infeasible : cost;
    }

    // ---- plans ----

    private sealed class State
    {
        public required bool[] Set;
        public required Eval[] Evals;

        /// <summary>Per item, its stacks on the set's worlds by unit price (null until first needed; cleared when the item's worlds change).</summary>
        public required int[]?[] Sorted;
    }

    /// <summary>
    /// Every item's purchase for exactly the worlds of <paramref name="set"/>; null when an item cannot be supplied there, or (when
    /// <paramref name="bounded"/>) when the budget runs out first (checked before each item, so at most one solve past it).
    /// </summary>
    private State? Start(bool[] set, bool bounded = true)
    {
        var copy = (bool[])set.Clone();
        var evals = new Eval[_items.Length];
        for (var i = 0; i < _items.Length; i++)
        {
            if (bounded && !Continue())
                return null;
            evals[i] = Evaluate(i, Move.Of(copy));
            if (!evals[i].Feasible)
                return null;
        }
        return new State { Set = copy, Evals = evals, Sorted = new int[]?[_items.Length] };
    }

    /// <summary>Clears the sorted stacks of every item that world <paramref name="w"/> sells (its set changed).</summary>
    private void Changed(State state, int w)
    {
        foreach (var i in _itemsByWorld[w])
            state.Sorted[i] = null;
    }

    private int[] SortedIn(State state, int i)
    {
        if (state.Sorted[i] is { } done)
            return done;
        var item = _items[i];
        var list = new List<int>();
        foreach (var s in item.ByPrice)
            if (state.Set[item.Worlds[item.StackWorld[s]]])
                list.Add(s);
        _work += item.Stacks.Length;
        return state.Sorted[i] = list.ToArray();
    }

    /// <summary>
    /// <see cref="Fractional(int, Move)"/> for a move made from <paramref name="state"/>'s set: the set's own stacks (already sorted)
    /// merged with the added world's, skipping the removed worlds'. Cheap: it stops as soon as the target is reached.
    /// </summary>
    private long Fractional(State state, int i, Move move)
    {
        var item = _items[i];
        var mine = SortedIn(state, i);
        var addLocal = move.Add >= 0 && !state.Set[move.Add] ? item.LocalOf[move.Add] : -1;
        var theirs = addLocal >= 0 ? item.WorldByPrice[addLocal] : [];
        int a = 0, b = 0, visited = 0;
        long need = item.Target, cost = 0;
        while (need > 0)
        {
            while (a < mine.Length)
            {
                var g = item.Worlds[item.StackWorld[mine[a]]];
                if (g != move.Rem1 && g != move.Rem2)
                    break;
                a++;
                visited++;
            }
            int s;
            if (a < mine.Length && (b >= theirs.Length || item.Stacks[mine[a]].UnitPrice <= item.Stacks[theirs[b]].UnitPrice))
                s = mine[a++];
            else if (b < theirs.Length)
                s = theirs[b++];
            else
                break;
            visited++;
            var st = item.Stacks[s];
            var take = Math.Min(need, st.Quantity);
            cost += take * st.UnitPrice;
            need -= take;
        }
        _work += visited;
        return need > 0 ? Infeasible : cost;
    }

    private Candidate ToCandidate(State state)
    {
        var lines = new List<AllocationLine>();
        var used = new HashSet<int>();
        long total = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            var e = state.Evals[i];
            total += e.Cost;
            for (var k = 0; k < e.Used.Length; k++)
            {
                var w = item.Worlds[e.Used[k]];
                used.Add(w);
                lines.Add(new AllocationLine(item.Id, _worldIds[w], e.Units[k]));
            }
        }
        return new Candidate(total, used.Count, lines);
    }

    private bool[] UsedWorlds(State state)
    {
        var used = new bool[_worldIds.Length];
        for (var i = 0; i < _items.Length; i++)
            foreach (var l in state.Evals[i].Used)
                used[_items[i].Worlds[l]] = true;
        return used;
    }

    /// <summary>The worlds of <paramref name="lines"/> (worlds unknown to the book are ignored).</summary>
    public bool[] WorldSet(IReadOnlyList<AllocationLine> lines)
    {
        var set = new bool[_worldIds.Length];
        foreach (var line in lines)
            if (_worldIndex.TryGetValue(line.WorldId, out var w))
                set[w] = true;
        return set;
    }

    /// <summary>
    /// <paramref name="lines"/> as a candidate, each line at its world's exact whole-stack cost; null when a line names an unknown
    /// item or world, cannot be priced at its own quantity, or an item is covered less (capped at its need) than its target.
    /// </summary>
    /// <remarks>
    /// A line above its world's <see cref="WorldOffer.ExactTotalsLimit"/> (not the full supply) has no exact cost. Since 2026-11-24 it
    /// is priced the way <see cref="AllocationState"/> would store it (<see cref="LinePricer.Price"/>) and accepted when that keeps
    /// its quantity, so a plan whose lines the allocation would re-price differently is never offered as is. A line from an exact
    /// cover always keeps its quantity there: the part of a cheapest cover bought on one world is a cheapest cover of that many
    /// units on that world alone.
    /// </remarks>
    public Candidate? Price(IReadOnlyList<AllocationLine> lines)
    {
        long total = 0;
        var bought = new long[_items.Length];
        var worlds = new HashSet<uint>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || !_itemIndex.TryGetValue(line.ItemId, out var i) || !_worldIndex.TryGetValue(line.WorldId, out var w))
                return null;
            var local = _items[i].LocalOf[w];
            if (local < 0)
                return null;
            var offer = _items[i].Offers[local];
            long cost;
            if (offer.ExactCost(line.Quantity) is { } exact)
                cost = exact;
            else if (LinePricer.Price(offer, line.Quantity) is { } priced && priced.Quantity == line.Quantity)
                cost = priced.Cost;
            else
                return null;
            total += cost;
            bought[i] += line.Quantity;
            worlds.Add(line.WorldId);
        }
        for (var i = 0; i < _items.Length; i++)
            if (Math.Min(bought[i], _items[i].Needed) != _items[i].Target)
                return null;
        return new Candidate(total, worlds.Count, lines);
    }

    /// <summary>
    /// Records the plan for exactly the worlds of <paramref name="set"/>, whatever the budget: the fallback for a fixed plan whose lines
    /// <see cref="Price"/> rejects, so the options are never empty. At most one solve per item.
    /// </summary>
    public void Fixed(bool[] set, Action<Candidate> record)
    {
        if (Start(set, bounded: false) is { } state)
            record(ToCandidate(state));
    }

    // ---- paths ----

    /// <summary>From the worlds in <paramref name="start"/>, repeatedly drops the world whose removal raises the total least.</summary>
    public void Backward(bool[] start, Action<Candidate> record)
    {
        if (Start(start) is not { } state)
            return;
        record(ToCandidate(state));

        // removal[i][w]: item i's purchase if world w, which it currently buys on, were dropped.
        var removal = new Dictionary<int, Eval>[_items.Length];
        for (var i = 0; i < _items.Length; i++)
        {
            if (!Continue())
                return;
            removal[i] = RemovalEvals(state, i, null, -1);
        }

        for (var step = 0; step < _worldIds.Length; step++)
        {
            if (!Continue())
                return;
            var delta = new long[_worldIds.Length];
            var blocked = new bool[_worldIds.Length];
            for (var i = 0; i < _items.Length; i++)
                foreach (var (w, e) in removal[i])
                {
                    if (!e.Feasible)
                        blocked[w] = true;
                    else
                        delta[w] += e.Cost - state.Evals[i].Cost;
                }

            var drop = -1;
            for (var w = 0; w < _worldIds.Length; w++)
                if (state.Set[w] && !blocked[w] && (drop < 0 || delta[w] < delta[drop]))
                    drop = w;
            if (drop < 0)
                return;

            // Every item the dropped world sells is affected: those bought there take their removal answer, and for the others the
            // answers for dropping each of their worlds may have changed (those that bought on the dropped world are re-solved).
            // The new plan is recorded before the removal answers are refreshed (each item's depend only on its own purchase), so a
            // budget that runs out during the refresh still keeps the step.
            state.Set[drop] = false;
            Changed(state, drop);
            var affected = _itemsByWorld[drop];
            var boughtThere = new bool[affected.Count];
            for (var k = 0; k < affected.Count; k++)
            {
                var i = affected[k];
                if (state.Evals[i].Uses(_items[i].LocalOf[drop]))
                {
                    boughtThere[k] = true;
                    state.Evals[i] = removal[i][drop];
                }
                else
                    state.Evals[i] = Remember(i, Move.Of(state.Set), state.Evals[i]);
            }
            record(ToCandidate(state));
            for (var k = 0; k < affected.Count; k++)
            {
                if (!Continue())
                    return;
                var i = affected[k];
                removal[i] = boughtThere[k]
                    ? RemovalEvals(state, i, null, -1)
                    : RemovalEvals(state, i, removal[i], _items[i].LocalOf[drop]);
            }
        }
    }

    /// <summary>
    /// Item <paramref name="i"/>'s purchase for each world it buys on, if that world were dropped from the state's set. Answers in
    /// <paramref name="previous"/> (for the set before <paramref name="droppedLocal"/> was dropped) are carried over when they do not
    /// buy on the dropped world.
    /// </summary>
    private Dictionary<int, Eval> RemovalEvals(State state, int i, Dictionary<int, Eval>? previous, int droppedLocal)
    {
        var item = _items[i];
        var result = new Dictionary<int, Eval>();
        foreach (var l in state.Evals[i].Used)
        {
            var w = item.Worlds[l];
            var move = new Move(state.Set, -1, w, -1);
            result[w] = previous is not null && previous.TryGetValue(w, out var old) && !old.Uses(droppedLocal)
                ? Remember(i, move, old)
                : Evaluate(i, move);
        }
        return result;
    }

    /// <summary>
    /// From the worlds in <paramref name="start"/>, repeatedly adds the outside world that lowers the total most (ties: lowest id),
    /// until the plan visits <paramref name="maxWorlds"/> worlds or no world lowers it.
    /// </summary>
    public void Forward(bool[] start, int maxWorlds, Action<Candidate> record)
    {
        if (Start(start) is not { } state)
            return;
        record(ToCandidate(state));

        for (var step = 0; step < _worldIds.Length; step++)
        {
            if (UsedWorlds(state).Count(u => u) >= maxWorlds)
                return;
            if (!Continue())
                return;

            // Screen: an upper bound on each world's gain from the fractional lower bound on the items' new costs.
            var screened = new List<(long Bound, int World)>();
            for (var w = 0; w < _worldIds.Length; w++)
            {
                if (state.Set[w])
                    continue;
                if (!Continue())
                    return;
                var move = new Move(state.Set, w, -1, -1);
                long bound = 0;
                foreach (var i in _itemsByWorld[w])
                    bound += state.Evals[i].Cost - Fractional(state, i, move);
                if (bound > 0)
                    screened.Add((bound, w));
            }
            screened.Sort((a, b) => a.Bound != b.Bound ? b.Bound.CompareTo(a.Bound) : a.World.CompareTo(b.World));

            // When the budget runs out part way, the best world found so far is still added (its answers are cached), then the phase ends.
            var best = -1;
            long bestGain = 0;
            var cut = false;
            foreach (var (bound, w) in screened)
            {
                if (bound < bestGain)
                    break;
                if (bound == bestGain && best >= 0 && w > best)
                    continue;
                if (!Continue())
                {
                    cut = true;
                    break;
                }
                var move = new Move(state.Set, w, -1, -1);
                long gain = 0;
                foreach (var i in _itemsByWorld[w])
                    gain += state.Evals[i].Cost - Evaluate(i, move, state.Evals[i].Cost).Cost;
                if (gain > bestGain || (gain == bestGain && gain > 0 && w < best))
                {
                    best = w;
                    bestGain = gain;
                }
            }
            if (best < 0)
                return;

            state.Set[best] = true;
            Changed(state, best);
            foreach (var i in _itemsByWorld[best])
                state.Evals[i] = Evaluate(i, Move.Of(state.Set), state.Evals[i].Cost);
            record(ToCandidate(state));
            if (cut)
                return;
        }
    }

    /// <summary>A book with at most this many worlds gets every set of worlds tried (<see cref="Exhaustive"/>): 2^10 = 1,024 sets.</summary>
    public const int ExhaustiveWorldLimit = 10;

    /// <summary>Number of worlds that sell at least one needed item.</summary>
    public int WorldCount => _worldIds.Length;

    /// <summary>
    /// Tries every set of worlds, smallest first, and records each plan that is the cheapest so far with at most its number of
    /// worlds. <paramref name="bestAtMost"/>[k] is the lowest total known with at most k worlds (<see cref="long.MaxValue"/> for
    /// none); a set whose fractional lower bound is above it is skipped without solving. Only for books with at most
    /// <see cref="ExhaustiveWorldLimit"/> worlds. Returns true when every set was tried (the options are then exact, item covers
    /// past the size guard aside), false when the budget ran out first.
    /// </summary>
    public bool Exhaustive(long[] bestAtMost, Action<Candidate> record)
    {
        var n = _worldIds.Length;
        if (n > ExhaustiveWorldLimit)
            throw new InvalidOperationException("too many worlds for an exhaustive search");
        for (var k = 1; k <= n; k++)
            for (var mask = 1; mask < 1 << n; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) != k)
                    continue;
                if (!Continue())
                    return false;
                var set = new bool[n];
                for (var w = 0; w < n; w++)
                    set[w] = (mask & (1 << w)) != 0;

                long bound = 0;
                for (var i = 0; i < _items.Length && bound != Infeasible; i++)
                {
                    var f = Fractional(i, Move.Of(set));
                    bound = f == Infeasible ? Infeasible : bound + f;
                }
                if (bound == Infeasible || bound > bestAtMost[k] || Start(set) is not { } state)
                    continue;

                // A plan that leaves a world of the set unused is the plan of a smaller set, which is tried on its own.
                var candidate = ToCandidate(state);
                if (candidate.Worlds != k || candidate.Total > bestAtMost[k])
                    continue;
                record(candidate);
                for (var j = k; j <= n; j++)
                    bestAtMost[j] = Math.Min(bestAtMost[j], candidate.Total);
            }
        return true;
    }

    /// <summary>Items a move can change: those that sell the added world, and those that buy on a removed one.</summary>
    private List<int> Affected(State state, Move move)
    {
        var mark = new bool[_items.Length];
        if (move.Add >= 0)
            foreach (var i in _itemsByWorld[move.Add])
                mark[i] = true;
        foreach (var r in new[] { move.Rem1, move.Rem2 })
            if (r >= 0)
                foreach (var i in _itemsByWorld[r])
                    if (state.Evals[i].Uses(_items[i].LocalOf[r]))
                        mark[i] = true;
        var list = new List<int>();
        for (var i = 0; i < mark.Length; i++)
            if (mark[i])
                list.Add(i);
        return list;
    }

    private bool UsesRemoved(State state, int i, Move move)
        => (move.Rem1 >= 0 && state.Evals[i].Uses(_items[i].LocalOf[move.Rem1]))
           || (move.Rem2 >= 0 && state.Evals[i].Uses(_items[i].LocalOf[move.Rem2]));

    /// <summary>A lower bound on the move's change in total; null when some item cannot be supplied after it.</summary>
    private long? LowerDelta(State state, Move move)
    {
        long delta = 0;
        foreach (var i in Affected(state, move))
        {
            var bound = Fractional(state, i, move);
            if (bound == Infeasible)
                return null;
            var current = state.Evals[i].Cost;
            // An item that does not sell the added world only loses worlds: it cannot get cheaper.
            if (move.Add < 0 || _items[i].LocalOf[move.Add] < 0)
                bound = Math.Max(bound, current);
            delta += bound - current;
        }
        return delta;
    }

    /// <summary>The move's exact change in total; null when some item cannot be supplied after it.</summary>
    private long? ExactDelta(State state, Move move)
    {
        long delta = 0;
        foreach (var i in Affected(state, move))
        {
            var current = state.Evals[i].Cost;
            var e = Evaluate(i, move, UsesRemoved(state, i, move) ? long.MaxValue : current);
            if (!e.Feasible)
                return null;
            delta += e.Cost - current;
        }
        return delta;
    }

    private State Apply(State state, Move move)
    {
        var set = move.Apply();
        var evals = (Eval[])state.Evals.Clone();
        foreach (var i in Affected(state, move))
            evals[i] = Evaluate(i, Move.Of(set), UsesRemoved(state, i, move) ? long.MaxValue : state.Evals[i].Cost);
        var next = new State { Set = set, Evals = evals, Sorted = (int[]?[])state.Sorted.Clone() };
        foreach (var w in new[] { move.Add, move.Rem1, move.Rem2 })
            if (w >= 0)
                Changed(next, w);
        return Prune(next);
    }

    /// <summary>Drops worlds that supply nothing; every item's cost stays the same.</summary>
    private State Prune(State state)
    {
        var used = UsedWorlds(state);
        var dropped = Enumerable.Range(0, used.Length).Where(w => state.Set[w] && !used[w]).ToList();
        if (dropped.Count == 0)
            return state;
        var set = (bool[])state.Set.Clone();
        foreach (var w in dropped)
            set[w] = false;
        var evals = (Eval[])state.Evals.Clone();
        foreach (var i in dropped.SelectMany(w => _itemsByWorld[w]).Distinct().Order())
            evals[i] = Remember(i, Move.Of(set), state.Evals[i]);
        var next = new State { Set = set, Evals = evals, Sorted = (int[]?[])state.Sorted.Clone() };
        foreach (var w in dropped)
            Changed(next, w);
        return next;
    }

    /// <summary>True when world <paramref name="v"/> sells at least one item bought on <paramref name="a"/> or <paramref name="b"/>.</summary>
    private bool CanReplace(State state, int a, int b, int v)
    {
        _work += _itemsByWorld[v].Count; // charged in full, even when the scan stops early: an upper bound, and cheap to count
        foreach (var i in _itemsByWorld[v])
        {
            var item = _items[i];
            if (state.Evals[i].Uses(item.LocalOf[a]) || (b >= 0 && state.Evals[i].Uses(item.LocalOf[b])))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces two worlds of <paramref name="start"/> with one outside world, choosing the cheapest such move that keeps every item
    /// supplied (ties: the first pair, then outside world, by id); returns the new set, or null when none exists or the budget ran out.
    /// </summary>
    public bool[]? Shrink(bool[] start, Action<Candidate> record)
    {
        if (Start(start) is not { } state)
            return null;
        state = Prune(state);
        var inside = Enumerable.Range(0, _worldIds.Length).Where(w => state.Set[w]).ToArray();

        var moves = new List<(long Bound, int A, int B, int V)>();
        for (var a = 0; a < inside.Length; a++)
            for (var b = a + 1; b < inside.Length; b++)
                for (var v = 0; v < _worldIds.Length; v++)
                {
                    if (!Continue())
                        return null;
                    if (state.Set[v] || !CanReplace(state, inside[a], inside[b], v))
                        continue;
                    if (LowerDelta(state, new Move(state.Set, v, inside[a], inside[b])) is { } bound)
                        moves.Add((bound, inside[a], inside[b], v));
                }
        moves.Sort();

        (long Delta, int A, int B, int V)? best = null;
        foreach (var m in moves)
        {
            if (best is { } b0 && m.Bound > b0.Delta)
                break;
            if (!Continue())
                return null;
            if (ExactDelta(state, new Move(state.Set, m.V, m.A, m.B)) is not { } delta)
                continue;
            if (best is not { } b1 || delta < b1.Delta || (delta == b1.Delta && (m.A, m.B, m.V).CompareTo((b1.A, b1.B, b1.V)) < 0))
                best = (delta, m.A, m.B, m.V);
        }
        if (best is not { } chosen)
            return null;

        state = Apply(state, new Move(state.Set, chosen.V, chosen.A, chosen.B));
        record(ToCandidate(state));
        return (bool[])state.Set.Clone();
    }

    /// <summary>Swaps one world of <paramref name="start"/> for one outside world while that lowers the total (first improvement, by id).</summary>
    public void Swap(bool[] start, Action<Candidate> record)
    {
        if (Start(start) is not { } state)
            return;
        state = Prune(state);
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var w = 0; w < _worldIds.Length && !improved; w++)
            {
                if (!state.Set[w])
                    continue;
                for (var v = 0; v < _worldIds.Length && !improved; v++)
                {
                    if (!Continue())
                        return;
                    if (state.Set[v] || !CanReplace(state, w, -1, v))
                        continue;
                    var move = new Move(state.Set, v, w, -1);
                    if (LowerDelta(state, move) is not < 0)
                        continue;
                    if (ExactDelta(state, move) is < 0)
                    {
                        state = Apply(state, move);
                        record(ToCandidate(state));
                        improved = true;
                    }
                }
            }
        }
    }
}
