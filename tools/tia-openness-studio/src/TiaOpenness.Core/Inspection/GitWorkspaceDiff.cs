using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Inspection
{
    /// <summary>
    /// Reads a version-control workspace's uncommitted changes with the local <c>git</c>.
    ///
    /// The diff cannot come from Openness. VCI reports whether a mapped object still matches its
    /// file, but offers no way to read the project's side of the comparison without writing it out
    /// first — so there is nothing to compare until a push has happened. After a push the change
    /// is in the working tree, which is exactly what Git is for.
    ///
    /// Shelling out to the installed <c>git</c> rather than embedding a Git implementation is
    /// deliberate: it is the same Git the engineer commits with, so what is reviewed here and what
    /// lands in the repository cannot disagree.
    /// </summary>
    public static class GitWorkspaceDiff
    {
        /// <summary>Long enough for a large block, short enough that a hung git does not hang the bridge.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <param name="root">The workspace folder — normally a Git working tree.</param>
        /// <param name="file">One file to diff, or null for every change in the workspace.</param>
        public static WorkspaceDiff Read(string workspaceName, string root, string file)
        {
            var diff = new WorkspaceDiff { WorkspaceName = workspaceName, RootPath = root };

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                diff.Detail = "The workspace folder does not exist: " + root;
                return diff;
            }

            string version;
            if (!TryRun(root, "--version", out version))
            {
                diff.Detail = "Git is not installed, or not on PATH. The version control tab can still " +
                              "map and push; reviewing a change needs Git.";
                return diff;
            }

            string inside;
            if (!TryRun(root, "rev-parse --is-inside-work-tree", out inside)
                || inside.Trim() != "true")
            {
                diff.Detail = "This workspace folder is not a Git repository. Run 'git init' in " +
                              root + ", or point the workspace at a working tree.";
                return diff;
            }

            diff.Available = true;

            string names;
            if (TryRun(root, "status --porcelain", out names))
            {
                diff.ChangedFiles = names
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Length > 3 ? line.Substring(3).Trim().Trim('"') : line.Trim())
                    .Where(name => name.Length > 0)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Untracked files have no committed side, so `git diff` says nothing about them.
            // --no-index against an empty file shows them as wholly new, which is what a first
            // push after mapping actually is.
            var pathspec = PathSpec(root, file);

            var arguments = new StringBuilder("diff --no-color");
            if (pathspec != null) arguments.Append(" -- \"").Append(pathspec).Append('"');

            string output;
            if (!TryRun(root, arguments.ToString(), out output))
            {
                diff.Detail = "git " + arguments + " failed.";
                return diff;
            }

            output += UntrackedDiff(root, pathspec);

            diff.Detail = "git " + arguments;
            diff.Lines = Parse(output);
            return diff;
        }

        /// <summary>
        /// Turns what version control reports into something Git can match.
        ///
        /// A mapped object names its file without an extension — VCI decides that from the file
        /// format — so an exact path would match nothing and read as "no change". A trailing
        /// wildcard matches whichever extension it got. The path is also made relative, because a
        /// pathspec is resolved against the repository, not the caller's working directory.
        /// </summary>
        private static string PathSpec(string root, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;

            var path = file.Trim();
            var rooted = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (path.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(rooted.Length);
            }

            path = path.Replace('\\', '/');
            return Path.HasExtension(path) ? path : path + "*";
        }

        /// <summary>
        /// A file Git has never seen produces no diff at all, which reads as "no change" when the
        /// truth is "all of it is new". Comparing it against nothing says so.
        /// </summary>
        private static string UntrackedDiff(string root, string pathspec)
        {
            // Let Git expand the VCI pathspec, then compare actual filenames. --no-index does
            // not expand a wildcard, and a new workspace may contain several untracked files.
            var arguments = "ls-files --others --exclude-standard -z";
            if (pathspec != null) arguments += " -- \"" + pathspec + "\"";
            if (!TryRun(root, arguments, out var files)) return string.Empty;
            var nul = System.Environment.OSVersion.Platform == PlatformID.Win32NT ? "NUL" : "/dev/null";
            var result = new StringBuilder();
            foreach (var file in files.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                if (TryRun(root, "diff --no-color --no-index -- " + nul + " \"" + file + "\"", out var output))
                    result.Append(output);
            return result.ToString();
        }

        public static List<DiffLine> Parse(string unified)
        {
            var lines = new List<DiffLine>();
            if (string.IsNullOrEmpty(unified)) return lines;

            foreach (var text in unified.Split('\n'))
            {
                var line = text.TrimEnd('\r');
                lines.Add(new DiffLine { Kind = KindOf(line), Text = line });
            }

            // A trailing newline produces one empty line that means nothing.
            if (lines.Count > 0 && lines[lines.Count - 1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private static DiffLineKind KindOf(string line)
        {
            if (line.Length == 0) return DiffLineKind.Context;

            // Order matters: "+++" and "---" are file headers, not an added and a removed line.
            if (line.StartsWith("+++", StringComparison.Ordinal)) return DiffLineKind.Header;
            if (line.StartsWith("---", StringComparison.Ordinal)) return DiffLineKind.Header;
            if (line.StartsWith("@@", StringComparison.Ordinal)) return DiffLineKind.Hunk;
            if (line.StartsWith("diff ", StringComparison.Ordinal)) return DiffLineKind.Header;
            if (line.StartsWith("index ", StringComparison.Ordinal)) return DiffLineKind.Header;
            if (line.StartsWith("new file", StringComparison.Ordinal)) return DiffLineKind.Header;
            if (line.StartsWith("deleted file", StringComparison.Ordinal)) return DiffLineKind.Header;

            if (line[0] == '+') return DiffLineKind.Added;
            if (line[0] == '-') return DiffLineKind.Removed;
            return DiffLineKind.Context;
        }

        private static bool TryRun(string workingDirectory, string arguments, out string output)
        {
            output = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };

                using (var process = Process.Start(startInfo))
                {
                    var text = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return false;
                    }

                    System.Threading.Tasks.Task.WaitAll(text, errors);
                    output = text.Result;

                    // `git diff` exits 1 when it found differences, which is not a failure.
                    return process.ExitCode == 0 || process.ExitCode == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
