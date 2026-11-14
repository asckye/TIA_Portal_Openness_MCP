namespace TiaMcp.FoundationHost;

internal sealed record RebindTarget(int ProcessId, string? ProcessStartUtc, string? ProjectFile, string? ProjectName, bool WorkerStartedPortal = false);
internal sealed record PortalProcessFact(int Id, string? ProcessStartUtc, string? ProjectPath, int AttachedSessions);
internal enum RebindDisposition { Same, Moved, Gone, Ambiguous, Held }
internal sealed record RebindResolution(RebindDisposition Disposition, PortalProcessFact? Process, IReadOnlyList<PortalProcessFact> Candidates);

internal static class RebindTargetResolver
{
    internal static RebindResolution Resolve(RebindTarget target, IReadOnlyList<PortalProcessFact> processes)
    {
        var same = processes.FirstOrDefault(process => process.Id == target.ProcessId
            && target.ProcessStartUtc != null && process.ProcessStartUtc == target.ProcessStartUtc);
        if (same != null && processes.Count(process => process.Id == target.ProcessId) == 1) return new(RebindDisposition.Same, same, new[] { same });
        var matches = processes.Where(process => SameProject(target.ProjectFile, process.ProjectPath)).ToArray();
        if (matches.Length == 0) return new(RebindDisposition.Gone, null, matches);
        if (matches.Length > 1) return new(RebindDisposition.Ambiguous, null, matches);
        var match = matches[0];
        return new(match.AttachedSessions == 0 ? RebindDisposition.Moved : RebindDisposition.Held,
            match, matches);
    }

    private static bool SameProject(string? expected, string? actual)
    {
        string? left = ComparableProject(expected), right = ComparableProject(actual);
        return left != null && right != null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ComparableProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string value = path.Trim().Replace('\\', '/');
        if (value.Length > 260 || value.Any(character => character < ' ' || "<>|?*\"".Contains(character))) return null;
        return value;
    }
}
