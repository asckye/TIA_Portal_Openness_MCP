#!/usr/bin/env dotnet
// Lexically audit engine source against the local Siemens PublicAPI XML documentation.
// Usage: dotnet run scripts/diagnostics/Audit-OpennessCoverage.cs -- --api-dir <directory> [options]
#:property PublishAot=false

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var options = args.ToList();
var root = FindRoot(Directory.GetCurrentDirectory());
var apiPath = FullPath(Required(options, "--api-dir"));
var version = Take(options, "--version") ?? "V21";
var outputPath = FullPath(Take(options, "--output-dir") ?? Path.Combine(root, "bin-build", "audits", "openness-coverage-" + DateTime.Now.ToString("yyyyMMdd")));
var summaryPath = Take(options, "--summary-markdown");
var dynamicWasSupplied = options.Contains("--dynamic-coverage");
var dynamicPath = Take(options, "--dynamic-coverage");
if (!dynamicWasSupplied) dynamicPath = Path.Combine(root, "scripts", "diagnostics", "openness-dynamic-coverage.json");
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
if (!Directory.Exists(apiPath)) throw new DirectoryNotFoundException("PublicAPI directory not found: " + apiPath);

var sourcePath = Path.Combine(root, "src", "Engine");
Directory.CreateDirectory(outputPath);
var boilerplate = new HashSet<string>(new[]
{
    "Equals", "GetHashCode", "ToString", "GetEnumerator", "Any", "Contains", "IndexOf", "CopyTo",
    "GetAttribute", "GetAttributes", "GetAttributeInfos", "SetAttribute", "SetAttributes", "GetComposition",
    "GetCompositionInfos", "GetInvocationInfos", "GetCreationInfos", "GetService", "GetServiceInfos", "Invoke",
    "Find", "Count", "Parent", "Dispose", "Clone", "CompareTo", "GetType", "MemberwiseClone", "Finalize", "Item"
}, StringComparer.Ordinal);
var identPattern = new Regex("[A-Za-z_][A-Za-z_0-9]*", RegexOptions.Compiled);
var callPattern = new Regex(@"\.([A-Za-z_][A-Za-z_0-9]*)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.Compiled);
var accessPattern = new Regex(@"\.([A-Za-z_][A-Za-z_0-9]*)\b", RegexOptions.Compiled);
var tokens = new HashSet<string>(StringComparer.Ordinal);
var calls = new HashSet<string>(StringComparer.Ordinal);
var accesses = new HashSet<string>(StringComparer.Ordinal);
var sourceFiles = Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
    .Where(p => !Regex.IsMatch(p, @"[\\/](obj|bin|obj-v20|bin-v20)[\\/]", RegexOptions.IgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
Console.WriteLine("Scanning engine source under " + sourcePath + " ...");
foreach (var path in sourceFiles)
{
    var text = File.ReadAllText(path);
    foreach (Match m in identPattern.Matches(text)) tokens.Add(m.Value);
    foreach (Match m in callPattern.Matches(text)) calls.Add(m.Groups[1].Value);
    foreach (Match m in accessPattern.Matches(text)) accesses.Add(m.Groups[1].Value);
}
Console.WriteLine($"  {sourceFiles.Length} files, {tokens.Count} identifiers, {calls.Count} call names, {accesses.Count} member accesses");

var rows = new List<MemberRow>();
var xmlFiles = Directory.GetFiles(apiPath, "Siemens.Engineering*.xml")
    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
foreach (var path in xmlFiles)
{
    var assembly = Path.GetFileNameWithoutExtension(path);
    if (assembly.StartsWith("Siemens.Engineering.AddIn", StringComparison.OrdinalIgnoreCase)) continue;
    var doc = XDocument.Load(path);
    foreach (var memberElement in doc.Descendants("member"))
    {
        var signature = (string?)memberElement.Attribute("name") ?? "";
        if (signature.Length < 3) continue;
        var kind = signature[0];
        if (kind is not ('T' or 'M' or 'P')) continue;
        var full = signature[2..];
        if (full.StartsWith("Siemens.Engineering.Private.", StringComparison.Ordinal) ||
            full.StartsWith("Siemens.Engineering.Compiler.CompileProvider", StringComparison.Ordinal)) continue;
        var stem = full.Split('(', 2)[0];
        string owner;
        string member;
        if (kind == 'T') { owner = stem; member = ""; }
        else
        {
            var index = stem.LastIndexOf('.');
            if (index < 0) continue;
            owner = stem[..index];
            member = stem[(index + 1)..];
        }
        if (member.Contains('#'))
        {
            if (member.Contains("#ctor", StringComparison.Ordinal) || member.Contains("#cctor", StringComparison.Ordinal) ||
                Regex.IsMatch(member, @"#IEngineering|#I[A-Z]")) continue;
            member = member.Split('#').Last();
        }
        var ownerSimple = Regex.Replace(owner.Split('.').Last(), @"`\d+$", "");
        var lastDot = owner.LastIndexOf('.');
        var ns = lastDot >= 0 ? owner[..lastDot] : owner;
        ns = Regex.Replace(ns, @"`\d+", "");
        var isBoiler = kind is 'M' or 'P' && boilerplate.Contains(member);
        rows.Add(new MemberRow(version, assembly, ns, kind, owner, ownerSimple, member, signature, isBoiler));
    }
}
Console.WriteLine($"  {xmlFiles.Length} XML files, {rows.Count} T/M/P members (AddIn.* excluded)");

foreach (var row in rows)
{
    var ownerHit = tokens.Contains(row.OwnerSimple);
    if (row.Kind == 'T') row.Verdict = ownerHit ? "TYPE_NAMED" : "TYPE_UNNAMED";
    else if (row.Boilerplate) row.Verdict = "BOILERPLATE";
    else
    {
        var memberHit = row.Kind == 'M' ? calls.Contains(row.Member) : accesses.Contains(row.Member);
        row.Verdict = ownerHit && memberHit ? "REFERENCED" : ownerHit ? "OWNER_ONLY" : "UNREFERENCED";
    }
}

var dynamicEntries = new List<DynamicEntry>();
if (!string.IsNullOrEmpty(dynamicPath) && File.Exists(dynamicPath))
{
    var dynamicRoot = JsonNode.Parse(File.ReadAllText(dynamicPath, Encoding.UTF8))!.AsObject();
    foreach (var node in dynamicRoot["entries"]!.AsArray())
    {
        var entry = node!.AsObject();
        dynamicEntries.Add(new DynamicEntry(GetText(entry, "pattern"), GetText(entry, "tool"), GetText(entry, "verified")));
    }
    Console.WriteLine($"  {dynamicEntries.Count} dynamic-coverage patterns from {dynamicPath}");
}

var typeGroups = rows.Where(r => r.Kind != 'T' && !r.Boilerplate)
    .GroupBy(r => r.Owner, StringComparer.CurrentCultureIgnoreCase)
    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToArray();
var types = new List<TypeSummary>();
foreach (var group in typeGroups)
{
    var members = group.ToArray();
    var referenced = members.Count(r => r.Verdict == "REFERENCED");
    var ownerOnly = members.Count(r => r.Verdict == "OWNER_ONLY");
    var unreferenced = members.Count(r => r.Verdict == "UNREFERENCED");
    var first = members[0];
    var status = referenced > 0 ? "PARTIAL_OR_COVERED" : ownerOnly > 0 ? "TYPE_NAMED_NO_MEMBER" : "UNTOUCHED";
    DynamicEntry? dynamic = null;
    if (status != "PARTIAL_OR_COVERED")
    {
        dynamic = dynamicEntries.FirstOrDefault(d => Like(first.Owner, d.Pattern));
        if (dynamic != null) status = "DYNAMIC";
    }
    var unreferencedMembers = members.Where(r => r.Verdict != "REFERENCED").Select(r => r.Member)
        .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase);
    types.Add(new TypeSummary(first.Assembly, first.Namespace, group.Key, first.OwnerSimple, members.Length,
        referenced, ownerOnly, unreferenced, status, dynamic?.Tool ?? "", dynamic?.Verified ?? "", string.Join(" ", unreferencedMembers)));
}

var namespaceRows = types.GroupBy(t => t.Namespace, StringComparer.CurrentCultureIgnoreCase).Select(group =>
{
    var rowsInNamespace = group.ToArray();
    return new NamespaceSummary(rowsInNamespace[0].Assembly, group.Key, rowsInNamespace.Length,
        rowsInNamespace.Count(t => t.Status == "PARTIAL_OR_COVERED"),
        rowsInNamespace.Count(t => t.Status == "TYPE_NAMED_NO_MEMBER"),
        rowsInNamespace.Count(t => t.Status == "DYNAMIC"), rowsInNamespace.Count(t => t.Status == "UNTOUCHED"),
        rowsInNamespace.Sum(t => t.Members), rowsInNamespace.Sum(t => t.Referenced));
}).OrderBy(n => n.Assembly, StringComparer.CurrentCultureIgnoreCase)
  .ThenBy(n => n.Namespace, StringComparer.CurrentCultureIgnoreCase).ToArray();

WriteCsv(Path.Combine(outputPath, version + "-members.csv"),
    new[] { "version", "assembly", "namespace", "kind", "owner", "ownerSimple", "member", "signature", "boilerplate", "verdict" },
    rows.Select(r => new[] { r.Version, r.Assembly, r.Namespace, r.Kind.ToString(), r.Owner, r.OwnerSimple, r.Member, r.Signature, r.Boilerplate.ToString(), r.Verdict }));
WriteCsv(Path.Combine(outputPath, version + "-types.csv"),
    new[] { "assembly", "namespace", "type", "typeSimple", "members", "referenced", "ownerOnly", "unreferenced", "status", "dynamicTool", "dynamicVerified", "unreferencedMembers" },
    types.OrderBy(t => t.Assembly, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.Namespace, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.Type, StringComparer.CurrentCultureIgnoreCase)
        .Select(t => new[] { t.Assembly, t.Namespace, t.Type, t.TypeSimple, I(t.Members), I(t.Referenced), I(t.OwnerOnly), I(t.Unreferenced), t.Status, t.DynamicTool, t.DynamicVerified, t.UnreferencedMembers }));
WriteCsv(Path.Combine(outputPath, version + "-namespaces.csv"),
    new[] { "namespace", "assembly", "types", "typesCovered", "typesNamedOnly", "typesDynamic", "typesUntouched", "members", "membersReferenced" },
    namespaceRows.Select(n => new[] { n.Namespace, n.Assembly, I(n.Types), I(n.TypesCovered), I(n.TypesNamedOnly), I(n.TypesDynamic), I(n.TypesUntouched), I(n.Members), I(n.MembersReferenced) }));

var domainMembers = rows.Where(r => r.Kind != 'T' && !r.Boilerplate).ToArray();
var statistics = new JsonObject
{
    ["version"] = version,
    ["publicApiDirectory"] = apiPath,
    ["generatedAt"] = DateTime.Now.ToString("s"),
    ["xmlFiles"] = xmlFiles.Length,
    ["sourceFiles"] = sourceFiles.Length,
    ["domainMembers"] = domainMembers.Length,
    ["referenced"] = domainMembers.Count(r => r.Verdict == "REFERENCED"),
    ["ownerOnly"] = domainMembers.Count(r => r.Verdict == "OWNER_ONLY"),
    ["unreferenced"] = domainMembers.Count(r => r.Verdict == "UNREFERENCED"),
    ["types"] = types.Count,
    ["typesCovered"] = types.Count(t => t.Status == "PARTIAL_OR_COVERED"),
    ["typesNamedOnly"] = types.Count(t => t.Status == "TYPE_NAMED_NO_MEMBER"),
    ["typesDynamic"] = types.Count(t => t.Status == "DYNAMIC"),
    ["typesUntouched"] = types.Count(t => t.Status == "UNTOUCHED"),
    ["dynamicCoverageFile"] = dynamicEntries.Count > 0 ? dynamicPath : "",
    ["caveat"] = "Lexical only. REFERENCED = owner type name AND .member( both appear in engine source. Generic reflection tools can reach unreferenced members dynamically. AddIn.* assemblies and documented non-public types (Siemens.Engineering.Private.*, Compiler.CompileProvider) excluded."
};
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(outputPath, version + "-summary.json"), statistics.ToJsonString(jsonOptions), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine($"Domain members {statistics["domainMembers"]}: referenced {statistics["referenced"]} / owner-only {statistics["ownerOnly"]} / unreferenced {statistics["unreferenced"]}");
Console.WriteLine($"Types {statistics["types"]}: covered {statistics["typesCovered"]} / named-only {statistics["typesNamedOnly"]} / dynamic {statistics["typesDynamic"]} / untouched {statistics["typesUntouched"]}");
Console.WriteLine("Output: " + outputPath);

if (!string.IsNullOrEmpty(summaryPath))
{
    var sb = new StringBuilder();
    void Line(string value = "") => sb.Append(value).Append(Environment.NewLine);
    Line("<!-- 由 scripts/diagnostics/Audit-OpennessCoverage.cs 生成，勿手工编辑数字 -->");
    Line();
    Line("| 程序集 | 命名空间 | 类型数 | 有专用引用 | 仅类型名 | 动态覆盖 | 完全未触及 | 成员数 | 已引用成员 |");
    Line("|---|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var n in namespaceRows)
        Line($"| {Regex.Replace(n.Assembly, "^Siemens\\.Engineering\\.?", "")} | `{n.Namespace}` | {n.Types} | {n.TypesCovered} | {n.TypesNamedOnly} | {n.TypesDynamic} | {n.TypesUntouched} | {n.Members} | {n.MembersReferenced} |");
    if (dynamicEntries.Count > 0)
    {
        Line();
        Line("## 动态覆盖的类型（登记表 scripts/diagnostics/openness-dynamic-coverage.json）");
        Line();
        foreach (var group in types.Where(t => t.Status == "DYNAMIC").GroupBy(t => t.DynamicTool, StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            Line($"- **{group.Key}**：{group.Count()} 个类型（{string.Join('、', group.OrderBy(t => t.Type, StringComparer.CurrentCultureIgnoreCase).Select(t => t.TypeSimple))}）");
    }
    Line();
    Line("## 完全未触及的类型（按命名空间）");
    Line();
    foreach (var group in types.Where(t => t.Status == "UNTOUCHED").GroupBy(t => t.Namespace, StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
    {
        Line($"### `{group.Key}`");
        Line();
        foreach (var type in group.OrderBy(t => t.Type, StringComparer.CurrentCultureIgnoreCase))
        {
            var members = type.UnreferencedMembers.Length > 220 ? type.UnreferencedMembers[..220] + " …" : type.UnreferencedMembers;
            Line($"- **{type.TypeSimple}**（{type.Members} 个成员）：{members}");
        }
        Line();
    }
    File.WriteAllText(FullPath(summaryPath), sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine("Summary markdown: " + summaryPath);
}

static string GetText(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
static string I(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
static string FullPath(string path) => Path.GetFullPath(path);
static string Required(List<string> args, string name) => Take(args, name) ?? throw new ArgumentException("Missing required " + name);
static string? Take(List<string> args, string name)
{
    var index = args.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= args.Count) throw new ArgumentException("Missing value for " + name);
    var value = args[index + 1];
    args.RemoveAt(index + 1);
    args.RemoveAt(index);
    return value;
}
static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run this helper from within the repository.");
}
static bool Like(string value, string pattern)
{
    var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
static void WriteCsv(string path, string[] header, IEnumerable<string[]> rows)
{
    var sb = new StringBuilder();
    void WriteRow(IEnumerable<string> values) => sb.Append(string.Join(",", values.Select(v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\""))).Append(Environment.NewLine);
    WriteRow(header);
    foreach (var row in rows) WriteRow(row);
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
}

sealed record MemberRow(string Version, string Assembly, string Namespace, char Kind, string Owner, string OwnerSimple,
    string Member, string Signature, bool Boilerplate)
{
    public string Verdict { get; set; } = "";
}
sealed record DynamicEntry(string Pattern, string Tool, string Verified);
sealed record TypeSummary(string Assembly, string Namespace, string Type, string TypeSimple, int Members,
    int Referenced, int OwnerOnly, int Unreferenced, string Status, string DynamicTool, string DynamicVerified, string UnreferencedMembers);
sealed record NamespaceSummary(string Assembly, string Namespace, int Types, int TypesCovered, int TypesNamedOnly,
    int TypesDynamic, int TypesUntouched, int Members, int MembersReferenced);
