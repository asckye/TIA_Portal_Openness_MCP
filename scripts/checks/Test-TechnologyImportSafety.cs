#!/usr/bin/env dotnet
// Compile current production members against managed stand-ins; no TIA or Siemens assembly is loaded.
// Usage: dotnet run scripts/checks/Test-TechnologyImportSafety.cs -- -WorkDir bin-build/Test-TechnologyImportSafety
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../build-tools/common/TiaMcp.BuildCommon/TiaMcp.BuildCommon.csproj

using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

var options = args.ToList();
var root = FindRoot();
var work = Take(options, "-WorkDir", "--work-dir");
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
var sources = new EngineSources(root);
var helper = sources.Member("TryImportEngineeringObjectIntoCollection", signature: "bool overwrite");
var technology = string.Join("\n", sources.Member("ImportTechnologyObject", owner: "Portal", signature: "public void"), sources.Member("ImportTechnologyObject", signature: "private void"), sources.Member("ImportTechnologyObject", owner: "TechnologyObjectsService", tool: false), sources.Member("ImportTechnologyObjectsFromDirectory", owner: "TechnologyObjectsService", tool: false));
foreach (var required in new[] { "bool overwrite = true", "importPath, true, new List<string>()", "file, overwrite, imported", "Batch stopped; later files were not attempted.", "break;" }) Require(technology.Contains(required), required);
Require(!technology.Contains("imported.Add(name)"), "native identity");
Require(Regex.Matches(helper, "method.Invoke\\(").Count == 1 && !helper.Contains("typeof(FileInfo) });") && helper.Contains("overwrite ? ImportOptions.Override : ImportOptions.None") && !helper.Contains("GetFileNameWithoutExtension"), "single typed importer");
var program = """"
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
enum ImportOptions { None, Override }
class Named { public string Name { get; set; } = "NativeName"; }
class Collection {
    public int Calls;
    public int FallbackCalls;
    public ImportOptions Seen;
    public Func<object?> Result = () => new[] { new Named() };
    public object? Import(FileInfo file, ImportOptions options) { Calls++; Seen = options; return Result(); }
    public object Import(FileInfo file) { FallbackCalls++; throw new Exception("must not fall back"); }
}
class Optionless { public int Calls; public object Import(FileInfo file) { Calls++; return new[] { new Named() }; } }
class BrokenName { public string Name => throw new Exception("name read failed"); }
class Program {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static IEnumerable Partial() { yield return new Named { Name = "ActualFirst" }; throw new Exception("enumeration failed"); }
    static void Main() {
        var file = Path.GetTempFileName();
        try {
            foreach (var overwrite in new[] { false, true }) {
                var col = new Collection(); var names = new List<string>();
                Check(TryImportEngineeringObjectIntoCollection(col, file, overwrite, names, out var error), "success");
                Check(error == null && col.Calls == 1 && col.FallbackCalls == 0, "single call");
                Check(col.Seen == (overwrite ? ImportOptions.Override : ImportOptions.None), "overwrite mapping");
                Check(names.SequenceEqual(new[] { "NativeName" }), "native identity");
            }
            var multiple = new Collection { Result = () => new[] { new Named { Name = "A" }, new Named { Name = "B" } } };
            var found = new List<string>();
            Check(TryImportEngineeringObjectIntoCollection(multiple, file, false, found, out _) && found.SequenceEqual(new[] { "A", "B" }), "all identities");
            var only = new Optionless();
            Check(!TryImportEngineeringObjectIntoCollection(only, file, false, new List<string>(), out var missing) && only.Calls == 0 && missing!.Contains("no import was attempted"), "optionless rejected");
            var absent = new Collection();
            Check(!TryImportEngineeringObjectIntoCollection(absent, file + ".missing", false, new List<string>(), out _) && absent.Calls == 0, "missing file preflight");
            foreach (var result in new Func<object?>[] { () => throw new Exception("native failure"), () => null, () => Array.Empty<Named>(), () => "not identities", () => new object[] { new BrokenName() }, () => new[] { new Named { Name = " " } } }) {
                var col = new Collection { Result = result };
                Check(!TryImportEngineeringObjectIntoCollection(col, file, false, new List<string>(), out var error), "uncertain result rejected");
                Check(col.Calls == 1 && col.FallbackCalls == 0 && error!.Contains("project may have changed"), "uncertain no retry");
            }
            var partial = new Collection { Result = () => Partial() }; found = new List<string> { "PreviousFile" };
            Check(!TryImportEngineeringObjectIntoCollection(partial, file, true, found, out var partialError), "partial fails");
            Check(found.SequenceEqual(new[] { "PreviousFile", "ActualFirst" }) && partialError!.Contains("ActualFirst") && !partialError.Contains("PreviousFile"), "partial identities preserved");
            Console.WriteLine($"Technology import fake-collection checks passed: {checks}");
        } finally { File.Delete(file); }
    }
__HELPER__
}
"""".Replace("__HELPER__", helper);
using var scratch = work is null ? new OfflineFixtures("technology-import-", root) : null;
return ExtractedChecks.Run(root, Required(work ?? scratch!.DirectoryPath), program, links: ["src/Shared/NativePathSelection.cs", "src/Shared/NativeInputPolicy.cs", "src/Adapters.Contracts/AdapterPreconditionException.cs"]);

static string FindRoot() => Repository.FindRoot();
static string? Take(List<string> values, params string[] names)
{
    var index = values.FindIndex(names.Contains);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException("Option requires a value: " + values[index]);
    var value = values[index + 1]; values.RemoveRange(index, 2); return value;
}
static string Required(string? value) => value ?? throw new ArgumentException("-WorkDir is required");
static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
