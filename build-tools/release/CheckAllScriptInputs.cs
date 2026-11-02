using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class CheckAllScriptInputs
{
    internal static string[] Closure(string script, string? root = null)
    {
        root ??= Repository.FindRoot();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string path)
        {
            if (!paths.Add(path)) return;
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) return;
            var text = File.ReadAllText(full);
            // An unresolved dynamic/relative loader retains the complete local data directory.
            if (Regex.IsMatch(text, @"(?m)^\s*from\s+\.|(?:import_module|__import__)\s*\(") ||
                text.Contains("spec_from_file_location", StringComparison.Ordinal) && Regex.IsMatch(text, @"with_name\s*\(\s*[A-Za-z_]"))
                paths.Add(Path.GetDirectoryName(path)!.Replace('\\', '/'));
            // Python local imports, including imports inside functions. Standard-library imports
            // have no sibling file; interpreter bytes remain part of the nightly identity.
            foreach (Match match in Regex.Matches(text, @"(?m)^\s*(?:from\s+([\w.]+)\s+import|import\s+([^\r\n#]+))"))
            {
                var names = match.Groups[1].Success ? new[] { match.Groups[1].Value } :
                    match.Groups[2].Value.Split(',').Select(name => name.Trim().Split(' ')[0]);
                foreach (var name in names)
                {
                    var module = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + name.TrimStart('.').Replace('.', '/');
                    // Missing local candidates remain inputs too: deleting a helper must still select its callers.
                    Visit(module + ".py");
                    Visit(module + "/__init__.py");
                }
            }
            // importlib/subprocess helpers also pass finite module stems to load(name + ".py").
            foreach (Match match in Regex.Matches(text, "['\"]([A-Za-z0-9_.-]+)['\"]"))
            {
                var name = match.Groups[1].Value;
                var sibling = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + (name.EndsWith(".py", StringComparison.Ordinal) ? name : name + ".py");
                if (File.Exists(Path.Combine(root, sibling)) || name.EndsWith(".py", StringComparison.Ordinal) ||
                    System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:Test-|Snapshot-|Check-|Inventory-)")) Visit(sibling);
            }
        }
        Visit(script);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] Data(string family) => family switch
    {
        "contracts" => ["manifest/contracts/v4", "reference/tool-catalog", "reference/tool-examples", "src/Adapters.Contracts/PortedFamilies.cs"],
        "responses" => ["manifest/contracts/v4", "reference/tool-catalog", "reference/tool-examples", "templates", "catalog", "scripts/checks/call-latency-baseline.json", "src/Adapters.Contracts/PortedFamilies.cs"],
        "usage" or "transport-usage" => ["reference/tool-examples", "reference/tool-catalog", "manifest/contracts/v4", "src/Shared/ToolUsageData.json", "src/Logic/ModelContextProtocol/ToolTaxonomy.cs", "scripts/checks/call-latency-baseline.json", "src/Adapters.Contracts/PortedFamilies.cs"],
        "campaign-inputs" => ["scripts/diagnostics/campaign", "catalog", "templates", "reference/tool-catalog"],
        "resources" or "ecosystem" => ["reference", "templates", "catalog", "third_party"],
        "harness" => ["reference", "templates", "catalog", "manifest/contracts/v4", "tests/Engine/TiaMcp.Engine.Harness/Golden"],
        "approval" or "stability" => ["manifest/contracts/v4"],
        "adapter-coverage" => [],
        _ => ["reference", "manifest", "templates", "catalog"]
    };
}
