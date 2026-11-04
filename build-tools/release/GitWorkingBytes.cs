using System.Text;

namespace TiaMcp.ReleaseTool;

internal static class GitWorkingBytes
{
    internal static string[] NonNormalizedWorkingFiles(string root, string git)
    {
        // Git's C-quoted ASCII form also survives a runner whose redirected console uses a legacy code page.
        var tracked = ProcessRunner.Run(git, ["-C", root, "-c", "core.quotepath=true", "ls-files", "--cached"], root);
        ProcessRunner.RequireSuccess(tracked, "Enumerate tracked working bytes");
        var paths = tracked.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(UnquoteGitPath).Distinct(StringComparer.Ordinal)
            .Where(path => File.Exists(Path.Combine(root, path))).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return [];
        var input = string.Join('\n', paths.Select(QuoteGitPath)) + "\n";
        string[] Hashes(bool raw)
        {
            var result = ProcessRunner.Run(git, ["-C", root, "hash-object", "--stdin-paths", raw ? "--no-filters" : "--filters"], root, standardInput: input);
            ProcessRunner.RequireSuccess(result, "Hash " + (raw ? "raw" : "Git-normalized") + " tracked working bytes");
            var hashes = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (hashes.Length != paths.Length) throw new ReleaseException("Tracked working-byte hash count differs from file count");
            return hashes;
        }
        var raw = Hashes(true);
        var normalized = Hashes(false);
        return paths.Where((_, index) => raw[index] != normalized[index]).ToArray();
    }

    private static string QuoteGitPath(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (byte value in Encoding.UTF8.GetBytes(path))
            if (value is < 32 or >= 127 || value is (byte)'"' or (byte)'\\')
                quoted.Append('\\').Append(Convert.ToString(value, 8).PadLeft(3, '0'));
            else quoted.Append((char)value);
        return quoted.Append('"').ToString();
    }

    private static string UnquoteGitPath(string path)
    {
        if (!path.StartsWith('"')) return path;
        var bytes = new List<byte>();
        for (int index = 1; index < path.Length - 1; index++)
        {
            char value = path[index];
            if (value != '\\') { bytes.Add((byte)value); continue; }
            value = path[++index];
            if (value is >= '0' and <= '7')
            {
                bytes.Add(Convert.ToByte(path.Substring(index, 3), 8)); index += 2;
            }
            else bytes.Add(value switch { 'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13, _ => (byte)value });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal static void RequireNormalizedWorkingBytes(string root, string git)
    {
        var paths = NonNormalizedWorkingFiles(root, git);
        if (paths.Length == 0) return;
        throw new ReleaseException("Tracked working bytes differ from Git index/HEAD normalization (including CRLF/LF rewrites):\n  " +
            string.Join("\n  ", paths) + "\nPreserve any edits, convert the listed text files to the normalized bytes (normally UTF-8/LF), " +
            "and use git -c core.autocrlf=false restore --worktree -- <paths> only for files whose edits may be discarded. " +
            "Check .gitattributes/core.autocrlf, rerun prerequisites and collect fresh evidence after fixing the checkout; no build started.");
    }
}
