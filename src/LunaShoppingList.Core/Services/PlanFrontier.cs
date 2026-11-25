namespace LunaShoppingList.Core;

/// <summary>The trade-off between worlds to visit and gil: every worthwhile plan from the cheapest down to the fewest worlds.</summary>
/// <remarks>
/// Whole stacks (release 0.16.0.0): a market listing can only be bought as a whole stack. Every option buys whole stacks: a line's
/// quantity is the units bought on that world (a legal total there, possibly above the item's need), and an option's total is the
/// exact price of the stacks it buys.
/// </remarks>
public static class PlanFrontier
{
    /// <summary>
    /// The work budget <see cref="Build"/> uses, in units of roughly one knapsack table cell or one stack visited, plus a fixed charge
    /// per exact solve (counted, not timed, so the result never depends on the machine's speed or load). Measured on 2026-11-24 on
    /// the development machine: 1.9 s of search on a book of 100 items by 150 worlds, 0.9 s on 60 items by 40 worlds with 30 stacks
    /// of up to 999 units each; both books hit the budget.
    /// </summary>
    public const long DefaultWorkBudget = 300_000_000;

    /// <summary>
    /// The work budget of the exact world-set phase alone (books of 11 to 64 worlds), separate from <see cref="DefaultWorkBudget"/>, which
    /// the heuristic phases keep. Counted work, not time: 14,000,000,000 units were measured on 2026-11-25 on the development machine at
    /// 0.13 to 0.21 s per 100 million units, so about 30 s at most for a book that does not finish (books up to 40 worlds finished well
    /// below it). A coordinator decision (N1), chosen so that the few-worlds end of a 48-world book is proven; a book that finishes
    /// earlier returns earlier.
    /// </summary>
    public const long DefaultExactWorkBudget = 14_000_000_000;

    /// <summary>Elimination may run until this share (percent) of the budget is spent, the forward path until <see cref="ForwardShare"/>; the rest is for the exhaustive or improvement phase.</summary>
    private const int BackwardShare = 40;

    private const int ForwardShare = 65;

    /// <summary>
    /// Builds the options for <paramref name="book"/>, ordered by world count descending (the first is the cheapest option found, with
    /// zero extra: see <see cref="BuildDetailed(OfferBook, long)"/> for how it relates to the cheapest plan). Same as
    /// <see cref="BuildDetailed(OfferBook)"/>'s options.
    /// </summary>
    public static IReadOnlyList<PlanOption> Build(OfferBook book) => BuildDetailed(book).Options;

    /// <summary>The options of <see cref="Build"/>, with whether the search ran out of its <see cref="DefaultWorkBudget"/>.</summary>
    public static PlanFrontierResult BuildDetailed(OfferBook book) => BuildDetailed(book, DefaultWorkBudget, DefaultExactWorkBudget, CancellationToken.None);

    /// <summary>
    /// <see cref="BuildDetailed(OfferBook, long, long, CancellationToken)"/> with <paramref name="workBudget"/> also as the exact
    /// phase's budget, so a budget of zero runs no solving at all, and no cancellation token.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workBudget"/> is negative.</exception>
    public static PlanFrontierResult BuildDetailed(OfferBook book, long workBudget)
        => BuildDetailed(book, workBudget, workBudget, CancellationToken.None);

    /// <summary>
    /// Builds the options for <paramref name="book"/> within <paramref name="workBudget"/> (see <see cref="DefaultWorkBudget"/>).
    /// Candidates come from the cheapest plan, the greedy fewest-worlds plan, a backward-elimination path that repeatedly removes the
    /// world whose removal raises the total least down to the smallest feasible world count, a forward path from the fewest-worlds
    /// plan's worlds, and then, for a book with at most ten worlds, every set of worlds (exact); for 11 to 64 worlds an exact
    /// branch-and-bound search that proves the cheapest total for one world, two, and so on, within <paramref name="exactWorkBudget"/>
    /// (see <see cref="DefaultExactWorkBudget"/>); and an improvement phase (two worlds for one, then one for one) for the world
    /// counts that are still unproven. For any set of worlds every item is bought
    /// the cheapest way with whole stacks from those worlds only (exact, see <see cref="StackCoverSolver.Cover"/>); a set is used
    /// only when every item can still be covered as far as the cheapest plan covers it (<c>min(Needed, supply in the book)</c>).
    /// The result is Pareto-filtered: for each world count the cheapest candidate (ties: fewer lines, then the lines compared in
    /// order), and no option has more worlds and a higher total than another. An empty book gives a single empty plan with zero
    /// worlds. Up to <see cref="PlanFrontierResult.ProvenWorldCount"/> worlds the options are proven optima; above it the choice of world
    /// sets is heuristic, so the options are good plans, not proven optima.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elimination may spend up to 40% of the budget, the forward path up to 65% counting what came before, the rest is left for the
    /// last phase. When a phase's share runs out it stops where it is; the options found so far are returned, with
    /// <see cref="PlanFrontierResult.BudgetExhausted"/> set. The cheapest plan and the fewest-worlds plan are priced as they are, at no
    /// cost to the budget, so with a budget of zero they are the only candidates (a plan whose lines cannot be priced at their own
    /// quantities is replaced by its worlds' re-optimised plan, also outside the budget). The budget is checked before each unit of
    /// solving, so a phase overruns its share by at most one such unit: one item's purchase for a set, one item's "if this world
    /// were dropped" answers (one solve per world it buys on), or one move's exact change (one solve per item the move touches),
    /// each solve bounded by the size guard.
    /// </para>
    /// <para>
    /// The exact search has the budget <paramref name="exactWorkBudget"/> of its own, counted from where the forward path ended, and what it
    /// spends is not charged to the heuristic budget, so the improvement phase still gets the rest of <paramref name="workBudget"/>. Running
    /// out of it sets <see cref="PlanFrontierResult.BudgetExhausted"/> and leaves <see cref="PlanFrontierResult.ProvenWorldCount"/> at the
    /// last world count it settled. The two-argument overload gives the exact phase the same budget as the heuristic phases, so a budget of
    /// zero turns it off. <paramref name="cancellationToken"/> is checked wherever the budget is: cancelling throws
    /// <see cref="OperationCanceledException"/>, and a cancelled run never returns partial options.
    /// </para>
    /// <para>
    /// The first option's total is the cheapest plan's total, or lower: lower only when the size guard made the cheapest plan's cover
    /// of some item approximate (greedy) and a smaller set of worlds lets the same item be solved exactly
    /// (<c>BuildDetailed_FirstOptionCanBeatACheapestPlanThatWentGreedy</c>). It is never higher while the cheapest plan's lines can be
    /// priced at their own quantities, which every exact cover's lines can.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workBudget"/> or <paramref name="exactWorkBudget"/> is negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before or during the search.</exception>
    public static PlanFrontierResult BuildDetailed(OfferBook book, long workBudget, long exactWorkBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(exactWorkBudget);
        cancellationToken.ThrowIfCancellationRequested();

        var cheapest = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        var search = new FrontierSearch(book, workBudget, cancellationToken);

        // The two fixed plans, as priced. Only when one cannot be priced at its own quantities is its set of worlds solved instead,
        // whatever the budget, so the options are never empty.
        var candidates = new List<FrontierSearch.Candidate>();
        foreach (var lines in new[] { cheapest, fewest })
            if (search.Price(lines) is { } priced)
                candidates.Add(priced);
            else
                search.Fixed(search.WorldSet(lines), candidates.Add);
        search.Phase(BackwardShare);
        search.Backward(search.WorldSet(cheapest), candidates.Add);
        search.Phase(ForwardShare);
        search.Forward(search.WorldSet(fewest), WorldsIn(cheapest), candidates.Add);
        search.Phase(100);

        // Few worlds: try every set. When that finishes the options are exact and the improvement phase has nothing to add. More worlds
        // (up to 64): the exact search, with a budget of its own, proves the cheapest total for k = 1, 2, ... in turn; when it proves
        // them all the improvement phase has nothing to add either, otherwise it runs for the world counts above the proven ones.
        var worlds = search.WorldCount;
        var proven = 0;
        if (worlds <= FrontierSearch.ExhaustiveWorldLimit)
        {
            if (search.Exhaustive(BestAtMost(candidates, worlds), candidates.Add))
                proven = worlds;
        }
        else if (worlds <= FrontierSearch.ExactWorldLimit)
        {
            proven = search.ExactFrontier(BestAtMost(candidates, worlds), candidates.Add, exactWorkBudget);
            search.Phase(100);
        }
        var exact = proven == worlds;

        // Repeat while a round reaches fewer worlds: a swapped plan can open a shrink that the unswapped one could not.
        for (var fewestSoFar = int.MaxValue; !exact && search.Continue();)
        {
            while (search.Continue())
            {
                var smallest = BestPerWorldCount(candidates).First().Value.Lines;
                if (search.Shrink(search.WorldSet(smallest), candidates.Add) is not { } smaller)
                    break;
                search.Backward(smaller, candidates.Add);
            }
            foreach (var (_, best) in BestPerWorldCount(candidates))
            {
                if (!search.Continue())
                    break;
                search.Swap(search.WorldSet(best.Lines), candidates.Add);
            }
            var reached = BestPerWorldCount(candidates).First().Key;
            if (reached >= fewestSoFar)
                break;
            fewestSoFar = reached;
        }

        return new PlanFrontierResult(Frontier(candidates), search.Cut) { ProvenWorldCount = proven };
    }

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

    /// <summary>For every k up to <paramref name="worlds"/>, the lowest candidate total with at most k worlds (<see cref="long.MaxValue"/> for none).</summary>
    private static long[] BestAtMost(List<FrontierSearch.Candidate> candidates, int worlds)
    {
        var best = new long[worlds + 1];
        Array.Fill(best, long.MaxValue);
        foreach (var c in candidates)
            for (var k = Math.Min(c.Worlds, worlds); k <= worlds; k++)
                best[k] = Math.Min(best[k], c.Total);
        return best;
    }

    private static IReadOnlyList<PlanOption> Frontier(List<FrontierSearch.Candidate> candidates)
    {
        var best = BestPerWorldCount(candidates);

        // Fewest worlds first: an option survives only if it is strictly cheaper than every option with fewer worlds.
        var kept = new List<FrontierSearch.Candidate>();
        var floor = long.MaxValue;
        foreach (var (_, c) in best)
        {
            if (c.Total >= floor)
                continue;
            floor = c.Total;
            kept.Add(c);
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

    /// <summary>The cheapest candidate per world count (ties: fewer lines, then the lines compared in order), by world count ascending.</summary>
    private static SortedDictionary<int, FrontierSearch.Candidate> BestPerWorldCount(List<FrontierSearch.Candidate> candidates)
    {
        var best = new SortedDictionary<int, FrontierSearch.Candidate>();
        foreach (var c in candidates)
            if (!best.TryGetValue(c.Worlds, out var current) || Better(c, current))
                best[c.Worlds] = c;
        return best;
    }

    private static bool Better(FrontierSearch.Candidate a, FrontierSearch.Candidate b)
    {
        if (a.Total != b.Total)
            return a.Total < b.Total;
        if (a.Lines.Count != b.Lines.Count)
            return a.Lines.Count < b.Lines.Count;
        for (var k = 0; k < a.Lines.Count; k++)
        {
            var c = Compare(a.Lines[k], b.Lines[k]);
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
}
