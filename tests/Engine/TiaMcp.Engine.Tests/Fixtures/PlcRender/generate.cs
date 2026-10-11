#!/usr/bin/env dotnet
// Regenerate renderer goldens and reviewer samples; no TIA or native API is invoked.
// Usage: dotnet run tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender/generate.cs -- [-Check]
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../../../../src/Logic/TiaMcp.Logic.csproj

using System.Net;
using System.Text;
using TiaMcpServer.ModelContextProtocol;

var check = args.Length == 1 && args[0] is "-Check" or "--check";
if (args.Length != 0 && !check) throw new ArgumentException("Unexpected argument");
var root = FindRoot();
var fixtures = Path.GetFullPath(Path.Combine(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender"));
var artifacts = Path.GetFullPath(Path.Combine(root, "bin-build/P6-62"));
var samples = Path.Combine(artifacts, "samples");
Directory.CreateDirectory(samples);
var inputs = Directory.GetFiles(Path.GetFullPath(Path.Combine(root, "templates/mcp-full-e2e-verify/plc/blocks")), "*.xml").Order(StringComparer.Ordinal).ToList();
inputs.Add(Path.GetFullPath(Path.Combine(root, "templates/plc/block-xml/DB_HMI_Interface.xml")));
inputs.AddRange(Directory.GetFiles(Path.Combine(fixtures, "Parts"), "*.xml").Order(StringComparer.Ordinal));
if (inputs.Count != 10) throw new InvalidOperationException($"Expected 10 inputs, got {inputs.Count}");
inputs.Add(Path.GetFullPath(Path.Combine(fixtures, "Catalog/Primer.xml")));
var stage = Path.Combine(artifacts, "goldens-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stage);
var targets = new List<(string Output, string Target, List<(string Absolute, string Relative)> Mappings)>();
for (var index = 0; index < inputs.Count; index++)
{
    var source = inputs[index]; var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
    var local = Path.GetDirectoryName(source) == Path.Combine(fixtures, "Parts") || Path.GetDirectoryName(source) == Path.Combine(fixtures, "Catalog");
    var target = local ? source + ".html" : Path.Combine(fixtures, relative.Replace('/', '_') + ".html");
    var output = Path.Combine(stage, index + ".html");
    var result = PlcProgramRenderer.Write(source, output, false, "21");
    if (!result.Ok) throw new InvalidOperationException(result.Error!.Message);
    targets.Add((output, target, [(source, relative)]));
}
var exports = Path.Combine(stage, "exports");
Directory.CreateDirectory(exports);
var mappings = new List<(string, string)>();
foreach (var source in inputs.Take(4))
{
    var copy = Path.Combine(exports, Path.GetFileName(source)); File.Copy(source, copy);
    mappings.Add((copy, Path.GetRelativePath(root, source).Replace('\\', '/')));
}
var atlas = Path.Combine(stage, "atlas.html");
var atlasResult = PlcProgramRenderer.Write(exports, atlas, true, "21");
if (!atlasResult.Ok) throw new InvalidOperationException(atlasResult.Error!.Message);
targets.Add((atlas, Path.Combine(fixtures, "Catalog/cookbook.html"), mappings));
var failed = 0;
foreach (var (output, target, replacements) in targets)
{
    var html = File.ReadAllText(output).Replace("\r\n", "\n", StringComparison.Ordinal);
    foreach (var (absolute, relative) in replacements) html = html.Replace(WebUtility.HtmlEncode(absolute), relative, StringComparison.Ordinal);
    var bytes = Encoding.UTF8.GetBytes(html);
    if (check)
    {
        if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) { Console.Error.WriteLine("stale renderer golden: " + Path.GetRelativePath(root, target)); failed++; }
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
        File.WriteAllBytes(Path.Combine(samples, Path.GetFileName(target) == "cookbook.html" ? "atlas.html" : Path.GetFileName(target)), bytes);
    }
}
foreach (var obsolete in Directory.GetFiles(fixtures, "plugin_skill_lad-cookbook_*.html"))
    if (check) { Console.Error.WriteLine("obsolete renderer golden: " + obsolete); failed++; } else File.Delete(obsolete);
Console.WriteLine(check ? $"Renderer goldens: {targets.Count - failed} passed, {failed} failed." : "Generated 11 block goldens, 1 catalog golden and 12 reviewer samples.");
return failed == 0 ? 0 : 1;

static string FindRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Repository root not found");
}
