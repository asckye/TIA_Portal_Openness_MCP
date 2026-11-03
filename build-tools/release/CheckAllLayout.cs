using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static class CheckAllLayout
{
    internal static string[] SourceInventory(string root)
    {
        var tracked = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root);
        ProcessRunner.RequireSuccess(tracked, "layout input inventory");
        return tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] ResultSourceInventory(string root, IEnumerable<string> layout)
    {
        var files = layout.ToHashSet(StringComparer.Ordinal);
        // The layout supplies the roots. Also include ignored/new data below
        // those roots; Git's ignore rules do not declare check dependencies.
        void Walk(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source input: " + directory);
            foreach (var file in Directory.EnumerateFiles(directory)) files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                // These are build outputs, enumerated from runtime receipts.
                if (name is "bin" or "obj" or ".git" || name.StartsWith("bin-", StringComparison.Ordinal) || name.StartsWith("obj-", StringComparison.Ordinal)) continue;
                Walk(child);
            }
        }
        foreach (var top in files.Select(file => file.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray())
            if (top != "bin-build" && Directory.Exists(Path.Combine(root, top))) Walk(Path.Combine(root, top));
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string InputKey(IEnumerable<KeyValuePair<string, string>> inputs, IEnumerable<string> properties) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            inputs = inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal), properties = properties.ToArray()
        })))).ToLowerInvariant();

}
