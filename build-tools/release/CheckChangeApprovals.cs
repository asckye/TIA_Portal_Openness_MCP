using System.Globalization;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record ReviewFinding(string Path, string Kind, string Identifier, string Message, int Line = 0);
internal sealed record CheckChangeApproval(string Path, string Kind, string Identifier, string MaintainerDate, string Reason, string Task = "");
internal sealed record ApprovalItem(string Path, string Kind, string Identifier);
internal sealed record ApprovalTask(string Task, string MaintainerDate, string Reason, ApprovalItem[] Items);
internal sealed record ApprovalAudit(CheckChangeApproval[] Added, CheckChangeApproval[] Unauthorized, ReviewFinding[]? Violations = null);

internal static class CheckChangeApprovals
{
    internal const string RelativePath = "build-tools/release/baselines/approved-check-changes.json";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record Document(int Format, ApprovalTask[] Tasks);

    internal static CheckChangeApproval[] Load(string root) => File.Exists(Path.Combine(root, RelativePath))
        ? Parse(File.ReadAllText(Path.Combine(root, RelativePath))) : [];

    internal static CheckChangeApproval[] Parse(string text)
    {
        var document = JsonSerializer.Deserialize<Document>(text, Json);
        if (document is not { Format: 2, Tasks: not null } || document.Tasks.Any(task =>
            task == null || string.IsNullOrWhiteSpace(task.Task) || task.Items == null))
            throw new ReleaseException("Approvals require format 2 with task groups");
        var rows = document.Tasks.SelectMany(task => task.Items.Select(item =>
            new CheckChangeApproval(item.Path, item.Kind, item.Identifier, task.MaintainerDate, task.Reason, task.Task))).ToArray();
        foreach (var row in rows)
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || row.Path.Contains('\\') || row.Path.StartsWith('/') ||
                row.Path.Split('/').Any(part => part is ".." or "." or "") || row.Path.Contains(':') ||
                string.IsNullOrWhiteSpace(row.Kind) || string.IsNullOrWhiteSpace(row.Identifier) || string.IsNullOrWhiteSpace(row.Reason) ||
                !DateOnly.TryParseExact(row.MaintainerDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ReleaseException("Approval requires an exact path, kind, identifier, maintainer date and reason");
        if (rows.DistinctBy(row => (row.Path, row.Kind, row.Identifier)).Count() != rows.Length)
            throw new ReleaseException("Duplicate check-change approval");
        return rows;
    }

    internal static bool IsLeadCommit(string subject, string[] paths) =>
        subject.StartsWith("Record maintainer approval", StringComparison.Ordinal) &&
        paths.Length == 1 && paths[0] == RelativePath;

    internal static ApprovalAudit Audit(string root, string basis, IReadOnlyList<CheckChange> changes,
        Func<string[], CommandResult>? run = null)
    {
        run ??= args => ReviewChangeSet.Git("git", args, root);
        string Git(params string[] args)
        {
            var result = run(args);
            ProcessRunner.RequireSuccess(result, "Approval provenance");
            return result.StandardOutput.Trim();
        }
        CheckChangeApproval[] At(string revision)
        {
            var result = run(["show", revision + ":" + RelativePath]);
            return result.ExitCode == 0 ? Parse(result.StandardOutput) : [];
        }
        string common = Git("merge-base", basis, "HEAD");
        var current = Load(root);
        var added = current.Except(At(common)).ToArray();
        var authorized = new HashSet<CheckChangeApproval>();
        var violations = new List<ReviewFinding>();
        foreach (var commit in Git("rev-list", "--reverse", common + "..HEAD", "--", RelativePath).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var rows = At(commit);
            var introduced = rows.Except(At(commit + "^")).ToArray();
            var paths = Git("diff-tree", "--no-commit-id", "--name-only", "-r", commit).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (IsLeadCommit(Git("show", "-s", "--format=%s", commit), paths)) authorized.UnionWith(introduced);
            else violations.Add(new(RelativePath, "approval-change-unauthorized", "commit:" + commit,
                "Approval file changed outside an approval-only maintainer commit"));
            authorized.IntersectWith(rows);
        }
        var head = run(["show", "HEAD:" + RelativePath]);
        string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string? working = File.Exists(Path.Combine(root, RelativePath)) ? File.ReadAllText(Path.Combine(root, RelativePath)) : null;
        if (head.ExitCode == 0 ? working == null || Normalize(head.StandardOutput) != Normalize(working) : working != null)
            violations.Add(new(RelativePath, "approval-change-unauthorized", "working-tree",
                "Approval file has uncommitted changes"));
        return new(added, added.Where(row => !authorized.Contains(row)).ToArray(), violations.ToArray());
    }

    internal static void Record(string root, string task, string date, string reason, IEnumerable<ReviewFinding> findings, string? output,
        IEnumerable<CheckChangeApproval>? unauthorized = null)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new ReleaseException("-RecordApprovals requires -Task", 64);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ReleaseException("-RecordApprovals requires -Reason naming the task spec or maintainer decision", 64);
        var current = findings.ToArray();
        var old = Load(root).Except(unauthorized ?? []).ToArray();
        var keys = current.Select(row => (row.Path, row.Kind, row.Identifier)).ToHashSet();
        var requested = (unauthorized ?? []).Concat(output != null && File.Exists(output) ? Parse(File.ReadAllText(output)).Except(old) : []);
        if (requested.Any(row => !keys.Contains((row.Path, row.Kind, row.Identifier))))
            throw new ReleaseException("Approval does not match a finding in the current diff", 64);
        var rows = Unapproved(current, old).Select(row => new CheckChangeApproval(row.Path, row.Kind, row.Identifier, date, reason, task));
        var groups = old.Concat(rows).GroupBy(row => (row.Task, row.MaintainerDate, row.Reason))
            .Select(group => new ApprovalTask(group.Key.Task, group.Key.MaintainerDate, group.Key.Reason,
                group.Select(row => new ApprovalItem(row.Path, row.Kind, row.Identifier)).ToArray())).ToArray();
        string text = JsonSerializer.Serialize(new Document(2, groups), Json) + "\n";
        Parse(text);
        File.WriteAllText(output ?? Path.Combine(root, RelativePath), text);
    }

    internal static ReviewFinding[] Unapproved(IEnumerable<ReviewFinding> findings, IEnumerable<CheckChangeApproval> approvals)
    {
        var keys = approvals.Select(row => (row.Path, row.Kind, row.Identifier)).ToHashSet();
        return findings.Where(row => !keys.Contains((row.Path, row.Kind, row.Identifier))).ToArray();
    }
}
