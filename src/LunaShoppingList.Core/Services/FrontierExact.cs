namespace LunaShoppingList.Core;

/// <summary>
/// The exact world-set search (branch and bound) for books with more than <see cref="ExhaustiveWorldLimit"/> worlds. Proves the cheapest
/// total with at most k worlds for k = 1, 2, ... until the budget runs out. Every pruning rule is listed with its proof next to it; whole
/// stacks are not convex, so none of them relies on convexity (the exact cost of a set is only monotone in the set, which the exactness
/// gate guarantees by keeping every item inside the exact solver's size guard).
/// </summary>
internal sealed partial class FrontierSearch
{
    /// <summary>Counted work spent so far (see the class remarks of <see cref="FrontierSearch"/>).</summary>
    internal long Work => _work;

    /// <summary>The most worlds the exact phase handles: its sets are <see cref="ulong"/> masks.</summary>
    internal const int ExactWorldLimit = 64;

    /// <summary>Per item: the highest unit price over all its stacks (M). Shortfall units are priced at it in the cardinality bounds.</summary>
    private long[] _m = [];

    private long[] _best = [];
    private int _k;
    private bool _stop;
    private bool _approximate;

    /// <summary>Per-item data of one search node: stacks of the included worlds by price, f_M, supply, and the f_M gain of each candidate world.</summary>
    private sealed class NodeState
    {
        public required int[][] Sorted;
        public required long[] A;
        public required long[] Sup;
        public required long[][] G;
    }

    // Speed rules R7 to R12 (task exact-t7, 2026-11-25). None uses convexity; each rests on R1 (exact covers are monotone: a cover from a
    // set S' inside S is a cover from S, so exact(S) <= exact(S')), on R2 (the fractional cover is a relaxation), or on nothing at all
    // because it only changes WHICH valid bound is computed or in what order. Let S = included + L[j..] be a node's superset at step j and
    // S' = S minus the world that leaves at step j + 1 (S' inside S); every completion still ahead at step j is a subset of S.
    // - R7a. Keep c[i] = max(frac(S'), old c[i]) when a world leaves. Proof: old c[i] <= exact(S) (it was a fractional or exact bound over
    //   S, R2), and exact(S) <= exact(S') (R1), so old c[i] is still a lower bound over S', hence for every completion.
    // - R7b. If item i's exact cover over S does not buy on the world that left, it is also the exact cover over S' and needs no new solve.
    //   Proof: that cover lies inside S', so exact(S') <= its cost = exact(S); and exact(S) <= exact(S') by R1. The same argument as
    //   Remember. An infeasible exact(S) makes every subset infeasible, so it is kept too.
    // - R8. A child (included + u, candidates L[j+1..]) starts from a copy of its parent's bounds at step j. Proof: its superset
    //   included + u + L[j+1..] equals the parent's S at step j (u = L[j]), so every bound over that set is a bound over the child's set.
    //   The copy matters: the parent keeps stepping, and its bounds over smaller sets must not flow back into the child's, nor the child's
    //   into the parent's (bounds over a subset are not bounds over the superset).
    // - R9. A one-world leaf I + u (u in L[j..]) adds item i with a lower bound max(f_M bound, c[i]). Proof: c[i] <= exact(S) in every
    //   form it takes (fractional over S by R2, exact over S, or carried from a larger set by R7a), and I + u lies inside S, so
    //   exact(I + u) >= exact(S) >= c[i] by R1. The leaf's cut-off only needs a lower bound for the items not yet added.
    // - R10. Upgrades are skipped (or stopped) once s1 + potential < best[k], where potential sums max(0, Ub[i] - per[i]) over the items
    //   not yet upgraded and Ub[i] is a real whole-stack cover from S. Proof: an upgrade sets per[i] to max(per[i], exact(S)) and
    //   exact(S) <= Ub[i], so after all of them s1 is at most s1 + potential; the shared bound S2 is already below best[k] (else the node
    //   was pruned before this point), so max(s1, S2) would stay below best[k] and no upgrade could prune. Skipping a bound that cannot
    //   prune never changes an answer; the items with the largest potential are upgraded first (an order, which cannot change an answer).
    // - R11. A leaf reuses an item's exact cover over S (or over a set between S and the leaf) when it buys only inside the leaf's set:
    //   R7b's argument again. Cached under the leaf's set through Remember.
    // - R12. A one-world leaf adds the items that do not sell u first. The total is a sum and does not depend on the order; only where
    //   the cut-off stops does, and those items were already solved for I (cache hits), so the cut-off can stop before the new solves.

    /// <summary>
    /// Per item, over the included worlds plus the current suffix (a superset of every completion still ahead): a lower bound on the
    /// exact cover (<see cref="C"/>), the greedy whole-stack cover (<see cref="Ub"/>, a real cover there, so at least the exact cost), and
    /// the exact cover itself once it was solved (<see cref="Exact"/>, null until then). A child node starts from a copy of its parent's,
    /// because its superset (included + u + L[j+1..]) is the parent's at step j (rule R8).
    /// </summary>
    private sealed class SupersetBounds
    {
        public required long[] C;
        public required long[] Ub;
        public required Eval?[] Exact;

        public SupersetBounds Copy() => new() { C = (long[])C.Clone(), Ub = (long[])Ub.Clone(), Exact = (Eval?[])Exact.Clone() };
    }

    /// <summary>
    /// Proves <paramref name="bestAtMost"/>[k] (the lowest total with at most k worlds, <see cref="long.MaxValue"/> for none, n + 1 entries,
    /// seeded with the caller's candidates and lowered in place) for k = 1, 2, ... in turn, within the budget (<see cref="Phase"/>
    /// at 100 percent). Every plan that strictly improves a world count is passed to <paramref name="record"/>. Returns the largest k
    /// such that every world count up to it is proven, n when all are; 0 when the book has more than <see cref="ExactWorldLimit"/>
    /// worlds, when some item can pass the size guard (an approximate cover is not monotone in the world set), or when nothing was
    /// proven before the budget ran out. <paramref name="exactWorkBudget"/> is the phase's own budget in counted work units, on top of
    /// whatever was already spent (<see cref="PhaseAbsolute"/>), so the heuristic shares are unaffected; what the phase spends is then
    /// not charged to the phases that follow it. Null: the phase may run until the whole constructor budget is spent.
    /// </summary>
    public int ExactFrontier(long[] bestAtMost, Action<Candidate> record, long? exactWorkBudget = null)
    {
        var start = _work;
        try
        {
            return ExactFrontierCore(bestAtMost, record, exactWorkBudget);
        }
        finally
        {
            if (exactWorkBudget is not null)
                _exactSpent += _work - start;
        }
    }

    private int ExactFrontierCore(long[] bestAtMost, Action<Candidate> record, long? exactWorkBudget)
    {
        var n = _worldIds.Length;
        if (n == 0 || n > ExactWorldLimit || _items.Length == 0)
            return 0;
        if (!AllItemsFitTheGuard())
            return 0;

        _best = bestAtMost;
        _stop = false;
        _approximate = false;
        if (exactWorkBudget is { } own)
            PhaseAbsolute(_work + own);
        else
            Phase(100);
        _m = _items.Select(it => (long)it.Stacks.Max(s => s.UnitPrice)).ToArray();

        // R1 (monotone): the cost over every world is the global minimum, so a world count that reaches it is proven, and so is every larger one.
        var all = n == 64 ? ulong.MaxValue : (1UL << n) - 1;
        var costAll = ExactTotal(all, long.MaxValue, out var usedAll);
        if (_stop)
            return 0;
        for (var k = usedAll; k <= n; k++)
            _best[k] = Math.Min(_best[k], costAll);

        // R6 (pinned worlds): a world whose removal leaves an item below its target is in every feasible set.
        ulong pinned = 0;
        foreach (var item in _items)
        {
            long total = 0;
            foreach (var s in item.Supply)
                total += s;
            for (var l = 0; l < item.Worlds.Length; l++)
                if (total - item.Supply[l] < item.Target)
                    pinned |= 1UL << item.Worlds[l];
        }

        var pinnedCount = System.Numerics.BitOperations.PopCount(pinned);
        var free = new List<int>();
        for (var w = 0; w < n; w++)
            if (((pinned >> w) & 1) == 0)
                free.Add(w);
        var freeMask = free.Aggregate(0UL, (acc, w) => acc | (1UL << w));
        var root = RootState(pinned, freeMask);

        var proven = 0;
        for (var k = 1; k <= n; k++)
        {
            if (_best[k - 1] <= costAll)
            {
                proven = n; // every larger k is the global minimum
                break;
            }
            _best[k] = Math.Min(_best[k], _best[k - 1]); // R4: sets with fewer worlds are proven, so only exactly k can still improve
            if (k < pinnedCount || _best[k] <= costAll)
            {
                proven = k; // fewer worlds than the pinned ones have no feasible set; a total at the global minimum cannot improve
                continue;
            }
            _k = k;
            var ok = k == pinnedCount
                ? Leaf(pinned, null, record)
                : Node(pinned, free.ToArray(), k - pinnedCount, root, record);
            if (!ok)
                break;
            proven = k;
        }
        return _approximate ? 0 : proven;
    }

    /// <summary>
    /// 6.8: true when every set of worlds is inside the exact solver's size guard for every item. <see cref="StackKnapsack.Fits"/> is
    /// monotone in both arguments, a subset has no more stacks (pruned or not) than the item has and no larger cap, so an item that fits
    /// over all its worlds fits over every subset, and then every cover in the phase is exact (R1 holds). An item whose target equals its
    /// whole supply never builds a table, and is fine.
    /// </summary>
    private bool AllItemsFitTheGuard()
    {
        foreach (var item in _items)
        {
            _work += item.Stacks.Length;
            long supply = 0, smallSupply = 0;
            var maxSmall = 0;
            foreach (var s in item.Stacks)
            {
                supply += s.Quantity;
                if (s.Quantity < item.Target)
                {
                    smallSupply += s.Quantity;
                    maxSmall = Math.Max(maxSmall, s.Quantity);
                }
            }
            if (supply <= item.Target || smallSupply < item.Target)
                continue;
            var cap = Math.Min(supply, item.Target + maxSmall - 1);
            if (!StackKnapsack.Fits(item.Stacks.Length, cap))
                return false;
        }
        return true;
    }

    /// <summary>The state with the pinned worlds included, with gains for every free world.</summary>
    private NodeState RootState(ulong pinned, ulong freeMask)
    {
        var count = _items.Length;
        var state = new NodeState
        {
            Sorted = _items.Select(_ => Array.Empty<int>()).ToArray(),
            A = new long[count],
            Sup = new long[count],
            G = new long[count][],
        };
        for (var i = 0; i < count; i++)
        {
            var item = _items[i];
            state.A[i] = item.Target * _m[i];
            state.G[i] = new long[item.Worlds.Length];
            for (var l = 0; l < item.Worlds.Length; l++)
                if (((freeMask >> item.Worlds[l]) & 1) != 0)
                    state.G[i][l] = state.A[i] - FM(i, state.Sorted[i], item.WorldByPrice[l]);
        }
        for (var w = 0; w < _worldIds.Length; w++)
            if (((pinned >> w) & 1) != 0)
                state = Include(state, w, freeMask);
        return state;
    }

    /// <summary>The state after including world <paramref name="u"/>; gains are recomputed only for items that sell it, and only for worlds in <paramref name="candidates"/>.</summary>
    private NodeState Include(NodeState parent, int u, ulong candidates)
    {
        var count = _items.Length;
        var child = new NodeState
        {
            Sorted = (int[][])parent.Sorted.Clone(),
            A = (long[])parent.A.Clone(),
            Sup = (long[])parent.Sup.Clone(),
            G = (long[][])parent.G.Clone(),
        };
        _work += count; // the copied state
        foreach (var i in _itemsByWorld[u])
        {
            var item = _items[i];
            var lu = item.LocalOf[u];
            child.Sorted[i] = Merge(i, parent.Sorted[i], item.WorldByPrice[lu]);
            child.A[i] = FM(i, child.Sorted[i], []);
            child.Sup[i] = parent.Sup[i] + item.Supply[lu];
            var ng = new long[item.Worlds.Length];
            for (var l = 0; l < item.Worlds.Length; l++)
                if (((candidates >> item.Worlds[l]) & 1) != 0)
                    ng[l] = child.A[i] - FM(i, child.Sorted[i], item.WorldByPrice[l]);
            child.G[i] = ng;
        }
        return child;
    }

    /// <summary>
    /// R2: the fractional cover of the target with every shortfall unit priced at M (the item's highest unit price): the T cheapest units of
    /// <paramref name="mine"/> (the included worlds' stacks) plus <paramref name="theirs"/> (a candidate world's), both sorted by price.
    /// At most the whole-stack cost when the set is feasible (every real unit costs at most M), and finite when it is not.
    /// </summary>
    private long FM(int i, int[] mine, int[] theirs)
    {
        var item = _items[i];
        long need = item.Target, cost = 0;
        int a = 0, b = 0, visited = 0;
        while (need > 0)
        {
            int s;
            if (a < mine.Length && (b >= theirs.Length || item.Stacks[mine[a]].UnitPrice <= item.Stacks[theirs[b]].UnitPrice))
                s = mine[a++];
            else if (b < theirs.Length)
                s = theirs[b++];
            else
                break;
            visited++;
            var st = item.Stacks[s];
            if (st.UnitPrice >= _m[i])
                break;
            var take = Math.Min(need, st.Quantity);
            cost += take * st.UnitPrice;
            need -= take;
        }
        _work += visited + 1;
        return cost + need * _m[i];
    }

    private int[] Merge(int i, int[] mine, int[] theirs)
    {
        var item = _items[i];
        var r = new int[mine.Length + theirs.Length];
        int a = 0, b = 0, k = 0;
        while (a < mine.Length || b < theirs.Length)
            r[k++] = a < mine.Length && (b >= theirs.Length || item.Stacks[mine[a]].UnitPrice <= item.Stacks[theirs[b]].UnitPrice)
                ? mine[a++]
                : theirs[b++];
        _work += r.Length;
        return r;
    }

    /// <summary>
    /// Plain fractional cover of item <paramref name="i"/> from the worlds in <paramref name="mask"/>: a lower bound on its exact cost there
    /// (R2); <see cref="Infeasible"/> when short. <paramref name="whole"/> is the same walk with every stack it touches bought whole (cheapest
    /// unit price first until the target is met): a real cover from the set, so at least the exact cost there (used by R10).
    /// </summary>
    private long FracMask(int i, ulong mask, out long whole)
    {
        var item = _items[i];
        long need = item.Target, cost = 0;
        whole = 0;
        var visited = 0;
        foreach (var s in item.ByPrice)
        {
            visited++;
            if (((mask >> item.Worlds[item.StackWorld[s]]) & 1) == 0)
                continue;
            var st = item.Stacks[s];
            var take = Math.Min(need, st.Quantity);
            cost += take * st.UnitPrice;
            whole += st.Cost;
            need -= take;
            if (need == 0)
                break;
        }
        _work += visited;
        if (need > 0)
        {
            whole = Infeasible;
            return Infeasible;
        }
        return cost;
    }

    private Eval ExactItem(int i, ulong mask)
    {
        var set = new bool[_worldIds.Length];
        foreach (var w in _items[i].Worlds)
            set[w] = ((mask >> w) & 1) != 0;
        var e = Evaluate(i, Move.Of(set));
        if (e.Approximate)
        {
            _approximate = true; // cannot happen behind the gate; if it does, the proof is void
            _stop = true;
        }
        return e;
    }

    /// <summary>
    /// The exact total for the worlds in <paramref name="mask"/>. With a <paramref name="cutoff"/> it stops once the total (plus
    /// <paramref name="lowerRest"/>, a lower bound per item still to come) cannot be below it and returns a value of at least the cutoff.
    /// Sets <see cref="_stop"/> and returns <see cref="Infeasible"/> when the budget runs out first (checked before each item).
    /// <paramref name="order"/> is the order the items are added in (null: by index; the total does not depend on it, only where the cut-off
    /// can stop). <paramref name="known"/> holds, per item, an exact cover of some superset of <paramref name="mask"/> (or null): R11.
    /// </summary>
    private long ExactTotal(ulong mask, long cutoff, out int used, long[]? lowerRest = null, int[]? order = null, Eval?[]? known = null)
    {
        used = 0;
        long total = 0, rest = 0;
        if (lowerRest is not null)
            foreach (var v in lowerRest)
                rest += v;
        ulong usedMask = 0;
        for (var oi = 0; oi < _items.Length; oi++)
        {
            var i = order is null ? oi : order[oi];
            if (!Continue())
            {
                _stop = true;
                return Infeasible;
            }
            if (lowerRest is not null)
                rest -= lowerRest[i];
            var e = known?[i] is { Feasible: true } k && Inside(i, k, mask) ? RememberIn(i, mask, k) : ExactItem(i, mask);
            if (_stop)
                return Infeasible;
            if (!e.Feasible)
                return Infeasible;
            total += e.Cost;
            foreach (var l in e.Used)
                usedMask |= 1UL << _items[i].Worlds[l];
            if (cutoff != long.MaxValue && total + rest >= cutoff)
                return Math.Max(total + rest, cutoff);
        }
        used = System.Numerics.BitOperations.PopCount(usedMask);
        return total;
    }

    /// <summary>True when every world <paramref name="cover"/> buys on is in <paramref name="mask"/>.</summary>
    private bool Inside(int i, Eval cover, ulong mask)
    {
        var worlds = _items[i].Worlds;
        _work += cover.Used.Length;
        foreach (var l in cover.Used)
            if (((mask >> worlds[l]) & 1) == 0)
                return false;
        return true;
    }

    /// <summary>
    /// R11: <paramref name="known"/>, the exact cover of a superset of <paramref name="mask"/>, buys only inside it, so it is also the
    /// cheapest cover of <paramref name="mask"/> (<see cref="Remember"/>'s argument: every cover of the smaller set is a cover of the larger,
    /// so none is cheaper). Cached under the smaller set without a solve.
    /// </summary>
    private Eval RememberIn(int i, ulong mask, Eval known)
    {
        var set = new bool[_worldIds.Length];
        foreach (var w in _items[i].Worlds)
            set[w] = ((mask >> w) & 1) != 0;
        return Remember(i, Move.Of(set), known);
    }

    /// <summary>One completed set: exact total with early cut-off; a strict improvement lowers every world count from k up and is recorded with its lines.</summary>
    private bool Leaf(ulong set, long[]? lowerRest, Action<Candidate> record, int[]? order = null, Eval?[]? known = null)
    {
        var total = ExactTotal(set, _best[_k], out _, lowerRest, order, known);
        if (_stop)
            return false;
        if (total == Infeasible || total >= _best[_k])
            return true;
        for (var k = _k; k < _best.Length; k++)
            if (total < _best[k])
                _best[k] = total;
        var flags = new bool[_worldIds.Length];
        for (var w = 0; w < flags.Length; w++)
            flags[w] = ((set >> w) & 1) != 0;
        if (Start(flags, bounded: false) is { } state) // everything is cached by now, so this only assembles the lines
            record(ToCandidate(state));
        return true;
    }

    /// <summary>
    /// Completions I + R, R a subset of L with |R| = r (R4: exactly r, smaller sets are already proven). Returns false when the budget
    /// ran out or the phase was voided.
    /// </summary>
    private bool Node(ulong included, int[] candidates, int r, NodeState state, Action<Candidate> record, SupersetBounds? inherited = null)
    {
        var nItems = _items.Length;
        var n = _worldIds.Length;

        // Candidates by summed gain, descending (ties: lower index), so the first r of any suffix are its r largest gains.
        var gain = new long[n];
        foreach (var w in candidates)
        {
            foreach (var i in _itemsByWorld[w])
                gain[w] += state.G[i][_items[i].LocalOf[w]];
            _work += _itemsByWorld[w].Count;
        }
        var L = candidates.OrderByDescending(w => gain[w]).ThenBy(w => w).ToArray();
        _work += L.Length * 4L;
        var pos = new int[n];
        Array.Fill(pos, -1);
        for (var p = 0; p < L.Length; p++)
            pos[L[p]] = p;

        // Per item: its candidate worlds' gains descending (with positions and supplies) for the per-item cardinality bound and the supply count.
        var itemGains = new (long G, int Pos, long Sup)[nItems][];
        for (var i = 0; i < nItems; i++)
        {
            var item = _items[i];
            var list = new List<(long, int, long)>();
            for (var l = 0; l < item.Worlds.Length; l++)
                if (pos[item.Worlds[l]] >= 0)
                    list.Add((state.G[i][l], pos[item.Worlds[l]], item.Supply[l]));
            list.Sort((x, y) => x.Item1 != y.Item1 ? y.Item1.CompareTo(x.Item1) : x.Item2.CompareTo(y.Item2));
            itemGains[i] = list.ToArray();
            _work += item.Worlds.Length + list.Count;
        }

        long sumA = 0;
        foreach (var v in state.A)
            sumA += v;

        ulong suffix = 0;
        foreach (var w in L)
            suffix |= 1UL << w;
        // Per item over included + suffix: lower bound (fractional, lazily upgraded to the exact cover), greedy whole cover, exact cover.
        SupersetBounds b;
        if (inherited is not null)
        {
            b = inherited.Copy(); // R8: the parent's superset at its step j is this node's superset
            _work += 3L * nItems;
        }
        else
        {
            b = new SupersetBounds { C = new long[nItems], Ub = new long[nItems], Exact = new Eval?[nItems] };
            for (var i = 0; i < nItems; i++)
                b.C[i] = FracMask(i, included | suffix, out b.Ub[i]);
        }
        var c = b.C;
        var per = new long[nItems];
        var order = new int[nItems];
        var potentials = new long[nItems];

        for (var j = 0; j + r <= L.Length; j++)
        {
            if (!Continue())
            {
                _stop = true;
                return false;
            }
            if (j > 0)
            {
                var gone = L[j - 1];
                suffix &= ~(1UL << gone);
                foreach (var i in _itemsByWorld[gone])
                {
                    _work++;
                    // R7b: an exact cover of the larger set that does not buy on the world that left is the smaller set's exact cover too.
                    if (b.Exact[i] is { } known && (!known.Feasible || !known.Uses(_items[i].LocalOf[gone])))
                        continue;
                    // R7a (R1): the smaller set costs at least the larger one, so the old lower bound still holds.
                    var f = FracMask(i, included | suffix, out b.Ub[i]);
                    c[i] = f == Infeasible ? Infeasible : Math.Max(f, c[i]);
                    b.Exact[i] = null;
                }
            }

            // Lower bound on every completion I + R, R within L[j..], |R| = r.
            long s1 = 0;
            var infeasible = false;
            for (var i = 0; i < nItems && !infeasible; i++)
            {
                if (c[i] == Infeasible)
                {
                    infeasible = true;
                    break;
                }
                // R3: f_M is supermodular in the world set, so adding the r best worlds saves at most the sum of their single gains.
                long topG = 0, topSup = 0;
                var taken = 0;
                foreach (var e in itemGains[i])
                {
                    _work++;
                    if (e.Pos < j)
                        continue;
                    topG += e.G;
                    taken++;
                    if (taken == r)
                        break;
                }
                var bound = Math.Max(c[i], state.A[i] - topG);
                if (state.Sup[i] < _items[i].Target)
                {
                    // R5: supplies add up, so a short item needs its target from at most r suffix worlds.
                    var sups = itemGains[i].Where(e => e.Pos >= j).Select(e => e.Sup).OrderByDescending(x => x).Take(r);
                    _work += itemGains[i].Length;
                    foreach (var x in sups)
                        topSup += x;
                    if (state.Sup[i] + topSup < _items[i].Target)
                        infeasible = true;
                }
                per[i] = bound;
                s1 += bound;
            }
            if (infeasible)
                break; // a smaller suffix cannot fix it (the suffix only shrinks)
            long top = 0;
            for (var t = 0; t < r; t++)
                top += gain[L[j + t]];
            var lb = Math.Max(s1, sumA - top); // R3 again, summed over items

            // No incumbent yet: only feasibility can prune, and the fractional pass already decided it, so exact upgrades are waste.
            if (_best[_k] != long.MaxValue && lb < _best[_k])
            {
                // R10: an upgrade raises item i's term from per[i] to at most Ub[i] (a real cover over the same set costs at least the exact
                // one), so the upgrades together can add at most `potential` to s1. Items with the most to add go first.
                long potential = 0;
                var open = 0;
                for (var i = 0; i < nItems; i++)
                {
                    if (b.Exact[i] is not null)
                        continue;
                    var p = Math.Max(0, b.Ub[i] - per[i]);
                    potential += p;
                    potentials[i] = p;
                    order[open++] = i;
                }
                _work += nItems + 4L * open;
                Array.Sort(order, 0, open, Comparer<int>.Create((x, y) => potentials[x] != potentials[y] ? potentials[y].CompareTo(potentials[x]) : x.CompareTo(y)));

                // R1: the exact cover over included + suffix (monotone, cached) bounds every completion from below. One item at a time until
                // pruned, or until even the rest of the potential cannot prune (S2 is already below the incumbent, so only s1 can).
                for (var oi = 0; oi < open && lb < _best[_k] && s1 + potential >= _best[_k]; oi++)
                {
                    var i = order[oi];
                    potential -= potentials[i];
                    var e = ExactItem(i, included | suffix);
                    if (_stop)
                        return false;
                    b.Exact[i] = e;
                    if (e.Feasible)
                        b.Ub[i] = Math.Min(b.Ub[i], e.Cost);
                    c[i] = Math.Max(c[i], e.Feasible ? e.Cost : long.MaxValue / 4);
                    if (c[i] > per[i])
                    {
                        s1 += c[i] - per[i];
                        per[i] = c[i];
                        lb = Math.Max(lb, s1);
                    }
                }
            }
            if (lb >= _best[_k])
                break; // the bound only grows as the suffix shrinks (supersets cost less, top-r sums fall, feasibility only gets harder)

            var u = L[j];
            if (r == 1 || j + r == L.Length)
            {
                // Leaf: exactly one completion here (r == 1: I + u; otherwise I plus the whole suffix).
                long[]? lowerRest = null;
                int[]? leafOrder = null;
                if (r == 1)
                {
                    lowerRest = new long[nItems];
                    leafOrder = new int[nItems];
                    var front = 0;
                    var back = nItems;
                    for (var i = 0; i < nItems; i++)
                    {
                        var l = _items[i].LocalOf[u];
                        // R9: I + u lies inside included + suffix, so c[i] (a lower bound there, R1/R2) bounds it too.
                        lowerRest[i] = Math.Max(l >= 0 ? state.A[i] - state.G[i][l] : state.A[i], c[i]);
                        if (l < 0)
                            leafOrder[front++] = i; // R12: items that do not sell u were solved for I already, add them first
                    }
                    for (var i = nItems - 1; i >= 0; i--)
                        if (_items[i].LocalOf[u] >= 0)
                            leafOrder[--back] = i;
                    _work += 2L * nItems;
                }
                if (!Leaf(r == 1 ? included | (1UL << u) : included | suffix, lowerRest, record, leafOrder, b.Exact))
                    return false;
                if (j + r == L.Length)
                    break;
                continue;
            }

            // Child: include u, candidates L[j+1..].
            var childCandidates = L[(j + 1)..];
            ulong candidateMask = 0;
            foreach (var w in childCandidates)
                candidateMask |= 1UL << w;
            if (!Node(included | (1UL << u), childCandidates, r - 1, Include(state, u, candidateMask), record, b))
                return false;
        }
        return true;
    }
}
