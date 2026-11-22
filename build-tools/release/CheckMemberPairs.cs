namespace TiaMcp.ReleaseTool;

// Reserve the strongest identities across the whole change before trying a weaker match.
internal sealed class CheckMemberPairs<T>(IEnumerable<T> before, IEnumerable<T> after) where T : class
{
    private readonly HashSet<T> remainingBefore = before.ToHashSet();
    private readonly HashSet<T> remainingAfter = after.ToHashSet();
    private readonly Dictionary<T, T> pairs = new();

    internal IEnumerable<T> RemainingBefore => remainingBefore;
    internal IEnumerable<T> RemainingAfter => remainingAfter;
    internal T? Replacement(T previous) => pairs.GetValueOrDefault(previous);

    internal void Pair(T previous, T current)
    {
        pairs.Add(previous, current);
        remainingBefore.Remove(previous);
        remainingAfter.Remove(current);
    }

    internal void MatchBy<TKey>(Func<T, TKey> key, bool unique = false, Func<T, bool>? include = null)
    {
        var oldGroups = remainingBefore.Where(item => include == null || include(item)).ToLookup(key);
        var newGroups = remainingAfter.Where(item => include == null || include(item)).ToLookup(key);
        foreach (var group in oldGroups)
        {
            var candidates = newGroups[group.Key];
            if (unique && (group.Count() != 1 || candidates.Count() != 1)) continue;
            foreach (var (previous, current) in group.Zip(candidates)) Pair(previous, current);
        }
    }
}
