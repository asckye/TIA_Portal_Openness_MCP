#!/usr/bin/env dotnet
// Compile current production members against managed stand-ins; no TIA or Siemens assembly is loaded.
// Usage: dotnet run scripts/checks/Test-HmiImportSafety.cs -- -WorkDir bin-build/Test-HmiImportSafety
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
var screen = sources.Member("ImportHmiScreen", owner: "HmiExchangeService", tool: false).Replace("_session.", "", StringComparison.Ordinal);
var batch = sources.Member("ImportHmiScreensFromDirectory", owner: "HmiExchangeService", tool: false).Replace("_session.", "", StringComparison.Ordinal);
var importItem = sources.Member("ImportItem", owner: "HmiExchangeService");
var helper = sources.Member("TryImportEngineeringObjectIntoCollection", signature: "out string? importedName");
var nameReader = sources.Member("BestEffortExtractFirstName");
Require(Regex.Matches(screen, "TryImportEngineeringObjectIntoCollection\\(").Count == 1, "one importer call");
Require(screen.Contains("GuardClassicScreenSize(sw, importPath);") && !screen.Contains("document.Save(") && !screen.Contains("Regex.Match("), "screen preflight");
Require(batch.Contains("break;") && batch.Contains("later matching files were not attempted") && helper.Contains("ImportOptions.Override"), "fail stop and existing default");
var wrapper = sources.Member("ImportHmiScreensFromDirectory", owner: "HmiExchangeTools", tool: false);
Require(wrapper.Contains("Imported = result.Imported") && wrapper.Contains("Failed = result.Failed") && wrapper.Contains("Meta = result.Meta"), "reporting path");
Require(batch.Contains("ResponseMeta.Basic(DateTime.Now, failed.Count == 0, (\"items\", items))"), "batch verdict");
var entry = sources.Member("ImportHmiScreensFromDirectoryV4", owner: "HmiExchangeTools", tool: true);
Require(entry.Contains("HmiExchangeContract.Run(\"ImportHmiScreensFromDirectory\", true, true,") && entry.Contains("() => ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite)"), "V4 path");
var program = """"
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
enum ImportOptions { None, Override }
enum PortalErrorCode { InvalidState, NotFound, ImportFailed, InvalidParams }
class PortalException : Exception {
    public PortalErrorCode Code { get; }
    public PortalException(PortalErrorCode code, string message, object? context = null, Exception? inner = null) : base(message, inner) { Code = code; }
}
class ImportFailure { public string? Path { get; set; } public string? Error { get; set; } }
class ResponseImportBatch { public List<string>? Imported { get; set; } public List<ImportFailure>? Failed { get; set; } public JsonObject? Meta { get; set; } }
class Container { public object? Software { get; set; } }
class Software { public object? Screens { get; set; } }
class Named { public string Name { get; set; } = "NativeName"; }
class FakeCollection {
    public int Calls, FallbackCalls;
    public ImportOptions Seen;
    public Func<FileInfo, object?> Result = file => new[] { new Named() };
    public object? Import(FileInfo file, ImportOptions options) { Calls++; Seen = options; return Result(file); }
    public object Import(FileInfo file) { FallbackCalls++; throw new Exception("must not invoke fallback"); }
}
class Optionless { public int Calls; public object Import(FileInfo file) { Calls++; return new[] { new Named() }; } }
class MissingImporter { }
class Harness {
    public bool NoProject, InvalidInput;
    public Container Container = new Container();
    public string? LastImportNotes { get; private set; }
    bool IsProjectNull() => NoProject;
    Container GetSoftwareContainer(string path) => Container;
    // Dependency double: exercise caller fail-stop when deterministic preflight rejects input.
    void GuardClassicScreenSize(object software, string path) { if (InvalidInput) throw new PortalException(PortalErrorCode.InvalidParams, "invalid preflight input"); }
    static object? TryGetPropertyValue(object obj, string name) => obj.GetType().GetProperty(name)?.GetValue(obj);
    static object? TryResolveChildGroupByPath(object root, string folder) => root;
__SCREEN__
__BATCH__
__IMPORT_ITEM__
__HELPER__
__NAME_READER__
}
class Program {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static string Failure(Action action) { try { action(); } catch (PortalException ex) { return ex.Message; } throw new Exception("expected failure"); }
    static Harness New(object collection) => new Harness { Container = new Container { Software = new Software { Screens = collection } } };
    static void Main() {
        var dir = Path.Combine(Path.GetTempPath(), "hmi-fake-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var file = Path.Combine(dir, "screen.xml");
            var xml = "<Document><Hmi.Screen.Button><AttributeList><Visible>true</Visible></AttributeList></Hmi.Screen.Button></Document>";
            File.WriteAllText(file, xml);
            foreach (var original in new[] { "'set_Visible' is not supported by type 'Hmi.Screen.Button'", "unknown native outcome after partial mutation" }) {
                var collection = new FakeCollection { Result = f => throw new Exception(original) };
                var harness = New(collection);
                var error = Failure(() => harness.ImportHmiScreen("hmi", "", file));
                Check(collection.Calls == 1 && collection.FallbackCalls == 0, "one invocation on native throw including old stripping trigger");
                Check(error.Contains(original) && error.Contains("project may have changed") && error.Contains("No automatic retry"), "original error and mutation uncertainty preserved");
                Check(File.ReadAllText(file) == xml && harness.LastImportNotes == null, "input retained and no stripping success note");
            }
            foreach (var result in new Func<FileInfo, object?>[] { f => null, f => Array.Empty<Named>(), f => new[] { new Named() } }) {
                var collection = new FakeCollection { Result = result }; New(collection).ImportHmiScreen("hmi", "", file);
                Check(collection.Calls == 1 && collection.FallbackCalls == 0 && collection.Seen == ImportOptions.Override, "legacy return handling/default retained without replay");
            }
            var optionless = new Optionless(); New(optionless).ImportHmiScreen("hmi", "", file);
            Check(optionless.Calls == 1, "pre-invocation overload discovery retained");
            foreach (var kind in new[] { "preflight", "missing file", "no project", "no software", "no collection" }) {
                var collection = new FakeCollection(); var harness = New(collection); var path = file;
                if (kind == "preflight") harness.InvalidInput = true;
                if (kind == "missing file") path += ".missing";
                if (kind == "no project") harness.NoProject = true;
                if (kind == "no software") harness.Container.Software = null;
                if (kind == "no collection") harness.Container.Software = new Software();
                Failure(() => harness.ImportHmiScreen("hmi", "", path));
                Check(collection.Calls == 0 && collection.FallbackCalls == 0, "zero calls for " + kind);
            }
            Failure(() => New(new MissingImporter()).ImportHmiScreen("hmi", "", file));
            Check(true, "missing overload rejected");
            File.WriteAllText(Path.Combine(dir, "second.xml"), xml);
            File.WriteAllText(Path.Combine(dir, "third.xml"), xml);
            var files = Directory.EnumerateFiles(dir, "*.xml").ToArray();
            var batchCollection = new FakeCollection { Result = f => f.FullName == files[1] ? throw new Exception("batch unknown outcome") : new[] { new Named() } };
            var batch = New(batchCollection).ImportHmiScreensFromDirectory("hmi", "", dir);
            Check(batchCollection.Calls == 2 && batchCollection.FallbackCalls == 0, "batch stops on first failed screen");
            Check(batch.Imported!.SequenceEqual(new[] { Path.GetFileNameWithoutExtension(files[0]) }), "prior successful file retained");
            Check(batch.Failed!.Count == 1 && batch.Failed[0].Path == files[1] && batch.Failed[0].Error!.Contains("batch unknown outcome") && batch.Failed[0].Error!.Contains("later matching files were not attempted"), "failed path and unattempted remainder preserved");
            Check((bool?)batch.Meta!["success"] == false, "a failed batch never reports success");
            var items = batch.Meta["items"]!.AsArray();
            Check(items.Count == 2 && (string?)items[0]!["target"] == files[0] && (string?)items[1]!["target"] == files[1], "batch evidence retains actual attempted file order");
            Check((bool?)items[0]!["evidence"]!["operationSuccess"] == true && (bool?)items[1]!["evidence"]!["operationSuccess"] == false
                && (bool?)items[1]!["evidence"]!["batchStopped"] == true, "success and fail-stop evidence survive together");
            var invalid = new FakeCollection(); var invalidHarness = New(invalid); invalidHarness.InvalidInput = true;
            var rejected = invalidHarness.ImportHmiScreensFromDirectory("hmi", "", dir);
            Check(invalid.Calls == 0 && rejected.Failed!.Count == 1 && rejected.Imported!.Count == 0, "batch preflight failure also stops without native entry");
            Check((bool?)rejected.Meta!["success"] == false && rejected.Meta["items"]!.AsArray().Count == 1, "preflight rejection retains its failed verdict and item");
            var accepted = New(new FakeCollection()).ImportHmiScreensFromDirectory("hmi", "", dir);
            Check((bool?)accepted.Meta!["success"] == true && accepted.Imported!.Count == files.Length
                && accepted.Meta["items"]!.AsArray().Count == files.Length, "only a completely successful batch reports success");
            Console.WriteLine($"HMI import fake-only checks passed: {checks}");
        } finally { Directory.Delete(dir, true); }
    }
}
"""".Replace("__SCREEN__", screen).Replace("__BATCH__", batch).Replace("__IMPORT_ITEM__", importItem).Replace("__HELPER__", helper).Replace("__NAME_READER__", nameReader);
using var scratch = work is null ? new OfflineFixtures("hmi-import-", root) : null;
return ExtractedChecks.Run(root, Required(work ?? scratch!.DirectoryPath), program, links: ["src/Shared/NativePathSelection.cs", "src/Shared/NativeInputPolicy.cs", "src/Adapters.Contracts/AdapterPreconditionException.cs"], copies: ["src/Logic/ModelContextProtocol/ResponseMeta.cs", "src/Logic/ModelContextProtocol/ResponseClock.cs"]);

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
