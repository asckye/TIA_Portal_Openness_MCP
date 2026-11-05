namespace TiaMcp.ReleaseTool;

internal sealed record CheckChange(string Path, string OldPath, string? Before, string? After);

internal static class ReviewChangeSet
{
    internal static string Basis(string root, string basis, string git)
    {
        var result = Git(git, ["merge-base", basis, "HEAD"], root);
        ProcessRunner.RequireSuccess(result, "Review merge base: " + basis);
        return result.StandardOutput.Trim();
    }

    internal static CheckChange[] Read(string root, string basis, string git = "git")
    {
        var common = Basis(root, basis, git);
        var changes = Git(git, ["diff", "--name-status", "-z", "--find-renames", common, "--"], root);
        ProcessRunner.RequireSuccess(changes, "Review change set");
        var parts = changes.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var rows = new List<CheckChange>();
        for (int index = 0; index < parts.Length;)
        {
            string status = parts[index++], oldPath = parts[index++];
            string path = status.StartsWith('R') || status.StartsWith('C') ? parts[index++] : oldPath;
            if (!Relevant(path) && !Relevant(oldPath)) continue;
            if (Regenerated(path) && Regenerated(oldPath))
            {
                rows.Add(new(path, oldPath, status == "A" ? null : "", status == "D" ? null : ""));
                continue;
            }
            string? before = null;
            if (status != "A")
            {
                var content = Git(git, ["show", common + ":" + oldPath], root);
                ProcessRunner.RequireSuccess(content, "Review base file: " + oldPath);
                before = content.StandardOutput;
            }
            rows.Add(new(path, oldPath, before, status == "D" ? null : File.ReadAllText(Path.Combine(root, path))));
        }
        var untracked = Git(git, ["ls-files", "--others", "--exclude-standard", "-z"], root);
        ProcessRunner.RequireSuccess(untracked, "Review new files");
        rows.AddRange(untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(Relevant)
            .Select(path => new CheckChange(path, path, null, Regenerated(path) ? "" : File.ReadAllText(Path.Combine(root, path)))));
        return PairRenames(rows);
    }

    // An unstaged move is a tracked deletion plus an untracked file to Git.
    internal static CheckChange[] PairRenames(IEnumerable<CheckChange> changes)
    {
        var rows = changes.ToList();
        string Identity(string path, string source) => Path.GetExtension(path) + "\0" + (path.EndsWith(".cs", StringComparison.Ordinal)
            ? CheckLoosening.Tokens(CheckLoosening.Parse(source)) : source.Replace("\r\n", "\n", StringComparison.Ordinal));
        var additions = rows.Where(row => row.Before == null && row.After != null && !Regenerated(row.Path))
            .GroupBy(row => Identity(row.Path, row.After!)).ToDictionary(group => group.Key, group => new Queue<CheckChange>(group));
        foreach (var deleted in rows.Where(row => row.After == null && !Regenerated(row.OldPath)).ToArray())
        {
            if (!additions.TryGetValue(Identity(deleted.Path, deleted.Before!), out var matches) || matches.Count == 0) continue;
            var added = matches.Dequeue();
            rows.Remove(deleted); rows.Remove(added);
            rows.Add(new(added.Path, deleted.OldPath, deleted.Before, added.After));
        }
        return rows.ToArray();
    }

    internal static CommandResult Git(string executable, IEnumerable<string> arguments, string root)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new ReleaseException("Could not start git");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    internal static bool Regenerated(string path) =>
        path == "build-tools/release/baselines/tia-feature-expectations.json" ||
        path.StartsWith("manifest/", StringComparison.Ordinal) && path.Contains("contracts", StringComparison.Ordinal) &&
            path.Contains("/baseline/", StringComparison.Ordinal) ||
        Path.GetExtension(path) is not (".cs" or ".py" or ".csproj" or ".props" or ".targets" or
            ".runsettings" or ".globalconfig" or ".yml" or ".yaml" or ".md") &&
            (path.Split('/').Any(part => part.Equals("Golden", StringComparison.OrdinalIgnoreCase)) ||
             Path.GetFileName(path).Contains("Golden", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".golden", StringComparison.OrdinalIgnoreCase));

    private static bool Relevant(string path) => Regenerated(path) ||
        (Path.GetExtension(path) != ".json" || path == "tests/test-suites.json" ||
            path == CheckChangeApprovals.RelativePath || CheckLimitChanges.Baselines.Contains(path)) &&
        (Path.GetFileName(path) == ".editorconfig" || Path
                .GetExtension(path) is ".cs" or ".py" or ".json" or ".yml" or ".yaml" or ".csproj" or ".props" or ".targets"
                or ".runsettings" or ".globalconfig");
}
