using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class AdapterBoundary
{
    private static readonly Regex Forbidden = new(@"(?:TiaMcp\.Logic|Newtonsoft(?:\.Json)?|System\.Text\.Json|System\.Json|System\.Web\.Extensions|Utf8Json|Jil)(?:\b|/)", RegexOptions.IgnoreCase);
    internal static List<string> Check(string root)
    {
        var errors = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        void Inspect(string path)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            foreach (var node in XDocument.Load(path).Descendants())
            {
                var kind = node.Name.LocalName;
                if (kind is not ("Reference" or "PackageReference" or "ProjectReference")) continue;
                var value = node.Attribute("Include")?.Value ?? "";
                if (Forbidden.IsMatch(value)) errors.Add($"{Relative(path)}: forbidden {kind}: {value}");
                if (kind != "ProjectReference" || node.Attribute("ReferenceOutputAssembly")?.Value.Equals("false", StringComparison.OrdinalIgnoreCase) == true) continue;
                var target = value.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(path) + "/", StringComparison.Ordinal).Replace('\\', '/');
                if (target.Contains("$(", StringComparison.Ordinal)) errors.Add($"{Relative(path)}: unevaluated project reference: {value}");
                else
                {
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
                    if (!File.Exists(resolved)) errors.Add($"{Relative(path)}: missing project reference: {value}"); else Inspect(resolved);
                }
            }
        }
        foreach (var folder in new[] { "src/Adapters", "src/Adapters.Contracts" })
            foreach (var path in Directory.Exists(Path.Combine(root, folder)) ? Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories) : [])
            {
                if (Relative(path).Split('/').Any(p => p is "bin" or "obj")) continue;
                if (Path.GetExtension(path) is ".csproj" or ".props" or ".targets") Inspect(path);
                else if (Path.GetExtension(path) == ".cs" && Relative(path) is not ("src/Adapters/build/Test-AdapterInputs.cs" or "src/Adapters/build/Test-WorkerIsolation.cs") && Forbidden.IsMatch(Repository.ReadSource(path))) errors.Add(Relative(path) + ": forbidden host/JSON source dependency");
            }
        return errors;
    }
}
