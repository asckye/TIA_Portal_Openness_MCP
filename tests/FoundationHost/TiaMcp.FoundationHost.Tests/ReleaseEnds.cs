using TiaMcp.Adapters.Contracts;

internal static class ReleaseEnds
{
    internal static IEnumerable<T> OldestAndNewest<T>(IEnumerable<T> rows, Func<T, string> subject, Func<T, string> release)
    {
        var releases = ReleaseRange.All;
        foreach (var group in rows.GroupBy(subject, StringComparer.Ordinal))
        {
            var available = group.GroupBy(release, StringComparer.Ordinal)
                .OrderBy(items => Array.IndexOf(releases, items.Key)).ToArray();
            // Keep every input variant at each end, including single-release subjects.
            foreach (var row in available[0]) yield return row;
            if (available.Length > 1)
                foreach (var row in available[^1]) yield return row;
        }
    }
}
