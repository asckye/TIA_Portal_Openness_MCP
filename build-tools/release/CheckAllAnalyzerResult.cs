using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static class CheckAllAnalyzerResult
{
    private sealed record Receipt(string Root, string Inputs, string Reports, string Diagnostics, bool Passed);
    private static string Inputs(string root) => CheckAllResultCache.Fingerprint(CheckAllResultCache.Enumerate(root,
        CheckAllLayout.ResultSourceInventory(root, CheckAllLayout.SourceInventory(root).Concat(["src", "tests", "build-tools", "scripts", "third_party"])).Where(path =>
            Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets" or ".globalconfig" or ".ruleset" || Path.GetFileName(path) == ".editorconfig" ||
            path is ".editorconfig" or "global.json" or "scripts/checks/analyzer-warnings-baseline.json"),
        new Dictionary<string, string>()), "analyzer-warning job inputs");
    private static string Reports(string root, string directory) => CheckAllResultCache.Fingerprint(
        CheckAllResultCache.Enumerate(root, [], new Dictionary<string, string> { ["reports"] = directory }), "compiler reports");
    internal static void Write(string root, string directory, string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(new Receipt(Path.GetFullPath(root), Inputs(root), Reports(root, directory), directory, true)) + "\n");
    internal static bool Valid(string root, string path)
    {
        try
        {
            var row = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
            return row != null && row.Passed && row.Root == Path.GetFullPath(root) && row.Inputs == Inputs(root) && row.Reports == Reports(root, row.Diagnostics);
        }
        catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex)) { return false; }
    }
}
